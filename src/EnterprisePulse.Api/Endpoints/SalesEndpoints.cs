using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Modules.Finance;
using EnterprisePulse.Core.Modules.Sales;

namespace EnterprisePulse.Api.Endpoints;

public sealed record AcceptSalesQuote(Guid WarehouseId, DateOnly? DeliveryDate);
public sealed record ChequeAction(Guid? AccountId, DateOnly? Date, string? Note);

public static class SalesEndpoints
{
    public static void MapSales(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/sales").WithTags("Sales").RequireAuthorization();
        g.MapPost("/quotes", (CreateSalesQuote body, SalesQuoteService s) => s.CreateAsync(body)).RequirePermission("sales:create");
        g.MapGet("/quotes/{id:guid}", (Guid id, SalesQuoteService s) => s.GetAsync(id)).RequirePermission("sales:read");
        g.MapPost("/quotes/{id:guid}/reject", (Guid id, SalesQuoteService s) => s.RejectAsync(id)).RequirePermission("sales:update");
        g.MapPost("/quotes/{id:guid}/accept", (Guid id, AcceptSalesQuote body, SalesQuoteService s) => s.AcceptAsync(id, body.WarehouseId, body.DeliveryDate)).RequirePermission("sales:create");

        g.MapGet("/orders/open", (SalesOrderService s) => s.OpenAsync()).RequirePermission("sales:read");
        g.MapPost("/orders", (SaveSalesOrder body, SalesOrderService s) => s.CreateAsync(body)).RequirePermission("sales:create");
        g.MapGet("/orders/{id:guid}", (Guid id, SalesOrderService s) => s.GetAsync(id)).RequirePermission("sales:read");
        g.MapPut("/orders/{id:guid}", (Guid id, SaveSalesOrder body, SalesOrderService s) => s.UpdateAsync(id, body)).RequirePermission("sales:update");
        g.MapPost("/orders/{id:guid}/approve", (Guid id, SalesOrderService s) => s.ApproveAsync(id)).RequirePermission("sales:approve");
        g.MapPost("/orders/{id:guid}/cancel", (Guid id, CancelRequest body, SalesOrderService s) => s.CancelAsync(id)).RequirePermission("sales:update");

        g.MapPost("/deliveries", (CreateDelivery body, DeliveryNoteService s) => s.CreateAsync(body)).RequirePermission("sales:deliver");
        g.MapGet("/deliveries/{id:guid}", (Guid id, DeliveryNoteService s) => s.GetAsync(id)).RequirePermission("sales:read");

        g.MapPost("/invoices", (CreateSalesInvoice body, SalesInvoiceService s) => s.CreateAsync(body)).RequirePermission("sales:invoice");
        g.MapGet("/invoices/{id:guid}", (Guid id, SalesInvoiceService s) => s.GetAsync(id)).RequirePermission("sales:read");
        g.MapPost("/invoices/{id:guid}/cancel", (Guid id, CancelRequest body, SalesInvoiceService s) => s.CancelAsync(id, body.Text)).RequirePermission("sales:invoice");

        g.MapPost("/returns", (CreateSalesReturn body, SalesReturnService s) => s.CreateAsync(body)).RequirePermission("sales:invoice");
        g.MapGet("/returns/{id:guid}", (Guid id, SalesReturnService s) => s.GetAsync(id)).RequirePermission("sales:read");

        var f = app.MapGroup("/api/finance").WithTags("Finance").RequireAuthorization();
        f.MapGet("/accounts", (TreasuryService s) => s.AccountsAsync()).RequirePermission("finance:read");
        f.MapGet("/accounts/{id:guid}/transactions", (Guid id, TreasuryService s) => s.TransactionsAsync(id)).RequirePermission("finance:read");
        f.MapPost("/payments", (CreatePaymentRequest body, PaymentService s) => s.CreateAsync(body)).RequirePermission("finance:create");
        f.MapGet("/payments/{id:guid}", (Guid id, PaymentService s) => s.GetAsync(id)).RequirePermission("finance:read");
        f.MapPost("/payments/{id:guid}/cancel", (Guid id, CancelRequest body, PaymentService s) => s.CancelAsync(id, body.Text)).RequirePermission("finance:update");
        f.MapGet("/cheques/{id:guid}", (Guid id, ChequeService s) => s.GetAsync(id)).RequirePermission("finance:read");
        f.MapPost("/cheques/{id:guid}/deposit", (Guid id, ChequeAction body, ChequeService s) => s.DepositAsync(id, body.AccountId!.Value, body.Date ?? DateOnly.FromDateTime(DateTime.UtcNow))).RequirePermission("finance:update");
        f.MapPost("/cheques/{id:guid}/collect", (Guid id, ChequeAction body, ChequeService s) => s.CollectAsync(id, body.Date ?? DateOnly.FromDateTime(DateTime.UtcNow))).RequirePermission("finance:approve");
        f.MapPost("/cheques/{id:guid}/bounce", (Guid id, ChequeAction body, ChequeService s) => s.BounceAsync(id, body.Date ?? DateOnly.FromDateTime(DateTime.UtcNow), body.Note)).RequirePermission("finance:update");
        f.MapPost("/cheques/{id:guid}/pay", (Guid id, ChequeAction body, ChequeService s) => s.PayAsync(id, body.Date ?? DateOnly.FromDateTime(DateTime.UtcNow))).RequirePermission("finance:approve");
        f.MapGet("/reports/aging", (string? kind, FinanceReportService s) => s.AgingAsync(kind ?? "sales")).RequirePermission("finance:read");
        f.MapGet("/reports/sales", (DateOnly from, DateOnly to, FinanceReportService s) => s.SalesAsync(from, to)).RequirePermission("finance:read");
        f.MapGet("/reports/purchases", (DateOnly from, DateOnly to, FinanceReportService s) => s.PurchasesAsync(from, to)).RequirePermission("finance:read");
        f.MapGet("/reports/cashflow", (DateOnly from, DateOnly to, FinanceReportService s) => s.CashFlowAsync(from, to)).RequirePermission("finance:read");
    }
}
