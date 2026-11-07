using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Modules.Accounting;
using EnterprisePulse.Core.Modules.Einvoice;
using EnterprisePulse.Core.Modules.Production;

namespace EnterprisePulse.Api.Endpoints;

/// <summary>Muhasebe, e-Fatura simülasyonu and Üretim/MRP. Lists and details of the plain records use the generic /api/r/{resource} endpoints.</summary>
public static class AccountingEndpoints
{
    public static void MapAccounting(this IEndpointRouteBuilder app)
    {
        var acc = app.MapGroup("/api/accounting").WithTags("Accounting").RequireAuthorization();
        acc.MapGet("/summary", (int? year, AccountingService s) => s.SummaryAsync(year)).RequirePermission("accounting:read");
        acc.MapGet("/options", (AccountingService s) => s.OptionsAsync()).RequirePermission("accounting:update");
        acc.MapGet("/settings", (AccountingService s) => s.SettingsAsync()).RequirePermission("accounting:read");
        acc.MapPatch("/settings", (UpdateAccountingSettings body, AccountingService s) => s.UpdateSettingsAsync(body)).RequirePermission("accounting:close");
        acc.MapGet("/pending", (AccountingService s) => s.PendingAsync()).RequirePermission("accounting:read");
        acc.MapPost("/run", (AccountingService s) => s.RunCompanyAsync()).RequirePermission("accounting:approve");
        acc.MapGet("/trial-balance", (DateOnly? from, DateOnly? to, bool? detailed, AccountingService s) => s.TrialBalanceAsync(from, to, detailed ?? false)).RequirePermission("accounting:read");
        acc.MapGet("/balance-sheet", (DateOnly? date, AccountingService s) => s.BalanceSheetAsync(date)).RequirePermission("accounting:read");
        acc.MapGet("/income-statement", (DateOnly? from, DateOnly? to, AccountingService s) => s.IncomeStatementAsync(from, to)).RequirePermission("accounting:read");

        var accounts = app.MapGroup("/api/ledger-accounts").WithTags("Accounting").RequireAuthorization();
        accounts.MapGet("/", (AccountingService s) => s.ListAccountsAsync()).RequirePermission("accounting:read");
        accounts.MapPost("/install", async (AccountingService s) => new { Added = await s.InstallChartAsync() }).RequirePermission("accounting:update");
        accounts.MapPost("/", (CreateLedgerAccount body, AccountingService s) => s.CreateAccountAsync(body)).RequirePermission("accounting:update");
        accounts.MapGet("/{id:guid}", (Guid id, AccountingService s) => s.GetAccountAsync(id)).RequirePermission("accounting:read");
        accounts.MapGet("/{id:guid}/ledger", (Guid id, DateOnly? from, DateOnly? to, Guid? partnerId, AccountingService s) => s.LedgerAsync(id, from, to, partnerId)).RequirePermission("accounting:read");
        accounts.MapPatch("/{id:guid}", (Guid id, UpdateLedgerAccount body, AccountingService s) => s.UpdateAccountAsync(id, body)).RequirePermission("accounting:update");
        accounts.MapDelete("/{id:guid}", async (Guid id, AccountingService s) =>
        {
            await s.RemoveAccountAsync(id);
            return Results.NoContent();
        }).RequirePermission("accounting:update");

        var entries = app.MapGroup("/api/journal-entries").WithTags("Accounting").RequireAuthorization();
        entries.MapGet("/{id:guid}", (Guid id, AccountingService s) => s.GetEntryAsync(id)).RequirePermission("accounting:read");
        entries.MapPost("/", (SaveJournalEntry body, AccountingService s) => s.CreateEntryAsync(body)).RequirePermission("accounting:update");
        entries.MapPatch("/{id:guid}", (Guid id, SaveJournalEntry body, AccountingService s) => s.UpdateEntryAsync(id, body)).RequirePermission("accounting:update");
        entries.MapDelete("/{id:guid}", async (Guid id, AccountingService s) =>
        {
            await s.RemoveEntryAsync(id);
            return Results.NoContent();
        }).RequirePermission("accounting:update");
        entries.MapPost("/{id:guid}/post", (Guid id, AccountingService s) => s.PostEntryAsync(id)).RequirePermission("accounting:approve");
        entries.MapPost("/{id:guid}/reverse", (Guid id, ReverseJournalEntry body, AccountingService s) => s.ReverseAsync(id, body)).RequirePermission("accounting:approve");

        var einvoice = app.MapGroup("/api/einvoices").WithTags("EInvoice").RequireAuthorization();
        einvoice.MapGet("/summary", (EInvoiceService s) => s.SummaryAsync()).RequirePermission("einvoice:read");
        einvoice.MapGet("/unsent", (EInvoiceService s) => s.UnsentAsync()).RequirePermission("einvoice:read");
        einvoice.MapGet("/settings", (EInvoiceService s) => s.SettingsAsync()).RequirePermission("einvoice:read");
        einvoice.MapPatch("/settings", (UpdateEInvoiceSettings body, EInvoiceService s) => s.UpdateSettingsAsync(body)).RequirePermission("einvoice:update");
        einvoice.MapPost("/", (SendEInvoice body, EInvoiceService s) => s.SendAsync(body)).RequirePermission("einvoice:send");
        einvoice.MapPost("/sync", (EInvoiceService s) => s.SyncAsync()).RequirePermission("einvoice:send");
        einvoice.MapPost("/simulate-incoming", (SimulateIncoming body, EInvoiceService s) => s.SimulateIncomingAsync(body)).RequirePermission("einvoice:update");
        einvoice.MapGet("/{id:guid}", (Guid id, EInvoiceService s) => s.GetAsync(id)).RequirePermission("einvoice:read");
        einvoice.MapPost("/{id:guid}/resend", (Guid id, EInvoiceService s) => s.ResendAsync(id)).RequirePermission("einvoice:send");
        einvoice.MapPost("/{id:guid}/respond", (Guid id, RespondEInvoice body, EInvoiceService s) => s.RespondAsync(id, body)).RequirePermission("einvoice:update");
        einvoice.MapPost("/{id:guid}/simulate-response", (Guid id, RespondEInvoice body, EInvoiceService s) => s.SimulateResponseAsync(id, body)).RequirePermission("einvoice:send");
        einvoice.MapPost("/{id:guid}/record", (Guid id, EInvoiceService s) => s.RecordAsync(id)).RequirePermission("einvoice:update", "purchasing:create");

        var production = app.MapGroup("/api/production").WithTags("Production").RequireAuthorization();
        production.MapGet("/summary", (ProductionService s) => s.SummaryAsync()).RequirePermission("production:read");
        production.MapGet("/requirements", (ProductionService s) => s.RequirementsAsync()).RequirePermission("production:read");
        production.MapPost("/requirements/purchase-request", (CreateRequirementRequest body, ProductionService s) => s.CreatePurchaseRequestAsync(body)).RequirePermission("production:read");
        production.MapPost("/boms", (CreateBom body, ProductionService s) => s.CreateBomAsync(body)).RequirePermission("production:update");
        production.MapGet("/boms/{id:guid}", (Guid id, ProductionService s) => s.GetBomAsync(id)).RequirePermission("production:read");
        production.MapGet("/boms/{id:guid}/plan", (Guid id, decimal? quantity, Guid? warehouseId, ProductionService s) => s.PlanAsync(id, quantity ?? 1, warehouseId)).RequirePermission("production:read");
        production.MapPatch("/boms/{id:guid}", (Guid id, UpdateBom body, ProductionService s) => s.UpdateBomAsync(id, body)).RequirePermission("production:update");
        production.MapDelete("/boms/{id:guid}", async (Guid id, ProductionService s) =>
        {
            await s.RemoveBomAsync(id);
            return Results.NoContent();
        }).RequirePermission("production:update");
        production.MapPost("/work-orders", (CreateWorkOrder body, ProductionService s) => s.CreateWorkOrderAsync(body)).RequirePermission("production:update");
        production.MapGet("/work-orders/{id:guid}", (Guid id, ProductionService s) => s.GetWorkOrderAsync(id)).RequirePermission("production:read");
        production.MapPatch("/work-orders/{id:guid}", (Guid id, UpdateWorkOrder body, ProductionService s) => s.UpdateWorkOrderAsync(id, body)).RequirePermission("production:update");
        production.MapPost("/work-orders/{id:guid}/release", (Guid id, ProductionService s) => s.ReleaseAsync(id)).RequirePermission("production:update");
        production.MapPost("/work-orders/{id:guid}/cancel", (Guid id, ProductionService s) => s.CancelAsync(id)).RequirePermission("production:update");
        production.MapPost("/work-orders/{id:guid}/complete", (Guid id, ProductionService s) => s.CompleteAsync(id)).RequirePermission("production:update");
        production.MapPost("/work-orders/{id:guid}/records", (Guid id, RecordProduction body, ProductionService s) => s.RecordAsync(id, body)).RequirePermission("production:record");
    }
}
