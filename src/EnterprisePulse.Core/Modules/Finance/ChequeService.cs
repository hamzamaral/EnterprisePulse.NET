using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Finance;

public sealed record ChequeEventDto(DateOnly EventDate, ChequeStatus? FromStatus, ChequeStatus ToStatus, string? Note);

public sealed record ChequeDetail(
    Guid Id, string Number, ChequeKind Kind, ChequeDirection Direction, ChequeStatus Status, string SerialNo, string? BankName, DateOnly IssueDate,
    DateOnly DueDate, decimal Amount, string CurrencyCode, Guid PartnerId, string PartnerTitle, Guid? AccountId, string? AccountName, Guid OriginPaymentId,
    Guid? EndorsementPaymentId, List<ChequeEventDto> Events);

/// <summary>Çek ve senet portföyü. Cash moves only when a received item is collected or an issued one is paid.</summary>
[ModuleService]
public sealed class ChequeService(AppDbContext db, TreasuryService treasury)
{
    public async Task<Cheque> OpenAsync(PartnerPayment payment, ChequeKind kind, string serialNo, string? bankName, string? branchName, string? drawer, DateOnly dueDate, string number)
    {
        var received = payment.Direction == PaymentDirection.IN;
        var cheque = new Cheque
        {
            Number = number, Kind = kind, Direction = received ? ChequeDirection.RECEIVED : ChequeDirection.ISSUED,
            Status = received ? ChequeStatus.PORTFOLIO : ChequeStatus.ISSUED,
            SerialNo = serialNo.Trim(), BankName = Blank(bankName), BranchName = Blank(branchName), Drawer = Blank(drawer),
            IssueDate = payment.PaymentDate, DueDate = dueDate, Amount = payment.Amount, CurrencyCode = payment.CurrencyCode, ExchangeRate = payment.ExchangeRate,
            PartnerId = payment.PartnerId, OriginPaymentId = payment.Id,
        };
        db.Cheques.Add(cheque);
        Event(cheque, null, cheque.Status, payment.PaymentDate, received ? "Portföye alındı" : "Keşide edildi");
        await db.SaveChangesAsync();
        return cheque;
    }

    public async Task EndorseAsync(Guid chequeId, PartnerPayment payment)
    {
        var cheque = await LockAsync(chequeId);
        Guard.State(cheque.Direction == ChequeDirection.RECEIVED && cheque.Status == ChequeStatus.PORTFOLIO, "Yalnızca portföydeki çek ciro edilir");
        Guard.That(cheque.CurrencyCode == payment.CurrencyCode && cheque.Amount == payment.Amount, "Ciro tutarı çek tutarıyla aynı olmalı");
        cheque.Status = ChequeStatus.ENDORSED;
        cheque.EndorsedToId = payment.PartnerId;
        cheque.EndorsementPaymentId = payment.Id;
        Event(cheque, ChequeStatus.PORTFOLIO, ChequeStatus.ENDORSED, payment.PaymentDate, "Tedarikçiye ciro", payment.PartnerId);
        await db.SaveChangesAsync();
    }

