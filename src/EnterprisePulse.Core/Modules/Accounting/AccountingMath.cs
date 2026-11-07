using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Trade;

namespace EnterprisePulse.Core.Modules.Accounting;

public sealed record DraftLine(string Code, decimal Debit = 0, decimal Credit = 0, Guid? PartnerId = null, Guid? EmployeeId = null, string? Description = null);

public sealed record PostingLine(string Code, decimal Debit, decimal Credit, Guid? PartnerId, Guid? EmployeeId, string? Description);

public sealed record AccountRef(string Code, string Name);

/// <summary>Posted totals of one account: before the period and inside it.</summary>
public sealed record AccountMovement(string Code, string Name, decimal OpeningDebit, decimal OpeningCredit, decimal PeriodDebit, decimal PeriodCredit);

public sealed record TrialBalanceTotals(decimal PeriodDebit, decimal PeriodCredit, decimal TotalDebit, decimal TotalCredit, decimal BalanceDebit, decimal BalanceCredit);

public sealed record TrialBalanceRow(
    string Code, string Name, int Level, decimal PeriodDebit, decimal PeriodCredit, decimal TotalDebit, decimal TotalCredit, decimal BalanceDebit, decimal BalanceCredit);

public sealed record AmountRow(string Code, string Name, decimal Amount);

public sealed record StatementGroup(string Code, string Name, decimal Total, List<AmountRow> Accounts);

public sealed record StatementClass(string Code, string Name, decimal Total, List<StatementGroup> Groups);

public sealed record BalanceSheet(List<StatementClass> Assets, List<StatementClass> Liabilities, decimal TotalAssets, decimal TotalLiabilities);

public sealed record IncomeSection(string Key, string Name, decimal Total, List<AmountRow> Accounts);

public sealed record IncomeStatement(
    List<IncomeSection> Sections, decimal NetSales, decimal GrossProfit, decimal OperatingProfit, decimal OrdinaryProfit, decimal ProfitBeforeTax, decimal NetProfit);

/// <summary>Journal arithmetic and the statements derived from posted lines (mizan, bilanço, gelir tablosu).</summary>
public static class AccountingMath
{
    /// <summary>Document amount in the base currency, rounded half up to 2 decimals.</summary>
    public static decimal ToBase(decimal amount, decimal rate) => DocumentMath.ToBase(amount, rate);

    /// <summary>
    /// Drops zero lines, moves negative amounts to the other side and merges lines with the same account, partner, employee,
    /// description and side. Debit lines come first (fiş düzeni).
    /// </summary>
    public static List<PostingLine> NormalizeLines(IEnumerable<DraftLine> lines)
    {
        var merged = new Dictionary<string, PostingLine>();
        var order = new List<string>();
        foreach (var line in lines)
        {
            var net = line.Debit - line.Credit;
            if (net == 0) continue;
            var debit = net > 0;
            var description = string.IsNullOrWhiteSpace(line.Description) ? null : line.Description.Trim();
            var key = string.Join('|', debit ? "D" : "C", line.Code, line.PartnerId, line.EmployeeId, description);
            if (!merged.TryGetValue(key, out var current))
            {
                current = new PostingLine(line.Code, 0, 0, line.PartnerId, line.EmployeeId, description);
                order.Add(key);
            }
            merged[key] = debit ? current with { Debit = current.Debit + net } : current with { Credit = current.Credit - net };
        }
        var rows = order.Select(k => merged[k]).ToList();
        return [.. rows.Where(l => l.Debit > 0), .. rows.Where(l => l.Credit > 0)];
    }

    public static (decimal Debit, decimal Credit) LineTotals(IEnumerable<(decimal Debit, decimal Credit)> lines) =>
        lines.Aggregate((Debit: 0m, Credit: 0m), (sum, l) => (sum.Debit + l.Debit, sum.Credit + l.Credit));

