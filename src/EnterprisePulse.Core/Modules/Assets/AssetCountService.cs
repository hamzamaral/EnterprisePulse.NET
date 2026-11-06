using System.Text.RegularExpressions;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Assets;

public sealed record CreateAssetCountRequest(DateOnly CountDate, Guid? LocationId, Guid? CategoryId, string? Notes);

public sealed record ScanAssetRequest(string Code);

public sealed record UpdateCountLineRequest(bool? Found, AssetCondition? Condition, string? Note, bool ClearCondition = false);

public sealed record CompleteAssetCountRequest(bool ApplyConditions);

public sealed record AssetCountLineDto(
    Guid Id, Guid AssetId, string AssetTag, string AssetName, string CategoryName, string? SerialNumber, bool Expected, string? ExpectedHolder,
    bool Found, DateTime? FoundAt, string? FoundByName, AssetCondition? Condition, AssetCondition CurrentCondition, string? Note);

public sealed record AssetCountDetailDto(
    Guid Id, string Number, AssetCountStatus Status, DateOnly CountDate, Guid? LocationId, string? LocationName, Guid? CategoryId, string? CategoryName,
    int ExpectedCount, int FoundCount, int MissingCount, int UnexpectedCount, string CreatedByName, DateTime CreatedAt, DateTime? CompletedAt,
    string? Notes, List<AssetCountLineDto> Lines);

/// <summary>FOUND, ALREADY_FOUND or UNEXPECTED (scanned but outside the count's scope).</summary>
public sealed record AssetScanResultDto(string Result, AssetCountLineDto Line);

/// <summary>
/// Asset verification (demirbaş sayımı). Starting a count snapshots every in-service asset in scope with its current holder;
/// assets are then marked found by scanning their QR label, asset tag or serial number. Scanned assets outside the scope are unexpected.
/// </summary>
[ModuleService]
public sealed partial class AssetCountService(AppDbContext db, NumberingService numbering, AssetService assets)
{
    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidPattern();

    public async Task<AssetCountDetailDto> DetailAsync(Guid id)
    {
        var c = Guard.Found(await db.AssetCounts.AsNoTracking().Include(x => x.Location).Include(x => x.Category).Include(x => x.CreatedBy)
            .FirstOrDefaultAsync(x => x.Id == id), "Sayım bulunamadı");
        var lines = await LinesAsync(db.AssetCountLines.Where(l => l.CountId == id));
        lines = lines.OrderBy(l => l.AssetTag, StringComparer.Ordinal).ToList();
        var expected = lines.Where(l => l.Expected).ToList();
        var found = expected.Count(l => l.Found);
        return new AssetCountDetailDto(c.Id, c.Number, c.Status, c.CountDate, c.LocationId, c.Location?.Name, c.CategoryId, c.Category?.Name,
            expected.Count, found, expected.Count - found, lines.Count - expected.Count, $"{c.CreatedBy.FirstName} {c.CreatedBy.LastName}",
            c.CreatedAt, c.CompletedAt, c.Notes, lines);
    }

