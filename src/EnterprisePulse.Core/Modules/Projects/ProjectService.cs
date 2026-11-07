using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Projects;

public sealed record SaveProject(string Name, string? Description, Guid? PartnerId, Guid ManagerId, DateOnly? StartDate, DateOnly? DueDate, decimal? BudgetAmount, decimal? ManagerHourlyCost);

public sealed record AddMember(Guid EmployeeId, ProjectMemberRole Role, decimal? HourlyCost);

public sealed record SaveTask(string Title, string? Description, ProjectTaskPriority Priority, Guid? AssigneeId, DateOnly? DueDate, decimal? EstimatedHours);

public sealed record MoveTask(ProjectTaskStatus Status);

public sealed record LogTime(Guid? TaskId, Guid? EmployeeId, DateOnly WorkDate, decimal Hours, string? Description);

public sealed record ProjectStatusChange(ProjectStatus Status);

public sealed record MemberDto(Guid Id, Guid EmployeeId, string EmployeeName, ProjectMemberRole Role, decimal? HourlyCost);

public sealed record TaskList(List<TaskDto> Items);
public sealed record TimeList(List<TimeDto> Items);

public sealed record TaskDto(Guid Id, int TaskNo, string Title, string? Description, ProjectTaskStatus Status, ProjectTaskPriority Priority, Guid? AssigneeId, string? AssigneeName, DateOnly? DueDate, decimal? EstimatedHours, int SortOrder);

public sealed record TimeDto(Guid Id, Guid EmployeeId, string EmployeeName, Guid? TaskId, int? TaskNo, DateOnly WorkDate, decimal Hours, decimal CostAmount, string? Description);

public sealed record ProjectDto(
    Guid Id, string Number, string Name, string? Description, Guid? PartnerId, string? PartnerTitle, Guid ManagerId, string ManagerName,
    ProjectStatus Status, DateOnly? StartDate, DateOnly? DueDate, decimal? BudgetAmount, decimal ExpenseCost, decimal LabourCost, decimal TotalCost, decimal? Remaining,
    List<MemberDto> Members, List<TaskDto> Tasks, List<TimeDto> Time);

/// <summary>Proje, pano görevleri ve saat maliyeti. Onaylı masraf proje maliyetine eklenir.</summary>
[ModuleService]
public sealed class ProjectService(AppDbContext db, NumberingService numbering)
{
    private CurrentUser Ctx => db.User;

