using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Metadata;
using EnterprisePulse.Core.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EnterprisePulse.Core.Seeding;

public sealed class SeedContext
{
    public Guid PulseId { get; set; }
    public Guid LogisticsId { get; set; }
    public Guid HqBranchId { get; set; }
    public Guid AdminId { get; set; }
    public Dictionary<string, Guid> PulseRoles { get; set; } = [];
    public Dictionary<string, Guid> UsersByName { get; } = [];
}

/// <summary>Demo data for one module; each step must be idempotent because the seeder runs on every start.</summary>
public interface ISeedStep
{
    int Order { get; }
    Task RunAsync(SeedContext ctx);
}

public sealed class DataSeeder(AppDbContext db, MetadataStore meta, CompanyBootstrap bootstrap, IEnumerable<ISeedStep> steps, ILogger<DataSeeder> log)
{
    public static readonly (string First, string Last)[] SampleEmployees =
    [
        ("Ayşe", "Yılmaz"), ("Mehmet", "Kaya"), ("Zeynep", "Demir"), ("Mustafa", "Çelik"), ("Elif", "Şahin"), ("Ahmet", "Yıldız"),
        ("Fatma", "Öztürk"), ("Emre", "Aydın"), ("Selin", "Arslan"), ("Burak", "Doğan"), ("Merve", "Koç"), ("Can", "Kurt"),
    ];

    public async Task SeedAsync()
    {
        using var _ = db.User.WithoutTenant();
        await SeedPermissionsAsync();
        await SeedCurrenciesAsync();

        var pulse = await CompanyAsync("PULSE", "Pulse Teknoloji", [("MERKEZ", "Genel Merkez", "İstanbul"), ("ANK", "Ankara Şube", "Ankara"), ("IZM", "İzmir Şube", "İzmir")]);
        var logistics = await CompanyAsync("PULSE-LOJ", "Pulse Lojistik", [("MERKEZ", "Merkez Depo", "Kocaeli")]);
        var pulseRoles = await bootstrap.InitializeAsync(pulse);
        var logisticsRoles = await bootstrap.InitializeAsync(logistics);
        var hq = await db.Branches.FirstAsync(b => b.CompanyId == pulse.Id && b.Code == "MERKEZ");

        var ctx = new SeedContext { PulseId = pulse.Id, LogisticsId = logistics.Id, HqBranchId = hq.Id, PulseRoles = pulseRoles };
        var admin = await UserAsync("admin@enterprisepulse.dev", "Admin123!", "Sistem", "Yöneticisi", true,
            [(pulse.Id, pulseRoles["admin"], true, hq.Id), (logistics.Id, logisticsRoles["admin"], false, null)]);
        var manager = await UserAsync("manager@enterprisepulse.dev", "Manager123!", "Deniz", "Aksoy", false,
            [(pulse.Id, pulseRoles["manager"], true, hq.Id), (logistics.Id, logisticsRoles["manager"], false, null)]);
        var employee = await UserAsync("employee@enterprisepulse.dev", "Employee123!", "Kerem", "Polat", false,
            [(pulse.Id, pulseRoles["employee"], true, hq.Id)]);
        ctx.AdminId = admin.Id;
        ctx.UsersByName["Deniz Aksoy"] = manager.Id;
        ctx.UsersByName["Kerem Polat"] = employee.Id;
        foreach (var (first, last) in SampleEmployees)
        {
            var user = await UserAsync($"{Slug(first)}.{Slug(last)}@enterprisepulse.dev", "Employee123!", first, last, false, [(pulse.Id, pulseRoles["employee"], true, null)]);
            ctx.UsersByName[$"{first} {last}"] = user.Id;
        }

        foreach (var step in steps.OrderBy(s => s.Order))
        {
            log.LogInformation("Seed: {Step}", step.GetType().Name);
            db.ChangeTracker.Clear();
            using (db.User.ActAs(pulse.Id, admin.Id, superAdmin: true))
            {
                await step.RunAsync(ctx);
            }
        }
    }

    public static string Slug(string value) => value.ToLowerInvariant()
        .Replace('ı', 'i').Replace('ş', 's').Replace('ğ', 'g').Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');

    private async Task SeedPermissionsAsync()
    {
        var existing = await db.Permissions.Select(p => p.Code).ToHashSetAsync();
        foreach (var module in meta.Document.Permissions)
        {
            foreach (var action in module.Actions.Where(a => !existing.Contains(a.Code)))
            {
                db.Permissions.Add(new Permission { Code = action.Code, Module = module.Module, Action = action.Action, Description = $"{module.Label.Tr} - {action.Label.Tr}" });
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task SeedCurrenciesAsync()
    {
        (string Code, string Name, string Symbol, decimal Buy, decimal Sell)[] currencies =
            [("TRY", "Türk Lirası", "₺", 1, 1), ("USD", "US Dollar", "$", 41.2m, 41.35m), ("EUR", "Euro", "€", 47.9m, 48.1m), ("GBP", "British Pound", "£", 55.1m, 55.4m)];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var c in currencies)
        {
            if (!await db.Currencies.AnyAsync(x => x.Code == c.Code)) db.Currencies.Add(new Currency { Code = c.Code, Name = c.Name, Symbol = c.Symbol });
            if (c.Code != "TRY" && !await db.ExchangeRates.AnyAsync(x => x.CurrencyCode == c.Code && x.RateDate == today))
            {
                db.ExchangeRates.Add(new ExchangeRate { CurrencyCode = c.Code, RateDate = today, Buying = c.Buy, Selling = c.Sell });
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task<Company> CompanyAsync(string code, string name, (string Code, string Name, string City)[] branches)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Code == code);
        if (company == null)
        {
            company = new Company { Code = code, Name = name, LegalName = $"{name} A.Ş.", City = branches[0].City, Email = $"info@{Slug(code)}.dev" };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
        }
        foreach (var b in branches.Where(b => !db.Branches.Any(x => x.CompanyId == company.Id && x.Code == b.Code)))
        {
            db.Branches.Add(new Branch { CompanyId = company.Id, Code = b.Code, Name = b.Name, City = b.City });
        }
        await db.SaveChangesAsync();
        return company;
    }

    private async Task<User> UserAsync(string email, string password, string first, string last, bool superAdmin, (Guid CompanyId, Guid RoleId, bool IsDefault, Guid? BranchId)[] memberships)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
        {
            user = new User { Email = email, PasswordHash = PasswordHasher.Hash(password), FirstName = first, LastName = last, IsSuperAdmin = superAdmin };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        foreach (var m in memberships)
        {
            if (!await db.UserCompanies.AnyAsync(x => x.UserId == user.Id && x.CompanyId == m.CompanyId))
            {
                db.UserCompanies.Add(new UserCompany { UserId = user.Id, CompanyId = m.CompanyId, IsDefault = m.IsDefault, DefaultBranchId = m.BranchId });
            }
            if (!await db.UserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == m.RoleId))
            {
                db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = m.RoleId, CompanyId = m.CompanyId });
            }
        }
        await db.SaveChangesAsync();
        return user;
    }
}