    /// <summary>Borç = alacak and the entry is not empty.</summary>
    public static bool IsBalanced(IEnumerable<(decimal Debit, decimal Credit)> lines)
    {
        var (debit, credit) = LineTotals(lines);
        return debit > 0 && debit == credit;
    }

    /// <summary>Order-independent identity of the lines; a changed source document gets a new fingerprint.</summary>
    public static string Fingerprint(IEnumerable<PostingLine> lines) =>
        string.Join(';', lines
            .Select(l => string.Join('|', l.Code, l.Debit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                l.Credit.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), l.PartnerId?.ToString() ?? "", l.EmployeeId?.ToString() ?? ""))
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>"100" &lt; "100.01" &lt; "100.02" &lt; "101".</summary>
    public static int CompareCodes(string a, string b)
    {
        var pa = a.Split('.').Select(int.Parse).ToArray();
        var pb = b.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            if (i >= pa.Length) return -1;
            if (i >= pb.Length) return 1;
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        }
        return 0;
    }

    public static bool Covers(string parent, string code) => code == parent || code.StartsWith(parent + ".", StringComparison.Ordinal);

    // ---------- Trial balance (mizan) ----------

    /// <summary>
    /// Mizan rows: each account totals its own and its sub-accounts' movements; accounts without any movement are left out.
    /// <c>Totals</c> adds up the main accounts only.
    /// </summary>
    public static (List<TrialBalanceRow> Rows, TrialBalanceTotals Totals) TrialBalance(IEnumerable<AccountRef> accounts, List<AccountMovement> movements, bool detailed)
    {
        var names = accounts.GroupBy(a => a.Code).ToDictionary(g => g.Key, g => g.First().Name);
        var codes = new HashSet<string>();
        foreach (var m in movements)
        {
            var parts = m.Code.Split('.');
            for (var i = 1; i <= parts.Length; i++) codes.Add(string.Join('.', parts.Take(i)));
        }
        var rows = new List<TrialBalanceRow>();
        foreach (var code in codes.OrderBy(c => c, Comparer<string>.Create(CompareCodes)))
        {
            var level = code.Split('.').Length - 1;
            if (!detailed && level > 0) continue;
            var own = movements.Where(m => Covers(code, m.Code)).ToList();
            var periodDebit = own.Sum(m => m.PeriodDebit);
            var periodCredit = own.Sum(m => m.PeriodCredit);
            var totalDebit = periodDebit + own.Sum(m => m.OpeningDebit);
            var totalCredit = periodCredit + own.Sum(m => m.OpeningCredit);
            if (totalDebit == 0 && totalCredit == 0) continue;
            var balance = totalDebit - totalCredit;
            rows.Add(new TrialBalanceRow(code, names.GetValueOrDefault(code, code), level, periodDebit, periodCredit, totalDebit, totalCredit,
                balance > 0 ? balance : 0, balance < 0 ? -balance : 0));
        }
        var mains = rows.Where(r => r.Level == 0).ToList();
        return (rows, new TrialBalanceTotals(mains.Sum(r => r.PeriodDebit), mains.Sum(r => r.PeriodCredit), mains.Sum(r => r.TotalDebit),
            mains.Sum(r => r.TotalCredit), mains.Sum(r => r.BalanceDebit), mains.Sum(r => r.BalanceCredit)));
    }

    // ---------- Balance sheet (bilanço) ----------

    /// <summary>
    /// Bilanço from main-account balances (debit − credit) up to the date. Classes 6 and 7 are not closed, so their result shows as
    /// the period's net profit and, for earlier fiscal years, as retained earnings.
    /// </summary>
    public static BalanceSheet BalanceSheetOf(IEnumerable<AmountRow> balances, decimal currentResult, decimal priorResult)
    {
        var rows = balances.Where(b => b.Code[0] <= '5' && b.Amount != 0)
            .Select(b => new AmountRow(AccountingChart.MainCode(b.Code), b.Name, b.Code[0] <= '2' ? b.Amount : -b.Amount)).ToList();
        void AddResult(string code, string name, decimal amount)
        {
            if (amount == 0) return;
            var index = rows.FindIndex(r => r.Code == code);
            if (index >= 0) rows[index] = rows[index] with { Amount = rows[index].Amount + amount };
            else rows.Add(new AmountRow(code, name, amount));
        }
        AddResult("570", "Geçmiş Yıllar Kârları (Zararları)", priorResult);
        AddResult("590", "Dönem Net Kârı (Zararı)", currentResult);

        List<StatementClass> Classes(params char[] digits) => digits.Select(cls =>
        {
            var inClass = rows.Where(r => r.Code[0] == cls).OrderBy(r => r.Code, Comparer<string>.Create(CompareCodes)).ToList();
            var groups = inClass.Select(r => r.Code[..2]).Distinct().Select(code =>
            {
                var accounts = inClass.Where(r => r.Code.StartsWith(code, StringComparison.Ordinal)).ToList();
                return new StatementGroup(code, AccountingChart.Groups.GetValueOrDefault(code, code), accounts.Sum(a => a.Amount), accounts);
            }).ToList();
            return new StatementClass(cls.ToString(), AccountingChart.BalanceClasses[cls.ToString()], groups.Sum(g => g.Total), groups);
        }).Where(c => c.Groups.Count > 0).ToList();

        var assets = Classes('1', '2');
        var liabilities = Classes('3', '4', '5');
        return new BalanceSheet(assets, liabilities, assets.Sum(c => c.Total), liabilities.Sum(c => c.Total));
    }

    // ---------- Income statement (gelir tablosu) ----------

    /// <summary>Gelir tablosu from main-account results (credit − debit) of classes 6 and 7.</summary>
    public static IncomeStatement IncomeStatementOf(IEnumerable<AmountRow> results)
    {
        var list = results.ToList();
        var sections = AccountingChart.IncomeSections.Select(def =>
        {
            var accounts = list.Where(r => AccountingChart.IncomeSection(r.Code) == def.Key && r.Amount != 0)
                .OrderBy(r => r.Code, Comparer<string>.Create(CompareCodes)).ToList();
            return new IncomeSection(def.Key, def.Label, accounts.Sum(a => a.Amount), accounts);
        }).ToList();
        decimal Total(string key) => sections.First(s => s.Key == key).Total;
        var netSales = Total("grossSales") + Total("salesDeductions");
        var grossProfit = netSales + Total("costOfSales");
        var operatingProfit = grossProfit + Total("operatingExpenses");
        var ordinaryProfit = operatingProfit + Total("otherIncome") + Total("otherExpenses") + Total("financeExpenses");
        var profitBeforeTax = ordinaryProfit + Total("extraordinaryIncome") + Total("extraordinaryExpenses");
        return new IncomeStatement(sections, netSales, grossProfit, operatingProfit, ordinaryProfit, profitBeforeTax, profitBeforeTax + Total("taxProvision"));
    }
}

