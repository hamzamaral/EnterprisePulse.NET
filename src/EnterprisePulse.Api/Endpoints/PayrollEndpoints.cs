using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Modules.Payroll;

namespace EnterprisePulse.Api.Endpoints;

public static class PayrollEndpoints
{
    public static void MapPayroll(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/payroll").WithTags("Payroll").RequireAuthorization();
        g.MapPut("/parameters", (SavePayrollParameter body, PayrollService s) => s.SaveParameterAsync(body)).RequirePermission("payroll:update");
        g.MapPost("/salaries", (SaveSalary body, PayrollService s) => s.SaveSalaryAsync(body)).RequirePermission("payroll:update");
        g.MapPost("/periods", (OpenPeriod body, PayrollService s) => s.OpenAsync(body.Year, body.Month)).RequirePermission("payroll:create");
        g.MapGet("/periods/{id:guid}", (Guid id, PayrollService s) => s.GetAsync(id)).RequirePermission("payroll:read");
        g.MapPost("/payslips/{id:guid}", (Guid id, PayslipAdjust body, PayrollService s) => s.AdjustAsync(id, body)).RequirePermission("payroll:update");
        g.MapPost("/periods/{id:guid}/approve", (Guid id, PayrollService s) => s.ApproveAsync(id)).RequirePermission("payroll:approve");
        g.MapPost("/periods/{id:guid}/pay", (Guid id, PayPayroll body, PayrollService s) => s.PayAsync(id, body)).RequirePermission("payroll:pay");
    }
}

public sealed record OpenPeriod(int Year, int Month);
