using System.Text.Json;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Accounting;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Einvoice;

public sealed class EInvoicesHook() : ManagedRecordHook("e-invoices", "e-Faturalar e-Fatura ekranından gönderilir ve yanıtlanır");

public sealed class EInvoiceEventsHook() : ManagedRecordHook("e-invoice-events", "e-Fatura olayları sistem tarafından yazılır");

public sealed class EInvoiceDashboard(AppDbContext db, EInvoiceService einvoice) : IDashboardProvider
{
    public int Order => 65;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("einvoice:read")) return null;
        var s = await einvoice.SummaryAsync();
        return new DashboardSection("einvoice", "e-Fatura",
        [
            new("einvoice-unsent", "Gönderilmemiş fatura", s.Unsent.ToString(), "Outbox", Link: "/e-invoices", Color: s.Unsent > 0 ? "warning" : "primary"),
            new("einvoice-errors", "GİB hatası", s.Errors.ToString(), "ErrorOutline", Link: "/r/e-invoices", Color: s.Errors > 0 ? "error" : "primary"),
            new("einvoice-respond", "Yanıt bekleyen gelen", s.ToRespond.ToString(), "MarkEmailUnread", Link: "/e-invoices", Color: s.ToRespond > 0 ? "warning" : "primary"),
        ]);
    }
}

/// <summary>
/// e-Fatura simulation data: valid VKN check digits for the sample company and partners, mailbox aliases for registered users, the GİB
/// number series and the default settings.
/// </summary>
public sealed class EInvoiceSeed(AppDbContext db) : ISeedStep
{
    public int Order => 82;

    /// <summary>Registered e-Fatura users among the sample partners; the others receive e-Arşiv invoices.</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["MUS-001"] = "urn:mail:defaultpk@bogaziciperakende.com.tr",
        ["MUS-003"] = "urn:mail:defaultpk@akdenizotelcilik.com.tr",
        ["CAR-001"] = "urn:mail:defaultpk@karadenizticaret.com.tr",
        ["TED-001"] = "urn:mail:defaultpk@anadolubilisim.com.tr",
        ["TED-002"] = "urn:mail:defaultpk@egekirtasiye.com.tr",
        ["TED-004"] = "urn:mail:defaultpk@marmaraambalaj.com.tr",
    };

    /// <summary>Left with an invalid tax number so the demo shows a GİB rejection.</summary>
    private static readonly HashSet<string> KeepInvalid = ["MUS-004"];

    public async Task RunAsync(SeedContext ctx)
    {
        var year = AccountingClock.Today.Year;
        foreach (var companyId in new[] { ctx.PulseId, ctx.LogisticsId })
        {
            using var _ = db.User.ActAs(companyId, ctx.AdminId, superAdmin: true);
            var company = await db.Companies.IgnoreQueryFilters().FirstAsync(c => c.Id == companyId);
            if (string.IsNullOrEmpty(company.TaxNumber))
            {
                company.TaxNumber = companyId == ctx.PulseId ? "1234567890" : "9876543210";
                company.TaxOffice ??= company.City;
            }
            if (company.TaxNumber is { Length: 10 } && !EInvoiceRules.IsValidVkn(company.TaxNumber)) company.TaxNumber = EInvoiceRules.WithVknCheckDigit(company.TaxNumber);

            foreach (var partner in await db.BusinessPartners.ToListAsync())
            {
                var tax = partner.TaxNumber;
                if (tax is { Length: 10 } && !EInvoiceRules.IsValidVkn(tax) && !KeepInvalid.Contains(partner.Code)) partner.TaxNumber = EInvoiceRules.WithVknCheckDigit(tax);
                if (string.IsNullOrEmpty(partner.EInvoiceAlias) && Aliases.TryGetValue(partner.Code, out var alias)) partner.EInvoiceAlias = alias;
            }

            foreach (var (code, name, prefix) in new[] { (EInvoiceService.InvoiceSequence, "e-Fatura", "EPF"), (EInvoiceService.ArchiveSequence, "e-Arşiv Fatura", "EPA") })
            {
                var row = await db.NumberSequences.FirstOrDefaultAsync(n => n.Code == code);
                if (row == null) db.NumberSequences.Add(new NumberSequence { CompanyId = companyId, Code = code, Name = name, Prefix = prefix, Padding = 9, CurrentYear = year });
                else if (row.NextValue == 1) row.Padding = 9;
            }

            if (!await db.Settings.AnyAsync(s => s.Key == EInvoiceService.SettingsKey))
            {
                db.Settings.Add(new Setting
                {
                    CompanyId = companyId, Key = EInvoiceService.SettingsKey,
                    Value = JsonSerializer.SerializeToDocument(new { senderAlias = $"urn:mail:defaultgb@{company.Code.ToLowerInvariant()}.enterprisepulse.dev", defaultProfile = "TICARIFATURA", autoSend = false }),
                });
            }
            await db.SaveChangesAsync();
        }
    }
}
