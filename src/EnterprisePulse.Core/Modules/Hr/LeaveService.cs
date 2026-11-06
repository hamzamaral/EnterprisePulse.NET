using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Workflow;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Hr;

public sealed record CreateLeaveRequest(Guid? EmployeeId, Guid LeaveTypeId, DateOnly StartDate, DateOnly EndDate, string? Reason);

public sealed record LeaveRequestDto(
    Guid Id, string Number, Guid EmployeeId, string EmployeeName, Guid LeaveTypeId, string LeaveTypeName, string LeaveTypeColor,
    DateOnly StartDate, DateOnly EndDate, int Days, string? Reason, LeaveRequestStatus Status, Guid? WorkflowInstanceId,
    DateTime? DecidedAt, DateTime CreatedAt, bool CanCancel);

public sealed record LeaveBalanceDto(
    Guid Id, Guid EmployeeId, string EmployeeName, Guid LeaveTypeId, string LeaveTypeName, string LeaveTypeColor,
    int Year, int EntitledDays, int UsedDays, int PendingDays, int RemainingDays);

public sealed record LeaveTypeOption(Guid Id, string Code, string Name, string Color, bool RequiresBalance);

/// <summary>Leave requests, balances and the approval round-trip (İzin).</summary>
[ModuleService]
public sealed class LeaveService(AppDbContext db, NumberingService numbering, WorkflowService workflow, NotificationService notifications)
{
    public const string WorkflowCode = "LEAVE_REQUEST";
    public const string EntityType = "LeaveRequest";

    private CurrentUser Ctx => db.User;

    public async Task<int> WorkingDaysAsync(DateOnly start, DateOnly end)
    {
        if (end < start) return 0;
        var holidays = await db.Holidays.Where(h => h.Date >= start && h.Date <= end).Select(h => h.Date).ToListAsync();
        return WorkingDays.Count(start, end, holidays.ToHashSet());
    }

    public Task<List<LeaveTypeOption>> TypesAsync() =>
        db.LeaveTypes.Where(t => t.IsActive).OrderBy(t => t.Name).Select(t => new LeaveTypeOption(t.Id, t.Code, t.Name, t.Color, t.RequiresBalance)).ToListAsync();

