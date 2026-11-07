using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Finance;

public sealed record TreasuryAccountDto(Guid Id, string Code, string Name, TreasuryAccountType Type, string CurrencyCode, decimal Balance, bool IsActive);

public sealed record TreasuryTxDto(
    Guid Id, DateOnly TxDate, TreasuryDocumentType DocumentType, string DocumentNumber, string? Description, decimal AmountIn, decimal AmountOut);

/// <summary>Kasa ve banka. Transactions are append-only; a correction swaps in and out and points at the original row.</summary>
[ModuleService]
public sealed class TreasuryService(AppDbContext db)
{
    public async Task<decimal> BalanceAsync(Guid accountId) =>
        await db.TreasuryTransactions.Where(t => t.AccountId == accountId).SumAsync(t => (decimal?)(t.AmountIn - t.AmountOut)) ?? 0;

    public async Task<List<TreasuryAccountDto>> AccountsAsync()
    {
        var accounts = await db.TreasuryAccounts.AsNoTracking().OrderBy(a => a.Code).ToListAsync();
        var sums = await db.TreasuryTransactions.GroupBy(t => t.AccountId)
            .Select(g => new { g.Key, Balance = g.Sum(t => t.AmountIn - t.AmountOut) }).ToDictionaryAsync(x => x.Key, x => x.Balance);
        return accounts.Select(a => new TreasuryAccountDto(a.Id, a.Code, a.Name, a.Type, a.CurrencyCode, sums.GetValueOrDefault(a.Id), a.IsActive)).ToList();
    }

    public async Task<List<TreasuryTxDto>> TransactionsAsync(Guid accountId) =>
        await db.TreasuryTransactions.AsNoTracking().Where(t => t.AccountId == accountId).OrderByDescending(t => t.TxDate).ThenByDescending(t => t.Seq)
            .Select(t => new TreasuryTxDto(t.Id, t.TxDate, t.DocumentType, t.DocumentNumber, t.Description, t.AmountIn, t.AmountOut)).ToListAsync();

    public async Task PostAsync(Guid accountId, DateOnly date, TreasuryDocumentType type, Guid? documentId, string number, string? description, decimal amountIn, decimal amountOut)
    {
        Guard.That(amountIn >= 0 && amountOut >= 0 && (amountIn > 0 ^ amountOut > 0), "Kasa hareketinin tek yönü olmalı");
        var account = Guard.Found(await db.TreasuryAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.IsActive), "Kasa veya banka hesabı bulunamadı");
        if (amountOut > 0)
        {
            var balance = await BalanceAsync(account.Id);
            Guard.That(balance + 0.004m >= amountOut, $"{account.Name} bakiyesi yetersiz ({balance:N2} {account.CurrencyCode})");
        }
        db.TreasuryTransactions.Add(new TreasuryTransaction
        {
            AccountId = account.Id, TxDate = date, DocumentType = type, DocumentId = documentId, DocumentNumber = number,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(), AmountIn = amountIn, AmountOut = amountOut,
            CreatedById = db.User.RequireUserId(),
        });
        await db.SaveChangesAsync();
    }

    public async Task ReverseAsync(TreasuryDocumentType type, Guid documentId, DateOnly date, string description)
    {
        var entries = await db.TreasuryTransactions.Where(t => t.DocumentType == type && t.DocumentId == documentId && t.ReversesId == null && t.Reversal == null).ToListAsync();
        foreach (var e in entries)
        {
            db.TreasuryTransactions.Add(new TreasuryTransaction
            {
                AccountId = e.AccountId, TxDate = date, DocumentType = type, DocumentId = documentId, DocumentNumber = e.DocumentNumber,
                Description = description, AmountIn = e.AmountOut, AmountOut = e.AmountIn, ReversesId = e.Id, CreatedById = db.User.RequireUserId(),
            });
        }
        if (entries.Count > 0) await db.SaveChangesAsync();
    }
}
