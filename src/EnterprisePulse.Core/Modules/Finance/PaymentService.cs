using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Finance;

public sealed record AllocationInput(Guid? SalesInvoiceId, Guid? PurchaseInvoiceId, decimal Amount);

public sealed record CreatePaymentRequest(
    PaymentDirection Direction, PaymentMethod Method, Guid PartnerId, DateOnly PaymentDate, Guid? AccountId, decimal Amount,
    string? CurrencyCode, decimal? ExchangeRate, string? Description, string? ChequeSerial, string? BankName, string? BranchName, string? Drawer,
    DateOnly? DueDate, Guid? EndorseChequeId, List<AllocationInput>? Allocations);

public sealed record AllocationDto(Guid? SalesInvoiceId, string? SalesInvoiceNumber, Guid? PurchaseInvoiceId, string? PurchaseInvoiceNumber, decimal Amount);

public sealed record PaymentDetail(
    Guid Id, string Number, PaymentDirection Direction, PaymentMethod Method, Guid PartnerId, string PartnerTitle, DateOnly PaymentDate,
    Guid? AccountId, string? AccountName, decimal Amount, string CurrencyCode, decimal ExchangeRate, decimal AmountBase, string? Description,
    PaymentRecordStatus Status, Guid? ChequeId, string? ChequeNumber, List<AllocationDto> Allocations);

/// <summary>
/// Tahsilat (TAH) and ödeme (ODM). Cash and bank hit the treasury immediately. A cheque or note stays in the portfolio
/// until it is collected, paid or endorsed.
/// </summary>
[ModuleService]
public sealed class PaymentService(AppDbContext db, NumberingService numbering, TradeService trade, PartnerLedgerService ledger, TreasuryService treasury, ChequeService cheques)
{
    public async Task<PaymentDetail> CreateAsync(CreatePaymentRequest dto)
    {
        Guard.That(dto.Amount > 0, "Tutar sıfırdan büyük olmalı");
        await using var tx = await db.Database.BeginTransactionAsync();
        var partner = await ledger.ForDocumentAsync(dto.PartnerId, dto.Direction == PaymentDirection.IN ? PartnerSides.Customers : PartnerSides.Suppliers);
        var date = dto.PaymentDate == default ? DateOnly.FromDateTime(DateTime.UtcNow) : dto.PaymentDate;
        var currency = await trade.ResolveCurrencyAsync(dto.CurrencyCode ?? partner.CurrencyCode, dto.ExchangeRate, date);
        TreasuryAccount? account = null;
        if (dto.Method is PaymentMethod.CASH or PaymentMethod.BANK)
        {
            account = Guard.Found(await db.TreasuryAccounts.FirstOrDefaultAsync(a => a.Id == dto.AccountId && a.IsActive), "Kasa veya banka hesabı seçilmeli");
            Guard.That(dto.Method == PaymentMethod.CASH ? account.Type == TreasuryAccountType.CASH : account.Type == TreasuryAccountType.BANK, "Hesap türü ödeme yöntemiyle uyuşmuyor");
            Guard.That(account.CurrencyCode == currency.CurrencyCode, "Hesabın para birimi belgenin para birimiyle aynı olmalı");
        }
        else
        {
            Guard.That(dto.EndorseChequeId != null || !string.IsNullOrWhiteSpace(dto.ChequeSerial), "Çek seri numarası gerekli");
            if (dto.EndorseChequeId == null) Guard.That(dto.DueDate != null, "Çek vadesi gerekli");
        }

        var payment = new PartnerPayment
        {
            Number = await numbering.NextAsync(dto.Direction == PaymentDirection.IN ? "COLLECTION" : "PAYMENT"),
            Direction = dto.Direction, Method = dto.Method, PartnerId = partner.Id, PaymentDate = date, AccountId = account?.Id,
            Amount = DocumentMath.Round2(dto.Amount), CurrencyCode = currency.CurrencyCode, ExchangeRate = currency.ExchangeRate,
            AmountBase = DocumentMath.ToBase(dto.Amount, currency.ExchangeRate),
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(), CreatedById = db.User.RequireUserId(),
        };
        db.PartnerPayments.Add(payment);
        await db.SaveChangesAsync();
        await AllocateAsync(payment, dto.Allocations ?? []);

        if (account != null)
        {
            var inbound = dto.Direction == PaymentDirection.IN;
            await treasury.PostAsync(account.Id, date, inbound ? TreasuryDocumentType.COLLECTION : TreasuryDocumentType.PAYMENT, payment.Id, payment.Number,
                payment.Description, inbound ? payment.Amount : 0, inbound ? 0 : payment.Amount);
        }
        else if (dto.EndorseChequeId is { } chequeId)
        {
            Guard.That(dto.Direction == PaymentDirection.OUT && dto.Method == PaymentMethod.CHEQUE, "Ciro yalnızca çek ile yapılan ödemede kullanılır");
            await cheques.EndorseAsync(chequeId, payment);
        }
        else
        {
            var kind = dto.Method == PaymentMethod.NOTE ? ChequeKind.NOTE : ChequeKind.CHEQUE;
            var number = await numbering.NextAsync(kind == ChequeKind.NOTE ? "PROMISSORY_NOTE" : "CHEQUE");
            await cheques.OpenAsync(payment, kind, dto.ChequeSerial!, dto.BankName, dto.BranchName, dto.Drawer, dto.DueDate!.Value, number);
        }

        var incoming = dto.Direction == PaymentDirection.IN;
        await ledger.PostAsync(new LedgerPosting(partner.Id, date, dto.DueDate, incoming ? LedgerDocumentType.COLLECTION : LedgerDocumentType.PAYMENT,
            payment.Id, payment.Number, payment.Description ?? (incoming ? "Tahsilat" : "Ödeme"),
            incoming ? 0 : payment.Amount, incoming ? payment.Amount : 0, currency.CurrencyCode, currency.ExchangeRate));
        await tx.CommitAsync();
        return await GetAsync(payment.Id);
    }