/// <summary>
/// Posting rules (muhasebeleştirme kuralları). Every amount is already in the base currency; each rule derives one side from
/// the others so the entry always balances.
/// </summary>
public static class PostingRules
{
    /// <summary>100 Kasa or 102 Bankalar.</summary>
    public static string TreasuryCode(TreasuryAccountType type) => type == TreasuryAccountType.CASH ? ACC.Cash : ACC.Banks;

    /// <summary>Satış faturası: 120 Alıcılar / 600 Yurt İçi Satışlar + 391 Hesaplanan KDV.</summary>
    public static List<DraftLine> SalesInvoice(Guid partnerId, decimal total, decimal vat) =>
    [
        new(ACC.Receivables, Debit: total, PartnerId: partnerId),
        new(ACC.Sales, Credit: total - vat),
        new(ACC.OutputVat, Credit: vat),
    ];

    /// <summary>Satış iadesi: 610 Satıştan İadeler + 391 / 120, and the returned goods back into stock at cost: 153 / 621.</summary>
    public static List<DraftLine> SalesReturn(Guid partnerId, decimal total, decimal vat, decimal cost) =>
    [
        new(ACC.SalesReturns, Debit: total - vat),
        new(ACC.OutputVat, Debit: vat),
        new(ACC.Receivables, Credit: total, PartnerId: partnerId),
        new(ACC.Merchandise, Debit: cost),
        new(ACC.CostOfGoodsSold, Credit: cost),
    ];

