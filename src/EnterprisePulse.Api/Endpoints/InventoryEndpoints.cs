using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Inventory;

namespace EnterprisePulse.Api.Endpoints;

public static class InventoryEndpoints
{
    public static void MapInventory(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/stock").WithTags("Inventory").RequireAuthorization();
        var read = new[] { "inventory:read", "purchasing:read", "sales:read" };

        g.MapGet("/levels", (Guid? warehouseId, Guid? categoryId, bool? lowStock, string? search, string? sort, string? order, int? page, int? pageSize, InventoryService s) =>
            s.LevelsAsync(new StockLevelQuery(warehouseId, categoryId, lowStock == true, search, sort, order == "desc", page ?? 1, pageSize ?? 25)))
            .RequirePermission("inventory:read");
        g.MapGet("/summary", (InventoryService s) => s.SummaryAsync()).RequirePermission("inventory:read");
        g.MapGet("/warehouses", (InventoryService s) => s.WarehousesAsync()).RequirePermission("inventory:read");
        g.MapGet("/items", (string? search, Guid? warehouseId, bool? inStock, bool? includeServices, Guid? id, InventoryService s) =>
            s.ItemsAsync(search, warehouseId, inStock == true, includeServices == true, id)).RequireAnyPermission(read);
        g.MapGet("/lots", (Guid? variantId, Guid? warehouseId, InventoryService s) => s.LotsAsync(variantId, warehouseId)).RequireAnyPermission(read);
        g.MapGet("/serials", (Guid? variantId, Guid? warehouseId, SerialStatus? status, InventoryService s) => s.SerialsAsync(variantId, warehouseId, status)).RequireAnyPermission(read);
        g.MapGet("/ledger", (Guid? productId, Guid? variantId, Guid? warehouseId, int? page, int? pageSize, InventoryService s) =>
            s.LedgerAsync(productId, variantId, warehouseId, page ?? 1, pageSize ?? 50)).RequireAnyPermission(read);

        g.MapGet("/products/{id:guid}", (Guid id, InventoryService s) => s.ProductAsync(id)).RequirePermission("inventory:read");
        g.MapPost("/products/{id:guid}/variants", (Guid id, VariantInput body, InventoryService s) => s.AddVariantAsync(id, body)).RequirePermission("inventory:update");
        g.MapPatch("/products/{id:guid}/variants/{variantId:guid}", (Guid id, Guid variantId, VariantInput body, InventoryService s) => s.UpdateVariantAsync(id, variantId, body))
            .RequirePermission("inventory:update");
        g.MapDelete("/products/{id:guid}/variants/{variantId:guid}", (Guid id, Guid variantId, InventoryService s) => s.RemoveVariantAsync(id, variantId)).RequirePermission("inventory:update");

        g.MapPost("/movements", (CreateMovementRequest body, StockMovementService s) => s.CreateAsync(body)).RequirePermission("inventory:move");
        g.MapGet("/movements/{id:guid}", (Guid id, StockMovementService s) => s.GetAsync(id)).RequirePermission("inventory:read");

        g.MapPost("/counts", (CreateStockCountRequest body, StockCountService s) => s.CreateAsync(body)).RequirePermission("inventory:count");
        g.MapGet("/counts/{id:guid}", (Guid id, StockCountService s) => s.GetAsync(id)).RequirePermission("inventory:read");
        g.MapPut("/counts/{id:guid}/lines", (Guid id, List<CountLineUpdate> body, StockCountService s) => s.UpdateLinesAsync(id, body)).RequirePermission("inventory:count");
        g.MapPost("/counts/{id:guid}/lines", (Guid id, AddCountLineRequest body, StockCountService s) => s.AddLineAsync(id, body)).RequirePermission("inventory:count");
        g.MapPost("/counts/{id:guid}/complete", (Guid id, StockCountService s) => s.CompleteAsync(id)).RequirePermission("inventory:count");
        g.MapPost("/counts/{id:guid}/cancel", (Guid id, StockCountService s) => s.CancelAsync(id)).RequirePermission("inventory:count");
    }
}
