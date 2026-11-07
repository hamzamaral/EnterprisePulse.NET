using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Support;

public sealed class SupportDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 90;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("support:read") && !db.User.Has("support:update")) return null;
        var open = await db.Tickets.CountAsync(t => t.Status == TicketStatus.OPEN || t.Status == TicketStatus.IN_PROGRESS || t.Status == TicketStatus.WAITING);
        var overdue = await db.Tickets.CountAsync(t => (t.Status == TicketStatus.OPEN || t.Status == TicketStatus.IN_PROGRESS || t.Status == TicketStatus.WAITING) && t.ResolutionDueAt < DateTime.UtcNow && t.PausedAt == null);
        return new DashboardSection("support", "Destek",
        [
            new("open-tickets", "Açık talep", open.ToString(), "SupportAgent", Link: "/r/tickets"),
            new("overdue-tickets", "SLA aşımı", overdue.ToString(), "Timer", Link: "/r/tickets", Color: overdue > 0 ? "warning" : "primary"),
        ]);
    }
}

public sealed class ContractDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 95;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("contracts:read")) return null;
        var active = await db.Contracts.CountAsync(c => c.Status == ContractStatus.ACTIVE);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expiring = await db.Contracts.CountAsync(c => c.Status == ContractStatus.ACTIVE && c.EndDate != null && c.EndDate <= today.AddDays(30));
        return new DashboardSection("contracts", "Sözleşmeler",
        [
            new("active-contracts", "Aktif sözleşme", active.ToString(), "Description", Link: "/r/contracts"),
            new("expiring-contracts", "Yaklaşan bitiş", expiring.ToString(), "Event", Link: "/r/contracts", Color: expiring > 0 ? "warning" : "primary"),
        ]);
    }
}

public sealed class BudgetDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 100;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("budgets:read")) return null;
        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;
        var approved = await db.Budgets.CountAsync(b => b.Year == year && b.Status == BudgetStatus.APPROVED);
        var alerts = await db.BudgetAlerts.CountAsync(a => a.Budget.Year == year);
        return new DashboardSection("budgets", "Bütçe",
        [
            new("approved-budgets", "Onaylı bütçe", approved.ToString(), "AccountBalance", Link: "/r/budgets"),
            new("budget-alerts", "Uyarı", alerts.ToString(), "Warning", Link: "/r/budgets", Color: alerts > 0 ? "warning" : "primary"),
        ]);
    }
}

public sealed class FleetDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 105;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("fleet:read")) return null;
        var active = await db.Vehicles.CountAsync(v => v.Status == VehicleStatus.ACTIVE);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var docs = await db.VehicleDocuments.AsNoTracking().Where(d => d.Vehicle.Status != VehicleStatus.RETIRED).ToListAsync();
        var warnings = docs.GroupBy(d => new { d.VehicleId, d.Type }).Select(g => g.OrderByDescending(d => d.EndDate).First()).Count(d => d.EndDate <= today.AddDays(30));
        return new DashboardSection("fleet", "Araç ve Filo",
        [
            new("active-vehicles", "Aktif araç", active.ToString(), "DirectionsCar", Link: "/r/vehicles"),
            new("doc-warnings", "Belge uyarısı", warnings.ToString(), "FactCheck", Link: "/r/vehicles", Color: warnings > 0 ? "warning" : "primary"),
        ]);
    }
}