    /// <summary>Alış faturası: goods to 153 Ticari Mallar, services and other lines to 770, VAT to 191 / 320 Satıcılar.</summary>
    public static List<DraftLine> PurchaseInvoice(Guid partnerId, decimal total, decimal vat, decimal goods)
    {
        var net = total - vat;
        var stocked = Math.Min(goods, net);
        return
        [
            new(ACC.Merchandise, Debit: stocked),
            new(ACC.GeneralAdminExpenses, Debit: net - stocked),
            new(ACC.InputVat, Debit: vat),
            new(ACC.Payables, Credit: total, PartnerId: partnerId),
        ];
    }

    /// <summary>Sevk irsaliyesi: cost of the delivered goods, 621 / 153.</summary>
    public static List<DraftLine> DeliveryCost(decimal cost) => [new(ACC.CostOfGoodsSold, Debit: cost), new(ACC.Merchandise, Credit: cost)];

    /// <summary>
    /// Stock movements outside purchasing and sales, valued at cost: receipt without invoice 153 / 329, internal issue 770 / 153,
    /// count surplus 153 / 679 and shortage 689 / 153.
    /// </summary>
    public static List<DraftLine> StockMovement(StockMovementType type, decimal increase, decimal decrease) => type switch
    {
        StockMovementType.RECEIPT => [new(ACC.Merchandise, Debit: increase), new(ACC.OtherTradePayables, Credit: increase)],
        StockMovementType.ISSUE => [new(ACC.GeneralAdminExpenses, Debit: decrease), new(ACC.Merchandise, Credit: decrease)],
        StockMovementType.ADJUSTMENT =>
        [
            new(ACC.Merchandise, Debit: increase), new(ACC.OtherExtraordinaryIncome, Credit: increase),
            new(ACC.OtherExtraordinaryExpense, Debit: decrease), new(ACC.Merchandise, Credit: decrease),
        ],
        _ => [],
    };

    /// <summary>Üretim fişi: materials used 710 / 153, and the output into stock at full cost 153 / 711 + 721 + 731 yansıtma hesapları.</summary>
    public static List<DraftLine> Production(decimal material, decimal labor, decimal overhead) =>
    [
        new(ACC.DirectMaterials, Debit: material),
        new(ACC.Merchandise, Credit: material),
        new(ACC.Merchandise, Debit: material + labor + overhead),
        new(ACC.DirectMaterialsApplied, Credit: material),
        new(ACC.DirectLaborApplied, Credit: labor),
        new(ACC.OverheadApplied, Credit: overhead),
    ];

    /// <summary>Account a payment method moves money through.</summary>
    private static string MethodCode(PaymentMethod method, bool incoming, bool endorsed) => method switch
    {
        PaymentMethod.CASH => ACC.Cash,
        PaymentMethod.BANK => ACC.Banks,
        PaymentMethod.CHEQUE => incoming || endorsed ? ACC.ChequesReceived : ACC.ChequesIssued,
        _ => incoming || endorsed ? ACC.NotesReceivable : ACC.NotesPayable,
    };

