using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Workflow;

public sealed record WorkflowFinished(Guid InstanceId, Guid CompanyId, string DefinitionCode, string EntityType, string EntityId, Guid ActorId, WorkflowStatus Status, string? Comment);

/// <summary>Business modules react to approvals of their documents (leave, expense, purchase request...).</summary>
public interface IWorkflowListener
{
    string EntityType { get; }
    Task OnFinishedAsync(WorkflowFinished e);
}

public sealed record StartWorkflowRequest(string DefinitionCode, string EntityId, string Title);

public sealed record WorkflowActionDto(Guid Id, int StepOrder, WorkflowActionType Type, Guid UserId, string UserName, string? Comment, DateTime CreatedAt);

public sealed record WorkflowStepDto(Guid Id, int Order, string Name, ApproverType ApproverType, Guid? ApproverRoleId, Guid? ApproverUserId, string ApproverName);

public sealed record WorkflowInstanceDto(
    Guid Id, Guid DefinitionId, string DefinitionName, string EntityType, string EntityId, string Title, WorkflowStatus Status,
    int CurrentStepOrder, string? CurrentStepName, int TotalSteps, Guid StartedById, string StartedByName, DateTime StartedAt, DateTime? CompletedAt,
    bool CanAct = false, List<WorkflowStepDto>? Steps = null, List<WorkflowActionDto>? Actions = null);

/// <summary>Generic multi-step approval engine shared by every module.</summary>
public sealed class WorkflowService(AppDbContext db, NotificationService notifications, IEnumerable<IWorkflowListener> listeners)
{
    private CurrentUser Ctx => db.User;

    public async Task<WorkflowInstanceDto> StartAsync(StartWorkflowRequest request)
    {
        var userId = Ctx.RequireUserId();
        var definition = await db.WorkflowDefinitions.Include(d => d.Steps)
            .FirstOrDefaultAsync(d => d.Code == request.DefinitionCode && d.IsActive)
            ?? throw new NotFoundAppException($"Onay akışı tanımlı değil: {request.DefinitionCode}");
        Guard.That(definition.Steps.Count > 0, "Onay akışında adım yok");
        var running = await db.WorkflowInstances.AnyAsync(i => i.EntityType == definition.EntityType && i.EntityId == request.EntityId && i.Status == WorkflowStatus.PENDING);
        Guard.State(!running, "Bu kayıt için devam eden bir onay süreci var");

        var instance = new WorkflowInstance
        {
            DefinitionId = definition.Id,
            EntityType = definition.EntityType,
            EntityId = request.EntityId,
            Title = request.Title,
            CurrentStepOrder = 1,
            StartedById = userId,
        };
        instance.Actions.Add(new WorkflowAction { StepOrder = 0, Type = WorkflowActionType.START, UserId = userId });
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        await NotifyApproversAsync(instance, definition.Steps.OrderBy(s => s.Order).First());
        return await GetAsync(instance.Id);
    }

    public async Task<WorkflowInstanceDto> ApproveAsync(Guid id, string? comment)
    {
        var instance = await PendingAsync(id);
        var steps = instance.Definition.Steps.OrderBy(s => s.Order).ToList();
        var step = Current(instance);
        await AssertCanActAsync(step);
        var isLast = step.Order == steps.Count;
        await TransitionAsync(instance, WorkflowActionType.APPROVE, comment, i =>
        {
            i.CurrentStepOrder = isLast ? step.Order : step.Order + 1;
            i.Status = isLast ? WorkflowStatus.APPROVED : WorkflowStatus.PENDING;
            i.CompletedAt = isLast ? DateTime.UtcNow : null;
        });
        if (isLast)
        {
            await FinishAsync(instance, comment);
            await notifications.NotifyAsync(new NotifyRequest([instance.StartedById], instance.CompanyId, "workflow.approved",
                "Onay tamamlandı", $"\"{instance.Title}\" onaylandı.", $"/workflow/instances/{instance.Id}"));
        }
        else
        {
            await NotifyApproversAsync(instance, steps.First(s => s.Order == step.Order + 1));
        }
        return await GetAsync(id);
    }

    public async Task<WorkflowInstanceDto> RejectAsync(Guid id, string comment)
    {
        Guard.That(!string.IsNullOrWhiteSpace(comment), "Ret gerekçesi zorunludur");
        var instance = await PendingAsync(id);
        await AssertCanActAsync(Current(instance));
        await TransitionAsync(instance, WorkflowActionType.REJECT, comment, i =>
        {
            i.Status = WorkflowStatus.REJECTED;
            i.CompletedAt = DateTime.UtcNow;
        });
        await FinishAsync(instance, comment);
        await notifications.NotifyAsync(new NotifyRequest([instance.StartedById], instance.CompanyId, "workflow.rejected",
            "Onay reddedildi", $"\"{instance.Title}\" reddedildi: {comment}", $"/workflow/instances/{instance.Id}"));
        return await GetAsync(id);
    }