    public async Task<ProjectDto> CreateAsync(SaveProject dto)
    {
        Guard.That(Ctx.Has("projects:create"), "Proje oluşturma yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Name), "Proje adı gerekli");
        Guard.That(dto.BudgetAmount is null or >= 0, "Bütçe negatif olamaz");
        Guard.That(dto.ManagerHourlyCost is null or >= 0, "Saat maliyeti negatif olamaz");
        var manager = await db.Employees.FirstOrDefaultAsync(e => e.Id == dto.ManagerId) ?? throw new NotFoundAppException("Proje yöneticisi bulunamadı");
        if (dto.PartnerId != null)
        {
            var partner = await db.BusinessPartners.FirstOrDefaultAsync(p => p.Id == dto.PartnerId) ?? throw new NotFoundAppException("Cari hesap bulunamadı");
            Guard.That(partner.Type is PartnerType.CUSTOMER or PartnerType.BOTH, "Proje yalnızca müşteri carisine bağlanır");
        }
        var project = new Project
        {
            Number = await numbering.NextAsync("PROJECT"),
            Name = dto.Name.Trim(),
            Description = Blank(dto.Description),
            PartnerId = dto.PartnerId,
            ManagerId = manager.Id,
            StartDate = dto.StartDate,
            DueDate = dto.DueDate,
            BudgetAmount = dto.BudgetAmount,
            CreatedById = Ctx.RequireUserId(),
        };
        project.Members.Add(new ProjectMember { EmployeeId = manager.Id, Role = ProjectMemberRole.LEAD, HourlyCost = dto.ManagerHourlyCost });
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return await GetAsync(project.Id);
    }

    public async Task<ProjectDto> ChangeStatusAsync(Guid id, ProjectStatus status)
    {
        Guard.That(Ctx.Has("projects:update"), "Proje güncelleme yetkiniz yok");
        var project = await OpenAsync(id);
        var ok = (project.Status, status) switch
        {
            (ProjectStatus.PLANNED, ProjectStatus.ACTIVE or ProjectStatus.CANCELLED) => true,
            (ProjectStatus.ACTIVE, ProjectStatus.ON_HOLD or ProjectStatus.COMPLETED or ProjectStatus.CANCELLED) => true,
            (ProjectStatus.ON_HOLD, ProjectStatus.ACTIVE or ProjectStatus.CANCELLED) => true,
            _ => false,
        };
        Guard.State(ok, "Proje bu duruma geçemez");
        project.Status = status;
        project.CompletedAt = status == ProjectStatus.COMPLETED ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<ProjectDto> AddMemberAsync(Guid id, AddMember dto)
    {
        Guard.That(Ctx.Has("projects:update"), "Proje güncelleme yetkiniz yok");
        await OpenAsync(id);
        Guard.That(dto.HourlyCost is null or >= 0, "Saat maliyeti negatif olamaz");
        Guard.That(await db.Employees.AnyAsync(e => e.Id == dto.EmployeeId), "Personel bulunamadı");
        Guard.That(!await db.ProjectMembers.AnyAsync(m => m.ProjectId == id && m.EmployeeId == dto.EmployeeId), "Personel projede zaten var");
        db.ProjectMembers.Add(new ProjectMember { ProjectId = id, EmployeeId = dto.EmployeeId, Role = dto.Role, HourlyCost = dto.HourlyCost });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<ProjectDto> AddTaskAsync(Guid id, SaveTask dto)
    {
        await EnsureCanEditAsync(id);
        Guard.That(!string.IsNullOrWhiteSpace(dto.Title), "Görev başlığı gerekli");
        Guard.That(dto.EstimatedHours is null or >= 0, "Tahmini süre negatif olamaz");
        if (dto.AssigneeId != null) await RequireMemberAsync(id, dto.AssigneeId.Value);
        var next = (await db.ProjectTasks.Where(t => t.ProjectId == id).MaxAsync(t => (int?)t.TaskNo) ?? 0) + 1;
        var sort = (await db.ProjectTasks.Where(t => t.ProjectId == id && t.Status == ProjectTaskStatus.TODO).MaxAsync(t => (int?)t.SortOrder) ?? 0) + 1;
        db.ProjectTasks.Add(new ProjectTask
        {
            ProjectId = id, TaskNo = next, Title = dto.Title.Trim(), Description = Blank(dto.Description), Priority = dto.Priority,
            AssigneeId = dto.AssigneeId, DueDate = dto.DueDate, EstimatedHours = dto.EstimatedHours, SortOrder = sort, CreatedById = Ctx.RequireUserId(),
        });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<ProjectDto> MoveTaskAsync(Guid taskId, ProjectTaskStatus status)
    {
        var task = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == taskId) ?? throw new NotFoundAppException("Görev bulunamadı");
        var mine = task.AssigneeId != null && await db.Employees.AnyAsync(e => e.Id == task.AssigneeId && e.UserId == Ctx.UserId);
        if (!mine) Guard.That(Ctx.Has("projects:update"), "Görevi yalnızca atanan kişi veya proje yetkilisi taşır");
        await OpenAsync(task.ProjectId);
        var sort = (await db.ProjectTasks.Where(t => t.ProjectId == task.ProjectId && t.Status == status).MaxAsync(t => (int?)t.SortOrder) ?? 0) + 1;
        task.Status = status;
        task.SortOrder = sort;
        task.CompletedAt = status == ProjectTaskStatus.DONE ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
        return await GetAsync(task.ProjectId);
    }

    public async Task<ProjectDto> LogTimeAsync(Guid id, LogTime dto)
    {
        var project = await OpenAsync(id);
        Guard.That(dto.Hours > 0 && dto.Hours <= 24, "Süre 0 ile 24 saat arasında olmalı");
        Guard.That(dto.WorkDate <= DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), "Çalışma tarihi ileri olamaz");
        var employeeId = dto.EmployeeId;
        if (employeeId == null)
        {
            employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync()
                ?? throw new NotFoundAppException("Personel kaydınız yok");
        }
        else if (!await db.Employees.AnyAsync(e => e.Id == employeeId && e.UserId == Ctx.UserId))
        {
            Guard.That(Ctx.Has("projects:update"), "Başkası adına süre girme yetkiniz yok");
        }
        var member = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == id && m.EmployeeId == employeeId)
            ?? throw new NotFoundAppException("Süre giren kişi projenin üyesi değil");
        if (dto.TaskId != null) Guard.That(await db.ProjectTasks.AnyAsync(t => t.Id == dto.TaskId && t.ProjectId == id), "Görev bu projede değil");
        var cost = Math.Round(dto.Hours * (member.HourlyCost ?? 0), 2, MidpointRounding.AwayFromZero);
        db.TimeEntries.Add(new TimeEntry
        {
            ProjectId = project.Id, TaskId = dto.TaskId, EmployeeId = member.EmployeeId, WorkDate = dto.WorkDate,
            Hours = dto.Hours, Description = Blank(dto.Description), CostAmount = cost,
        });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task AddExpenseCostAsync(Guid projectId, decimal delta)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId) ?? throw new NotFoundAppException("Proje bulunamadı");
        project.ExpenseCost = Math.Round(project.ExpenseCost + delta, 2, MidpointRounding.AwayFromZero);
        Guard.That(project.ExpenseCost >= 0, "Proje masraf maliyeti negatife düşemez");
    }

