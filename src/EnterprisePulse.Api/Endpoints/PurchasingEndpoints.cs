using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Purchasing;

namespace EnterprisePulse.Api.Endpoints;

public sealed record CancelRequest(string? Reason, string? Comment)
{
    public string? Text => string.IsNullOrWhiteSpace(Reason) ? Comment : Reason;
}

public static class PurchasingEndpoints
{
    public static void MapPurchasing(this IEndpointRouteBuilder app)
    {
        var partners = app.MapGroup("/api/partners").WithTags("Partners").RequireAuthorization();
        partners.MapGet("/{id:guid}/statement", (Guid id, DateOnly? dateFrom, DateOnly? dateTo, PartnerLedgerService s) => s.StatementAsync(id, dateFrom, dateTo))
            .RequireAnyPermission("partners:read", "purchasing:read", "sales:read", "finance:read");
        partners.MapGet("/{id:guid}/balance", (Guid id, PartnerLedgerService s) => s.BalanceAsync(id))
            .RequireAnyPermission("partners:read", "purchasing:read", "sales:read", "finance:read");

        var g = app.MapGroup("/api/purchasing").WithTags("Purchasing").RequireAuthorization();
        g.MapPost("/requests", (CreatePurchaseRequest body, PurchaseRequestService s) => s.CreateAsync(body)).RequireAnyPermission("workflow:start", "purchasing:create");
        g.MapGet("/requests/{id:guid}", (Guid id, PurchaseRequestService s) => s.GetAsync(id)).RequireAnyPermission("purchasing:read", "workflow:start", "purchasing:create");
        g.MapPost("/requests/{id:guid}/cancel", (Guid id, CancelRequest body, PurchaseRequestService s) => s.CancelAsync(id, body.Text))
            .RequireAnyPermission("workflow:start", "purchasing:update");
        g.MapGet("/requests/{id:guid}/compare", (Guid id, SupplierQuoteService s) => s.CompareAsync(id)).RequirePermission("purchasing:read");

        g.MapPost("/quotes", (CreateSupplierQuote body, SupplierQuoteService s) => s.CreateAsync(body)).RequirePermission("purchasing:create");
        g.MapGet("/quotes/{id:guid}", (Guid id, SupplierQuoteService s) => s.GetAsync(id)).RequirePermission("purchasing:read");
        g.MapPost("/quotes/{id:guid}/reject", (Guid id, SupplierQuoteService s) => s.RejectAsync(id)).RequirePermission("purchasing:update");
        g.MapPost("/quotes/{id:guid}/accept", (Guid id, AcceptQuote body, SupplierQuoteService s) => s.AcceptAsync(id, body)).RequirePermission("purchasing:create");

        g.MapGet("/orders/open", (Guid? partnerId, PurchaseOrderService s) => s.OpenAsync(partnerId)).RequirePermission("purchasing:read");
        g.MapPost("/orders", (SavePurchaseOrder body, PurchaseOrderService s) => s.CreateAsync(body)).RequirePermission("purchasing:create");
        g.MapGet("/orders/{id:guid}", (Guid id, PurchaseOrderService s) => s.GetAsync(id)).RequirePermission("purchasing:read");
        g.MapPut("/orders/{id:guid}", (Guid id, SavePurchaseOrder body, PurchaseOrderService s) => s.UpdateAsync(id, body)).RequirePermission("purchasing:update");
        g.MapPost("/orders/{id:guid}/approve", (Guid id, PurchaseOrderService s) => s.ApproveAsync(id)).RequirePermission("purchasing:approve");
        g.MapPost("/orders/{id:guid}/cancel", (Guid id, CancelRequest body, PurchaseOrderService s) => s.CancelAsync(id, body.Text)).RequirePermission("purchasing:update");

        g.MapPost("/receipts", (CreateGoodsReceipt body, GoodsReceiptService s) => s.CreateAsync(body)).RequirePermission("purchasing:receive");
        g.MapGet("/receipts/{id:guid}", (Guid id, GoodsReceiptService s) => s.GetAsync(id)).RequirePermission("purchasing:read");

        g.MapPost("/invoices", (CreatePurchaseInvoice body, PurchaseInvoiceService s) => s.CreateAsync(body)).RequireAnyPermission("purchasing:create", "finance:create");
        g.MapGet("/invoices/{id:guid}", (Guid id, PurchaseInvoiceService s) => s.GetAsync(id)).RequireAnyPermission("purchasing:read", "finance:read");
        g.MapPost("/invoices/{id:guid}/cancel", (Guid id, CancelRequest body, PurchaseInvoiceService s) => s.CancelAsync(id, body.Text)).RequirePermission("purchasing:update");
    }
}
