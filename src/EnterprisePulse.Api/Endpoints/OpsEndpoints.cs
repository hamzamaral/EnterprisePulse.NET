using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Modules.Budgets;
using EnterprisePulse.Core.Modules.Contracts;
using EnterprisePulse.Core.Modules.Fleet;
using EnterprisePulse.Core.Modules.Support;

namespace EnterprisePulse.Api.Endpoints;

public static class OpsEndpoints
{
    public static void MapOps(this IEndpointRouteBuilder app)
    {
        var support = app.MapGroup("/api/support").WithTags("Support").RequireAuthorization();
        support.MapPost("/tickets", (SaveTicket body, SupportService s) => s.CreateAsync(body));
        support.MapGet("/tickets/{id:guid}", (Guid id, SupportService s) => s.GetAsync(id));
        support.MapPut("/tickets/{id:guid}", (Guid id, UpdateTicket body, SupportService s) => s.UpdateAsync(id, body)).RequirePermission("support:update");
        support.MapPost("/tickets/{id:guid}/status", (Guid id, ChangeTicketStatus body, SupportService s) => s.ChangeStatusAsync(id, body)).RequirePermission("support:update");
        support.MapPost("/tickets/{id:guid}/comments", (Guid id, TicketCommentBody body, SupportService s) => s.CommentAsync(id, body));
        support.MapPost("/tickets/{id:guid}/close", (Guid id, CloseTicket body, SupportService s) => s.CloseAsync(id, body));
        support.MapPost("/tickets/{id:guid}/rate", (Guid id, RateTicket body, SupportService s) => s.RateAsync(id, body));
        support.MapPost("/tickets/{id:guid}/reopen", (Guid id, ReopenTicket body, SupportService s) => s.ReopenAsync(id, body));
        support.MapGet("/mine", (SupportService s) => s.MineAsync());

        var contracts = app.MapGroup("/api/contracts").WithTags("Contracts").RequireAuthorization();
        contracts.MapPost("/", (SaveContract body, ContractService s) => s.CreateAsync(body)).RequirePermission("contracts:create");
        contracts.MapGet("/{id:guid}", (Guid id, ContractService s) => s.GetAsync(id));
        contracts.MapPost("/{id:guid}/activate", (Guid id, ActivateBody body, ContractService s) => s.ActivateAsync(id, body.SignedDate)).RequirePermission("contracts:update");
        contracts.MapPost("/{id:guid}/renew", (Guid id, RenewContract body, ContractService s) => s.RenewAsync(id, body)).RequirePermission("contracts:update");
        contracts.MapPost("/{id:guid}/terminate", (Guid id, TerminateContract body, ContractService s) => s.TerminateAsync(id, body)).RequirePermission("contracts:update");
        contracts.MapPost("/run", (ContractService s) => s.RunCompanyAsync()).RequirePermission("contracts:update");

        var budgets = app.MapGroup("/api/budgets").WithTags("Budgets").RequireAuthorization();
        budgets.MapPost("/items", (SaveBudgetItem body, BudgetService s) => s.SaveItemAsync(body)).RequirePermission("budgets:update");
        budgets.MapPost("/", (SaveBudget body, BudgetService s) => s.CreateAsync(body)).RequirePermission("budgets:update");
        budgets.MapGet("/{id:guid}", (Guid id, BudgetService s) => s.GetAsync(id));
        budgets.MapPost("/{id:guid}/plan", (Guid id, SavePlan body, BudgetService s) => s.SavePlanAsync(id, body)).RequirePermission("budgets:update");
        budgets.MapPost("/{id:guid}/approve", (Guid id, BudgetService s) => s.ApproveAsync(id)).RequirePermission("budgets:approve");
        budgets.MapPost("/{id:guid}/reopen", (Guid id, BudgetService s) => s.ReopenAsync(id)).RequirePermission("budgets:update");
        budgets.MapPost("/entries", (SaveEntry body, BudgetService s) => s.AddEntryAsync(body)).RequirePermission("budgets:update");

        var fleet = app.MapGroup("/api/fleet").WithTags("Fleet").RequireAuthorization();
        fleet.MapPost("/vehicles", (SaveVehicle body, FleetService s) => s.CreateAsync(body)).RequirePermission("fleet:update");
        fleet.MapGet("/vehicles/{id:guid}", (Guid id, FleetService s) => s.GetAsync(id));
        fleet.MapPost("/vehicles/{id:guid}/assign", (Guid id, AssignVehicle body, FleetService s) => s.AssignAsync(id, body)).RequirePermission("fleet:update");
        fleet.MapPost("/vehicles/{id:guid}/return", (Guid id, ReturnVehicle body, FleetService s) => s.ReturnAsync(id, body)).RequirePermission("fleet:update");
        fleet.MapPost("/vehicles/{id:guid}/fuel", (Guid id, SaveFuel body, FleetService s) => s.AddFuelAsync(id, body));
        fleet.MapPost("/vehicles/{id:guid}/services", (Guid id, SaveService body, FleetService s) => s.AddServiceAsync(id, body)).RequirePermission("fleet:update");
        fleet.MapPost("/vehicles/{id:guid}/documents", (Guid id, SaveDocument body, FleetService s) => s.AddDocumentAsync(id, body)).RequirePermission("fleet:update");
        fleet.MapGet("/mine", (FleetService s) => s.MineAsync());
    }
}

public sealed record ActivateBody(DateOnly? SignedDate);
