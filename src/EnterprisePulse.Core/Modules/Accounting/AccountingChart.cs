using EnterprisePulse.Core.Data;

namespace EnterprisePulse.Core.Modules.Accounting;

/// <summary>System accounts used by automatic postings.</summary>
public static class ACC
{
    public const string Cash = "100";
    public const string ChequesReceived = "101";
    public const string Banks = "102";
    public const string ChequesIssued = "103";
    public const string Receivables = "120";
    public const string NotesReceivable = "121";
    public const string Merchandise = "153";
    public const string InputVat = "191";
    public const string Payables = "320";
    public const string NotesPayable = "321";
    public const string OtherTradePayables = "329";
    public const string DueToEmployees = "335";
    public const string OtherPayables = "336";
    public const string TaxesPayable = "360";
    public const string SocialSecurityPayable = "361";
    public const string OutputVat = "391";
    public const string Capital = "500";
    public const string Sales = "600";
    public const string SalesReturns = "610";
    public const string CostOfGoodsSold = "621";
    public const string OtherExtraordinaryIncome = "679";
    public const string OtherExtraordinaryExpense = "689";
    public const string DirectMaterials = "710";
    public const string DirectMaterialsApplied = "711";
    public const string DirectLaborApplied = "721";
    public const string OverheadApplied = "731";
    public const string GeneralAdminExpenses = "770";

    public static readonly HashSet<string> SystemCodes =
    [
        Cash, ChequesReceived, Banks, ChequesIssued, Receivables, NotesReceivable, Merchandise, InputVat, Payables, NotesPayable,
        OtherTradePayables, DueToEmployees, OtherPayables, TaxesPayable, SocialSecurityPayable, OutputVat, Capital, Sales, SalesReturns,
        CostOfGoodsSold, OtherExtraordinaryIncome, OtherExtraordinaryExpense, DirectMaterials, DirectMaterialsApplied, DirectLaborApplied,
        OverheadApplied, GeneralAdminExpenses,
    ];
}

public sealed record DefaultAccount(string Code, string Name, LedgerAccountType Type, bool IsSystem);

public sealed record IncomeSectionDef(string Key, string Label, string[] Prefixes);

