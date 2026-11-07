using System.Globalization;
using System.Text;
using EnterprisePulse.Core.Data;

namespace EnterprisePulse.Core.Modules.Einvoice;

public sealed record UblParty(string? TaxId, string Name, string? TaxOffice, string? Street, string? City, string Country, string? Email);

public sealed record OrderReference(string Number, string Date);

public sealed record UblInvoice(
    EInvoiceProfile Profile, string Number, Guid Uuid, string IssueDate, string IssueTime, string CurrencyCode, string? ExchangeRate, List<string> Notes,
    UblParty Supplier, UblParty Customer, List<EInvoiceLine> Lines, EInvoiceTotals Totals, OrderReference? OrderReference);

/// <summary>UBL-TR 2.1 invoice (TR1.2 customisation); the digital signature is left to the integrator.</summary>
public static class EInvoiceXml
{
    private static readonly Dictionary<string, string> Countries = new()
    {
        ["TR"] = "Türkiye", ["DE"] = "Almanya", ["SE"] = "İsveç", ["US"] = "Amerika Birleşik Devletleri", ["GB"] = "Birleşik Krallık",
    };

    private static readonly (string Name, string Value)[] Namespaces =
    [
        ("xmlns", "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"),
        ("xmlns:cac", "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"),
        ("xmlns:cbc", "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"),
        ("xmlns:ext", "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2"),
    ];

    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&apos;", _ => c.ToString() });
        }
        return sb.ToString();
    }

    private sealed class Writer
    {
        private readonly List<string> _out = [];
        private int _depth;

        public Writer Open(string tag, params (string Name, string Value)[] attrs)
        {
            _out.Add($"{Indent()}<{tag}{Attrs(attrs)}>");
            _depth++;
            return this;
        }

        public Writer Close(string tag)
        {
            _depth--;
            _out.Add($"{Indent()}</{tag}>");
            return this;
        }

        /// <summary>Writes the leaf element; skipped when the value is null or empty.</summary>
        public Writer Leaf(string tag, string? value, params (string Name, string Value)[] attrs)
        {
            if (string.IsNullOrEmpty(value)) return this;
            _out.Add($"{Indent()}<{tag}{Attrs(attrs)}>{Escape(value)}</{tag}>");
            return this;
        }

        public override string ToString() => string.Join('\n', _out) + "\n";

        private string Indent() => new(' ', _depth * 2);

        private static string Attrs((string Name, string Value)[] attrs) => string.Concat(attrs.Select(a => $" {a.Name}=\"{Escape(a.Value)}\""));
    }

    private static string Num(string value) => decimal.Parse(value, CultureInfo.InvariantCulture).ToString("0.##########", CultureInfo.InvariantCulture);

    private static void Party(Writer w, string tag, UblParty p, bool eArchive)
    {
        var taxId = !string.IsNullOrEmpty(p.TaxId) ? p.TaxId : eArchive ? EInvoiceRules.AnonymousTckn : "";
        var scheme = EInvoiceRules.TaxIdScheme(taxId);
        w.Open(tag).Open("cac:Party");
        w.Open("cac:PartyIdentification").Leaf("cbc:ID", taxId, ("schemeID", scheme)).Close("cac:PartyIdentification");
        if (scheme == "VKN") w.Open("cac:PartyName").Leaf("cbc:Name", p.Name).Close("cac:PartyName");
        w.Open("cac:PostalAddress");
        w.Leaf("cbc:StreetName", p.Street);
        w.Leaf("cbc:CitySubdivisionName", p.City);
        w.Leaf("cbc:CityName", p.City ?? "-");
        w.Open("cac:Country").Leaf("cbc:Name", Countries.GetValueOrDefault(p.Country, p.Country)).Close("cac:Country");
        w.Close("cac:PostalAddress");
        if (!string.IsNullOrEmpty(p.TaxOffice))
        {
            w.Open("cac:PartyTaxScheme").Open("cac:TaxScheme").Leaf("cbc:Name", p.TaxOffice);
            w.Close("cac:TaxScheme").Close("cac:PartyTaxScheme");
        }
        if (!string.IsNullOrEmpty(p.Email)) w.Open("cac:Contact").Leaf("cbc:ElectronicMail", p.Email).Close("cac:Contact");
        if (scheme == "TCKN")
        {
            var parts = p.Name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            var family = parts.Count > 1 ? parts[^1] : parts[0];
            if (parts.Count > 1) parts.RemoveAt(parts.Count - 1);
            w.Open("cac:Person").Leaf("cbc:FirstName", string.Join(' ', parts)).Leaf("cbc:FamilyName", family).Close("cac:Person");
        }
        w.Close("cac:Party").Close(tag);
    }

    private static void VatSubtotal(Writer w, (string, string) currency, string taxable, string vat, string rate)
    {
        w.Open("cac:TaxSubtotal");
        w.Leaf("cbc:TaxableAmount", taxable, currency);
        w.Leaf("cbc:TaxAmount", vat, currency);
        w.Leaf("cbc:Percent", Num(rate));
        w.Open("cac:TaxCategory").Open("cac:TaxScheme");
        w.Leaf("cbc:Name", "KDV").Leaf("cbc:TaxTypeCode", "0015");
        w.Close("cac:TaxScheme").Close("cac:TaxCategory");
        w.Close("cac:TaxSubtotal");
    }

    public static string Build(UblInvoice doc)
    {
        var eArchive = doc.Profile == EInvoiceProfile.EARSIVFATURA;
        var currency = ("currencyID", doc.CurrencyCode);
        var w = new Writer();
        w.Open("Invoice", Namespaces);
        w.Open("ext:UBLExtensions").Open("ext:UBLExtension").Leaf("ext:ExtensionContent", "-");
        w.Close("ext:UBLExtension").Close("ext:UBLExtensions");
        w.Leaf("cbc:UBLVersionID", "2.1");
        w.Leaf("cbc:CustomizationID", "TR1.2");
        w.Leaf("cbc:ProfileID", doc.Profile.ToString());
        w.Leaf("cbc:ID", doc.Number);
        w.Leaf("cbc:CopyIndicator", "false");
        w.Leaf("cbc:UUID", doc.Uuid.ToString());
        w.Leaf("cbc:IssueDate", doc.IssueDate);
        w.Leaf("cbc:IssueTime", doc.IssueTime);
        w.Leaf("cbc:InvoiceTypeCode", "SATIS");
        foreach (var note in doc.Notes) w.Leaf("cbc:Note", note);
        w.Leaf("cbc:DocumentCurrencyCode", doc.CurrencyCode);
        w.Leaf("cbc:LineCountNumeric", doc.Lines.Count.ToString());
        if (doc.OrderReference != null)
        {
            w.Open("cac:OrderReference");
            w.Leaf("cbc:ID", doc.OrderReference.Number).Leaf("cbc:IssueDate", doc.OrderReference.Date);
            w.Close("cac:OrderReference");
        }
        if (eArchive)
        {
            w.Open("cac:AdditionalDocumentReference");
            w.Leaf("cbc:ID", "ELEKTRONIK").Leaf("cbc:IssueDate", doc.IssueDate);
            w.Leaf("cbc:DocumentTypeCode", "EREPSENDT");
            w.Close("cac:AdditionalDocumentReference");
        }
        Party(w, "cac:AccountingSupplierParty", doc.Supplier, false);
        Party(w, "cac:AccountingCustomerParty", doc.Customer, eArchive);
        if (!string.IsNullOrEmpty(doc.ExchangeRate) && doc.CurrencyCode != "TRY")
        {
            w.Open("cac:PricingExchangeRate");
            w.Leaf("cbc:SourceCurrencyCode", doc.CurrencyCode).Leaf("cbc:TargetCurrencyCode", "TRY");
            w.Leaf("cbc:CalculationRate", doc.ExchangeRate).Leaf("cbc:Date", doc.IssueDate);
            w.Close("cac:PricingExchangeRate");
        }

        var taxTotal = doc.Totals.Taxes.Sum(t => decimal.Parse(t.Vat, CultureInfo.InvariantCulture)).ToString("0.00", CultureInfo.InvariantCulture);
        w.Open("cac:TaxTotal").Leaf("cbc:TaxAmount", taxTotal, currency);
        foreach (var t in doc.Totals.Taxes) VatSubtotal(w, currency, t.Base, t.Vat, t.Rate);
        w.Close("cac:TaxTotal");

        w.Open("cac:LegalMonetaryTotal");
        w.Leaf("cbc:LineExtensionAmount", doc.Totals.LineExtension, currency);
        w.Leaf("cbc:TaxExclusiveAmount", doc.Totals.TaxExclusive, currency);
        w.Leaf("cbc:TaxInclusiveAmount", doc.Totals.TaxInclusive, currency);
        w.Leaf("cbc:AllowanceTotalAmount", doc.Totals.Allowance, currency);
        w.Leaf("cbc:PayableAmount", doc.Totals.Payable, currency);
        w.Close("cac:LegalMonetaryTotal");

        foreach (var line in doc.Lines)
        {
            w.Open("cac:InvoiceLine");
            w.Leaf("cbc:ID", line.LineNo.ToString());
            w.Leaf("cbc:InvoicedQuantity", line.Quantity, ("unitCode", line.UnitCode));
            w.Leaf("cbc:LineExtensionAmount", line.Net, currency);
            if (decimal.Parse(line.Discount, CultureInfo.InvariantCulture) > 0)
            {
                w.Open("cac:AllowanceCharge");
                w.Leaf("cbc:ChargeIndicator", "false");
                w.Leaf("cbc:MultiplierFactorNumeric", (decimal.Parse(line.DiscountRate, CultureInfo.InvariantCulture) / 100).ToString("0.##########", CultureInfo.InvariantCulture));
                w.Leaf("cbc:Amount", line.Discount, currency);
                w.Leaf("cbc:BaseAmount", line.Gross, currency);
                w.Close("cac:AllowanceCharge");
            }
            w.Open("cac:TaxTotal").Leaf("cbc:TaxAmount", line.Vat, currency);
            VatSubtotal(w, currency, line.Net, line.Vat, line.VatRate);
            w.Close("cac:TaxTotal");
            w.Open("cac:Item").Leaf("cbc:Name", line.Description);
            if (!string.IsNullOrEmpty(line.SellerCode))
            {
                w.Open("cac:SellersItemIdentification").Leaf("cbc:ID", line.SellerCode);
                w.Close("cac:SellersItemIdentification");
            }
            w.Close("cac:Item");
            w.Open("cac:Price").Leaf("cbc:PriceAmount", line.UnitPrice, currency).Close("cac:Price");
            w.Close("cac:InvoiceLine");
        }
        w.Close("Invoice");
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n{w}";
    }
}