    public async Task<ChequeDetail> DepositAsync(Guid id, Guid accountId, DateOnly date)
    {
        var cheque = await LockAsync(id);
        Guard.State(cheque.Direction == ChequeDirection.RECEIVED && cheque.Status == ChequeStatus.PORTFOLIO, "Yalnızca portföydeki çek bankaya verilir");
        var account = Guard.Found(await db.TreasuryAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.IsActive && a.Type == TreasuryAccountType.BANK), "Banka hesabı bulunamadı");
        Guard.That(account.CurrencyCode == cheque.CurrencyCode, "Banka hesabının para birimi çekle aynı olmalı");
        cheque.Status = ChequeStatus.DEPOSITED;
        cheque.AccountId = account.Id;
        Event(cheque, ChequeStatus.PORTFOLIO, ChequeStatus.DEPOSITED, date, $"{account.Name} hesabına verildi", accountId: account.Id);
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<ChequeDetail> CollectAsync(Guid id, DateOnly date)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var cheque = await LockAsync(id);
        Guard.State(cheque.Status == ChequeStatus.DEPOSITED && cheque.AccountId != null, "Tahsil için çek önce bankaya verilmiş olmalı");
        cheque.Status = ChequeStatus.COLLECTED;
        Event(cheque, ChequeStatus.DEPOSITED, ChequeStatus.COLLECTED, date, "Tahsil edildi", accountId: cheque.AccountId);
        await treasury.PostAsync(cheque.AccountId!.Value, date, TreasuryDocumentType.CHEQUE_COLLECTION, cheque.Id, cheque.Number, $"Çek tahsil {cheque.SerialNo}", cheque.Amount, 0);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task<ChequeDetail> BounceAsync(Guid id, DateOnly date, string? note)
    {
        var cheque = await LockAsync(id);
        Guard.State(cheque.Status == ChequeStatus.DEPOSITED, "Yalnızca bankadaki çek karşılıksız çıkar");
        var from = cheque.Status;
        cheque.Status = ChequeStatus.BOUNCED;
        Event(cheque, from, ChequeStatus.BOUNCED, date, string.IsNullOrWhiteSpace(note) ? "Karşılıksız" : note.Trim());
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<ChequeDetail> PayAsync(Guid id, DateOnly date)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var cheque = await LockAsync(id);
        Guard.State(cheque.Direction == ChequeDirection.ISSUED && cheque.Status == ChequeStatus.ISSUED, "Yalnızca keşide edilmiş çek ödenir");
        var accountId = cheque.AccountId ?? (await db.PartnerPayments.Where(p => p.Id == cheque.OriginPaymentId).Select(p => p.AccountId).FirstAsync());
        Guard.That(accountId != null, "Çekin banka hesabı yok");
        cheque.AccountId = accountId;
        cheque.Status = ChequeStatus.PAID;
        Event(cheque, ChequeStatus.ISSUED, ChequeStatus.PAID, date, "Ödendi", accountId: accountId);
        await treasury.PostAsync(accountId!.Value, date, TreasuryDocumentType.CHEQUE_PAYMENT, cheque.Id, cheque.Number, $"Çek ödemesi {cheque.SerialNo}", 0, cheque.Amount);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    public async Task CancelWithPaymentAsync(PartnerPayment payment, DateOnly date)
    {
        var cheque = await db.Cheques.FirstOrDefaultAsync(c => c.OriginPaymentId == payment.Id);
        if (cheque != null)
        {
            var initial = cheque.Direction == ChequeDirection.RECEIVED ? ChequeStatus.PORTFOLIO : ChequeStatus.ISSUED;
            Guard.State(cheque.Status == initial, "Çek portföyden çıktığı için tahsilat iptal edilemez");
            var from = cheque.Status;
            cheque.Status = ChequeStatus.CANCELLED;
            Event(cheque, from, ChequeStatus.CANCELLED, date, "Belge iptali");
        }
        var endorsed = await db.Cheques.FirstOrDefaultAsync(c => c.EndorsementPaymentId == payment.Id);
        if (endorsed != null)
        {
            Guard.State(endorsed.Status == ChequeStatus.ENDORSED, "Ciro edilen çek artık geri alınamaz");
            endorsed.Status = ChequeStatus.PORTFOLIO;
            endorsed.EndorsedToId = null;
            endorsed.EndorsementPaymentId = null;
            Event(endorsed, ChequeStatus.ENDORSED, ChequeStatus.PORTFOLIO, date, "Ciro iptal");
        }
        await db.SaveChangesAsync();
    }

    public async Task<ChequeDetail> GetAsync(Guid id)
    {
        var c = Guard.Found(await db.Cheques.AsNoTracking().Include(x => x.Partner).Include(x => x.Account).Include(x => x.Events)
            .FirstOrDefaultAsync(x => x.Id == id), "Çek bulunamadı");
        return new ChequeDetail(c.Id, c.Number, c.Kind, c.Direction, c.Status, c.SerialNo, c.BankName, c.IssueDate, c.DueDate, c.Amount, c.CurrencyCode,
            c.PartnerId, c.Partner.Title, c.AccountId, c.Account?.Name, c.OriginPaymentId, c.EndorsementPaymentId,
            c.Events.OrderBy(e => e.CreatedAt).Select(e => new ChequeEventDto(e.EventDate, e.FromStatus, e.ToStatus, e.Note)).ToList());
    }

    private async Task<Cheque> LockAsync(Guid id)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM cheques WHERE id = {id} FOR UPDATE");
        return Guard.Found(await db.Cheques.FirstOrDefaultAsync(c => c.Id == id), "Çek bulunamadı");
    }

    private void Event(Cheque cheque, ChequeStatus? from, ChequeStatus to, DateOnly date, string note, Guid? partnerId = null, Guid? accountId = null) =>
        db.ChequeEvents.Add(new ChequeEvent
        {
            Cheque = cheque, EventDate = date, FromStatus = from, ToStatus = to, Note = note, PartnerId = partnerId, AccountId = accountId,
            CreatedById = db.User.RequireUserId(),
        });

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
