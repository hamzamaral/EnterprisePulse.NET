using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Inventory;

public sealed class InventoryDashboard(AppDbContext db, InventoryService stock) : IDashboardProvider
{
    public int Order => 30;

    public async Task<DashboardSection?> SectionAsync()
    {
        var ctx = db.User;
        var cards = new List<DashboardCard>();
        if (ctx.Has("inventory:read"))
        {
            var summary = await stock.SummaryAsync();
            cards.Add(new("stock-value", "Stok değeri", summary.StockValue.ToString("N2"), "Inventory", $"{summary.ItemCount} kalem, {summary.WarehouseCount} depo", "/inventory"));
            if (summary.LowStockCount > 0)
                cards.Add(new("low-stock", "Minimum altı stok", summary.LowStockCount.ToString(), "WarningAmber", Link: "/inventory?low=1", Color: "warning"));
        }
        if (ctx.Has("purchasing:read"))
        {
            var pending = await db.PurchaseRequests.CountAsync(r => r.Status == PurchaseRequestStatus.PENDING);
            var openOrders = await db.PurchaseOrders.CountAsync(o => o.Status == PurchaseOrderStatus.APPROVED || o.Status == PurchaseOrderStatus.PARTIALLY_RECEIVED);
            if (pending > 0) cards.Add(new("purchase-requests", "Bekleyen satın alma talebi", pending.ToString(), "RequestQuote", Link: "/r/purchase-requests?status=PENDING", Color: "warning"));
            cards.Add(new("open-orders", "Açık satın alma siparişi", openOrders.ToString(), "LocalShipping", Link: "/r/purchase-orders"));
        }
        return cards.Count == 0 ? null : new DashboardSection("inventory", "Stok ve Satın Alma", cards);
    }
}
