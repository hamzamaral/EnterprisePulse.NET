using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Assets;

public sealed class AssetDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 20;

    public async Task<DashboardSection?> SectionAsync()
    {
        var ctx = db.User;
        var cards = new List<DashboardCard>();
        var userId = ctx.UserId;
        var own = await db.Employees.AsNoTracking().Where(e => e.UserId == userId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (own != null)
        {
            var mine = await db.AssetAssignments.AsNoTracking()
                .Where(a => a.EmployeeId == own && a.Status == AssignmentStatus.ACTIVE)
                .Select(a => a.AcknowledgedAt == null).ToListAsync();
            var waiting = mine.Count(x => x);
            cards.Add(new("my-assets", "Zimmetimdeki demirbaşlar", mine.Count.ToString(), "Inventory2",
                waiting > 0 ? $"{waiting} tutanak onay bekliyor" : null, "/assets/my", waiting > 0 ? "warning" : null));
        }
        if (ctx.Has("assets:read"))
        {
            var total = await db.Assets.CountAsync();
            var assigned = await db.Assets.CountAsync(a => a.Status == AssetStatus.ASSIGNED);
            var maintenance = await db.Assets.CountAsync(a => a.Status == AssetStatus.IN_MAINTENANCE);
            cards.Add(new("assigned-assets", "Zimmetli demirbaş", $"{assigned} / {total}", "Laptop", Link: "/r/assets?status=ASSIGNED"));
            if (maintenance > 0)
                cards.Add(new("maintenance-assets", "Bakımdaki demirbaş", maintenance.ToString(), "Build", Link: "/r/assets?status=IN_MAINTENANCE", Color: "warning"));
        }
        return cards.Count == 0 ? null : new DashboardSection("assets", "Demirbaş", cards);
    }
}
