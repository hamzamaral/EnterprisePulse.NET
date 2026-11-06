using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;

namespace EnterprisePulse.Core.Modules.Assets;

public sealed record AssetHolderDto(AssigneeType Type, Guid AssignmentId, Guid Id, string Name);

public sealed record AssetDetailDto(
    Guid Id, string AssetTag, string Name, Guid CategoryId, string CategoryName, string? Brand, string? Model, string? SerialNumber,
    AssetStatus Status, AssetCondition Condition, Guid? LocationId, string? LocationName, AssetHolderDto? Holder,
    DateOnly? PurchaseDate, decimal? PurchaseCost, string Currency, decimal SalvageValue, int? UsefulLifeMonths, string? Supplier,
    string? InvoiceNo, DateOnly? WarrantyEndDate, string? WarrantyState, string? Notes, DateTime? RetiredAt, string? RetireReason,
    DepreciationResult? Depreciation);

public sealed record RetireAssetRequest(AssetStatus Status, string? Reason);

public sealed record MaintenanceDto(
    Guid Id, MaintenanceType Type, MaintenanceStatus Status, string Title, string? Description, string? Vendor, decimal? Cost,
    DateOnly StartedAt, DateOnly? CompletedAt);

public sealed record CreateMaintenanceRequest(MaintenanceType Type, string Title, string? Description, string? Vendor, decimal? Cost, DateOnly StartedAt);

public sealed record CompleteMaintenanceRequest(DateOnly CompletedAt, decimal? Cost, string? Description);

/// <summary>Asset card (demirbaş kartı): detail with depreciation, QR label, retirement and maintenance.</summary>
[ModuleService]
public sealed class AssetService(AppDbContext db, IOptions<AppOptions> options)
{
    public static readonly AssetStatus[] OutOfService = [AssetStatus.RETIRED, AssetStatus.LOST];

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

    public async Task<Asset> GetAsync(Guid id) =>
        Guard.Found(await db.Assets.FirstOrDefaultAsync(a => a.Id == id), "Demirbaş bulunamadı");

    public async Task<AssetDetailDto> DetailAsync(Guid id)
    {
        var a = Guard.Found(await db.Assets.AsNoTracking().Include(x => x.Category).Include(x => x.Location).FirstOrDefaultAsync(x => x.Id == id),
            "Demirbaş bulunamadı");
        var holder = (await HoldersAsync([a.Id])).GetValueOrDefault(a.Id);
        var life = a.UsefulLifeMonths ?? a.Category.UsefulLifeMonths;
        var depreciation = a.PurchaseDate is { } bought && a.PurchaseCost is { } cost
            ? Depreciation.Calculate(cost, a.SalvageValue, life, bought, Today)
            : null;
        return new AssetDetailDto(a.Id, a.AssetTag, a.Name, a.CategoryId, a.Category.Name, a.Brand, a.Model, a.SerialNumber, a.Status, a.Condition,
            a.LocationId, a.Location?.Name, holder, a.PurchaseDate, a.PurchaseCost, a.Currency, a.SalvageValue, a.UsefulLifeMonths, a.Supplier,
            a.InvoiceNo, a.WarrantyEndDate, WarrantyState(a.WarrantyEndDate), a.Notes, a.RetiredAt, a.RetireReason, depreciation);
    }

    /// <summary>expired, expiring (60 days or less) or active.</summary>
    public static string? WarrantyState(DateOnly? end)
    {
        if (end == null) return null;
        var days = end.Value.DayNumber - Today.DayNumber;
        return days < 0 ? "expired" : days <= 60 ? "expiring" : "active";
    }

    /// <summary>Current holder of each asset: the active assignment's employee, department or location.</summary>
    public async Task<Dictionary<Guid, AssetHolderDto>> HoldersAsync(IReadOnlyCollection<Guid> assetIds)
    {
        var rows = await db.AssetAssignments.AsNoTracking()
            .Where(x => assetIds.Contains(x.AssetId) && x.Status == AssignmentStatus.ACTIVE)
            .Select(x => new
            {
                x.Id, x.AssetId, x.AssigneeType, x.EmployeeId, x.DepartmentId, x.LocationId,
                Employee = x.Employee == null ? null : x.Employee.FirstName + " " + x.Employee.LastName,
                Department = x.Department == null ? null : x.Department.Name,
                Location = x.Location == null ? null : x.Location.Name,
            }).ToListAsync();
        return rows.GroupBy(r => r.AssetId).ToDictionary(g => g.Key, g =>
        {
            var r = g.First();
            var (id, name) = r.AssigneeType switch
            {
                AssigneeType.EMPLOYEE => (r.EmployeeId, r.Employee),
                AssigneeType.DEPARTMENT => (r.DepartmentId, r.Department),
                _ => (r.LocationId, r.Location),
            };
            return new AssetHolderDto(r.AssigneeType, r.Id, id ?? Guid.Empty, name ?? "");
        });
    }

