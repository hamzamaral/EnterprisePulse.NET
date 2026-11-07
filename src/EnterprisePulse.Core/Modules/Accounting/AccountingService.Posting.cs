using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Accounting;

/// <summary>
/// Muhasebeleştirme: turns source documents into journal entries. Idempotent — each run compares the documents with their open
/// entries and posts what is missing, reverses what was cancelled and re-posts recalculated payrolls. Documents dated in a
/// locked period are left pending.
/// </summary>
public sealed partial class AccountingService
{
    /// <summary>A source document and whether it should currently have a posted entry.</summary>
    private sealed record Candidate(
        JournalSource Source, Guid SourceId, string SourceNumber, string Description, DateOnly Date, decimal Amount,
        bool Wanted, DateOnly ReverseDate, Guid OwnerId, Func<Task<List<DraftLine>?>> Lines, bool Mutable = false);

    private sealed record Planned(Candidate Candidate, string Action, Guid? OpenEntryId, bool Locked);

    private sealed record MovementCost(decimal Increase, decimal Decrease);

    private static readonly Dictionary<TreasuryDocumentType, JournalSource> TreasurySources = new()
    {
        [TreasuryDocumentType.OPENING] = JournalSource.TREASURY_OPENING,
        [TreasuryDocumentType.CHEQUE_COLLECTION] = JournalSource.CHEQUE,
        [TreasuryDocumentType.CHEQUE_PAYMENT] = JournalSource.CHEQUE,
        [TreasuryDocumentType.EXPENSE_PAYMENT] = JournalSource.EXPENSE_PAYMENT,
        [TreasuryDocumentType.PAYROLL_PAYMENT] = JournalSource.PAYROLL_PAYMENT,
    };

    private static string Key(JournalSource source, Guid sourceId) => $"{source}:{sourceId}";

    /// <summary>Scheduled run (nightly): every company posts what its documents require.</summary>
    public async Task RunAsync()
    {
        using var bypass = Ctx.WithoutTenant();
        foreach (var companyId in await db.Companies.Select(c => c.Id).ToListAsync())
        {
            using var _ = Ctx.ActAs(companyId, superAdmin: true);
            db.ChangeTracker.Clear();
            try
            {
                await RunCompanyAsync();
            }
            catch (Exception)
            {
                // One company's failure must not stop the others; the next run retries.
            }
        }
    }

    /// <summary>Documents waiting to be posted or reversed.</summary>
    public async Task<List<PendingPostingDto>> PendingAsync()
    {
        var plan = await PlanAsync(Ctx.RequireCompanyId());
        return plan.Select(p => new PendingPostingDto(p.Candidate.Source, p.Candidate.SourceId, p.Candidate.SourceNumber, p.Candidate.Description,
                p.Action == "REVERSE" ? p.Candidate.ReverseDate : p.Candidate.Date, p.Candidate.Amount, p.Action, p.Locked))
            .OrderBy(p => p.Date).ThenBy(p => p.SourceNumber, StringComparer.Ordinal).ToList();
    }

    public async Task<PostingRunResultDto> RunCompanyAsync()
    {
        var companyId = Ctx.RequireCompanyId();
        await InstallChartAsync(companyId);
        var plan = await PlanAsync(companyId);
        int posted = 0, reversed = 0, skipped = 0;
        foreach (var item in plan)
        {
            if (item.Locked)
            {
                skipped++;
                continue;
            }
            var outcome = await ApplyAsync(companyId, item);
            if (outcome == null) skipped++;
            else
            {
                posted += outcome.Value.Posted;
                reversed += outcome.Value.Reversed;
            }
        }
        return new PostingRunResultDto(posted, reversed, skipped);
    }

