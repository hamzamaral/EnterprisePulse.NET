using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Modules.Crm;
using EnterprisePulse.Core.Modules.Expenses;
using EnterprisePulse.Core.Modules.Projects;

namespace EnterprisePulse.Api.Endpoints;

public static class WorkEndpoints
{
    public static void MapWork(this IEndpointRouteBuilder app)
    {
        var crm = app.MapGroup("/api/crm").WithTags("CRM").RequireAuthorization();
        crm.MapPost("/leads", (SaveLead body, LeadService s) => s.CreateAsync(body)).RequirePermission("crm:create");
        crm.MapGet("/leads/{id:guid}", (Guid id, LeadService s) => s.GetAsync(id)).RequirePermission("crm:read");
        crm.MapPut("/leads/{id:guid}", (Guid id, SaveLead body, LeadService s) => s.UpdateAsync(id, body)).RequirePermission("crm:update");
        crm.MapPost("/leads/{id:guid}/disqualify", (Guid id, DisqualifyLead body, LeadService s) => s.DisqualifyAsync(id, body.Reason)).RequirePermission("crm:update");
        crm.MapPost("/leads/{id:guid}/convert", (Guid id, ConvertLead body, LeadService s) => s.ConvertAsync(id, body)).RequirePermission("crm:update");
        crm.MapPost("/opportunities", (SaveOpportunity body, OpportunityService s) => s.CreateAsync(body)).RequirePermission("crm:create");
        crm.MapGet("/opportunities/{id:guid}", (Guid id, OpportunityService s) => s.GetAsync(id)).RequirePermission("crm:read");
        crm.MapPost("/opportunities/{id:guid}/stage", (Guid id, ChangeStage body, OpportunityService s) => s.ChangeStageAsync(id, body.Stage, body.LostReason)).RequirePermission("crm:update");
        crm.MapPost("/activities", (SaveActivity body, ActivityService s) => s.CreateAsync(body)).RequirePermission("crm:create");
        crm.MapGet("/activities/{id:guid}", (Guid id, ActivityService s) => s.GetAsync(id)).RequirePermission("crm:read");
        crm.MapPost("/activities/{id:guid}/complete", (Guid id, ActivityService s) => s.CompleteAsync(id)).RequirePermission("crm:update");

        var expenses = app.MapGroup("/api/expenses").WithTags("Expenses").RequireAuthorization();
        expenses.MapGet("/reports/mine", (ExpenseReportService s) => s.MineAsync());
        expenses.MapPost("/reports", (SaveExpenseReport body, ExpenseReportService s) => s.CreateAsync(body)).RequirePermission("expenses:create");
        expenses.MapGet("/reports/{id:guid}", (Guid id, ExpenseReportService s) => s.GetAsync(id));
        expenses.MapPut("/reports/{id:guid}", (Guid id, SaveExpenseReport body, ExpenseReportService s) => s.UpdateAsync(id, body)).RequirePermission("expenses:update");
        expenses.MapPost("/reports/{id:guid}/submit", (Guid id, ExpenseReportService s) => s.SubmitAsync(id));
        expenses.MapPost("/reports/{id:guid}/pay", (Guid id, PayExpense body, ExpenseReportService s) => s.PayAsync(id, body)).RequirePermission("expenses:pay");
        expenses.MapPost("/reports/{id:guid}/cancel", (Guid id, CancelRequest body, ExpenseReportService s) => s.CancelAsync(id, body.Reason ?? body.Comment)).RequirePermission("expenses:update");

        var projects = app.MapGroup("/api/projects").WithTags("Projects").RequireAuthorization();
        projects.MapGet("/my-tasks", (ProjectService s) => s.MyTasksAsync());
        projects.MapGet("/time", (DateOnly? from, DateOnly? to, ProjectService s) => s.TimeAsync(from, to));
        projects.MapPost("/", (SaveProject body, ProjectService s) => s.CreateAsync(body)).RequirePermission("projects:create");
        projects.MapGet("/{id:guid}", (Guid id, ProjectService s) => s.GetAsync(id));
        projects.MapPost("/{id:guid}/status", (Guid id, ProjectStatusChange body, ProjectService s) => s.ChangeStatusAsync(id, body.Status)).RequirePermission("projects:update");
        projects.MapPost("/{id:guid}/members", (Guid id, AddMember body, ProjectService s) => s.AddMemberAsync(id, body)).RequirePermission("projects:update");
        projects.MapPost("/{id:guid}/tasks", (Guid id, SaveTask body, ProjectService s) => s.AddTaskAsync(id, body)).RequirePermission("projects:update");
        projects.MapPost("/tasks/{id:guid}/move", (Guid id, MoveTask body, ProjectService s) => s.MoveTaskAsync(id, body.Status));
        projects.MapPost("/{id:guid}/time", (Guid id, LogTime body, ProjectService s) => s.LogTimeAsync(id, body));
    }
}