    public async Task<PaymentDetail> CancelAsync(Guid id, string? reason)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT id FROM partner_payments WHERE id = {id} FOR UPDATE");
        var payment = Guard.Found(await db.PartnerPayments.Include(p => p.Allocations).FirstOrDefaultAsync(p => p.Id == id), "Tahsilat veya ödeme bulunamadı");
        Guard.State(payment.Status == PaymentRecordStatus.POSTED, "Belge zaten iptal edilmiş");
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var allocation in payment.Allocations)
        {
            if (allocation.SalesInvoiceId is { } salesId)
            {
                var invoice = await db.SalesInvoices.FirstAsync(i => i.Id == salesId);
                InvoicePayments.Apply(invoice, -allocation.Amount);
            }
            if (allocation.PurchaseInvoiceId is { } purchaseId)
            {
                var invoice = await db.PurchaseInvoices.FirstAsync(i => i.Id == purchaseId);
                InvoicePayments.Apply(invoice, -allocation.Amount);
            }
        }
        if (payment.Method is PaymentMethod.CASH or PaymentMethod.BANK)
        {
            await treasury.ReverseAsync(payment.Direction == PaymentDirection.IN ? TreasuryDocumentType.COLLECTION : TreasuryDocumentType.PAYMENT,
                payment.Id, date, $"İptal: {payment.Number}");
        }
        else
        {
            await cheques.CancelWithPaymentAsync(payment, date);
        }
        await ledger.ReverseAsync(payment.Direction == PaymentDirection.IN ? LedgerDocumentType.COLLECTION : LedgerDocumentType.PAYMENT,
            payment.Id, date, $"İptal: {payment.Number}{(string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason.Trim()}")}");
        payment.Status = PaymentRecordStatus.CANCELLED;
        payment.CancelledAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<PaymentDetail> GetAsync(Guid id)
    {
        var p = Guard.Found(await db.PartnerPayments.AsNoTracking().Include(x => x.Partner).Include(x => x.Account)
            .Include(x => x.Allocations).ThenInclude(a => a.SalesInvoice).Include(x => x.Allocations).ThenInclude(a => a.PurchaseInvoice)
            .Include(x => x.OriginCheque).FirstOrDefaultAsync(x => x.Id == id), "Tahsilat veya ödeme bulunamadı");
        return new PaymentDetail(p.Id, p.Number, p.Direction, p.Method, p.PartnerId, p.Partner.Title, p.PaymentDate, p.AccountId, p.Account?.Name,
            p.Amount, p.CurrencyCode, p.ExchangeRate, p.AmountBase, p.Description, p.Status, p.OriginCheque?.Id, p.OriginCheque?.Number,
            p.Allocations.Select(a => new AllocationDto(a.SalesInvoiceId, a.SalesInvoice?.Number, a.PurchaseInvoiceId, a.PurchaseInvoice?.Number, a.Amount)).ToList());
    }

    private async Task AllocateAsync(PartnerPayment payment, List<AllocationInput> allocations)
    {
        decimal allocated = 0;
        foreach (var row in allocations)
        {
            Guard.That(row.Amount > 0, "Mahsup tutarı sıfırdan büyük olmalı");
            Guard.That((row.SalesInvoiceId != null) ^ (row.PurchaseInvoiceId != null), "Mahsup tek bir faturaya yazılır");
            if (row.SalesInvoiceId is { } salesId)
            {
                Guard.That(payment.Direction == PaymentDirection.IN, "Satış faturası yalnızca tahsilata mahsup edilir");
                await db.Database.ExecuteSqlAsync($"SELECT id FROM sales_invoices WHERE id = {salesId} FOR UPDATE");
                var invoice = Guard.Found(await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == salesId), "Satış faturası bulunamadı");
                Guard.State(invoice.Status == InvoiceStatus.ISSUED && invoice.PartnerId == payment.PartnerId, "Fatura bu cariye ait ve açık olmalı");
                Guard.That(invoice.CurrencyCode == payment.CurrencyCode, "Fatura para birimi belgenin para birimiyle aynı olmalı");
                var open = invoice.GrandTotal - invoice.PaidAmount;
                Guard.That(row.Amount <= open + 0.004m, $"{invoice.Number} açık tutarı {open:N2}");
                InvoicePayments.Apply(invoice, row.Amount);
                db.PaymentAllocations.Add(new PaymentAllocation { PaymentId = payment.Id, SalesInvoiceId = invoice.Id, Amount = DocumentMath.Round2(row.Amount) });
            }
            else
            {
                Guard.That(payment.Direction == PaymentDirection.OUT, "Alış faturası yalnızca ödemeye mahsup edilir");
                var purchaseId = row.PurchaseInvoiceId!.Value;
                await db.Database.ExecuteSqlAsync($"SELECT id FROM purchase_invoices WHERE id = {purchaseId} FOR UPDATE");
                var invoice = Guard.Found(await db.PurchaseInvoices.FirstOrDefaultAsync(i => i.Id == purchaseId), "Alış faturası bulunamadı");
                Guard.State(invoice.Status == InvoiceStatus.ISSUED && invoice.PartnerId == payment.PartnerId, "Fatura bu cariye ait ve açık olmalı");
                Guard.That(invoice.CurrencyCode == payment.CurrencyCode, "Fatura para birimi belgenin para birimiyle aynı olmalı");
                var open = invoice.GrandTotal - invoice.PaidAmount;
                Guard.That(row.Amount <= open + 0.004m, $"{invoice.Number} açık tutarı {open:N2}");
                InvoicePayments.Apply(invoice, row.Amount);
                db.PaymentAllocations.Add(new PaymentAllocation { PaymentId = payment.Id, PurchaseInvoiceId = invoice.Id, Amount = DocumentMath.Round2(row.Amount) });
            }
            allocated += row.Amount;
        }
        Guard.That(allocated <= payment.Amount + 0.004m, "Mahsuplar belge tutarını aşıyor");
        await db.SaveChangesAsync();
    }
}
