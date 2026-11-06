using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Hr;

public sealed class DepartmentsHook(AppDbContext db) : IResourceHook
{
    public string Resource => "departments";

    public async Task BeforeSaveAsync(object entity, bool isNew)
    {
        var d = (Department)entity;
        if (d.ParentId == null) return;
        Guard.That(d.ParentId != d.Id, "Bir departman kendisinin üst departmanı olamaz");
        var parents = await db.Departments.AsNoTracking().Select(x => new { x.Id, x.ParentId }).ToDictionaryAsync(x => x.Id, x => x.ParentId);
        Guard.That(parents.ContainsKey(d.ParentId.Value), "Üst departman bulunamadı");
        for (Guid? cursor = d.ParentId; cursor != null; cursor = parents.GetValueOrDefault(cursor.Value))
        {
            Guard.That(cursor != d.Id, "Departman hiyerarşisi döngüsel olamaz");
        }
    }

    public async Task BeforeDeleteAsync(object entity)
    {
        var d = (Department)entity;
        var busy = await db.Departments.AnyAsync(x => x.ParentId == d.Id)
            || await db.Employees.AnyAsync(e => e.DepartmentId == d.Id && e.Status == EmployeeStatus.ACTIVE);
        Guard.State(!busy, "Departmanın alt departmanları veya aktif personeli var");
        d.IsActive = false;
    }
}

public sealed class PositionsHook(AppDbContext db) : IResourceHook
{
    public string Resource => "positions";

    public async Task BeforeDeleteAsync(object entity)
    {
        var p = (Position)entity;
        Guard.State(!await db.Employees.AnyAsync(e => e.PositionId == p.Id && e.Status == EmployeeStatus.ACTIVE), "Pozisyon aktif personele atanmış");
        p.IsActive = false;
    }
}

public sealed class EmployeesHook(AppDbContext db) : IResourceHook
{
    public string Resource => "employees";

    public async Task BeforeSaveAsync(object entity, bool isNew)
    {
        var e = (Employee)entity;
        if (isNew)
        {
            e.Status = EmployeeStatus.ACTIVE;
            e.TerminationDate = null;
            e.TerminationReason = null;
        }
        else
        {
            // Termination goes through the terminate action so the asset and date checks always run.
            var entry = db.Entry(e);
            entry.Property(x => x.Status).CurrentValue = entry.Property(x => x.Status).OriginalValue;
            entry.Property(x => x.TerminationDate).CurrentValue = entry.Property(x => x.TerminationDate).OriginalValue;
            entry.Property(x => x.TerminationReason).CurrentValue = entry.Property(x => x.TerminationReason).OriginalValue;
        }
        if (e.UserId != null)
        {
            var companyId = db.User.RequireCompanyId();
            Guard.That(await db.UserCompanies.AnyAsync(m => m.UserId == e.UserId && m.CompanyId == companyId), "Kullanıcı bu şirketin üyesi değil");
            Guard.State(!await db.Employees.AnyAsync(x => x.UserId == e.UserId && x.Id != e.Id), "Bu kullanıcı başka bir personel kaydına bağlı");
        }
        if (e.ManagerId != null) await AssertManagerAsync(e);
    }

    private async Task AssertManagerAsync(Employee e)
    {
        Guard.That(e.ManagerId != e.Id, "Personel kendisinin yöneticisi olamaz");
        var managers = await db.Employees.AsNoTracking().Select(x => new { x.Id, x.ManagerId }).ToDictionaryAsync(x => x.Id, x => x.ManagerId);
        Guard.That(managers.ContainsKey(e.ManagerId!.Value), "Yönetici personel bulunamadı");
        for (Guid? cursor = e.ManagerId; cursor != null; cursor = managers.GetValueOrDefault(cursor.Value))
        {
            Guard.That(cursor != e.Id, "Raporlama hiyerarşisi döngüsel olamaz");
        }
    }

    public async Task BeforeDeleteAsync(object entity)
    {
        var e = (Employee)entity;
        var history = await db.Employees.AnyAsync(x => x.ManagerId == e.Id) || await db.LeaveRequests.AnyAsync(r => r.EmployeeId == e.Id);
        Guard.State(!history, "Personelin geçmiş kayıtları var; silmek yerine işten çıkışını yapın");
        await db.Departments.Where(d => d.ManagerId == e.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ManagerId, (Guid?)null));
        e.UserId = null;
    }
}

public sealed class ShiftsHook : IResourceHook
{
    public string Resource => "shifts";

    public Task BeforeSaveAsync(object entity, bool isNew)
    {
        var s = (Shift)entity;
        s.Code = s.Code.Trim().ToUpperInvariant();
        Guard.That(Timesheet.IsClock(s.StartTime) && Timesheet.IsClock(s.EndTime), "Saatler SS:dd biçiminde olmalı");
        Guard.That(s.StartTime != s.EndTime, "Vardiya başlangıç ve bitişi farklı olmalı");
        Guard.That(s.BreakMinutes is >= 0 and <= 240, "Mola 0-240 dakika arasında olmalı");
        var (start, end) = Timesheet.Window(new ShiftTimes(s.Code, s.StartTime, s.EndTime, s.BreakMinutes));
        Guard.That(s.BreakMinutes < end - start, "Mola vardiyadan kısa olmalı");
        return Task.CompletedTask;
    }
}

/// <summary>Requests are created and closed through the leave endpoints so balances and approvals stay consistent.</summary>
public sealed class LeaveRequestsHook : IResourceHook
{
    public string Resource => "leave-requests";

    public Task BeforeSaveAsync(object entity, bool isNew) =>
        throw new ConflictAppException("İzin talepleri İzinlerim / İzin Talepleri ekranından oluşturulur ve iptal edilir");

    public Task BeforeDeleteAsync(object entity) =>
        throw new ConflictAppException("İzin talepleri silinemez; iptal edin");
}

public sealed class LeaveBalancesHook(AppDbContext db) : IResourceHook
{
    public string Resource => "leave-balances";

    public Task BeforeSaveAsync(object entity, bool isNew)
    {
        var b = (LeaveBalance)entity;
        Guard.That(b.EntitledDays is >= 0 and <= 365, "Hak edilen gün 0-365 arasında olmalı");
        if (isNew) b.UsedDays = 0;
        else db.Entry(b).Property(x => x.UsedDays).CurrentValue = db.Entry(b).Property(x => x.UsedDays).OriginalValue;
        return Task.CompletedTask;
    }

    public async Task BeforeDeleteAsync(object entity)
    {
        var b = (LeaveBalance)entity;
        Guard.State(b.UsedDays == 0 && !await db.LeaveRequests.AnyAsync(r => r.EmployeeId == b.EmployeeId && r.LeaveTypeId == b.LeaveTypeId
            && r.StartDate.Year == b.Year && r.Status == LeaveRequestStatus.PENDING), "Kullanılmış veya bekleyen izni olan bakiye silinemez");
    }
}
