using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Trade;

/// <summary>A document line as entered: an item (optional for free-text lines) or a description, priced.</summary>
public sealed record TradeLineInput(
    Guid? VariantId, string? Description, decimal Quantity, decimal UnitPrice, decimal DiscountRate = 0, decimal VatRate = 0,
    Guid? RequestLineId = null, Guid? OrderLineId = null);

public sealed record PricedLine(
    int LineNo, Guid? VariantId, string Description, decimal Quantity, decimal UnitPrice, decimal DiscountRate, decimal VatRate,
    decimal LineTotal, TradeLineInput Input);

public sealed record PricedDocument(List<PricedLine> Lines, DocumentTotals Totals);

public sealed record CurrencyChoice(string CurrencyCode, decimal ExchangeRate, string BaseCurrency);

/// <summary>Stored line of a quote, order or invoice, as the screens show it.</summary>
public sealed record TradeLineDto(
    Guid Id, int LineNo, Guid? VariantId, Guid? ProductId, string? Sku, ProductType? ProductType, string? UnitCode,
    string Description, decimal Quantity, decimal UnitPrice, decimal DiscountRate, decimal VatRate, decimal LineTotal);

public sealed record TotalsDto(decimal Subtotal, decimal DiscountTotal, decimal VatTotal, decimal GrandTotal, decimal GrandTotalBase);

/// <summary>Helpers shared by purchasing and sales documents.</summary>
[ModuleService]
public sealed class TradeService(AppDbContext db)
{
    /// <summary>1 for the base currency, the given rate, or the latest stored selling rate on or before the date.</summary>
    public async Task<CurrencyChoice> ResolveCurrencyAsync(string? currencyCode, decimal? exchangeRate, DateOnly date)
    {
        var baseCurrency = await db.Companies.Where(c => c.Id == db.User.CompanyId).Select(c => c.BaseCurrency).FirstAsync();
        var code = string.IsNullOrWhiteSpace(currencyCode) ? baseCurrency : currencyCode.Trim().ToUpperInvariant();
        if (code == baseCurrency) return new CurrencyChoice(code, 1, baseCurrency);
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.Code == code);
        Guard.That(currency?.IsActive == true, $"Bilinmeyen para birimi: {code}");
        if (exchangeRate != null)
        {
            Guard.That(exchangeRate > 0, "Kur sıfırdan büyük olmalı");
            return new CurrencyChoice(code, exchangeRate.Value, baseCurrency);
        }
        var latest = await db.ExchangeRates.Where(r => r.CurrencyCode == code && r.RateDate <= date)
            .OrderByDescending(r => r.RateDate).Select(r => (decimal?)r.Selling).FirstOrDefaultAsync();
        Guard.That(latest != null, $"{code} için kur bulunamadı; kuru elle girin");
        return new CurrencyChoice(code, latest!.Value, baseCurrency);
    }

    /// <summary>Loads the variants used by the lines; inactive items are rejected.</summary>
    public async Task<Dictionary<Guid, ProductVariant>> LoadVariantsAsync(IEnumerable<Guid?> variantIds)
    {
        var ids = variantIds.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var rows = await db.ProductVariants.Include(v => v.Product).ThenInclude(p => p.Unit).Where(v => ids.Contains(v.Id)).ToListAsync();
        Guard.That(rows.Count == ids.Count, "Bir veya daha fazla ürün bulunamadı");
        foreach (var v in rows)
        {
            Guard.That(v.IsActive && v.Product.IsActive && v.Product.DeletedAt == null, $"{v.Sku} pasif durumda");
        }
        return rows.ToDictionary(v => v.Id);
    }

    /// <summary>Numbers the lines, fills descriptions and computes line and document totals.</summary>
    public static PricedDocument PriceLines(IReadOnlyList<TradeLineInput> inputs, IReadOnlyDictionary<Guid, ProductVariant> variants, bool requireItem = false)
    {
        Guard.That(inputs.Count > 0, "En az bir satır gerekli");
        var lines = new List<PricedLine>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var variant = input.VariantId is { } vid ? variants[vid] : null;
            Guard.That(!requireItem || variant != null, $"Satır {i + 1}: ürün seçilmeli");
            Guard.That(input.Quantity > 0, $"Satır {i + 1}: miktar sıfırdan büyük olmalı");
            Guard.That(input.UnitPrice >= 0, $"Satır {i + 1}: birim fiyat negatif olamaz");
            Guard.That(input.DiscountRate is >= 0 and <= 100, $"Satır {i + 1}: iskonto %0-100 arasında olmalı");
            Guard.That(input.VatRate is >= 0 and <= 100, $"Satır {i + 1}: KDV %0-100 arasında olmalı");
            var description = !string.IsNullOrWhiteSpace(input.Description) ? input.Description.Trim() : variant != null ? Label(variant) : "";
            Guard.That(description.Length > 0, $"Satır {i + 1}: açıklama gerekli");
            var amounts = DocumentMath.Line(Input(input));
            lines.Add(new PricedLine(i + 1, variant?.Id, description, DocumentMath.Round3(input.Quantity), input.UnitPrice, input.DiscountRate, input.VatRate,
                amounts.Total, input));
        }
        return new PricedDocument(lines, DocumentMath.Document(inputs.Select(Input)));
    }

    private static LineInput Input(TradeLineInput l) => new(l.Quantity, l.UnitPrice, l.DiscountRate, l.VatRate);

    public static string Label(ProductVariant v) => v.Name is { Length: > 0 } n ? $"{v.Product.Name} ({n})" : v.Product.Name;

    public static bool IsStocked(ProductVariant? v) => v?.Product.Type == ProductType.GOODS;

    public static TradeLineDto LineDto(Guid id, int lineNo, ProductVariant? v, string description, decimal quantity, decimal unitPrice,
        decimal discountRate, decimal vatRate, decimal lineTotal) =>
        new(id, lineNo, v?.Id, v?.ProductId, v?.Sku, v?.Product.Type, v?.Product.Unit?.Code, description, quantity, unitPrice, discountRate, vatRate, lineTotal);

    public static TotalsDto Totals(decimal subtotal, decimal discountTotal, decimal vatTotal, decimal grandTotal, decimal rate) =>
        new(subtotal, discountTotal, vatTotal, grandTotal, DocumentMath.ToBase(grandTotal, rate));

    public static bool IsOverdue(InvoiceStatus status, PaymentStatus payment, DateOnly dueDate) =>
        status == InvoiceStatus.ISSUED && payment != PaymentStatus.PAID && dueDate < DateOnly.FromDateTime(DateTime.UtcNow);
}
