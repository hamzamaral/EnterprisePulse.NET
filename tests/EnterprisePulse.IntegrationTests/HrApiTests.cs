using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class HrApiTests(ApiFactory api)
{
    private static DateOnly FirstWednesdayOfJune()
    {
        var d = new DateOnly(DateTime.UtcNow.Year, 6, 1);
        while (d.DayOfWeek != DayOfWeek.Wednesday) d = d.AddDays(1);
        return d;
    }

    private static JsonNode Balance(JsonArray balances, string type) =>
        balances.First(b => b!["leaveTypeName"]!.ToString() == type)!;

    [Fact]
    public async Task Leave_request_approval_updates_balance()
    {
        var employee = await api.LoginAsync("employee@enterprisepulse.dev", "Employee123!");
        var manager = await api.LoginAsync("manager@enterprisepulse.dev", "Manager123!");
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");

        var types = await employee.GetFromJsonAsync<JsonArray>("/api/hr/leave-types");
        var excuse = types!.First(t => t!["code"]!.ToString() == "MAZERET")!["id"]!.ToString();
        var day = FirstWednesdayOfJune();

        var before = Balance((await employee.GetFromJsonAsync<JsonArray>("/api/hr/leave-balances"))!, "Mazeret İzni");

        var created = await employee.PostAsJsonAsync("/api/hr/leave-requests", new { leaveTypeId = excuse, startDate = day, endDate = day, reason = "test" });
        created.EnsureSuccessStatusCode();
        var request = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("PENDING", request["status"]!.ToString());
        Assert.Equal(1, request["days"]!.GetValue<int>());

        var overlap = await employee.PostAsJsonAsync("/api/hr/leave-requests", new { leaveTypeId = excuse, startDate = day, endDate = day });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);

        var instance = request["workflowInstanceId"]!.ToString();
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync($"/api/workflow/instances/{instance}/approve", new { })).StatusCode);
        (await manager.PostAsJsonAsync($"/api/workflow/instances/{instance}/approve", new { comment = "ok" })).EnsureSuccessStatusCode();

        var approved = await employee.GetFromJsonAsync<JsonObject>($"/api/hr/leave-requests/{request["id"]}");
        Assert.Equal("APPROVED", approved!["status"]!.ToString());
        var after = Balance((await employee.GetFromJsonAsync<JsonArray>("/api/hr/leave-balances"))!, "Mazeret İzni");
        Assert.Equal(before["usedDays"]!.GetValue<int>() + 1, after["usedDays"]!.GetValue<int>());

        var cancelled = await admin.PostAsJsonAsync($"/api/hr/leave-requests/{request["id"]}/cancel", new { comment = "test" });
        if (day > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync($"/api/hr/leave-requests/{request["id"]}/cancel", new { })).StatusCode);
            cancelled.EnsureSuccessStatusCode();
            var restored = Balance((await employee.GetFromJsonAsync<JsonArray>("/api/hr/leave-balances"))!, "Mazeret İzni");
            Assert.Equal(before["usedDays"]!.GetValue<int>(), restored["usedDays"]!.GetValue<int>());
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        }
    }

    [Fact]
    public async Task Generic_leave_request_create_is_blocked()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var res = await admin.PostAsJsonAsync("/api/r/leave-requests", new { });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Employee_sees_only_own_hr_data()
    {
        var employee = await api.LoginAsync("employee@enterprisepulse.dev", "Employee123!");
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/hr/attendance/timesheet")).StatusCode);
        var me = await employee.GetFromJsonAsync<JsonObject>("/api/hr/employees/me");
        Assert.Equal("Kerem Polat", me!["fullName"]!.ToString());
        var mine = await employee.GetFromJsonAsync<JsonObject>("/api/hr/leave-requests");
        Assert.All(mine!["data"]!.AsArray(), r => Assert.Equal(me["id"]!.ToString(), r!["employeeId"]!.ToString()));
    }

    [Fact]
    public async Task Timesheet_and_org_chart_for_hr()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var month = DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM");
        var sheet = await admin.GetFromJsonAsync<JsonObject>($"/api/hr/attendance/timesheet?month={month}");
        Assert.Equal(15, sheet!["rows"]!.AsArray().Count);
        var chart = await admin.GetFromJsonAsync<JsonArray>("/api/hr/org-chart");
        Assert.NotEmpty(chart!);
    }
}