    /// <summary>Tahsilat: 100 / 102 / 101 / 121 against 120 Alıcılar.</summary>
    public static List<DraftLine> Collection(Guid partnerId, PaymentMethod method, decimal amount) =>
        [new(MethodCode(method, true, false), Debit: amount), new(ACC.Receivables, Credit: amount, PartnerId: partnerId)];

    /// <summary>Ödeme: 320 Satıcılar against 100 / 102, an own cheque or note, or an endorsed one.</summary>
    public static List<DraftLine> Payment(Guid partnerId, PaymentMethod method, decimal amount, bool endorsed) =>
        [new(ACC.Payables, Debit: amount, PartnerId: partnerId), new(MethodCode(method, false, endorsed), Credit: amount)];

    /// <summary>Received cheque / note collected: 100 / 102 against 101 Alınan Çekler or 121 Alacak Senetleri.</summary>
    public static List<DraftLine> ChequeCollection(ChequeKind kind, TreasuryAccountType treasury, decimal amount, Guid partnerId) =>
        [new(TreasuryCode(treasury), Debit: amount), new(kind == ChequeKind.CHEQUE ? ACC.ChequesReceived : ACC.NotesReceivable, Credit: amount, PartnerId: partnerId)];

    /// <summary>Own cheque / note paid at maturity: 103 Verilen Çekler or 321 Borç Senetleri against 100 / 102.</summary>
    public static List<DraftLine> ChequePayment(ChequeKind kind, TreasuryAccountType treasury, decimal amount, Guid partnerId) =>
        [new(kind == ChequeKind.CHEQUE ? ACC.ChequesIssued : ACC.NotesPayable, Debit: amount, PartnerId: partnerId), new(TreasuryCode(treasury), Credit: amount)];

    /// <summary>Opening balance of a cash box or bank account against 500 Sermaye; signed.</summary>
    public static List<DraftLine> TreasuryOpening(TreasuryAccountType treasury, decimal amount) =>
        [new(TreasuryCode(treasury), Debit: amount), new(ACC.Capital, Credit: amount)];

    /// <summary>Partner opening balance: receivable 120 / 500, payable 500 / 320.</summary>
    public static List<DraftLine> PartnerOpening(Guid partnerId, decimal debit, decimal credit) =>
    [
        new(ACC.Receivables, Debit: debit, PartnerId: partnerId),
        new(ACC.Capital, Credit: debit),
        new(ACC.Capital, Debit: credit),
        new(ACC.Payables, Credit: credit, PartnerId: partnerId),
    ];

    /// <summary>Approved expense report: 770 + 191 İndirilecek KDV / 335 Personele Borçlar.</summary>
    public static List<DraftLine> ExpenseReport(Guid employeeId, decimal total, decimal vat) =>
    [
        new(ACC.GeneralAdminExpenses, Debit: total - vat),
        new(ACC.InputVat, Debit: vat),
        new(ACC.DueToEmployees, Credit: total, EmployeeId: employeeId),
    ];

    /// <summary>Reimbursement or net salary payment: 335 / 100 or 102; signed.</summary>
    public static List<DraftLine> EmployeePayment(TreasuryAccountType treasury, decimal amount, Guid? employeeId) =>
        [new(ACC.DueToEmployees, Debit: amount, EmployeeId: employeeId), new(TreasuryCode(treasury), Credit: amount)];

    /// <summary>
    /// Approved payroll: employer cost to 770 against net salaries (335), income and stamp tax (360), employee and employer SGK and
    /// unemployment contributions (361) and other deductions (336).
    /// </summary>
    public static List<DraftLine> Payroll(decimal net, decimal tax, decimal sgk, decimal deductions) =>
    [
        new(ACC.GeneralAdminExpenses, Debit: net + tax + sgk + deductions),
        new(ACC.DueToEmployees, Credit: net),
        new(ACC.TaxesPayable, Credit: tax),
        new(ACC.SocialSecurityPayable, Credit: sgk),
        new(ACC.OtherPayables, Credit: deductions),
    ];
}