    public async Task<TaskList> MyTasksAsync()
    {
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (employeeId == null) return new TaskList([]);
        var tasks = await db.ProjectTasks.AsNoTracking().Include(t => t.Assignee).Include(t => t.Project)
            .Where(t => t.AssigneeId == employeeId && t.Status != ProjectTaskStatus.DONE && t.Project.Status != ProjectStatus.CANCELLED && t.Project.Status != ProjectStatus.COMPLETED)
            .OrderBy(t => t.DueDate).ToListAsync();
        return new TaskList(tasks.Select(MapTask).ToList());
    }

    public async Task<TimeList> TimeAsync(DateOnly? from, DateOnly? to)
    {
        var query = db.TimeEntries.AsNoTracking().Include(t => t.Employee).Include(t => t.Task).AsQueryable();
        if (!Ctx.Has("projects:read"))
        {
            var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
            query = query.Where(t => t.EmployeeId == employeeId);
        }
        if (from != null) query = query.Where(t => t.WorkDate >= from);
        if (to != null) query = query.Where(t => t.WorkDate <= to);
        return new TimeList((await query.OrderByDescending(t => t.WorkDate).Take(200).ToListAsync()).Select(MapTime).ToList());
    }

    public async Task<ProjectDto> GetAsync(Guid id)
    {
        var project = await db.Projects.AsNoTracking().Include(p => p.Partner).Include(p => p.Manager)
            .Include(p => p.Members).ThenInclude(m => m.Employee)
            .Include(p => p.Tasks).ThenInclude(t => t.Assignee)
            .Include(p => p.TimeEntries).ThenInclude(t => t.Employee)
            .Include(p => p.TimeEntries).ThenInclude(t => t.Task)
            .AsSplitQuery().FirstOrDefaultAsync(p => p.Id == id) ?? throw new NotFoundAppException("Proje bulunamadı");
        if (!Ctx.Has("projects:read") && project.Manager.UserId != Ctx.UserId && project.Members.All(m => m.Employee.UserId != Ctx.UserId))
            throw new NotFoundAppException("Proje bulunamadı");
        var labour = project.TimeEntries.Sum(t => t.CostAmount);
        var total = labour + project.ExpenseCost;
        return new ProjectDto(project.Id, project.Number, project.Name, project.Description, project.PartnerId, project.Partner?.Title,
            project.ManagerId, $"{project.Manager.FirstName} {project.Manager.LastName}", project.Status, project.StartDate, project.DueDate,
            project.BudgetAmount, project.ExpenseCost, labour, total, project.BudgetAmount == null ? null : project.BudgetAmount - total,
            project.Members.OrderBy(m => m.Employee.FirstName).Select(m => new MemberDto(m.Id, m.EmployeeId, $"{m.Employee.FirstName} {m.Employee.LastName}", m.Role, m.HourlyCost)).ToList(),
            project.Tasks.OrderBy(t => t.Status).ThenBy(t => t.SortOrder).Select(MapTask).ToList(),
            project.TimeEntries.OrderByDescending(t => t.WorkDate).Select(MapTime).ToList());
    }

    private async Task EnsureCanEditAsync(Guid id)
    {
        var project = await OpenAsync(id);
        var lead = project.Manager.UserId == Ctx.UserId || await db.ProjectMembers.AnyAsync(m => m.ProjectId == id && m.Role == ProjectMemberRole.LEAD && m.Employee.UserId == Ctx.UserId);
        if (!lead) Guard.That(Ctx.Has("projects:update"), "Görev ekleme yetkiniz yok");
    }

    private async Task<Project> OpenAsync(Guid id)
    {
        var project = await db.Projects.Include(p => p.Manager).FirstOrDefaultAsync(p => p.Id == id) ?? throw new NotFoundAppException("Proje bulunamadı");
        Guard.State(project.Status is not (ProjectStatus.COMPLETED or ProjectStatus.CANCELLED), "Kapanmış projede değişiklik yapılmaz");
        return project;
    }

    private async Task RequireMemberAsync(Guid projectId, Guid employeeId) =>
        Guard.That(await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.EmployeeId == employeeId), "Görev yalnızca proje üyesine atanır");

    private static TaskDto MapTask(ProjectTask t) => new(
        t.Id, t.TaskNo, t.Title, t.Description, t.Status, t.Priority, t.AssigneeId,
        t.Assignee == null ? null : $"{t.Assignee.FirstName} {t.Assignee.LastName}", t.DueDate, t.EstimatedHours, t.SortOrder);

    private static TimeDto MapTime(TimeEntry t) => new(
        t.Id, t.EmployeeId, $"{t.Employee.FirstName} {t.Employee.LastName}", t.TaskId, t.Task?.TaskNo, t.WorkDate, t.Hours, t.CostAmount, t.Description);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