    public async Task<WorkflowInstanceDto> CancelAsync(Guid id, string? comment)
    {
        var instance = await PendingAsync(id);
        Guard.State(instance.StartedById == Ctx.UserId || Ctx.IsSuperAdmin, "Yalnızca talep sahibi iptal edebilir");
        await TransitionAsync(instance, WorkflowActionType.CANCEL, comment, i =>
        {
            i.Status = WorkflowStatus.CANCELLED;
            i.CompletedAt = DateTime.UtcNow;
        });
        await FinishAsync(instance, comment);
        return await GetAsync(id);
    }

    /// <summary>Cancels the pending approval of a document (the module withdrew or deleted it).</summary>
    public async Task CancelForEntityAsync(string entityType, string entityId)
    {
        var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.EntityType == entityType && i.EntityId == entityId && i.Status == WorkflowStatus.PENDING);
        if (instance == null) return;
        instance.Status = WorkflowStatus.CANCELLED;
        instance.CompletedAt = DateTime.UtcNow;
        db.WorkflowActions.Add(new WorkflowAction { InstanceId = instance.Id, StepOrder = instance.CurrentStepOrder, Type = WorkflowActionType.CANCEL, UserId = Ctx.RequireUserId() });
        await db.SaveChangesAsync();
    }

    public async Task<Paged<WorkflowInstanceDto>> InboxAsync(int page, int pageSize)
    {
        var roleIds = await RoleIdsAsync();
        var pending = await db.WorkflowInstances.Include(i => i.Definition).ThenInclude(d => d.Steps)
            .Where(i => i.Status == WorkflowStatus.PENDING).OrderBy(i => i.StartedAt).ToListAsync();
        var mine = pending.Where(i => IsApprover(Current(i), roleIds)).ToList();
        var dtos = await ToDtosAsync(mine.Skip((page - 1) * pageSize).Take(pageSize).ToList());
        return Paged<WorkflowInstanceDto>.Of(dtos, mine.Count, page, pageSize);
    }

    public async Task<int> InboxCountAsync()
    {
        var roleIds = await RoleIdsAsync();
        var pending = await db.WorkflowInstances.Include(i => i.Definition).ThenInclude(d => d.Steps)
            .Where(i => i.Status == WorkflowStatus.PENDING).ToListAsync();
        return pending.Count(i => IsApprover(Current(i), roleIds));
    }

    public async Task<Paged<WorkflowInstanceDto>> ListAsync(WorkflowStatus? status, string? entityType, bool mine, string? search, int page, int pageSize)
    {
        if (!mine) Ctx.Require("workflow:read");
        var q = db.WorkflowInstances.Include(i => i.Definition).ThenInclude(d => d.Steps).AsQueryable();
        if (status != null) q = q.Where(i => i.Status == status);
        if (entityType != null) q = q.Where(i => i.EntityType == entityType);
        if (mine) q = q.Where(i => i.StartedById == Ctx.UserId);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(i => EF.Functions.ILike(i.Title, $"%{search}%"));
        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(i => i.StartedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Paged<WorkflowInstanceDto>.Of(await ToDtosAsync(rows), total, page, pageSize);
    }

    public async Task<WorkflowInstanceDto> GetAsync(Guid id)
    {
        var instance = await db.WorkflowInstances.Include(i => i.Definition).ThenInclude(d => d.Steps).Include(i => i.Actions)
            .FirstOrDefaultAsync(i => i.Id == id) ?? throw new NotFoundAppException("Onay süreci bulunamadı");
        var roleIds = await RoleIdsAsync();
        var canSee = Ctx.Has("workflow:read") || instance.StartedById == Ctx.UserId || instance.Actions.Any(a => a.UserId == Ctx.UserId)
            || (instance.Status == WorkflowStatus.PENDING && IsApprover(Current(instance), roleIds));
        if (!canSee) throw new ForbiddenAppException();
        var dto = (await ToDtosAsync([instance]))[0];
        var names = await NamesAsync(instance.Actions.Select(a => a.UserId).Concat(instance.Definition.Steps.Where(s => s.ApproverUserId != null).Select(s => s.ApproverUserId!.Value)));
        var roleNames = await db.Roles.Where(r => instance.Definition.Steps.Select(s => s.ApproverRoleId).Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);
        return dto with
        {
            CanAct = instance.Status == WorkflowStatus.PENDING && IsApprover(Current(instance), roleIds),
            Steps = instance.Definition.Steps.OrderBy(s => s.Order).Select(s => new WorkflowStepDto(s.Id, s.Order, s.Name, s.ApproverType, s.ApproverRoleId, s.ApproverUserId,
                s.ApproverType == ApproverType.ROLE ? roleNames.GetValueOrDefault(s.ApproverRoleId ?? Guid.Empty, "") : names.GetValueOrDefault(s.ApproverUserId ?? Guid.Empty, ""))).ToList(),
            Actions = instance.Actions.OrderBy(a => a.CreatedAt).Select(a => new WorkflowActionDto(a.Id, a.StepOrder, a.Type, a.UserId, names.GetValueOrDefault(a.UserId, ""), a.Comment, a.CreatedAt)).ToList(),
        };
    }

    /// <summary>Latest instance of a document, used by module detail pages.</summary>
    public async Task<WorkflowInstanceDto?> ForEntityAsync(string entityType, string entityId)
    {
        var id = await db.WorkflowInstances.Where(i => i.EntityType == entityType && i.EntityId == entityId)
            .OrderByDescending(i => i.StartedAt).Select(i => (Guid?)i.Id).FirstOrDefaultAsync();
        return id == null ? null : await GetAsync(id.Value);
    }

    private async Task<WorkflowInstance> PendingAsync(Guid id)
    {
        var instance = await db.WorkflowInstances.Include(i => i.Definition).ThenInclude(d => d.Steps).FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new NotFoundAppException("Onay süreci bulunamadı");
        Guard.State(instance.Status == WorkflowStatus.PENDING, "Onay süreci zaten sonuçlanmış");
        return instance;
    }

    /// <summary>Optimistic concurrency: two approvers clicking at once cannot both move the instance.</summary>
    private async Task TransitionAsync(WorkflowInstance instance, WorkflowActionType type, string? comment, Action<WorkflowInstance> change)
    {
        var expectedStep = instance.CurrentStepOrder;
        await using var tx = await db.Database.BeginTransactionAsync();
        var moved = await db.WorkflowInstances
            .Where(i => i.Id == instance.Id && i.Status == WorkflowStatus.PENDING && i.CurrentStepOrder == expectedStep)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.CurrentStepOrder, i => i.CurrentStepOrder));
        Guard.State(moved == 1, "Onay süreci başka biri tarafından güncellendi, sayfayı yenileyin");
        change(instance);
        db.WorkflowActions.Add(new WorkflowAction { InstanceId = instance.Id, StepOrder = expectedStep, Type = type, UserId = Ctx.RequireUserId(), Comment = comment });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private async Task FinishAsync(WorkflowInstance instance, string? comment)
    {
        var e = new WorkflowFinished(instance.Id, instance.CompanyId, instance.Definition.Code, instance.EntityType, instance.EntityId, Ctx.RequireUserId(), instance.Status, comment);
        foreach (var listener in listeners.Where(l => l.EntityType == instance.EntityType)) await listener.OnFinishedAsync(e);
    }

    private static WorkflowStep Current(WorkflowInstance i) =>
        i.Definition.Steps.FirstOrDefault(s => s.Order == i.CurrentStepOrder) ?? throw new ConflictAppException("Akış tanımında mevcut adım yok");

    private bool IsApprover(WorkflowStep step, HashSet<Guid> roleIds) =>
        Ctx.IsSuperAdmin || (step.ApproverType == ApproverType.USER ? step.ApproverUserId == Ctx.UserId : step.ApproverRoleId != null && roleIds.Contains(step.ApproverRoleId.Value));

    private async Task AssertCanActAsync(WorkflowStep step)
    {
        if (!IsApprover(step, await RoleIdsAsync())) throw new ForbiddenAppException("Bu adımın onaycısı değilsiniz");
    }

    private async Task<HashSet<Guid>> RoleIdsAsync()
    {
        var userId = Ctx.UserId;
        return (await db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync()).ToHashSet();
    }

    private async Task NotifyApproversAsync(WorkflowInstance instance, WorkflowStep step)
    {
        var userIds = step.ApproverType == ApproverType.USER
            ? [step.ApproverUserId!.Value]
            : await db.UserRoles.Where(ur => ur.RoleId == step.ApproverRoleId && ur.User.IsActive).Select(ur => ur.UserId).ToListAsync();
        await notifications.NotifyAsync(new NotifyRequest(userIds, instance.CompanyId, "workflow.approval_requested",
            "Onayınızı bekleyen talep", $"\"{instance.Title}\" — {step.Name}", $"/workflow/instances/{instance.Id}", Email: true));
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.IgnoreQueryFilters().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName);
    }

    private async Task<List<WorkflowInstanceDto>> ToDtosAsync(List<WorkflowInstance> rows)
    {
        var names = await NamesAsync(rows.Select(r => r.StartedById));
        return rows.Select(r => new WorkflowInstanceDto(r.Id, r.DefinitionId, r.Definition.Name, r.EntityType, r.EntityId, r.Title, r.Status,
            r.CurrentStepOrder, r.Definition.Steps.FirstOrDefault(s => s.Order == r.CurrentStepOrder)?.Name, r.Definition.Steps.Count,
            r.StartedById, names.GetValueOrDefault(r.StartedById, ""), r.StartedAt, r.CompletedAt)).ToList();
    }
}