    private async Task<List<Planned>> PlanAsync(Guid companyId)
    {
        var candidates = await CollectAsync(companyId);
        var entries = await db.JournalEntries.AsNoTracking().Where(e => e.Status == JournalEntryStatus.POSTED && e.Source != JournalSource.MANUAL)
            .Select(e => new { e.Id, e.Source, e.SourceId, e.ReversesId }).ToListAsync();
        var until = await LockedUntilAsync(companyId);
        var reversed = entries.Where(e => e.ReversesId != null).Select(e => e.ReversesId!.Value).ToHashSet();
        var open = new Dictionary<string, Guid>();
        foreach (var e in entries)
        {
            if (e.SourceId == null || e.ReversesId != null || reversed.Contains(e.Id)) continue;
            open[Key(e.Source, e.SourceId.Value)] = e.Id;
        }

        var plan = new List<Planned>();
        foreach (var candidate in candidates)
        {
            Guid? openId = open.TryGetValue(Key(candidate.Source, candidate.SourceId), out var id) ? id : null;
            string? action = null;
            if (candidate.Wanted && openId == null) action = "POST";
            else if (!candidate.Wanted && openId != null) action = "REVERSE";
            else if (candidate.Wanted && openId != null && candidate.Mutable && await ChangedAsync(openId.Value, candidate)) action = "REPOST";
            if (action == null) continue;
            var locked = (action != "POST" && IsLocked(candidate.ReverseDate, until)) || (action != "REVERSE" && IsLocked(candidate.Date, until));
            plan.Add(new Planned(candidate, action, openId, locked));
        }
        return plan;
    }

    private async Task<bool> ChangedAsync(Guid entryId, Candidate candidate)
    {
        var draft = await candidate.Lines();
        if (draft == null) return false;
        var posted = await db.JournalLines.AsNoTracking().Where(l => l.EntryId == entryId).Select(l => new PostingLine(l.Account.Code, l.Debit, l.Credit, l.PartnerId, l.EmployeeId, l.Description)).ToListAsync();
        return AccountingMath.Fingerprint(posted) != AccountingMath.Fingerprint(AccountingMath.NormalizeLines(draft));
    }

    private async Task<(int Posted, int Reversed)?> ApplyAsync(Guid companyId, Planned item)
    {
        var candidate = item.Candidate;
        var lines = item.Action == "REVERSE" ? [] : await candidate.Lines();
        if (item.Action != "REVERSE" && (lines == null || AccountingMath.NormalizeLines(lines).Count == 0)) return null;

        await using var tx = await db.Database.BeginTransactionAsync();
        await LockAccountingAsync(companyId);
        // Another run may have handled the document since the plan was made.
        var current = await db.JournalEntries.AsNoTracking()
            .Where(e => e.Source == candidate.Source && e.SourceId == candidate.SourceId && e.Status == JournalEntryStatus.POSTED)
            .Select(e => new { e.Id, e.ReversesId }).ToListAsync();
        var reversedIds = current.Where(e => e.ReversesId != null).Select(e => e.ReversesId!.Value).ToHashSet();
        var openEntry = current.FirstOrDefault(e => e.ReversesId == null && !reversedIds.Contains(e.Id));
        if (openEntry?.Id != item.OpenEntryId) return null;

        var actor = Ctx.UserId ?? candidate.OwnerId;
        int posted = 0, reversed = 0;
        if (openEntry != null)
        {
            await ReverseEntryAsync(companyId, actor, openEntry.Id, item.Action == "REPOST" ? AccountingClock.Today : candidate.ReverseDate);
            reversed++;
        }
        if (item.Action != "REVERSE")
        {
            await WriteEntryAsync(companyId, actor, candidate.Date, candidate.Source, candidate.SourceId, candidate.SourceNumber, candidate.Description, lines!);
            posted++;
        }
        await tx.CommitAsync();
        return (posted, reversed);
    }

    // ---------- Collectors ----------

