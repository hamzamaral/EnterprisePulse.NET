using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Hr;

public sealed record OrgChartNode(Guid Id, string FullName, string? PositionName, string? DepartmentName, string? Email, List<OrgChartNode> Children);

public sealed record TerminateRequest(DateOnly TerminationDate, string? Reason);

public sealed record MyEmployeeDto(
    Guid Id, string EmployeeNo, string FullName, string? Email, string? Phone, DateOnly HireDate, string? DepartmentName,
    string? PositionName, string? ManagerName, string? BranchName, EmploymentType EmploymentType);

[ModuleService]
public sealed class EmployeeService(AppDbContext db)
{
    /// <summary>Reporting hierarchy of active employees built from the manager links; visible to every employee.</summary>
    public async Task<List<OrgChartNode>> OrgChartAsync()
    {
        var rows = await db.Employees.AsNoTracking().Where(e => e.Status == EmployeeStatus.ACTIVE)
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new { e.Id, e.FirstName, e.LastName, e.ManagerId, e.Email, Position = e.Position!.Name, Department = e.Department!.Name })
            .ToListAsync();
        var nodes = rows.ToDictionary(r => r.Id, r => new OrgChartNode(r.Id, $"{r.FirstName} {r.LastName}", r.Position, r.Department, r.Email, []));
        var roots = new List<OrgChartNode>();
        foreach (var r in rows)
        {
            if (r.ManagerId is { } m && nodes.TryGetValue(m, out var parent)) parent.Children.Add(nodes[r.Id]);
            else roots.Add(nodes[r.Id]);
        }
        return roots;
    }

    public async Task<MyEmployeeDto> MineAsync()
    {
        var userId = db.User.RequireUserId();
        var e = await db.Employees.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => new MyEmployeeDto(x.Id, x.EmployeeNo, x.FirstName + " " + x.LastName, x.Email, x.Phone, x.HireDate,
                x.Department!.Name, x.Position!.Name, x.Manager == null ? null : x.Manager.FirstName + " " + x.Manager.LastName, x.Branch!.Name, x.EmploymentType))
            .FirstOrDefaultAsync();
        return e ?? throw new NotFoundAppException("Hesabınıza bağlı bir personel kaydı yok");
    }

    public async Task<Employee> TerminateAsync(Guid id, TerminateRequest body)
    {
        var e = Guard.Found(await db.Employees.FirstOrDefaultAsync(x => x.Id == id), "Personel bulunamadı");
        Guard.State(e.Status != EmployeeStatus.TERMINATED, "Personelin çıkışı zaten yapılmış");
        Guard.That(body.TerminationDate >= e.HireDate, "Çıkış tarihi işe giriş tarihinden önce olamaz");
        var held = await db.AssetAssignments.CountAsync(a => a.EmployeeId == id && a.Status == AssignmentStatus.ACTIVE);
        Guard.State(held == 0, $"Personelin üzerinde {held} zimmetli demirbaş var; çıkıştan önce iade alın");
        e.Status = EmployeeStatus.TERMINATED;
        e.TerminationDate = body.TerminationDate;
        e.TerminationReason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim();
        await db.SaveChangesAsync();
        return e;
    }
}