    public async Task<AssetCountDetailDto> CreateAsync(CreateAssetCountRequest body)
    {
        if (body.LocationId != null) Guard.That(await db.AssetLocations.AnyAsync(l => l.Id == body.LocationId), "Lokasyon bulunamadı");
        if (body.CategoryId != null) Guard.That(await db.AssetCategories.AnyAsync(c => c.Id == body.CategoryId), "Demirbaş kategorisi bulunamadı");
        var open = await db.AssetCounts.Where(c => c.Status == AssetCountStatus.DRAFT && c.LocationId == body.LocationId && c.CategoryId == body.CategoryId)
            .Select(c => c.Number).FirstOrDefaultAsync();
        Guard.State(open == null, $"Bu kapsam için {open} numaralı sayım hâlâ açık");

        var q = db.Assets.Where(a => !AssetService.OutOfService.Contains(a.Status));
        if (body.CategoryId != null) q = q.Where(a => a.CategoryId == body.CategoryId);
        if (body.LocationId != null)
        {
            q = q.Where(a => a.LocationId == body.LocationId
                || a.Assignments.Any(x => x.Status == AssignmentStatus.ACTIVE && x.LocationId == body.LocationId));
        }
        var inScope = await q.OrderBy(a => a.AssetTag).Select(a => new { a.Id, Location = a.Location == null ? null : a.Location.Name }).ToListAsync();
        Guard.That(inScope.Count > 0, "Bu kapsamda demirbaş yok");
        var holders = await assets.HoldersAsync(inScope.Select(a => a.Id).ToList());

        await using var tx = await db.Database.BeginTransactionAsync();
        var count = new AssetCount
        {
            Number = await numbering.NextAsync("ASSET_COUNT"), CountDate = body.CountDate, LocationId = body.LocationId, CategoryId = body.CategoryId,
            Notes = AssetService.Clean(body.Notes), CreatedById = db.User.RequireUserId(),
        };
        db.AssetCounts.Add(count);
        foreach (var a in inScope)
        {
            db.AssetCountLines.Add(new AssetCountLine { CountId = count.Id, AssetId = a.Id, ExpectedHolder = holders.GetValueOrDefault(a.Id)?.Name ?? a.Location });
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await DetailAsync(count.Id);
    }

    public async Task<AssetScanResultDto> ScanAsync(Guid id, ScanAssetRequest body)
    {
        var code = body.Code?.Trim() ?? "";
        Guard.That(code.Length is > 0 and <= 500, "Kod zorunludur");
        await using var tx = await db.Database.BeginTransactionAsync();
        await LockDraftAsync(id);
        var match = UuidPattern().Match(code);
        var upper = code.ToUpperInvariant();
        var asset = match.Success
            ? await db.Assets.FirstOrDefaultAsync(a => a.Id == Guid.Parse(match.Value))
            : await db.Assets.FirstOrDefaultAsync(a => a.AssetTag.ToUpper() == upper || (a.SerialNumber != null && a.SerialNumber.ToUpper() == upper));
        if (asset == null) throw new NotFoundAppException($"\"{code}\" ile eşleşen demirbaş yok");

        var line = await db.AssetCountLines.FirstOrDefaultAsync(l => l.CountId == id && l.AssetId == asset.Id);
        string result;
        if (line?.FoundAt != null) result = "ALREADY_FOUND";
        else
        {
            var userId = db.User.RequireUserId();
            if (line != null) result = "FOUND";
            else
            {
                var holder = (await assets.HoldersAsync([asset.Id])).GetValueOrDefault(asset.Id)?.Name
                    ?? await db.AssetLocations.Where(l => l.Id == asset.LocationId).Select(l => l.Name).FirstOrDefaultAsync();
                line = new AssetCountLine { CountId = id, AssetId = asset.Id, Expected = false, ExpectedHolder = holder };
                db.AssetCountLines.Add(line);
                result = "UNEXPECTED";
            }
            line.FoundAt = DateTime.UtcNow;
            line.FoundById = userId;
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
        return new AssetScanResultDto(result, await LineAsync(line.Id));
    }

    public async Task<AssetCountLineDto> UpdateLineAsync(Guid id, Guid lineId, UpdateCountLineRequest body)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await LockDraftAsync(id);
        var line = Guard.Found(await db.AssetCountLines.FirstOrDefaultAsync(l => l.Id == lineId && l.CountId == id), "Sayım satırı bulunamadı");
        Guard.That(!(body.Found == false && !line.Expected), "Beklenmeyen demirbaşı bulunmadı yapmak yerine sayımdan çıkarın");
        if (body.Condition != null) line.Condition = body.Condition;
        if (body.ClearCondition) line.Condition = null;
        if (body.Note != null) line.Note = AssetService.Clean(body.Note);
        if (body.Found == true && line.FoundAt == null)
        {
            line.FoundAt = DateTime.UtcNow;
            line.FoundById = db.User.RequireUserId();
        }
        if (body.Found == false)
        {
            line.FoundAt = null;
            line.FoundById = null;
            line.Condition = null;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await LineAsync(lineId);
    }

    public async Task RemoveLineAsync(Guid id, Guid lineId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await LockDraftAsync(id);
        var line = Guard.Found(await db.AssetCountLines.FirstOrDefaultAsync(l => l.Id == lineId && l.CountId == id), "Sayım satırı bulunamadı");
        Guard.That(!line.Expected, "Beklenen demirbaşlar sayımdan çıkarılamaz");
        db.AssetCountLines.Remove(line);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Optionally copies the conditions recorded during the count onto the asset cards.</summary>
    public async Task<AssetCountDetailDto> CompleteAsync(Guid id, CompleteAssetCountRequest body)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var count = await LockDraftAsync(id);
        if (body.ApplyConditions)
        {
            var lines = await db.AssetCountLines.Include(l => l.Asset).Where(l => l.CountId == id && l.FoundAt != null && l.Condition != null).ToListAsync();
            foreach (var l in lines.Where(l => l.Condition != l.Asset.Condition)) l.Asset.Condition = l.Condition!.Value;
        }
        count.Status = AssetCountStatus.COMPLETED;
        count.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await DetailAsync(id);
    }

    public async Task<AssetCountDetailDto> CancelAsync(Guid id)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var count = await LockDraftAsync(id);
        count.Status = AssetCountStatus.CANCELLED;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return await DetailAsync(id);
    }

    /// <summary>Locks the count row so concurrent scans and completion cannot interleave.</summary>
    private async Task<AssetCount> LockDraftAsync(Guid id)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM asset_counts WHERE id = {id} FOR UPDATE");
        var count = Guard.Found(await db.AssetCounts.FirstOrDefaultAsync(c => c.Id == id), "Sayım bulunamadı");
        Guard.State(count.Status == AssetCountStatus.DRAFT, "Yalnızca açık sayımlar değiştirilebilir");
        return count;
    }

    private async Task<AssetCountLineDto> LineAsync(Guid lineId) => (await LinesAsync(db.AssetCountLines.Where(l => l.Id == lineId))).Single();

    private static Task<List<AssetCountLineDto>> LinesAsync(IQueryable<AssetCountLine> q) =>
        q.AsNoTracking().Select(l => new AssetCountLineDto(l.Id, l.AssetId, l.Asset.AssetTag, l.Asset.Name, l.Asset.Category.Name, l.Asset.SerialNumber,
            l.Expected, l.ExpectedHolder, l.FoundAt != null, l.FoundAt, l.FoundBy == null ? null : l.FoundBy.FirstName + " " + l.FoundBy.LastName,
            l.Condition, l.Asset.Condition, l.Note)).ToListAsync();
}
