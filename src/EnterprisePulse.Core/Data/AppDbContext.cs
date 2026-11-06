using System.Linq.Expressions;
using System.Text.Json;
using EnterprisePulse.Core.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EnterprisePulse.Core.Data;

public partial class AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser user) : DbContext(options)
{
    /// <summary>Audit data itself, high-volume ledgers and secrets are not audited.</summary>
    private static readonly HashSet<string> SkippedAuditModels =
    [
        nameof(AuditLog), nameof(Notification), nameof(RefreshToken), nameof(PasswordResetToken), nameof(NumberSequence),
        nameof(StockBalance), nameof(LotBalance), nameof(StockTransaction), nameof(StockMovementLine),
        nameof(StockMovementSerial), nameof(StockCountLine), nameof(JournalLine),
    ];

    private static readonly HashSet<string> RedactedFields = ["PasswordHash", "TwoFactorSecret", "TokenHash"];

    public CurrentUser User => user;

    // Read by the query filters on every query, so the filter follows the current request.
    private bool BypassTenant => user.BypassTenant;
    private Guid? TenantId => user.CompanyId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        ConfigureGenerated(b);

        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.ToUniversalTime(), DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v.Value.ToUniversalTime(), DateTimeKind.Utc),
            v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTime)) property.SetValueConverter(utc);
                if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(utcNullable);
            }
            var method = typeof(AppDbContext).GetMethod(nameof(ApplyFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(entity.ClrType);
            method.Invoke(this, [b]);
        }
    }

    private void ApplyFilter<T>(ModelBuilder b) where T : class
    {
        var tenant = typeof(ITenantEntity).IsAssignableFrom(typeof(T));
        var soft = typeof(ISoftDelete).IsAssignableFrom(typeof(T));
        if (!tenant && !soft) return;

        Expression<Func<T, bool>> filter = (tenant, soft) switch
        {
            (true, true) => e => (BypassTenant || EF.Property<Guid>(e, "CompanyId") == TenantId) && EF.Property<DateTime?>(e, "DeletedAt") == null,
            (true, false) => e => BypassTenant || EF.Property<Guid>(e, "CompanyId") == TenantId,
            _ => e => EF.Property<DateTime?>(e, "DeletedAt") == null,
        };
        b.Entity<T>().HasQueryFilter(filter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var audits = new List<AuditLog>();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.State == EntityState.Added && entry.Entity is ITenantEntity tenant && tenant.CompanyId == Guid.Empty)
            {
                tenant.CompanyId = user.RequireCompanyId();
            }
            if (entry.State == EntityState.Modified && entry.Entity is IUpdatedAt updated) updated.UpdatedAt = now;
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDelete soft)
            {
                entry.State = EntityState.Modified;
                soft.DeletedAt = now;
            }
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && !SkippedAuditModels.Contains(entry.Metadata.ClrType.Name))
            {
                var audit = BuildAudit(entry);
                if (audit != null) audits.Add(audit);
            }
        }

        if (audits.Count > 0) Set<AuditLog>().AddRange(audits);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private AuditLog? BuildAudit(EntityEntry entry)
    {
        var action = entry.State switch
        {
            EntityState.Added => "create",
            EntityState.Deleted => "delete",
            _ when entry.Entity is ISoftDelete { DeletedAt: not null } && entry.Property("DeletedAt").IsModified => "delete",
            _ => "update",
        };

        Dictionary<string, object?>? oldValues = null;
        Dictionary<string, object?>? newValues = null;
        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (name is "UpdatedAt" or "CreatedAt") continue;
            var redact = RedactedFields.Contains(name);
            object? Clean(object? v) => redact && v != null ? "[REDACTED]" : v is JsonDocument doc ? doc.RootElement.Clone() : v;
            var key = char.ToLowerInvariant(name[0]) + name[1..];
            switch (entry.State)
            {
                case EntityState.Added:
                    (newValues ??= [])[key] = Clean(property.CurrentValue);
                    break;
                case EntityState.Deleted:
                    (oldValues ??= [])[key] = Clean(property.OriginalValue);
                    break;
                default:
                    if (!property.IsModified || Equals(property.OriginalValue, property.CurrentValue)) continue;
                    (oldValues ??= [])[key] = Clean(property.OriginalValue);
                    (newValues ??= [])[key] = Clean(property.CurrentValue);
                    break;
            }
        }
        if (entry.State == EntityState.Modified && newValues == null) return null;

        var companyId = entry.Entity is ITenantEntity t ? t.CompanyId : entry.Entity is Company c ? c.Id : user.CompanyId;
        return new AuditLog
        {
            CompanyId = companyId,
            UserId = user.UserId,
            UserEmail = user.Email,
            Action = action,
            EntityType = entry.Metadata.ClrType.Name,
            EntityId = entry.Entity is IEntity e ? e.Id.ToString() : null,
            OldValues = oldValues == null ? null : JsonSerializer.SerializeToDocument(oldValues),
            NewValues = newValues == null ? null : JsonSerializer.SerializeToDocument(newValues),
            Ip = user.Ip,
            UserAgent = user.UserAgent,
            RequestId = user.RequestId,
        };
    }
}