    public async Task<AssetDetailDto> RetireAsync(Guid id, RetireAssetRequest body)
    {
        var a = await GetAsync(id);
        Guard.That(OutOfService.Contains(body.Status), "Durum Hizmet dışı veya Kayıp olmalı");
        Guard.That((body.Reason?.Trim().Length ?? 0) >= 2, "Neden zorunludur");
        Guard.State(a.Status != AssetStatus.ASSIGNED, "Hizmet dışı bırakmadan önce demirbaşı iade alın");
        Guard.State(!OutOfService.Contains(a.Status), "Demirbaş zaten hizmet dışı");
        a.Status = body.Status;
        a.RetiredAt = DateTime.UtcNow;
        a.RetireReason = body.Reason!.Trim();
        await db.SaveChangesAsync();
        return await DetailAsync(id);
    }

    public async Task<List<MaintenanceDto>> MaintenancesAsync(Guid assetId)
    {
        await GetAsync(assetId);
        return await db.AssetMaintenances.AsNoTracking().Where(m => m.AssetId == assetId).OrderByDescending(m => m.StartedAt).ThenByDescending(m => m.CreatedAt)
            .Select(m => new MaintenanceDto(m.Id, m.Type, m.Status, m.Title, m.Description, m.Vendor, m.Cost, m.StartedAt, m.CompletedAt)).ToListAsync();
    }

    /// <summary>Opening a maintenance record takes an in-stock asset out of circulation until it is completed.</summary>
    public async Task<MaintenanceDto> CreateMaintenanceAsync(Guid assetId, CreateMaintenanceRequest body)
    {
        var a = await GetAsync(assetId);
        Guard.State(!OutOfService.Contains(a.Status), "Demirbaş hizmet dışı");
        Guard.That(!string.IsNullOrWhiteSpace(body.Title), "Başlık zorunludur");
        Guard.That(body.Cost is null or >= 0, "Tutar negatif olamaz");
        var m = new AssetMaintenance
        {
            AssetId = assetId, Type = body.Type, Title = body.Title.Trim(), Description = Clean(body.Description), Vendor = Clean(body.Vendor),
            Cost = body.Cost, StartedAt = body.StartedAt, CreatedById = db.User.RequireUserId(),
        };
        db.AssetMaintenances.Add(m);
        if (a.Status == AssetStatus.IN_STOCK) a.Status = AssetStatus.IN_MAINTENANCE;
        await db.SaveChangesAsync();
        return ToDto(m);
    }

    public async Task<MaintenanceDto> CompleteMaintenanceAsync(Guid assetId, Guid maintenanceId, CompleteMaintenanceRequest body)
    {
        var a = await GetAsync(assetId);
        var m = Guard.Found(await db.AssetMaintenances.FirstOrDefaultAsync(x => x.Id == maintenanceId && x.AssetId == assetId), "Bakım kaydı bulunamadı");
        Guard.State(m.Status != MaintenanceStatus.COMPLETED, "Bakım zaten tamamlanmış");
        Guard.That(body.CompletedAt >= m.StartedAt, "Tamamlanma tarihi başlangıçtan önce olamaz");
        Guard.That(body.Cost is null or >= 0, "Tutar negatif olamaz");
        m.Status = MaintenanceStatus.COMPLETED;
        m.CompletedAt = body.CompletedAt;
        m.Cost = body.Cost ?? m.Cost;
        m.Description = Clean(body.Description) ?? m.Description;
        await db.SaveChangesAsync();
        var stillOpen = await db.AssetMaintenances.AnyAsync(x => x.AssetId == assetId && x.Status == MaintenanceStatus.OPEN);
        if (!stillOpen && a.Status == AssetStatus.IN_MAINTENANCE)
        {
            a.Status = AssetStatus.IN_STOCK;
            await db.SaveChangesAsync();
        }
        return ToDto(m);
    }

    /// <summary>PNG QR label that opens the asset card; the count screen also accepts the scanned URL.</summary>
    public async Task<(byte[] Png, string FileName)> LabelAsync(Guid id)
    {
        var a = await GetAsync(id);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode($"{options.Value.WebAppUrl.TrimEnd('/')}/r/assets/{a.Id}", QRCodeGenerator.ECCLevel.M);
        return (new PngByteQRCode(data).GetGraphic(8), $"{a.AssetTag}.png");
    }

    private static MaintenanceDto ToDto(AssetMaintenance m) =>
        new(m.Id, m.Type, m.Status, m.Title, m.Description, m.Vendor, m.Cost, m.StartedAt, m.CompletedAt);

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
