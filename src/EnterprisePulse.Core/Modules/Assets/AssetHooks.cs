using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Assets;

public sealed class AssetCategoriesHook(AppDbContext db) : IResourceHook
{
    public string Resource => "asset-categories";

    public Task BeforeSaveAsync(object entity, bool isNew)
    {
        var c = (AssetCategory)entity;
        c.Code = c.Code.Trim().ToUpperInvariant();
        Guard.That(c.UsefulLifeMonths is >= 1 and <= 600, "Faydalı ömür 1-600 ay arasında olmalı");
        return Task.CompletedTask;
    }

    public async Task BeforeDeleteAsync(object entity)
    {
        var c = (AssetCategory)entity;
        Guard.State(!await db.Assets.IgnoreQueryFilters().AnyAsync(a => a.CategoryId == c.Id), "Kategoride demirbaş var; silmek yerine pasifleştirin");
    }
}

public sealed class AssetLocationsHook(AppDbContext db) : IResourceHook
{
    public string Resource => "asset-locations";

    public Task BeforeSaveAsync(object entity, bool isNew)
    {
        var l = (AssetLocation)entity;
        l.Code = l.Code.Trim().ToUpperInvariant();
        return Task.CompletedTask;
    }

    public async Task BeforeDeleteAsync(object entity)
    {
        var l = (AssetLocation)entity;
        var used = await db.Assets.IgnoreQueryFilters().AnyAsync(a => a.LocationId == l.Id) || await db.AssetAssignments.AnyAsync(a => a.LocationId == l.Id);
        Guard.State(!used, "Lokasyon demirbaş veya zimmet kayıtlarında kullanılıyor; silmek yerine pasifleştirin");
    }
}

/// <summary>Status changes go through assign / return / maintenance / retire so the history stays consistent.</summary>
public sealed class AssetsHook(AppDbContext db) : IResourceHook
{
    public string Resource => "assets";

    public Task BeforeSaveAsync(object entity, bool isNew)
    {
        var a = (Asset)entity;
        if (isNew)
        {
            a.Status = AssetStatus.IN_STOCK;
            a.RetiredAt = null;
            a.RetireReason = null;
        }
        else
        {
            var entry = db.Entry(a);
            Guard.State(!AssetService.OutOfService.Contains(entry.Property(x => x.Status).OriginalValue), "Hizmet dışı demirbaş düzenlenemez");
            entry.Property(x => x.Status).CurrentValue = entry.Property(x => x.Status).OriginalValue;
            entry.Property(x => x.RetiredAt).CurrentValue = entry.Property(x => x.RetiredAt).OriginalValue;
            entry.Property(x => x.RetireReason).CurrentValue = entry.Property(x => x.RetireReason).OriginalValue;
        }
        Guard.That(a.PurchaseCost is null or >= 0 && a.SalvageValue >= 0, "Tutarlar negatif olamaz");
        Guard.That(a.PurchaseCost == null || a.SalvageValue <= a.PurchaseCost, "Hurda değeri alış bedelini aşamaz");
        Guard.That(a.UsefulLifeMonths is null or >= 1 and <= 600, "Faydalı ömür 1-600 ay arasında olmalı");
        Guard.That(a.WarrantyEndDate == null || a.PurchaseDate == null || a.WarrantyEndDate >= a.PurchaseDate, "Garanti bitişi alış tarihinden önce olamaz");
        return Task.CompletedTask;
    }

    public async Task BeforeDeleteAsync(object entity)
    {
        var a = (Asset)entity;
        Guard.State(!await db.AssetAssignments.AnyAsync(x => x.AssetId == a.Id), "Demirbaşın zimmet geçmişi var; silmek yerine hizmet dışı bırakın");
    }
}

/// <summary>Zimmet, maintenance and count records change only through their workflows (assign, complete, scan...).</summary>
public abstract class ManagedRecordHook(string resource, string message) : IResourceHook
{
    public string Resource => resource;

    public Task BeforeSaveAsync(object entity, bool isNew) => throw new ConflictAppException(message);

    public Task BeforeDeleteAsync(object entity) => throw new ConflictAppException(message);
}

public sealed class AssetAssignmentsHook() : ManagedRecordHook("asset-assignments", "Zimmetler demirbaş kartındaki Zimmetle / İade al / Devret işlemleriyle yönetilir");

public sealed class AssetMaintenancesHook() : ManagedRecordHook("asset-maintenances", "Bakım kayıtları demirbaş kartının Bakım sekmesinden yönetilir");

public sealed class AssetCountsHook() : ManagedRecordHook("asset-counts", "Sayımlar Demirbaş Sayımı ekranından yönetilir");

public sealed class AssetCountLinesHook() : ManagedRecordHook("asset-count-lines", "Sayım satırları sayım ekranında okutularak güncellenir");