/// <summary>Tekdüzen Hesap Planı: the standard chart, account classes and statement layouts.</summary>
public static class AccountingChart
{
    private static readonly (string Code, string Name)[] Accounts =
    [
        ("100", "Kasa"), ("101", "Alınan Çekler"), ("102", "Bankalar"), ("103", "Verilen Çekler ve Ödeme Emirleri (-)"), ("108", "Diğer Hazır Değerler"),
        ("120", "Alıcılar"), ("121", "Alacak Senetleri"), ("126", "Verilen Depozito ve Teminatlar"), ("127", "Diğer Ticari Alacaklar"),
        ("128", "Şüpheli Ticari Alacaklar"), ("135", "Personelden Alacaklar"), ("136", "Diğer Çeşitli Alacaklar"), ("150", "İlk Madde ve Malzeme"),
        ("153", "Ticari Mallar"), ("159", "Verilen Sipariş Avansları"), ("180", "Gelecek Aylara Ait Giderler"), ("190", "Devreden KDV"),
        ("191", "İndirilecek KDV"), ("193", "Peşin Ödenen Vergiler ve Fonlar"), ("195", "İş Avansları"), ("196", "Personel Avansları"),
        ("253", "Tesis, Makine ve Cihazlar"), ("254", "Taşıtlar"), ("255", "Demirbaşlar"), ("257", "Birikmiş Amortismanlar (-)"), ("260", "Haklar"),
        ("300", "Banka Kredileri"), ("320", "Satıcılar"), ("321", "Borç Senetleri"), ("326", "Alınan Depozito ve Teminatlar"),
        ("329", "Diğer Ticari Borçlar"), ("335", "Personele Borçlar"), ("336", "Diğer Çeşitli Borçlar"), ("340", "Alınan Sipariş Avansları"),
        ("360", "Ödenecek Vergi ve Fonlar"), ("361", "Ödenecek Sosyal Güvenlik Kesintileri"),
        ("370", "Dönem Kârı Vergi ve Diğer Yasal Yükümlülük Karşılıkları"), ("380", "Gelecek Aylara Ait Gelirler"), ("391", "Hesaplanan KDV"),
        ("400", "Banka Kredileri (Uzun Vadeli)"), ("500", "Sermaye"), ("540", "Yasal Yedekler"), ("570", "Geçmiş Yıllar Kârları"),
        ("580", "Geçmiş Yıllar Zararları (-)"), ("590", "Dönem Net Kârı"), ("591", "Dönem Net Zararı (-)"), ("600", "Yurt İçi Satışlar"),
        ("601", "Yurt Dışı Satışlar"), ("602", "Diğer Gelirler"), ("610", "Satıştan İadeler (-)"), ("611", "Satış İskontoları (-)"),
        ("620", "Satılan Mamuller Maliyeti (-)"), ("621", "Satılan Ticari Mallar Maliyeti (-)"), ("622", "Satılan Hizmet Maliyeti (-)"),
        ("630", "Araştırma ve Geliştirme Giderleri (-)"), ("631", "Pazarlama, Satış ve Dağıtım Giderleri (-)"), ("632", "Genel Yönetim Giderleri (-)"),
        ("642", "Faiz Gelirleri"), ("646", "Kambiyo Kârları"), ("649", "Diğer Olağan Gelir ve Kârlar"), ("653", "Komisyon Giderleri (-)"),
        ("656", "Kambiyo Zararları (-)"), ("659", "Diğer Olağan Gider ve Zararlar (-)"), ("660", "Kısa Vadeli Borçlanma Giderleri (-)"),
        ("679", "Diğer Olağandışı Gelir ve Kârlar"), ("689", "Diğer Olağandışı Gider ve Zararlar (-)"),
        ("691", "Dönem Kârı Vergi ve Diğer Yasal Yükümlülük Karşılıkları (-)"), ("710", "Direkt İlk Madde ve Malzeme Giderleri"),
        ("711", "Direkt İlk Madde ve Malzeme Yansıtma Hesabı"), ("720", "Direkt İşçilik Giderleri"), ("721", "Direkt İşçilik Giderleri Yansıtma Hesabı"),
        ("730", "Genel Üretim Giderleri"), ("731", "Genel Üretim Giderleri Yansıtma Hesabı"), ("760", "Pazarlama, Satış ve Dağıtım Giderleri"),
        ("770", "Genel Yönetim Giderleri"), ("780", "Finansman Giderleri"),
    ];

    /// <summary>Account type from the first digits of the code (class 6 mixes income and expense).</summary>
    public static LedgerAccountType TypeOf(string code) => code[0] switch
    {
        '1' or '2' => LedgerAccountType.ASSET,
        '3' or '4' => LedgerAccountType.LIABILITY,
        '5' => LedgerAccountType.EQUITY,
        '6' when code[..2] is "60" or "64" or "67" => LedgerAccountType.INCOME,
        _ => LedgerAccountType.EXPENSE,
    };

    public static List<DefaultAccount> Default() =>
        Accounts.Select(a => new DefaultAccount(a.Code, a.Name, TypeOf(a.Code), ACC.SystemCodes.Contains(a.Code))).ToList();

    public static bool IsValidCode(string code) =>
        System.Text.RegularExpressions.Regex.IsMatch(code, @"^[1-7]\d{2}(\.\d{1,4})*$");

    /// <summary>Three-digit main account of any code.</summary>
    public static string MainCode(string code) => code[..3];

    public static readonly Dictionary<string, string> BalanceClasses = new()
    {
        ["1"] = "Dönen Varlıklar", ["2"] = "Duran Varlıklar", ["3"] = "Kısa Vadeli Yabancı Kaynaklar",
        ["4"] = "Uzun Vadeli Yabancı Kaynaklar", ["5"] = "Özkaynaklar",
    };