    private async Task<List<Candidate>> CollectAsync(Guid companyId)
    {
        var baseCurrency = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.BaseCurrency).FirstAsync();
        var costs = await MovementCostsAsync();
        var all = new List<Candidate>();
        all.AddRange(await SalesInvoicesAsync());
        all.AddRange(await SalesReturnsAsync(costs));
        all.AddRange(await PurchaseInvoicesAsync());
        all.AddRange(await DeliveriesAsync(costs));
        all.AddRange(await StockMovementsAsync(costs));
        all.AddRange(await PaymentsAsync());
        all.AddRange(await TreasuryAsync(baseCurrency));
        all.AddRange(await PartnerOpeningsAsync());
        all.AddRange(await ExpenseReportsAsync());
        all.AddRange(await PayrollsAsync());
        all.AddRange(await ProductionAsync());
        return all;
    }

    private static Task<List<DraftLine>?> Lines(List<DraftLine> lines) => Task.FromResult<List<DraftLine>?>(lines);

    private async Task<List<Candidate>> SalesInvoicesAsync()
    {
        var rows = await db.SalesInvoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.ISSUED || i.Status == InvoiceStatus.CANCELLED)
            .Select(i => new { i.Id, i.Number, i.PartnerId, Title = i.Partner.Title, i.InvoiceDate, i.Status, i.CancelledAt, i.CreatedById, i.GrandTotal, i.VatTotal, i.ExchangeRate }).ToListAsync();
        return rows.Select(r =>
        {
            var total = AccountingMath.ToBase(r.GrandTotal, r.ExchangeRate);
            var vat = AccountingMath.ToBase(r.VatTotal, r.ExchangeRate);
            return new Candidate(JournalSource.SALES_INVOICE, r.Id, r.Number, $"Satış faturası — {r.Title}", r.InvoiceDate, total, r.Status == InvoiceStatus.ISSUED,
                AccountingClock.LocalDate(r.CancelledAt ?? DateTime.UtcNow), r.CreatedById, () => Lines(PostingRules.SalesInvoice(r.PartnerId, total, vat)));
        }).ToList();
    }

    private async Task<List<Candidate>> SalesReturnsAsync(Dictionary<Guid, MovementCost> costs)
    {
        var rows = await db.SalesReturns.AsNoTracking()
            .Select(r => new { r.Id, r.Number, r.PartnerId, Title = r.Partner.Title, r.ReturnDate, r.CreatedById, r.GrandTotal, r.VatTotal, r.ExchangeRate, r.StockMovementId }).ToListAsync();
        return rows.Select(r =>
        {
            var total = AccountingMath.ToBase(r.GrandTotal, r.ExchangeRate);
            var vat = AccountingMath.ToBase(r.VatTotal, r.ExchangeRate);
            var cost = r.StockMovementId is { } m && costs.TryGetValue(m, out var c) ? c.Increase : 0m;
            return new Candidate(JournalSource.SALES_RETURN, r.Id, r.Number, $"Satış iadesi — {r.Title}", r.ReturnDate, total, true, r.ReturnDate, r.CreatedById,
                () => Lines(PostingRules.SalesReturn(r.PartnerId, total, vat, cost)));
        }).ToList();
    }

    private async Task<List<Candidate>> PurchaseInvoicesAsync()
    {
        var rows = await db.PurchaseInvoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.ISSUED || i.Status == InvoiceStatus.CANCELLED)
            .Select(i => new
            {
                i.Id, i.Number, i.SupplierInvoiceNo, i.PartnerId, Title = i.Partner.Title, i.InvoiceDate, i.Status, i.CancelledAt, i.CreatedById, i.GrandTotal, i.VatTotal, i.ExchangeRate,
                Lines = i.Lines.Select(l => new { l.Quantity, l.UnitPrice, l.DiscountRate, l.VatRate, IsGoods = l.Variant != null && l.Variant.Product.Type == ProductType.GOODS }).ToList(),
            }).ToListAsync();
        return rows.Select(r =>
        {
            var total = AccountingMath.ToBase(r.GrandTotal, r.ExchangeRate);
            var vat = AccountingMath.ToBase(r.VatTotal, r.ExchangeRate);
            var goods = AccountingMath.ToBase(r.Lines.Where(l => l.IsGoods).Sum(l => DocumentMath.Line(new LineInput(l.Quantity, l.UnitPrice, l.DiscountRate, l.VatRate)).Net), r.ExchangeRate);
            return new Candidate(JournalSource.PURCHASE_INVOICE, r.Id, r.Number, $"Alış faturası {r.SupplierInvoiceNo} — {r.Title}", r.InvoiceDate, total,
                r.Status == InvoiceStatus.ISSUED, AccountingClock.LocalDate(r.CancelledAt ?? DateTime.UtcNow), r.CreatedById, () => Lines(PostingRules.PurchaseInvoice(r.PartnerId, total, vat, goods)));
        }).ToList();
    }

    private async Task<List<Candidate>> DeliveriesAsync(Dictionary<Guid, MovementCost> costs)
    {
        var rows = await db.DeliveryNotes.AsNoTracking().Where(d => d.StockMovementId != null)
            .Select(d => new { d.Id, d.Number, Title = d.Partner.Title, d.DeliveryDate, d.CreatedById, d.StockMovementId }).ToListAsync();
        return rows.Select(r =>
        {
            var cost = costs.TryGetValue(r.StockMovementId!.Value, out var c) ? c.Decrease : 0m;
            return new Candidate(JournalSource.DELIVERY_NOTE, r.Id, r.Number, $"Satılan malın maliyeti — {r.Title}", r.DeliveryDate, cost, true, r.DeliveryDate, r.CreatedById,
                () => Lines(PostingRules.DeliveryCost(cost)));
        }).ToList();
    }

    /// <summary>Receipts, issues and adjustments that no purchasing, sales or production document covers.</summary>
    private async Task<List<Candidate>> StockMovementsAsync(Dictionary<Guid, MovementCost> costs)
    {
        var rows = await db.StockMovements.AsNoTracking()
            .Where(m => m.Type != StockMovementType.TRANSFER && m.GoodsReceipt == null && m.DeliveryNote == null && m.SalesReturn == null
                && m.ProductionIssue == null && m.ProductionReceipt == null)
            .Select(m => new { m.Id, m.Number, m.Type, m.Reference, m.MovementDate, m.CreatedById }).ToListAsync();
        return rows.Select(r =>
        {
            var value = costs.GetValueOrDefault(r.Id) ?? new MovementCost(0, 0);
            var label = r.Type switch
            {
                StockMovementType.RECEIPT => "Faturasız stok girişi",
                StockMovementType.ISSUE => "Stok çıkışı (sarf)",
                StockMovementType.ADJUSTMENT => "Stok sayım farkı",
                _ => "Depo transferi",
            };
            return new Candidate(JournalSource.STOCK_MOVEMENT, r.Id, r.Number, string.IsNullOrWhiteSpace(r.Reference) ? label : $"{label} — {r.Reference}", r.MovementDate,
                value.Increase + value.Decrease, true, r.MovementDate, r.CreatedById, () => Lines(PostingRules.StockMovement(r.Type, value.Increase, value.Decrease)));
        }).ToList();
    }

    private async Task<List<Candidate>> PaymentsAsync()
    {
        var rows = await db.PartnerPayments.AsNoTracking()
            .Select(p => new
            {
                p.Id, p.Number, p.Direction, p.Method, p.PartnerId, Title = p.Partner.Title, p.PaymentDate, p.AmountBase, p.Status, p.CancelledAt, p.CreatedById,
                Endorsed = p.EndorsedCheque != null,
            }).ToListAsync();
        return rows.Select(r =>
        {
            var incoming = r.Direction == PaymentDirection.IN;
            return new Candidate(incoming ? JournalSource.COLLECTION : JournalSource.PAYMENT, r.Id, r.Number, $"{(incoming ? "Tahsilat" : "Ödeme")} — {r.Title}", r.PaymentDate,
                r.AmountBase, r.Status == PaymentRecordStatus.POSTED, AccountingClock.LocalDate(r.CancelledAt ?? DateTime.UtcNow), r.CreatedById,
                () => Lines(incoming ? PostingRules.Collection(r.PartnerId, r.Method, r.AmountBase) : PostingRules.Payment(r.PartnerId, r.Method, r.AmountBase, r.Endorsed)));
        }).ToList();
    }

    /// <summary>Treasury postings without a partner document; a reversing transaction cancels them.</summary>
    private async Task<List<Candidate>> TreasuryAsync(string baseCurrency)
    {
        var types = TreasurySources.Keys.ToList();
        var rows = await db.TreasuryTransactions.AsNoTracking().Where(t => t.ReversesId == null && types.Contains(t.DocumentType))
            .Select(t => new
            {
                t.Id, t.DocumentType, t.DocumentId, t.DocumentNumber, t.TxDate, t.AmountIn, t.AmountOut, t.CreatedById,
                AccountType = t.Account.Type, t.Account.CurrencyCode, AccountName = t.Account.Name, ReversalDate = t.Reversal == null ? (DateOnly?)null : t.Reversal.TxDate,
            }).ToListAsync();
        var chequeIds = rows.Where(r => r.DocumentType is TreasuryDocumentType.CHEQUE_COLLECTION or TreasuryDocumentType.CHEQUE_PAYMENT && r.DocumentId != null).Select(r => r.DocumentId!.Value).ToList();
        var reportIds = rows.Where(r => r.DocumentType == TreasuryDocumentType.EXPENSE_PAYMENT && r.DocumentId != null).Select(r => r.DocumentId!.Value).ToList();
        var cheques = await db.Cheques.AsNoTracking().Where(c => chequeIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Kind, c.PartnerId, c.ExchangeRate, Title = c.Partner.Title }).ToDictionaryAsync(c => c.Id);
        var reports = await db.ExpenseReports.AsNoTracking().Where(r => reportIds.Contains(r.Id))
            .Select(r => new { r.Id, r.EmployeeId, Name = r.Employee.FirstName + " " + r.Employee.LastName }).ToDictionaryAsync(r => r.Id);

        return rows.Select(r =>
        {
            var signed = r.AmountIn - r.AmountOut;
            var cheque = r.DocumentId is { } cid && cheques.TryGetValue(cid, out var c) ? c : null;
            var report = r.DocumentId is { } rid && reports.TryGetValue(rid, out var rp) ? rp : null;
            var needsCheque = r.DocumentType is TreasuryDocumentType.CHEQUE_COLLECTION or TreasuryDocumentType.CHEQUE_PAYMENT;

            async Task<decimal?> BaseAsync()
            {
                if (r.CurrencyCode == baseCurrency) return signed;
                var rate = cheque?.ExchangeRate ?? await RateOnAsync(r.CurrencyCode, r.TxDate);
                return rate == null ? null : AccountingMath.ToBase(signed, rate.Value);
            }

            var kindLabel = cheque?.Kind == ChequeKind.NOTE ? "Senet" : "Çek";
            var description = r.DocumentType switch
            {
                TreasuryDocumentType.OPENING => $"Açılış bakiyesi — {r.AccountName}",
                TreasuryDocumentType.CHEQUE_COLLECTION => $"{kindLabel} tahsili — {cheque?.Title}",
                TreasuryDocumentType.CHEQUE_PAYMENT => $"{kindLabel} ödemesi — {cheque?.Title}",
                TreasuryDocumentType.EXPENSE_PAYMENT => $"Masraf ödemesi — {report?.Name}",
                _ => "Net maaş ödemesi",
            };
            List<DraftLine> Build(decimal amount) => r.DocumentType switch
            {
                TreasuryDocumentType.OPENING => PostingRules.TreasuryOpening(r.AccountType, amount),
                TreasuryDocumentType.CHEQUE_COLLECTION => PostingRules.ChequeCollection(cheque!.Kind, r.AccountType, amount, cheque.PartnerId),
                TreasuryDocumentType.CHEQUE_PAYMENT => PostingRules.ChequePayment(cheque!.Kind, r.AccountType, -amount, cheque.PartnerId),
                TreasuryDocumentType.EXPENSE_PAYMENT => PostingRules.EmployeePayment(r.AccountType, -amount, report?.EmployeeId),
                _ => PostingRules.EmployeePayment(r.AccountType, -amount, null),
            };
            return new Candidate(TreasurySources[r.DocumentType], r.Id, r.DocumentNumber, description.TrimEnd(' ', '—'), r.TxDate, Math.Abs(signed), r.ReversalDate == null,
                r.ReversalDate ?? AccountingClock.Today, r.CreatedById, async () =>
                {
                    if (needsCheque && cheque == null) return null;
                    var amount = await BaseAsync();
                    return amount == null ? null : Build(amount.Value);
                });
        }).ToList();
    }

    private async Task<List<Candidate>> PartnerOpeningsAsync()
    {
        var rows = await db.PartnerLedgerEntries.AsNoTracking().Where(e => e.DocumentType == LedgerDocumentType.OPENING && e.ReversesId == null)
            .Select(e => new { e.Id, e.PartnerId, Title = e.Partner.Title, e.DocumentNumber, e.EntryDate, e.DebitBase, e.CreditBase, e.CreatedById, ReversalDate = e.Reversal == null ? (DateOnly?)null : e.Reversal.EntryDate })
            .ToListAsync();
        return rows.Select(r => new Candidate(JournalSource.PARTNER_OPENING, r.Id, r.DocumentNumber, $"Cari açılış bakiyesi — {r.Title}", r.EntryDate, r.DebitBase + r.CreditBase,
            r.ReversalDate == null, r.ReversalDate ?? AccountingClock.Today, r.CreatedById, () => Lines(PostingRules.PartnerOpening(r.PartnerId, r.DebitBase, r.CreditBase)))).ToList();
    }

    private async Task<List<Candidate>> ExpenseReportsAsync()
    {
        var rows = await db.ExpenseReports.AsNoTracking().Where(r => r.DecidedAt != null)
            .Select(r => new
            {
                r.Id, r.Number, r.EmployeeId, Name = r.Employee.FirstName + " " + r.Employee.LastName, r.DecidedAt, r.TotalAmount, r.Status, r.UpdatedAt, r.CreatedById,
                Vats = r.Lines.Select(l => new { l.VatAmount, l.ExchangeRate }).ToList(),
            }).ToListAsync();
        return rows.Select(r =>
        {
            var vat = r.Vats.Sum(l => AccountingMath.ToBase(l.VatAmount, l.ExchangeRate));
            return new Candidate(JournalSource.EXPENSE_REPORT, r.Id, r.Number, $"Masraf formu — {r.Name}", AccountingClock.LocalDate(r.DecidedAt!.Value), r.TotalAmount,
                r.Status is ExpenseReportStatus.APPROVED or ExpenseReportStatus.PAID, AccountingClock.LocalDate(r.UpdatedAt), r.CreatedById,
                () => Lines(PostingRules.ExpenseReport(r.EmployeeId, r.TotalAmount, vat)));
        }).ToList();
    }

    /// <summary>Accrued at the end of the month (or on approval when the month is not over yet).</summary>
    private async Task<List<Candidate>> PayrollsAsync()
    {
        var rows = await db.PayrollPeriods.AsNoTracking().Where(p => !p.Imported).ToListAsync();
        var today = AccountingClock.Today;
        return rows.Select(r =>
        {
            var monthEnd = new DateOnly(r.Year, r.Month, DateTime.DaysInMonth(r.Year, r.Month));
            var approved = r.ApprovedAt != null ? AccountingClock.LocalDate(r.ApprovedAt.Value) : today;
            return new Candidate(JournalSource.PAYROLL, r.Id, r.Number, $"{r.Month:00}/{r.Year} bordro tahakkuku", monthEnd > today ? approved : monthEnd, r.EmployerCostTotal,
                r.Status is PayrollPeriodStatus.APPROVED or PayrollPeriodStatus.PAID, AccountingClock.LocalDate(r.UpdatedAt), r.ApprovedById ?? r.CreatedById,
                () => Lines(PostingRules.Payroll(r.NetTotal, r.TaxTotal, r.SgkTotal, r.DeductionTotal)), Mutable: true);
        }).ToList();
    }

    /// <summary>Production records at the costs stored when the output was received.</summary>
    private async Task<List<Candidate>> ProductionAsync()
    {
        var rows = await db.ProductionRecords.AsNoTracking()
            .Select(r => new { r.Id, r.Number, WorkOrderNumber = r.WorkOrder.Number, r.RecordDate, r.MaterialCost, r.LaborCost, r.OverheadCost, r.CreatedById }).ToListAsync();
        return rows.Select(r => new Candidate(JournalSource.PRODUCTION, r.Id, r.Number, $"Üretim — iş emri {r.WorkOrderNumber}", r.RecordDate, r.MaterialCost + r.LaborCost + r.OverheadCost,
            true, r.RecordDate, r.CreatedById, () => Lines(PostingRules.Production(r.MaterialCost, r.LaborCost, r.OverheadCost)))).ToList();
    }

    // ---------- Lookups ----------

    /// <summary>Stock value moved in and out by each movement, at the recorded unit cost.</summary>
    private async Task<Dictionary<Guid, MovementCost>> MovementCostsAsync()
    {
        var rows = await db.StockTransactions.AsNoTracking().GroupBy(t => t.MovementId).Select(g => new
        {
            g.Key,
            Increase = g.Sum(t => t.Quantity > 0 ? t.Quantity * t.UnitCost : 0m),
            Decrease = g.Sum(t => t.Quantity < 0 ? -t.Quantity * t.UnitCost : 0m),
        }).ToListAsync();
        return rows.ToDictionary(r => r.Key, r => new MovementCost(DocumentMath.Round2(r.Increase), DocumentMath.Round2(r.Decrease)));
    }

    /// <summary>Selling rate of the date (as documents use), else the closest later one.</summary>
    private async Task<decimal?> RateOnAsync(string currencyCode, DateOnly date)
    {
        var rate = await db.ExchangeRates.AsNoTracking().Where(r => r.CurrencyCode == currencyCode && r.RateDate <= date).OrderByDescending(r => r.RateDate).FirstOrDefaultAsync()
            ?? await db.ExchangeRates.AsNoTracking().Where(r => r.CurrencyCode == currencyCode && r.RateDate > date).OrderBy(r => r.RateDate).FirstOrDefaultAsync();
        return rate?.Selling;
    }
}
