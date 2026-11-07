using System.Globalization;
using System.Text.RegularExpressions;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Trade;

namespace EnterprisePulse.Core.Modules.Einvoice;

public sealed record EInvoiceLineInput(string Description, string? SellerCode, Guid? VariantId, string? Unit, decimal Quantity, decimal UnitPrice, decimal DiscountRate, decimal VatRate);

/// <summary>Line snapshot stored on the e-invoice (JSON) and written to the XML.</summary>
public sealed record EInvoiceLine(
    int LineNo, string Description, string? SellerCode, Guid? VariantId, string Quantity, string UnitCode, string UnitPrice, string DiscountRate,
    string Gross, string Discount, string Net, string VatRate, string Vat);

public sealed record VatBreakdown(string Rate, string Base, string Vat);

public sealed record EInvoiceTotals(string LineExtension, string Allowance, string TaxExclusive, string TaxInclusive, string Payable, List<VatBreakdown> Taxes);

public sealed record ValidationInput(
    EInvoiceProfile Profile, string Number, string? SupplierTaxId, string? CustomerTaxId, string? ReceiverAlias, string CurrencyCode, decimal ExchangeRate,
    List<EInvoiceLine> Lines, decimal Payable);

public sealed record CancelCheck(bool Allowed, bool CancelEInvoice, string? Reason);

public sealed record OutgoingStep(EInvoiceStatus Status, string Code, string Message);

/// <summary>e-Fatura / e-Arşiv rules: identifiers, scenario, simulated GİB validation, response window and status machine.</summary>
public static class EInvoiceRules
{
    /// <summary>e-Fatura posta kutusu etiketi, e.g. "urn:mail:defaultpk@firma.com.tr".</summary>
    public static readonly Regex AliasPattern = new(@"^urn:mail:[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);

    /// <summary>GİB invoice number: 3-character prefix, 4-digit year and a 9-digit sequence.</summary>
    public static readonly Regex GibNumber = new(@"^[A-Z0-9]{3}\d{4}\d{9}$", RegexOptions.Compiled);

    /// <summary>Ticari fatura: the receiver may accept or reject within 8 days of delivery.</summary>
    public const int ResponseDays = 8;

    /// <summary>GİB accepts this TCKN on e-Arşiv invoices for consumers without a tax id.</summary>
    public const string AnonymousTckn = "11111111111";

    public static class Codes
    {
        public const string Queued = "1000";
        public const string SchemaError = "1150";
        public const string Processed = "1200";
        public const string Completed = "1300";
        public const string Accepted = "KABUL";
        public const string Rejected = "RED";
        public const string Cancelled = "IPTAL";
    }

    /// <summary>Vergi kimlik numarası (10 digits) check digit.</summary>
    public static bool IsValidVkn(string? value)
    {
        if (value is not { Length: 10 } || !value.All(char.IsAsciiDigit)) return false;
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var t = (value[i] - '0' + 9 - i) % 10;
            var v = (int)((t * Math.Pow(2, 9 - i)) % 9);
            if (t != 0 && v == 0) v = 9;
            sum += v;
        }
        return (10 - sum % 10) % 10 == value[9] - '0';
    }

    /// <summary>The VKN with its check digit fixed; used to repair sample data.</summary>
    public static string WithVknCheckDigit(string value)
    {
        var head = value[..9];
        for (var digit = 0; digit <= 9; digit++)
        {
            if (IsValidVkn($"{head}{digit}")) return $"{head}{digit}";
        }
        return value;
    }

    /// <summary>T.C. kimlik numarası (11 digits).</summary>
    public static bool IsValidTckn(string? value)
    {
        if (value is not { Length: 11 } || !value.All(char.IsAsciiDigit) || value[0] == '0') return false;
        var d = value.Select(c => c - '0').ToArray();
        var odd = d[0] + d[2] + d[4] + d[6] + d[8];
        var even = d[1] + d[3] + d[5] + d[7];
        var tenth = (((odd * 7 - even) % 10) + 10) % 10;
        var eleventh = d.Take(10).Sum() % 10;
        return d[9] == tenth && d[10] == eleventh;
    }

    public static bool IsValidTaxId(string? value) => !string.IsNullOrEmpty(value) && (value.Length == 11 ? IsValidTckn(value) : IsValidVkn(value));

