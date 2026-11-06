using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Assets;

public sealed record AssignAssetRequest(
    AssigneeType AssigneeType, Guid? EmployeeId, Guid? DepartmentId, Guid? LocationId, DateOnly AssignedAt, DateOnly? ExpectedReturnDate, string? Notes);

public sealed record ReturnAssetRequest(DateOnly ReturnedAt, AssetCondition ConditionIn, Guid? LocationId, string? ReturnNotes);

public sealed record AssignmentDto(
    Guid Id, string Number, Guid AssetId, string AssetTag, string AssetName, AssigneeType AssigneeType, Guid AssigneeId, string AssigneeName,
    AssignmentStatus Status, DateOnly AssignedAt, DateOnly? ExpectedReturnDate, AssetCondition ConditionOut, string? Notes, DateTime? AcknowledgedAt,
    DateOnly? ReturnedAt, AssetCondition? ConditionIn, string? ReturnNotes, string AssignedByName, string? ReturnedByName);

public sealed record DocumentAsset(string AssetTag, string Name, string CategoryName, string? Brand, string? Model, string? SerialNumber);

public sealed record DocumentAssignee(string Name, string? EmployeeNo, string? DepartmentName, string? PositionName);

/// <summary>Data of the printable hand-over form (zimmet tutanağı).</summary>
public sealed record AssignmentDocumentDto(
    AssignmentDto Assignment, string CompanyName, string? CompanyAddress, DocumentAsset Asset, DocumentAssignee Assignee, bool CanAcknowledge);

/// <summary>Zimmet: hand-over, return and transfer (devir) of assets, the holder's acknowledgement and "my assets".</summary>
[ModuleService]
public sealed class AssignmentService(AppDbContext db, NumberingService numbering, NotificationService notifications, AssetService assets)
{
    private IQueryable<AssetAssignment> Query => db.AssetAssignments.AsNoTracking()
        .Include(x => x.Asset).ThenInclude(a => a.Category)
        .Include(x => x.Employee).ThenInclude(e => e!.Department)
        .Include(x => x.Employee).ThenInclude(e => e!.Position)
        .Include(x => x.Department).Include(x => x.Location).Include(x => x.AssignedBy).Include(x => x.ReturnedBy);

    public async Task<List<AssignmentDto>> MineAsync()
    {
        var userId = db.User.RequireUserId();
        var rows = await Query.Where(x => x.Employee != null && x.Employee.UserId == userId)
            .OrderBy(x => x.Status).ThenByDescending(x => x.AssignedAt).ToListAsync();
        return rows.Select(ToDto).ToList();
    }

    public async Task<List<AssignmentDto>> ForAssetAsync(Guid assetId)
    {
        var rows = await Query.Where(x => x.AssetId == assetId).OrderByDescending(x => x.AssignedAt).ThenByDescending(x => x.CreatedAt).ToListAsync();
        return rows.Select(ToDto).ToList();
    }

