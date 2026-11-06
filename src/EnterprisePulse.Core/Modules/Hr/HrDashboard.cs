using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Hr;

public sealed class HrDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 10;

    public async Task<DashboardSection?> SectionAsync()
    {
        var ctx = db.User;
        var cards = new List<DashboardCard>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var userId = ctx.UserId;
        var own = await db.Employees.AsNoTracking().Where(e => e.UserId == userId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (own != null)
        {
            var annual = await db.LeaveBalances.AsNoTracking()
                .Where(b => b.EmployeeId == own && b.Year == today.Year && b.LeaveType.Code == "YILLIK")
                .Select(b => (int?)(b.EntitledDays - b.UsedDays)).FirstOrDefaultAsync();
            if (annual != null) cards.Add(new("my-leave", "Kalan yıllık izin", $"{annual} gün", "BeachAccess", Link: "/hr/my-leave", Color: "info"));
        }
        if (ctx.Has("employees:read"))
        {
            cards.Add(new("employees", "Aktif personel", (await db.Employees.CountAsync(e => e.Status == EmployeeStatus.ACTIVE)).ToString(), "Badge", Link: "/r/employees"));
        }
        if (ctx.Has("leave:read"))
        {
            var onLeave = await db.LeaveRequests.CountAsync(r => r.Status == LeaveRequestStatus.APPROVED && r.StartDate <= today && r.EndDate >= today);
            var pending = await db.LeaveRequests.CountAsync(r => r.Status == LeaveRequestStatus.PENDING);
            cards.Add(new("on-leave", "Bugün izinde", onLeave.ToString(), "EventBusy", Link: "/r/leave-requests?status=APPROVED"));
            cards.Add(new("leave-pending", "Bekleyen izin talebi", pending.ToString(), "HourglassTop", Link: "/r/leave-requests?status=PENDING", Color: "warning"));
        }
        return cards.Count == 0 ? null : new DashboardSection("hr", "İnsan Kaynakları", cards);
    }
}
