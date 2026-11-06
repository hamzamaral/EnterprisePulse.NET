using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Workflow;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Platform;

public sealed class PlatformDashboard(AppDbContext db, WorkflowService workflow) : IDashboardProvider
{
    public int Order => 0;

    public async Task<DashboardSection?> SectionAsync()
    {
        var userId = db.User.RequireUserId();
        var cards = new List<DashboardCard>
        {
            new("approvals", "Onayımı bekleyenler", (await workflow.InboxCountAsync()).ToString(), "TaskAlt", Link: "/workflow/inbox", Color: "warning"),
            new("notifications", "Okunmamış bildirim", (await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null)).ToString(), "Notifications", Link: "/notifications"),
        };
        if (db.User.Has("users:read"))
        {
            var companyId = db.User.RequireCompanyId();
            var users = await db.UserCompanies.CountAsync(m => m.CompanyId == companyId && m.User.IsActive);
            cards.Add(new("users", "Aktif kullanıcı", users.ToString(), "People", Link: "/r/users"));
        }
        if (db.User.Has("workflow:read"))
        {
            cards.Add(new("running", "Devam eden onay süreçleri", (await db.WorkflowInstances.CountAsync(i => i.Status == WorkflowStatus.PENDING)).ToString(), "AccountTree", Link: "/workflow/instances"));
        }
        return new DashboardSection("platform", "Genel", cards);
    }
}