    /// <summary>Visible to the holder or with assets:read.</summary>
    public async Task<AssignmentDocumentDto> DocumentAsync(Guid id)
    {
        var row = await Query.FirstOrDefaultAsync(x => x.Id == id);
        var isHolder = row?.Employee?.UserId is { } holder && holder == db.User.UserId;
        if (row == null || !(isHolder || db.User.Has("assets:read"))) throw new NotFoundAppException("Zimmet kaydı bulunamadı");
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == row.CompanyId);
        var address = string.Join(", ", new[] { company.Address, company.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var a = row.Asset;
        return new AssignmentDocumentDto(ToDto(row), company.LegalName ?? company.Name, address.Length == 0 ? null : address,
            new DocumentAsset(a.AssetTag, a.Name, a.Category.Name, a.Brand, a.Model, a.SerialNumber),
            new DocumentAssignee(AssigneeOf(row).Name, row.Employee?.EmployeeNo, row.Employee?.Department?.Name ?? row.Department?.Name, row.Employee?.Position?.Name),
            isHolder && row.Status == AssignmentStatus.ACTIVE && row.AcknowledgedAt == null);
    }

    public async Task<AssignmentDocumentDto> AcknowledgeAsync(Guid id)
    {
        var doc = await DocumentAsync(id);
        if (!doc.CanAcknowledge) throw new ForbiddenAppException("Yalnızca demirbaşı teslim alan personel bir kez onaylayabilir");
        await db.AssetAssignments.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AcknowledgedAt, DateTime.UtcNow));
        return await DocumentAsync(id);
    }

    public async Task<AssignmentDto> AssignAsync(Guid assetId, AssignAssetRequest body)
    {
        var asset = await assets.GetAsync(assetId);
        Guard.State(asset.Status == AssetStatus.IN_STOCK, "Yalnızca depodaki demirbaşlar zimmetlenebilir");
        var assignee = await ResolveAssigneeAsync(body);
        var row = await CreateAsync(asset, body, assignee);
        await db.SaveChangesAsync();
        await NotifyHolderAsync(row.Id);
        return await GetDtoAsync(row.Id);
    }

    public async Task<AssignmentDto> ReturnAsync(Guid assetId, ReturnAssetRequest body)
    {
        var asset = await assets.GetAsync(assetId);
        var active = await ActiveAsync(assetId);
        Guard.That(body.ReturnedAt >= active.AssignedAt, "İade tarihi zimmet tarihinden önce olamaz");
        if (body.LocationId != null) Guard.That(await db.AssetLocations.AnyAsync(l => l.Id == body.LocationId), "Lokasyon bulunamadı");
        active.Status = AssignmentStatus.RETURNED;
        active.ReturnedAt = body.ReturnedAt;
        active.ConditionIn = body.ConditionIn;
        active.ReturnNotes = AssetService.Clean(body.ReturnNotes);
        active.ReturnedById = db.User.RequireUserId();
        asset.Status = AssetStatus.IN_STOCK;
        asset.Condition = body.ConditionIn;
        asset.LocationId = body.LocationId ?? asset.LocationId;
        await db.SaveChangesAsync();
        return await GetDtoAsync(active.Id);
    }

    /// <summary>Closes the current assignment and opens a new one in a single save (devir).</summary>
    public async Task<AssignmentDto> TransferAsync(Guid assetId, AssignAssetRequest body)
    {
        var asset = await assets.GetAsync(assetId);
        var active = await ActiveAsync(assetId);
        var assignee = await ResolveAssigneeAsync(body);
        Guard.That(HolderId(active) != assignee.Id, "Demirbaş zaten bu sorumluya zimmetli");
        Guard.That(body.AssignedAt >= active.AssignedAt, "Devir tarihi mevcut zimmetten önce olamaz");
        active.Status = AssignmentStatus.RETURNED;
        active.ReturnedAt = body.AssignedAt;
        active.ConditionIn = asset.Condition;
        active.ReturnNotes = $"Devir: {assignee.Name}";
        active.ReturnedById = db.User.RequireUserId();
        await db.SaveChangesAsync();
        var row = await CreateAsync(asset, body, assignee);
        await db.SaveChangesAsync();
        await NotifyHolderAsync(row.Id);
        return await GetDtoAsync(row.Id);
    }

    private async Task<AssetAssignment> CreateAsync(Asset asset, AssignAssetRequest body, (Guid Id, string Name) assignee)
    {
        if (body.ExpectedReturnDate != null) Guard.That(body.ExpectedReturnDate >= body.AssignedAt, "Beklenen iade tarihi zimmet tarihinden önce olamaz");
        var row = new AssetAssignment
        {
            Number = await numbering.NextAsync("ASSET_ASSIGNMENT"),
            AssetId = asset.Id, AssigneeType = body.AssigneeType,
            EmployeeId = body.AssigneeType == AssigneeType.EMPLOYEE ? assignee.Id : null,
            DepartmentId = body.AssigneeType == AssigneeType.DEPARTMENT ? assignee.Id : null,
            LocationId = body.AssigneeType == AssigneeType.LOCATION ? assignee.Id : null,
            AssignedAt = body.AssignedAt, ExpectedReturnDate = body.ExpectedReturnDate, ConditionOut = asset.Condition,
            Notes = AssetService.Clean(body.Notes), AssignedById = db.User.RequireUserId(),
        };
        db.AssetAssignments.Add(row);
        asset.Status = AssetStatus.ASSIGNED;
        return row;
    }

    private async Task<AssetAssignment> ActiveAsync(Guid assetId) =>
        await db.AssetAssignments.FirstOrDefaultAsync(x => x.AssetId == assetId && x.Status == AssignmentStatus.ACTIVE)
        ?? throw new ConflictAppException("Demirbaş zimmetli değil");

    private static Guid? HolderId(AssetAssignment a) => a.AssigneeType switch
    {
        AssigneeType.EMPLOYEE => a.EmployeeId,
        AssigneeType.DEPARTMENT => a.DepartmentId,
        _ => a.LocationId,
    };

    private async Task<(Guid Id, string Name)> ResolveAssigneeAsync(AssignAssetRequest body)
    {
        switch (body.AssigneeType)
        {
            case AssigneeType.EMPLOYEE:
                var e = Guard.Found(await db.Employees.FirstOrDefaultAsync(x => x.Id == body.EmployeeId), "Personel bulunamadı");
                Guard.That(e.Status == EmployeeStatus.ACTIVE, "Demirbaş yalnızca aktif personele zimmetlenebilir");
                return (e.Id, $"{e.FirstName} {e.LastName}");
            case AssigneeType.DEPARTMENT:
                var d = Guard.Found(await db.Departments.FirstOrDefaultAsync(x => x.Id == body.DepartmentId), "Departman bulunamadı");
                return (d.Id, d.Name);
            default:
                var l = Guard.Found(await db.AssetLocations.FirstOrDefaultAsync(x => x.Id == body.LocationId), "Lokasyon bulunamadı");
                return (l.Id, l.Name);
        }
    }

    private async Task NotifyHolderAsync(Guid assignmentId)
    {
        var row = await Query.FirstAsync(x => x.Id == assignmentId);
        if (row.Employee?.UserId is not { } userId) return;
        await notifications.NotifyAsync(new NotifyRequest([userId], row.CompanyId, "assets.assigned", "Adınıza zimmet kaydı oluşturuldu",
            $"{row.Asset.AssetTag} · {row.Asset.Name} — teslim alındığını onaylayın.", $"/assets/assignments/{row.Id}"));
    }

    private async Task<AssignmentDto> GetDtoAsync(Guid id) => ToDto(await Query.FirstAsync(x => x.Id == id));

    public static (Guid Id, string Name) AssigneeOf(AssetAssignment row) => row.AssigneeType switch
    {
        AssigneeType.EMPLOYEE when row.Employee != null => (row.EmployeeId!.Value, $"{row.Employee.FirstName} {row.Employee.LastName}"),
        AssigneeType.DEPARTMENT when row.Department != null => (row.DepartmentId!.Value, row.Department.Name),
        _ => (row.LocationId ?? Guid.Empty, row.Location?.Name ?? ""),
    };

    private static AssignmentDto ToDto(AssetAssignment row)
    {
        var (id, name) = AssigneeOf(row);
        return new AssignmentDto(row.Id, row.Number, row.AssetId, row.Asset.AssetTag, row.Asset.Name, row.AssigneeType, id, name, row.Status,
            row.AssignedAt, row.ExpectedReturnDate, row.ConditionOut, row.Notes, row.AcknowledgedAt, row.ReturnedAt, row.ConditionIn, row.ReturnNotes,
            $"{row.AssignedBy.FirstName} {row.AssignedBy.LastName}", row.ReturnedBy == null ? null : $"{row.ReturnedBy.FirstName} {row.ReturnedBy.LastName}");
    }
}
