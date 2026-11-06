using System.Text.Json;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Seeding;

/// <summary>Everything a new company needs before its first document: taxes, units, series, roles, approvals.</summary>
public sealed class CompanyBootstrap(AppDbContext db, MetadataStore meta)
{
    public async Task<Dictionary<string, Guid>> InitializeAsync(Company company)
    {
        var companyId = company.Id;
        var year = DateTime.UtcNow.Year;
        using var _ = db.User.WithoutTenant();

        (string Code, string Name, decimal Rate, bool Default)[] taxes =
            [("KDV20", "KDV %20", 20, true), ("KDV10", "KDV %10", 10, false), ("KDV1", "KDV %1", 1, false), ("KDV0", "KDV İstisna", 0, false)];
        foreach (var t in taxes.Where(t => !db.TaxRates.Any(x => x.CompanyId == companyId && x.Code == t.Code)))
        {
            db.TaxRates.Add(new TaxRate { CompanyId = companyId, Code = t.Code, Name = t.Name, Rate = t.Rate, IsDefault = t.Default });
        }

        (string Code, string Name)[] units = [("ADET", "Adet"), ("KG", "Kilogram"), ("LT", "Litre"), ("M", "Metre"), ("M2", "Metrekare"), ("KOLI", "Koli"), ("SAAT", "Saat")];
        foreach (var u in units.Where(u => !db.Units.Any(x => x.CompanyId == companyId && x.Code == u.Code)))
        {
            db.Units.Add(new Unit { CompanyId = companyId, Code = u.Code, Name = u.Name });
        }

        foreach (var s in meta.Document.Sequences.Where(s => !db.NumberSequences.Any(x => x.CompanyId == companyId && x.Code == s.Code)))
        {
            db.NumberSequences.Add(new NumberSequence
            {
                CompanyId = companyId, Code = s.Code, Name = s.Name, Prefix = s.Prefix,
                IncludeYear = s.Yearly, ResetYearly = s.Yearly, CurrentYear = year,
            });
        }

        if (!await db.Settings.AnyAsync(x => x.CompanyId == companyId && x.Key == "general"))
        {
            db.Settings.Add(new Setting
            {
                CompanyId = companyId,
                Key = "general",
                Value = JsonSerializer.SerializeToDocument(new { dateFormat = "dd.MM.yyyy", timezone = "Europe/Istanbul", fiscalYearStartMonth = 1 }),
            });
        }
        await db.SaveChangesAsync();

        var permissions = await db.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id);
        var roles = new Dictionary<string, Guid>();
        foreach (var (code, def) in meta.Document.Roles)
        {
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Code == code);
            if (role == null)
            {
                role = new Role { CompanyId = companyId, Code = code, Name = def.Name, Description = def.Description, IsSystem = code == "admin" };
                db.Roles.Add(role);
            }
            db.RolePermissions.RemoveRange(role.Permissions);
            foreach (var c in def.Codes.Where(permissions.ContainsKey))
            {
                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[c] });
            }
            roles[code] = role.Id;
        }
        await db.SaveChangesAsync();

        await EnsureWorkflowAsync(companyId, "PURCHASE_REQUEST", "Satın Alma Talebi Onayı", "PurchaseRequest",
            "Önce müdür, ardından şirket yöneticisi onaylar", [("Müdür Onayı", roles["manager"]), ("Yönetici Onayı", roles["admin"])]);
        await EnsureWorkflowAsync(companyId, "LEAVE_REQUEST", "İzin Talebi Onayı", "LeaveRequest",
            "Yönetici onayı", [("Yönetici Onayı", roles["manager"])]);
        await EnsureWorkflowAsync(companyId, "EXPENSE_REPORT", "Masraf Formu Onayı", "ExpenseReport",
            "Müdür onayından sonra ödemeye hazır olur", [("Müdür Onayı", roles["manager"])]);
        return roles;
    }

    public async Task EnsureWorkflowAsync(Guid companyId, string code, string name, string entityType, string description, (string Name, Guid RoleId)[] steps)
    {
        if (await db.WorkflowDefinitions.AnyAsync(d => d.CompanyId == companyId && d.Code == code)) return;
        var definition = new WorkflowDefinition { CompanyId = companyId, Code = code, Name = name, EntityType = entityType, Description = description };
        var order = 1;
        foreach (var step in steps)
        {
            definition.Steps.Add(new WorkflowStep { Order = order++, Name = step.Name, ApproverType = ApproverType.ROLE, ApproverRoleId = step.RoleId });
        }
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
    }
}