    public static string TaxIdScheme(string value) => value.Length == 11 ? "TCKN" : "VKN";

    /// <summary>"EPF-2026-000000001" becomes "EPF2026000000001".</summary>
    public static string ToGibNumber(string formatted) => formatted.Replace("-", "");

    /// <summary>
    /// The scenario of an outgoing invoice: registered receivers (with an alias) get e-Fatura, the rest e-Arşiv. Returns null when
    /// the requested profile does not fit the receiver.
    /// </summary>
    public static EInvoiceProfile? ResolveProfile(string? receiverAlias, EInvoiceProfile? requested, EInvoiceProfile fallback)
    {
        if (string.IsNullOrEmpty(receiverAlias))
            return requested is null or EInvoiceProfile.EARSIVFATURA ? EInvoiceProfile.EARSIVFATURA : null;
        var profile = requested ?? fallback;
        return profile == EInvoiceProfile.EARSIVFATURA ? null : profile;
    }

    private static readonly Dictionary<string, string> UnitCodes = new()
    {
        ["ADET"] = "C62", ["KG"] = "KGM", ["LT"] = "LTR", ["M"] = "MTR", ["M2"] = "MTK", ["KOLI"] = "CT", ["SAAT"] = "HUR",
    };

    /// <summary>UN/ECE Rec. 20 unit code used by UBL-TR; unknown units are sent as pieces.</summary>
    public static string UnitCode(string? unit) => unit != null && UnitCodes.TryGetValue(unit, out var code) ? code : "C62";

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string TrimQty(decimal v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static (List<EInvoiceLine> Lines, EInvoiceTotals Totals) BuildLines(IReadOnlyList<EInvoiceLineInput> inputs)
    {
        var amounts = inputs.Select(i => DocumentMath.Line(new LineInput(i.Quantity, i.UnitPrice, i.DiscountRate, i.VatRate))).ToList();
        var lines = inputs.Select((input, index) =>
        {
            var a = amounts[index];
            return new EInvoiceLine(index + 1, input.Description, input.SellerCode, input.VariantId, TrimQty(input.Quantity), UnitCode(input.Unit),
                input.UnitPrice.ToString("0.0000", CultureInfo.InvariantCulture), Money(input.DiscountRate), Money(a.Gross), Money(a.Discount), Money(a.Net),
                Money(input.VatRate), Money(a.Vat));
        }).ToList();
        var net = amounts.Sum(a => a.Net);
        var vat = amounts.Sum(a => a.Vat);
        var taxes = inputs.Select((i, index) => (Rate: i.VatRate, A: amounts[index])).GroupBy(x => x.Rate).OrderBy(g => g.Key)
            .Select(g => new VatBreakdown(Money(g.Key), Money(g.Sum(x => x.A.Net)), Money(g.Sum(x => x.A.Vat)))).ToList();
        return (lines, new EInvoiceTotals(Money(net), Money(amounts.Sum(a => a.Discount)), Money(net), Money(net + vat), Money(net + vat), taxes));
    }

    /// <summary>Simulated GİB schematron checks; returns the error messages (empty when valid).</summary>
    public static List<string> ValidateForGib(ValidationInput input)
    {
        var errors = new List<string>();
        if (!GibNumber.IsMatch(input.Number)) errors.Add($"Fatura numarası GİB formatına uymuyor: {input.Number}");
        if (!IsValidTaxId(input.SupplierTaxId)) errors.Add("Gönderici VKN/TCKN geçersiz");
        var eArchive = input.Profile == EInvoiceProfile.EARSIVFATURA;
        if (string.IsNullOrEmpty(input.CustomerTaxId))
        {
            if (!eArchive) errors.Add("Alıcı VKN/TCKN zorunludur");
        }
        else if (!IsValidTaxId(input.CustomerTaxId))
        {
            errors.Add($"Alıcı VKN/TCKN geçersiz: {input.CustomerTaxId}");
        }
        if (!eArchive && !(input.ReceiverAlias != null && AliasPattern.IsMatch(input.ReceiverAlias))) errors.Add("Alıcı posta kutusu etiketi (urn:mail) geçersiz");
        if (input.CurrencyCode != "TRY" && input.ExchangeRate <= 0) errors.Add("Döviz kuru zorunludur");
        if (input.Lines.Count == 0) errors.Add("Fatura en az bir satır içermelidir");
        foreach (var line in input.Lines)
        {
            if (!(decimal.Parse(line.Quantity, CultureInfo.InvariantCulture) > 0)) errors.Add($"Satır {line.LineNo}: miktar sıfırdan büyük olmalıdır");
        }
        var total = input.Lines.Sum(l => decimal.Parse(l.Net, CultureInfo.InvariantCulture) + decimal.Parse(l.Vat, CultureInfo.InvariantCulture));
        if (total != input.Payable) errors.Add("Ödenecek tutar satır toplamlarıyla uyuşmuyor");
        return errors;
    }

    /// <summary>Whether a ticari fatura can still be accepted or rejected.</summary>
    public static bool CanRespond(EInvoiceProfile profile, EInvoiceStatus status, DateTime? deliveredAt, DateTime now) =>
        profile == EInvoiceProfile.TICARIFATURA && status == EInvoiceStatus.DELIVERED && deliveredAt != null && now - deliveredAt.Value <= TimeSpan.FromDays(ResponseDays);

    /// <summary>A delivered ticari fatura without a response after 8 days counts as accepted.</summary>
    public static bool ResponseExpired(EInvoiceProfile profile, EInvoiceStatus status, DateTime? deliveredAt, DateTime now) =>
        profile == EInvoiceProfile.TICARIFATURA && status == EInvoiceStatus.DELIVERED && deliveredAt != null && now - deliveredAt.Value > TimeSpan.FromDays(ResponseDays);

    /// <summary>
    /// Whether the sales invoice behind an e-invoice may be cancelled. Delivered e-Arşiv invoices are cancelled with it; delivered
    /// e-Fatura can only be undone by the receiver rejecting it.
    /// </summary>
    public static CancelCheck SalesCancelCheck(EInvoiceProfile? profile, EInvoiceStatus? status)
    {
        if (profile == null || status == null) return new CancelCheck(true, false, null);
        switch (status)
        {
            case EInvoiceStatus.QUEUED or EInvoiceStatus.SENT:
                return new CancelCheck(false, false, "e-Fatura gönderiliyor; GİB yanıtını bekleyin");
            case EInvoiceStatus.DELIVERED or EInvoiceStatus.ACCEPTED:
                return profile == EInvoiceProfile.EARSIVFATURA
                    ? new CancelCheck(true, true, null)
                    : new CancelCheck(false, false, "e-Fatura alıcıya ulaştı; yalnızca alıcı reddederse iptal edilebilir");
            default:
                return new CancelCheck(true, false, null);
        }
    }

    /// <summary>Next status of an outgoing envelope in one integrator run; null when nothing changes.</summary>
    public static OutgoingStep? NextOutgoingStep(EInvoiceProfile profile, EInvoiceStatus status, DateTime? deliveredAt, Func<List<string>> errors, DateTime now)
    {
        if (status == EInvoiceStatus.QUEUED)
        {
            var problems = errors();
            return problems.Count > 0
                ? new OutgoingStep(EInvoiceStatus.ERROR, Codes.SchemaError, string.Join("; ", problems))
                : new OutgoingStep(EInvoiceStatus.SENT, Codes.Processed, "Zarf başarıyla işlendi");
        }
        if (status == EInvoiceStatus.SENT)
        {
            return profile switch
            {
                EInvoiceProfile.EARSIVFATURA => new OutgoingStep(EInvoiceStatus.DELIVERED, Codes.Completed, "e-Arşiv raporuna eklendi; alıcıya e-posta ile iletildi"),
                EInvoiceProfile.TICARIFATURA => new OutgoingStep(EInvoiceStatus.DELIVERED, Codes.Completed, "Alıcıya ulaştı; ticari fatura yanıtı bekleniyor"),
                _ => new OutgoingStep(EInvoiceStatus.DELIVERED, Codes.Completed, "Başarıyla tamamlandı"),
            };
        }
        if (ResponseExpired(profile, status, deliveredAt, now))
            return new OutgoingStep(EInvoiceStatus.ACCEPTED, Codes.Accepted, $"{ResponseDays} gün içinde yanıt verilmedi; kabul edilmiş sayılır");
        return null;
    }
}
