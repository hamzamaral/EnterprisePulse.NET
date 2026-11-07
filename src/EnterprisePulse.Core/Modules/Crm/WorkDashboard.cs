using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Crm;

public sealed class CrmDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 50;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("crm:read")) return null;
        var openLeads = await db.Leads.CountAsync(l => l.Status == LeadStatus.NEW || l.Status == LeadStatus.CONTACTED || l.Status == LeadStatus.QUALIFIED);
        var pipeline = await db.Opportunities.Where(o => o.Stage != OpportunityStage.WON && o.Stage != OpportunityStage.LOST).ToListAsync();
        var weighted = pipeline.Sum(o => Math.Round(o.Amount * o.Probability / 100m, 2, MidpointRounding.AwayFromZero));
        var overdue = await db.CrmActivities.CountAsync(a => a.CompletedAt == null && a.DueAt != null && a.DueAt < DateTime.UtcNow);
        return new DashboardSection("crm", "CRM",
        [
            new("open-leads", "Açık potansiyel", openLeads.ToString(), "PersonAdd", Link: "/r/leads"),
            new("pipeline", "Ağırlıklı fırsat", weighted.ToString("N2"), "TrendingUp", $"{pipeline.Count} fırsat", "/r/opportunities"),
            new("overdue-activities", "Geciken aktivite", overdue.ToString(), "EventBusy", Link: "/r/crm-activities", Color: overdue > 0 ? "warning" : "primary"),
        ]);
    }
}

public sealed class ExpenseDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 55;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("expenses:read") && !db.User.Has("expenses:pay")) return null;
        var pending = await db.ExpenseReports.CountAsync(r => r.Status == ExpenseReportStatus.PENDING || r.Status == ExpenseReportStatus.APPROVED);
        var approved = await db.ExpenseReports.Where(r => r.Status == ExpenseReportStatus.APPROVED).SumAsync(r => (decimal?)r.TotalAmount) ?? 0;
        return new DashboardSection("expenses", "Masraf ve Harcırah",
        [
            new("open-expenses", "Bekleyen masraf", pending.ToString(), "Receipt", Link: "/r/expense-reports"),
            new("payable-expenses", "Ödenecek masraf", approved.ToString("N2"), "Payments", Link: "/r/expense-reports"),
        ]);
    }
}

public sealed class ProjectDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 60;

    public async Task<DashboardSection?> SectionAsync()
    {
        var employeeId = await db.Employees.Where(e => e.UserId == db.User.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        var myTasks = employeeId == null ? 0 : await db.ProjectTasks.CountAsync(t => t.AssigneeId == employeeId && t.Status != ProjectTaskStatus.DONE);
        if (!db.User.Has("projects:read") && myTasks == 0) return null;
        var active = db.User.Has("projects:read") ? await db.Projects.CountAsync(p => p.Status == ProjectStatus.ACTIVE) : 0;
        return new DashboardSection("projects", "Projeler",
        [
            new("active-projects", "Aktif proje", active.ToString(), "AccountTree", Link: "/r/projects"),
            new("my-tasks", "Görevlerim", myTasks.ToString(), "TaskAlt", Link: "/projects/my-tasks"),
        ]);
    }
}
