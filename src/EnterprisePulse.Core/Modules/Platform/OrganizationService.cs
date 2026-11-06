using System.Text.Json;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Platform;

public sealed record CreateCompanyRequest(string Code, string Name, string? LegalName, string? TaxNumber, string? TaxOffice, string? Email, string? Phone, string? Address, string? City, string? BaseCurrency);

public sealed class OrganizationService(AppDbContext db, CompanyBootstrap bootstrap)
{
    public async Task<Company> CreateCompanyAsync(CreateCompanyRequest request)
    {
        db.User.Require("companies:create");
        var userId = db.User.RequireUserId();
        var code = request.Code.Trim().ToUpperInvariant();
        Guard.That(code.Length >= 2, "Şirket kodu en az 2 karakter olmalı");
        Guard.State(!await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.Code == code), "Bu şirket kodu kullanılıyor");

        var company = new Company
        {
            Code = code, Name = request.Name.Trim(), LegalName = request.LegalName, TaxNumber = request.TaxNumber, TaxOffice = request.TaxOffice,
            Email = request.Email, Phone = request.Phone, Address = request.Address, City = request.City, BaseCurrency = request.BaseCurrency ?? "TRY",
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        using (db.User.WithoutTenant())
        {
            var hq = new Branch { CompanyId = company.Id, Code = "MERKEZ", Name = "Genel Merkez", City = request.City };
            db.Branches.Add(hq);
            await db.SaveChangesAsync();
            var roles = await bootstrap.InitializeAsync(company);
            db.UserCompanies.Add(new UserCompany { UserId = userId, CompanyId = company.Id, DefaultBranchId = hq.Id });
            db.UserRoles.Add(new UserRole { UserId = userId, CompanyId = company.Id, RoleId = roles["admin"] });
            await db.SaveChangesAsync();
        }
        return company;
    }

    public async Task<JsonDocument> GetSettingAsync(string key)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        return setting?.Value ?? JsonDocument.Parse("{}");
    }

    public async Task<JsonDocument> SaveSettingAsync(string key, JsonElement value)
    {
        db.User.Require("settings:update");
        Guard.That(value.ValueKind == JsonValueKind.Object, "Ayar değeri bir nesne olmalı");
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key);
        var doc = JsonDocument.Parse(value.GetRawText());
        if (setting == null) db.Settings.Add(new Setting { Key = key, Value = doc });
        else setting.Value = doc;
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Converts an amount into the company's base currency with the latest selling rate.</summary>
    public async Task<decimal> RateAsync(string currency, DateOnly? date = null)
    {
        if (currency == "TRY") return 1;
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var rate = await db.ExchangeRates.Where(r => r.CurrencyCode == currency && r.RateDate <= day)
            .OrderByDescending(r => r.RateDate).Select(r => (decimal?)r.Selling).FirstOrDefaultAsync();
        return rate ?? throw new BadRequestAppException($"{currency} için kur bulunamadı");
    }
}
