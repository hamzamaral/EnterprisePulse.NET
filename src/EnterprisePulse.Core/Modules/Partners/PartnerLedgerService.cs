using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Partners;

/// <summary>Exactly one of <see cref="Debit"/> / <see cref="Credit"/> is positive, in the document currency.</summary>
public sealed record LedgerPosting(
    Guid PartnerId, DateOnly EntryDate, DateOnly? DueDate, LedgerDocumentType DocumentType, Guid DocumentId, string DocumentNumber,
    string? Description, decimal Debit, decimal Credit, string CurrencyCode, decimal ExchangeRate);

public sealed record StatementEntryDto(
    Guid Id, DateOnly EntryDate, DateOnly? DueDate, LedgerDocumentType DocumentType, Guid? DocumentId, string DocumentNumber,
    string? Description, string CurrencyCode, decimal ExchangeRate, decimal Debit, decimal Credit, decimal DebitBase, decimal CreditBase,
    decimal Balance, bool IsReversal);

public sealed record PartnerStatementDto(
    Guid PartnerId, string Code, string Title, string BaseCurrency, DateOnly? DateFrom, DateOnly? DateTo,
    decimal OpeningBalance, decimal TotalDebit, decimal TotalCredit, decimal ClosingBalance, List<StatementEntryDto> Entries);

public static class PartnerSides
{
    public static readonly PartnerType[] Customers = [PartnerType.CUSTOMER, PartnerType.BOTH];
    public static readonly PartnerType[] Suppliers = [PartnerType.SUPPLIER, PartnerType.BOTH];
}

/// <summary>
/// Cari hesap. Entries are append-only: documents post inside their own transaction and cancellations write reversing
/// entries. Debit increases what the partner owes us; credit what we owe the partner.
/// </summary>
[ModuleService]
public sealed class PartnerLedgerService(AppDbContext db)
{
    public async Task<PartnerLedgerEntry> PostAsync(LedgerPosting p)
    {
        Guard.That(p.Debit >= 0 && p.Credit >= 0 && p.Debit + p.Credit > 0, "Cari tutarı sıfırdan büyük olmalı");
        var entry = new PartnerLedgerEntry
        {
            PartnerId = p.PartnerId, EntryDate = p.EntryDate, DueDate = p.DueDate, DocumentType = p.DocumentType, DocumentId = p.DocumentId,
            DocumentNumber = p.DocumentNumber, Description = p.Description, Debit = p.Debit, Credit = p.Credit, CurrencyCode = p.CurrencyCode,
            ExchangeRate = p.ExchangeRate, DebitBase = DocumentMath.ToBase(p.Debit, p.ExchangeRate), CreditBase = DocumentMath.ToBase(p.Credit, p.ExchangeRate),
            CreatedById = db.User.RequireUserId(),
        };
        db.PartnerLedgerEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    /// <summary>Reverses every open entry of a document (debit and credit swapped), dated <paramref name="entryDate"/>.</summary>
    public async Task ReverseAsync(LedgerDocumentType type, Guid documentId, DateOnly entryDate, string description)
    {
        var entries = await db.PartnerLedgerEntries
            .Where(e => e.DocumentType == type && e.DocumentId == documentId && e.ReversesId == null && e.Reversal == null).ToListAsync();
        foreach (var e in entries)
        {
            db.PartnerLedgerEntries.Add(new PartnerLedgerEntry
            {
                PartnerId = e.PartnerId, EntryDate = entryDate, DocumentType = type, DocumentId = documentId, DocumentNumber = e.DocumentNumber,
                Description = description, Debit = e.Credit, Credit = e.Debit, CurrencyCode = e.CurrencyCode, ExchangeRate = e.ExchangeRate,
                DebitBase = e.CreditBase, CreditBase = e.DebitBase, ReversesId = e.Id, CreatedById = db.User.RequireUserId(),
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Base currency balances (debit − credit) by partner.</summary>
    public async Task<Dictionary<Guid, decimal>> BalancesAsync(IReadOnlyCollection<Guid> partnerIds)
    {
        var rows = await db.PartnerLedgerEntries.Where(e => partnerIds.Contains(e.PartnerId)).GroupBy(e => e.PartnerId)
            .Select(g => new { g.Key, Balance = g.Sum(e => e.DebitBase) - g.Sum(e => e.CreditBase) }).ToListAsync();
        var result = partnerIds.Distinct().ToDictionary(id => id, _ => 0m);
        foreach (var r in rows) result[r.Key] = r.Balance;
        return result;
    }

    public async Task<decimal> BalanceAsync(Guid partnerId) => (await BalancesAsync([partnerId]))[partnerId];

    /// <summary>Ekstre: opening balance before the period, entries with a running balance, closing balance.</summary>
    public async Task<PartnerStatementDto> StatementAsync(Guid partnerId, DateOnly? from, DateOnly? to)
    {
        var partner = Guard.Found(await db.BusinessPartners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partnerId), "Cari hesap bulunamadı");
        var baseCurrency = await db.Companies.Where(c => c.Id == partner.CompanyId).Select(c => c.BaseCurrency).FirstAsync();
        var q = db.PartnerLedgerEntries.AsNoTracking().Where(e => e.PartnerId == partnerId);
        var opening = from == null ? 0m : await q.Where(e => e.EntryDate < from).SumAsync(e => e.DebitBase - e.CreditBase);
        var rows = await q.Where(e => (from == null || e.EntryDate >= from) && (to == null || e.EntryDate <= to))
            .OrderBy(e => e.EntryDate).ThenBy(e => e.Seq).ToListAsync();
        decimal running = opening, debit = 0, credit = 0;
        var entries = rows.Select(r =>
        {
            running += r.DebitBase - r.CreditBase;
            debit += r.DebitBase;
            credit += r.CreditBase;
            return new StatementEntryDto(r.Id, r.EntryDate, r.DueDate, r.DocumentType, r.DocumentId, r.DocumentNumber, r.Description, r.CurrencyCode,
                r.ExchangeRate, r.Debit, r.Credit, r.DebitBase, r.CreditBase, running, r.ReversesId != null);
        }).ToList();
        return new PartnerStatementDto(partner.Id, partner.Code, partner.Title, baseCurrency, from, to, opening, debit, credit, running, entries);
    }

    /// <summary>Loads an active partner for a new document and checks it may act on that side.</summary>
    public async Task<BusinessPartner> ForDocumentAsync(Guid id, PartnerType[] allowed)
    {
        var partner = Guard.Found(await db.BusinessPartners.FirstOrDefaultAsync(p => p.Id == id), "Cari hesap bulunamadı");
        Guard.State(partner.IsActive, $"{partner.Title} pasif durumda");
        Guard.State(allowed.Contains(partner.Type),
            allowed.Contains(PartnerType.CUSTOMER) ? $"{partner.Title} bir müşteri değil" : $"{partner.Title} bir tedarikçi değil");
        return partner;
    }
}
