using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Hr;
using EnterprisePulse.Core.Resources;

namespace EnterprisePulse.Api.Endpoints;

public sealed record CancelLeaveRequest(string? Comment);
public sealed record AccrueRequest(int Year);
public sealed record EntitlementRequest(int EntitledDays);

public static class HrEndpoints
{
    public static void MapHr(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hr").WithTags("HR").RequireAuthorization();

        g.MapGet("/org-chart", (EmployeeService s) => s.OrgChartAsync());
        g.MapGet("/employees/me", (EmployeeService s) => s.MineAsync());
        g.MapPost("/employees/{id:guid}/terminate", (Guid id, TerminateRequest body, EmployeeService s) => s.TerminateAsync(id, body))
            .RequirePermission("employees:update");

        g.MapGet("/leave-types", (LeaveService s) => s.TypesAsync());
        g.MapGet("/leave-requests/working-days", async (DateOnly startDate, DateOnly endDate, LeaveService s) => new { days = await s.WorkingDaysAsync(startDate, endDate) });
        g.MapGet("/leave-requests", (bool? mine, LeaveRequestStatus? status, Guid? employeeId, string? search, int? page, int? pageSize, LeaveService s) =>
            s.ListAsync(mine ?? false, status, employeeId, search, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, 200)));
        g.MapGet("/leave-requests/{id:guid}", (Guid id, LeaveService s) => s.GetAsync(id));
        g.MapPost("/leave-requests", (CreateLeaveRequest body, LeaveService s) => s.CreateAsync(body));
        g.MapPost("/leave-requests/{id:guid}/cancel", (Guid id, CancelLeaveRequest body, LeaveService s) => s.CancelAsync(id, body.Comment));

        g.MapGet("/leave-balances", (Guid? employeeId, int? year, LeaveService s) => s.VisibleBalancesAsync(employeeId, year ?? DateTime.UtcNow.Year));
        g.MapPost("/leave-balances/accrue", async (AccrueRequest body, LeaveService s) => new { created = await s.AccrueAsync(body.Year) })
            .RequirePermission("leave:update");
        g.MapPatch("/leave-balances/{id:guid}", (Guid id, EntitlementRequest body, LeaveService s) => s.UpdateEntitlementAsync(id, body.EntitledDays))
            .RequirePermission("leave:update");

        g.MapGet("/shift-plan", (DateOnly from, DateOnly to, Guid? departmentId, AttendanceService s) => s.PlanAsync(from, to, departmentId))
            .RequirePermission("attendance:read");
        g.MapPut("/shift-plan", async (UpdateShiftPlanRequest body, AttendanceService s) => new { updated = await s.UpdatePlanAsync(body) })
            .RequirePermission("attendance:update");
        g.MapGet("/attendance/timesheet", (string month, Guid? departmentId, AttendanceService s) => s.TimesheetAsync(month, departmentId))
            .RequirePermission("attendance:read");
        g.MapGet("/attendance/timesheet/export", async (string month, Guid? departmentId, string? format, AttendanceService s, ExportService export) =>
        {
            var sheet = await s.TimesheetAsync(month, departmentId);
            List<string> headers = ["Sicil No", "Personel", "Departman", "Planlı gün", "Gelinen gün", "Geç kalınan", "Devamsız", "İzinli", "Planlı saat", "Çalışılan saat", "Fazla mesai (saat)"];
            static string H(int minutes) => (minutes / 60.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
            var rows = sheet.Rows.Select(r => new List<string>
            {
                r.EmployeeNo, r.EmployeeName, r.DepartmentName ?? "", r.Totals.PlannedDays.ToString(), r.Totals.PresentDays.ToString(), r.Totals.LateDays.ToString(),
                r.Totals.AbsentDays.ToString(), r.Totals.LeaveDays.ToString(), H(r.Totals.PlannedMinutes), H(r.Totals.WorkedMinutes), H(r.Totals.OvertimeMinutes),
            }).ToList();
            var file = export.Table($"Puantaj {month}", $"puantaj-{month}", headers, rows, format == "pdf" ? "pdf" : "xlsx");
            return Results.File(file.Content, file.ContentType, file.FileName);
        }).RequirePermission("attendance:read");
        g.MapPut("/attendance/records", (UpdateRecordRequest body, AttendanceService s) => s.UpdateRecordAsync(body))
            .RequirePermission("attendance:update");
        g.MapGet("/attendance/me", (string? month, AttendanceService s) => s.MineAsync(month));
        g.MapPost("/attendance/check-in", (AttendanceService s) => s.CheckInAsync());
        g.MapPost("/attendance/check-out", (AttendanceService s) => s.CheckOutAsync());
    }
}
