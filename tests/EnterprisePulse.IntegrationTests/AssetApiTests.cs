using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class AssetApiTests(ApiFactory api)
{
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).ToString("yyyy-MM-dd");

    private static async Task<JsonObject> FirstAsync(HttpClient client, string url)
    {
        var res = await client.GetFromJsonAsync<JsonObject>(url);
        return res!["data"]!.AsArray().First()!.AsObject();
    }

    private static async Task<JsonObject> OkAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task Assign_acknowledge_transfer_return_and_maintenance()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var employee = await api.LoginAsync("employee@enterprisepulse.dev", "Employee123!");
        var kerem = (await employee.GetFromJsonAsync<JsonObject>("/api/hr/employees/me"))!["id"]!.ToString();
        var chair = await FirstAsync(admin, "/api/r/assets?status=IN_STOCK&search=Aeron");
        var id = chair["id"]!.ToString();

        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync($"/api/assets/{id}/assign", new { assigneeType = "EMPLOYEE", employeeId = kerem, assignedAt = Today })).StatusCode);
        var assignment = await OkAsync(await admin.PostAsJsonAsync($"/api/assets/{id}/assign", new { assigneeType = "EMPLOYEE", employeeId = kerem, assignedAt = Today, notes = "test" }));
        Assert.Equal("ACTIVE", assignment["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/assets/{id}/assign", new { assigneeType = "EMPLOYEE", employeeId = kerem, assignedAt = Today })).StatusCode);

        var detail = (await admin.GetFromJsonAsync<JsonObject>($"/api/assets/{id}"))!;
        Assert.Equal("ASSIGNED", detail["status"]!.ToString());
        Assert.Equal("Kerem Polat", detail["holder"]!["name"]!.ToString());
        Assert.NotNull(detail["depreciation"]);

        var mine = (await employee.GetFromJsonAsync<JsonArray>("/api/assets/assignments/mine"))!;
        Assert.Contains(mine, a => a!["id"]!.ToString() == assignment["id"]!.ToString() && a["acknowledgedAt"] == null);
        var doc = (await employee.GetFromJsonAsync<JsonObject>($"/api/assets/assignments/{assignment["id"]}"))!;
        Assert.True(doc["canAcknowledge"]!.GetValue<bool>());
        var signed = await OkAsync(await employee.PostAsJsonAsync($"/api/assets/assignments/{assignment["id"]}/acknowledge", new { }));
        Assert.NotNull(signed["assignment"]!["acknowledgedAt"]);
        Assert.False(signed["canAcknowledge"]!.GetValue<bool>());

        var finance = await FirstAsync(admin, "/api/r/departments?code=FIN");
        var transferred = await OkAsync(await admin.PostAsJsonAsync($"/api/assets/{id}/transfer", new { assigneeType = "DEPARTMENT", departmentId = finance["id"]!.ToString(), assignedAt = Today }));
        Assert.Equal("DEPARTMENT", transferred["assigneeType"]!.ToString());
        var history = (await admin.GetFromJsonAsync<JsonArray>($"/api/assets/{id}/assignments"))!;
        Assert.Equal(2, history.Count);
        Assert.Contains(history, a => a!["status"]!.ToString() == "RETURNED" && a["returnNotes"]!.ToString().StartsWith("Devir"));

        var depot = await FirstAsync(admin, "/api/r/asset-locations?code=DEPO");
        await OkAsync(await admin.PostAsJsonAsync($"/api/assets/{id}/return", new { returnedAt = Today, conditionIn = "FAIR", locationId = depot["id"]!.ToString() }));
        detail = (await admin.GetFromJsonAsync<JsonObject>($"/api/assets/{id}"))!;
        Assert.Equal("IN_STOCK", detail["status"]!.ToString());
        Assert.Equal("FAIR", detail["condition"]!.ToString());
        Assert.Null(detail["holder"]);

        var maintenance = await OkAsync(await admin.PostAsJsonAsync($"/api/assets/{id}/maintenances", new { type = "REPAIR", title = "Amortisör", startedAt = Today }));
        Assert.Equal("IN_MAINTENANCE", (await admin.GetFromJsonAsync<JsonObject>($"/api/assets/{id}"))!["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/assets/{id}/assign", new { assigneeType = "EMPLOYEE", employeeId = kerem, assignedAt = Today })).StatusCode);
        await OkAsync(await admin.PostAsJsonAsync($"/api/assets/{id}/maintenances/{maintenance["id"]}/complete", new { completedAt = Today, cost = 750 }));
        Assert.Equal("IN_STOCK", (await admin.GetFromJsonAsync<JsonObject>($"/api/assets/{id}"))!["status"]!.ToString());

        var label = await admin.GetAsync($"/api/assets/{id}/label");
        label.EnsureSuccessStatusCode();
        Assert.Equal("image/png", label.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Generic_writes_cannot_bypass_asset_rules()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/asset-assignments", new { })).StatusCode);
        var retired = await FirstAsync(admin, "/api/r/assets?status=RETIRED");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/r/assets/{retired["id"]}", new { name = "x" })).StatusCode);
        var assigned = await FirstAsync(admin, "/api/r/assets?status=ASSIGNED");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/r/assets/{assigned["id"]}")).StatusCode);
    }

    [Fact]
    public async Task Count_scan_and_complete()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var tablet = await FirstAsync(admin, "/api/r/assets?search=iPad");
        var count = await OkAsync(await admin.PostAsJsonAsync("/api/assets/counts", new { countDate = Today, categoryId = tablet["categoryId"]!.ToString() }));
        Assert.Equal("DRAFT", count["status"]!.ToString());
        Assert.True(count["expectedCount"]!.GetValue<int>() >= 1);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/assets/counts", new { countDate = Today, categoryId = tablet["categoryId"]!.ToString() })).StatusCode);

        var id = count["id"]!.ToString();
        var scan = await OkAsync(await admin.PostAsJsonAsync($"/api/assets/counts/{id}/scan", new { code = tablet["assetTag"]!.ToString().ToLowerInvariant() }));
        Assert.Equal("FOUND", scan["result"]!.ToString());
        scan = await OkAsync(await admin.PostAsJsonAsync($"/api/assets/counts/{id}/scan", new { code = $"http://localhost:5101/r/assets/{tablet["id"]}" }));
        Assert.Equal("ALREADY_FOUND", scan["result"]!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/assets/counts/{id}/scan", new { code = "YOK-123" })).StatusCode);

        var laptop = await FirstAsync(admin, "/api/r/assets?search=Latitude");
        scan = await OkAsync(await admin.PostAsJsonAsync($"/api/assets/counts/{id}/scan", new { code = laptop["serialNumber"]!.ToString() }));
        Assert.Equal("UNEXPECTED", scan["result"]!.ToString());
        (await admin.DeleteAsync($"/api/assets/counts/{id}/lines/{scan["line"]!["id"]}")).EnsureSuccessStatusCode();

        var line = (await admin.GetFromJsonAsync<JsonObject>($"/api/assets/counts/{id}"))!["lines"]!.AsArray()
            .First(l => l!["assetId"]!.ToString() == tablet["id"]!.ToString())!;
        await OkAsync(await admin.PatchAsJsonAsync($"/api/assets/counts/{id}/lines/{line["id"]}", new { condition = "DAMAGED" }));
        var done = await OkAsync(await admin.PostAsJsonAsync($"/api/assets/counts/{id}/complete", new { applyConditions = true }));
        Assert.Equal("COMPLETED", done["status"]!.ToString());
        Assert.Equal(0, done["unexpectedCount"]!.GetValue<int>());
        Assert.Equal("DAMAGED", (await admin.GetFromJsonAsync<JsonObject>($"/api/assets/{tablet["id"]}"))!["condition"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/assets/counts/{id}/scan", new { code = tablet["assetTag"]!.ToString() })).StatusCode);
    }
}