    public static readonly Dictionary<string, string> Groups = new()
    {
        ["10"] = "Hazır Değerler", ["11"] = "Menkul Kıymetler", ["12"] = "Ticari Alacaklar", ["13"] = "Diğer Alacaklar", ["15"] = "Stoklar",
        ["17"] = "Yıllara Yaygın İnşaat ve Onarım Maliyetleri", ["18"] = "Gelecek Aylara Ait Giderler ve Gelir Tahakkukları", ["19"] = "Diğer Dönen Varlıklar",
        ["22"] = "Ticari Alacaklar", ["23"] = "Diğer Alacaklar", ["24"] = "Mali Duran Varlıklar", ["25"] = "Maddi Duran Varlıklar",
        ["26"] = "Maddi Olmayan Duran Varlıklar", ["27"] = "Özel Tükenmeye Tabi Varlıklar", ["28"] = "Gelecek Yıllara Ait Giderler ve Gelir Tahakkukları",
        ["29"] = "Diğer Duran Varlıklar", ["30"] = "Mali Borçlar", ["32"] = "Ticari Borçlar", ["33"] = "Diğer Borçlar", ["34"] = "Alınan Avanslar",
        ["35"] = "Yıllara Yaygın İnşaat ve Onarım Hakedişleri", ["36"] = "Ödenecek Vergi ve Diğer Yükümlülükler", ["37"] = "Borç ve Gider Karşılıkları",
        ["38"] = "Gelecek Aylara Ait Gelirler ve Gider Tahakkukları", ["39"] = "Diğer Kısa Vadeli Yabancı Kaynaklar", ["40"] = "Mali Borçlar",
        ["42"] = "Ticari Borçlar", ["43"] = "Diğer Borçlar", ["44"] = "Alınan Avanslar", ["47"] = "Borç ve Gider Karşılıkları",
        ["48"] = "Gelecek Yıllara Ait Gelirler ve Gider Tahakkukları", ["49"] = "Diğer Uzun Vadeli Yabancı Kaynaklar", ["50"] = "Ödenmiş Sermaye",
        ["52"] = "Sermaye Yedekleri", ["54"] = "Kâr Yedekleri", ["57"] = "Geçmiş Yıllar Kârları", ["58"] = "Geçmiş Yıllar Zararları (-)",
        ["59"] = "Dönem Net Kârı (Zararı)",
    };

    /// <summary>Gelir tablosu sections in statement order.</summary>
    public static readonly IncomeSectionDef[] IncomeSections =
    [
        new("grossSales", "A. Brüt Satışlar", ["60"]),
        new("salesDeductions", "B. Satış İndirimleri (-)", ["61"]),
        new("costOfSales", "D. Satışların Maliyeti (-)", ["62"]),
        new("operatingExpenses", "E. Faaliyet Giderleri (-)", ["63", "71", "72", "73", "74", "75", "76", "77", "79"]),
        new("otherIncome", "F. Diğer Faaliyetlerden Olağan Gelir ve Kârlar", ["64"]),
        new("otherExpenses", "G. Diğer Faaliyetlerden Olağan Gider ve Zararlar (-)", ["65"]),
        new("financeExpenses", "H. Finansman Giderleri (-)", ["66", "78"]),
        new("extraordinaryIncome", "I. Olağandışı Gelir ve Kârlar", ["67"]),
        new("extraordinaryExpenses", "J. Olağandışı Gider ve Zararlar (-)", ["68"]),
        new("taxProvision", "K. Dönem Kârı Vergi ve Diğer Yasal Yükümlülük Karşılıkları (-)", ["69"]),
    ];

    public static string? IncomeSection(string code) => IncomeSections.FirstOrDefault(s => s.Prefixes.Contains(code[..2]))?.Key;

    /// <summary>Classes 6 and 7 make up the result of the period.</summary>
    public static bool IsResultAccount(string code) => code[0] is '6' or '7';
}