    public async Task<Paged<LeaveRequestDto>> ListAsync(bool mine, LeaveRequestStatus? status, Guid? employeeId, string? search, int page, int pageSize)
    {
        var q = db.LeaveRequests.Include(r => r.Employee).Include(r => r.LeaveType).AsNoTracking();
        if (mine || !Ctx.Has("leave:read"))
        {
            var userId = Ctx.RequireUserId();
            q = q.Where(r => r.Employee.UserId == userId || r.CreatedById == userId);
        }
        if (status != null) q = q.Where(r => r.Status == status);
        if (employeeId != null) q = q.Where(r => r.EmployeeId == employeeId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = $"%{search.Trim()}%";
            q = q.Where(r => EF.Functions.ILike(r.Number, s) || EF.Functions.ILike(r.Employee.FirstName + " " + r.Employee.LastName, s));
        }
        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Paged<LeaveRequestDto>.Of(rows.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<LeaveRequestDto> GetAsync(Guid id) => ToDto(await VisibleAsync(id));

    public async Task<LeaveRequestDto> CreateAsync(CreateLeaveRequest body)
    {
        var employee = await ResolveEmployeeAsync(body.EmployeeId);
        var type = Guard.Found(await db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == body.LeaveTypeId), "İzin türü bulunamadı");
        Guard.That(type.IsActive, "İzin türü aktif değil");
        Guard.That(body.EndDate >= body.StartDate, "Bitiş tarihi başlangıçtan önce olamaz");
        Guard.That(body.EndDate.Year == body.StartDate.Year, "Bir izin talebi iki takvim yılına yayılamaz");
        var days = await WorkingDaysAsync(body.StartDate, body.EndDate);
        Guard.That(days > 0, "Seçilen aralıkta iş günü yok");

        var overlapping = await db.LeaveRequests.AnyAsync(r => r.EmployeeId == employee.Id
            && (r.Status == LeaveRequestStatus.PENDING || r.Status == LeaveRequestStatus.APPROVED)
            && r.StartDate <= body.EndDate && r.EndDate >= body.StartDate);
        Guard.State(!overlapping, "Başka bir izin talebiyle çakışıyor");

        var year = body.StartDate.Year;
        if (type.RequiresBalance)
        {
            var balance = (await BalancesAsync(employee.Id, year)).FirstOrDefault(b => b.LeaveTypeId == type.Id)
                ?? throw new BadRequestAppException($"{year} için {type.Name} bakiyesi tanımlı değil");
            Guard.That(days <= balance.RemainingDays, $"Yetersiz bakiye: {balance.RemainingDays} gün kaldı, {days} gün istendi");
        }

        // Without an active approval flow the request is approved immediately.
        var hasWorkflow = await db.WorkflowDefinitions.AnyAsync(d => d.Code == WorkflowCode && d.IsActive && d.Steps.Any());
        await using var tx = await db.Database.BeginTransactionAsync();
        var request = new LeaveRequest
        {
            Number = await numbering.NextAsync("LEAVE_REQUEST"),
            EmployeeId = employee.Id,
            LeaveTypeId = type.Id,
            StartDate = body.StartDate,
            EndDate = body.EndDate,
            Days = days,
            Reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim(),
            CreatedById = Ctx.RequireUserId(),
            Status = hasWorkflow ? LeaveRequestStatus.PENDING : LeaveRequestStatus.APPROVED,
            DecidedAt = hasWorkflow ? null : DateTime.UtcNow,
        };
        db.LeaveRequests.Add(request);
        await db.SaveChangesAsync();
        if (hasWorkflow)
        {
            var instance = await workflow.StartAsync(new StartWorkflowRequest(WorkflowCode, request.Id.ToString(),
                $"{employee.FirstName} {employee.LastName} — {type.Name} ({days} gün)"));
            request.WorkflowInstanceId = instance.Id;
            await db.SaveChangesAsync();
        }
        else
        {
            await AdjustUsedAsync(request, days);
        }
        await tx.CommitAsync();
        return await GetAsync(request.Id);
    }

    /// <summary>
    /// Pending requests are withdrawn by their creator (cancelling the approval); approved requests that have not started
    /// are cancelled by HR (<c>leave:update</c>) and give the days back.
    /// </summary>
    public async Task<LeaveRequestDto> CancelAsync(Guid id, string? comment)
    {
        var request = await VisibleAsync(id, tracked: true);
        if (request.Status == LeaveRequestStatus.PENDING)
        {
            Guard.State(request.CreatedById == Ctx.UserId || Ctx.IsSuperAdmin, "Bekleyen talebi yalnızca talep sahibi geri çekebilir");
            if (request.WorkflowInstanceId is { } instanceId) await workflow.CancelAsync(instanceId, comment);
            await DecideAsync(request.Id, LeaveRequestStatus.CANCELLED);
            return await GetAsync(id);
        }
        if (request.Status == LeaveRequestStatus.APPROVED)
        {
            if (!Ctx.Has("leave:update")) throw new ForbiddenAppException("Onaylanmış izni yalnızca İK iptal edebilir");
            Guard.State(!HasStarted(request), "İzin başlamış; iptal edilemez");
            await using var tx = await db.Database.BeginTransactionAsync();
            request.Status = LeaveRequestStatus.CANCELLED;
            request.DecidedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await AdjustUsedAsync(request, -request.Days);
            await tx.CommitAsync();
            return await GetAsync(id);
        }
        throw new ConflictAppException("Talep zaten sonuçlanmış");
    }

    /// <summary>Applies an approval outcome; only pending requests change, so repeated events are harmless.</summary>
    public async Task DecideAsync(Guid requestId, LeaveRequestStatus status)
    {
        var request = await db.LeaveRequests.Include(r => r.Employee).FirstOrDefaultAsync(r => r.Id == requestId && r.Status == LeaveRequestStatus.PENDING);
        if (request == null) return;
        request.Status = status;
        request.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (status == LeaveRequestStatus.APPROVED) await AdjustUsedAsync(request, request.Days);
        if (request.Employee.UserId is { } owner && owner != request.CreatedById && status != LeaveRequestStatus.CANCELLED)
        {
            await notifications.NotifyAsync(new NotifyRequest([owner], request.CompanyId, "leave.decided",
                status == LeaveRequestStatus.APPROVED ? "İzniniz onaylandı" : "İzniniz reddedildi",
                $"{request.Number} ({request.StartDate:dd.MM.yyyy} - {request.EndDate:dd.MM.yyyy})", "/hr/my-leave"));
        }
    }

    // ---------- balances ----------

    public async Task<List<LeaveBalanceDto>> BalancesAsync(Guid? employeeId, int year)
    {
        var q = db.LeaveBalances.Include(b => b.Employee).Include(b => b.LeaveType).AsNoTracking().Where(b => b.Year == year);
        if (employeeId != null) q = q.Where(b => b.EmployeeId == employeeId);
        var rows = await q.OrderBy(b => b.Employee.FirstName).ThenBy(b => b.Employee.LastName).ThenBy(b => b.LeaveType.Name).ToListAsync();

        var pq = db.LeaveRequests.Where(r => r.Status == LeaveRequestStatus.PENDING && r.StartDate.Year == year);
        if (employeeId != null) pq = pq.Where(r => r.EmployeeId == employeeId);
        var pending = await pq.GroupBy(r => new { r.EmployeeId, r.LeaveTypeId })
            .Select(g => new { g.Key.EmployeeId, g.Key.LeaveTypeId, Days = g.Sum(r => r.Days) }).ToListAsync();

        return rows.Select(b =>
        {
            var p = pending.FirstOrDefault(x => x.EmployeeId == b.EmployeeId && x.LeaveTypeId == b.LeaveTypeId)?.Days ?? 0;
            return new LeaveBalanceDto(b.Id, b.EmployeeId, $"{b.Employee.FirstName} {b.Employee.LastName}", b.LeaveTypeId, b.LeaveType.Name,
                b.LeaveType.Color, b.Year, b.EntitledDays, b.UsedDays, p, b.EntitledDays - b.UsedDays - p);
        }).ToList();
    }

    /// <summary>Balances of the caller, or anyone's with <c>leave:read</c>.</summary>
    public async Task<List<LeaveBalanceDto>> VisibleBalancesAsync(Guid? employeeId, int year)
    {
        if (Ctx.Has("leave:read")) return await BalancesAsync(employeeId, year);
        var own = await OwnEmployeeAsync();
        if (employeeId != null && employeeId != own.Id) throw new ForbiddenAppException("Missing permission: leave:read");
        return await BalancesAsync(own.Id, year);
    }

    /// <summary>Creates the missing balances of every active employee for balance-tracked leave types.</summary>
    public async Task<int> AccrueAsync(int year)
    {
        Guard.That(year is >= 2000 and <= 2100, "Geçersiz yıl");
        var employees = await db.Employees.Where(e => e.Status == EmployeeStatus.ACTIVE).Select(e => new { e.Id, e.HireDate }).ToListAsync();
        var types = await db.LeaveTypes.Where(t => t.IsActive && t.RequiresBalance).ToListAsync();
        var existing = (await db.LeaveBalances.Where(b => b.Year == year).Select(b => new { b.EmployeeId, b.LeaveTypeId }).ToListAsync())
            .Select(x => (x.EmployeeId, x.LeaveTypeId)).ToHashSet();
        var created = 0;
        foreach (var e in employees)
        {
            foreach (var t in types.Where(t => !existing.Contains((e.Id, t.Id))))
            {
                var days = t.Code == "YILLIK" ? Math.Max(t.DefaultDays, WorkingDays.AnnualLeaveDays(e.HireDate, year)) : t.DefaultDays;
                db.LeaveBalances.Add(new LeaveBalance { EmployeeId = e.Id, LeaveTypeId = t.Id, Year = year, EntitledDays = days });
                created++;
            }
        }
        await db.SaveChangesAsync();
        return created;
    }

    public async Task<LeaveBalanceDto> UpdateEntitlementAsync(Guid id, int entitledDays)
    {
        Guard.That(entitledDays is >= 0 and <= 365, "Hak edilen gün 0-365 arasında olmalı");
        var balance = Guard.Found(await db.LeaveBalances.FirstOrDefaultAsync(b => b.Id == id), "İzin bakiyesi bulunamadı");
        balance.EntitledDays = entitledDays;
        await db.SaveChangesAsync();
        return (await BalancesAsync(balance.EmployeeId, balance.Year)).First(b => b.Id == id);
    }

    // ---------- helpers ----------

    private async Task AdjustUsedAsync(LeaveRequest request, int delta)
    {
        await db.LeaveBalances
            .Where(b => b.EmployeeId == request.EmployeeId && b.LeaveTypeId == request.LeaveTypeId && b.Year == request.StartDate.Year && b.LeaveType.RequiresBalance)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.UsedDays, b => b.UsedDays + delta));
    }

    public async Task<Employee> OwnEmployeeAsync()
    {
        var userId = Ctx.RequireUserId();
        return await db.Employees.FirstOrDefaultAsync(e => e.UserId == userId)
            ?? throw new NotFoundAppException("Hesabınıza bağlı bir personel kaydı yok");
    }

    private async Task<Employee> ResolveEmployeeAsync(Guid? employeeId)
    {
        var userId = Ctx.RequireUserId();
        var own = await db.Employees.FirstOrDefaultAsync(e => e.UserId == userId);
        Employee employee;
        if (employeeId != null && employeeId != own?.Id)
        {
            if (!Ctx.Has("leave:create")) throw new ForbiddenAppException("Yalnızca kendiniz için izin talep edebilirsiniz");
            employee = Guard.Found(await db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId), "Personel bulunamadı");
        }
        else
        {
            employee = own ?? throw new NotFoundAppException("Hesabınıza bağlı bir personel kaydı yok");
        }
        Guard.That(employee.Status == EmployeeStatus.ACTIVE, "Personel aktif değil");
        return employee;
    }

    private async Task<LeaveRequest> VisibleAsync(Guid id, bool tracked = false)
    {
        var q = db.LeaveRequests.Include(r => r.Employee).Include(r => r.LeaveType).AsQueryable();
        if (!tracked) q = q.AsNoTracking();
        var row = await q.FirstOrDefaultAsync(r => r.Id == id);
        var visible = row != null && (Ctx.Has("leave:read") || row.Employee.UserId == Ctx.UserId || row.CreatedById == Ctx.UserId);
        return visible ? row! : throw new NotFoundAppException("İzin talebi bulunamadı");
    }

    private static bool HasStarted(LeaveRequest r) => r.StartDate <= DateOnly.FromDateTime(DateTime.UtcNow);

    private bool CanCancel(LeaveRequest r) => r.Status switch
    {
        LeaveRequestStatus.PENDING => r.CreatedById == Ctx.UserId || Ctx.IsSuperAdmin,
        LeaveRequestStatus.APPROVED => Ctx.Has("leave:update") && !HasStarted(r),
        _ => false,
    };

    private LeaveRequestDto ToDto(LeaveRequest r) => new(r.Id, r.Number, r.EmployeeId, $"{r.Employee.FirstName} {r.Employee.LastName}",
        r.LeaveTypeId, r.LeaveType.Name, r.LeaveType.Color, r.StartDate, r.EndDate, r.Days, r.Reason, r.Status, r.WorkflowInstanceId,
        r.DecidedAt, r.CreatedAt, CanCancel(r));
}

/// <summary>Keeps leave status and balances in sync with the approval flow.</summary>
public sealed class LeaveWorkflowListener(IServiceProvider services) : IWorkflowListener
{
    public string EntityType => LeaveService.EntityType;

    public Task OnFinishedAsync(WorkflowFinished e)
    {
        // Resolved lazily: LeaveService depends on WorkflowService, which depends on the listeners.
        var leave = (LeaveService)services.GetService(typeof(LeaveService))!;
        if (!Guid.TryParse(e.EntityId, out var id)) return Task.CompletedTask;
        var status = e.Status switch
        {
            WorkflowStatus.APPROVED => LeaveRequestStatus.APPROVED,
            WorkflowStatus.REJECTED => LeaveRequestStatus.REJECTED,
            _ => LeaveRequestStatus.CANCELLED,
        };
        return leave.DecideAsync(id, status);
    }
}
