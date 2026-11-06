using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class PlatformApiTests(ApiFactory api)
{
    [Fact]
    public async Task Login_rejects_wrong_password()
    {
        var res = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "admin@enterprisepulse.dev", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Me_returns_companies_and_permissions()
    {
        var client = await api.LoginAsync("manager@enterprisepulse.dev", "Manager123!");
        var me = await client.GetFromJsonAsync<JsonObject>("/api/auth/me");
        Assert.Equal(2, me!["companies"]!.AsArray().Count);
        Assert.Contains("workflow:read", me["permissions"]!.AsArray().Select(p => p!.ToString()));
    }

    [Fact]
    public async Task Employee_cannot_read_roles()
    {
        var client = await api.LoginAsync("employee@enterprisepulse.dev", "Employee123!");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/r/roles")).StatusCode);
    }

    [Fact]
    public async Task Generic_crud_with_validation_and_audit()
    {
        var client = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var missing = await client.PostAsJsonAsync("/api/r/units", new { code = "PK" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var created = await client.PostAsJsonAsync("/api/r/units", new { code = "PK", name = "Paket" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonObject>())!["data"]!["id"]!.ToString();

        var duplicate = await client.PostAsJsonAsync("/api/r/units", new { code = "PK", name = "Paket 2" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var updated = await client.PatchAsJsonAsync($"/api/r/units/{id}", new { name = "Paket (10'lu)" });
        Assert.Equal("Paket (10'lu)", (await updated.Content.ReadFromJsonAsync<JsonObject>())!["data"]!["name"]!.ToString());

        var list = await client.GetFromJsonAsync<JsonObject>("/api/r/units?search=paket");
        Assert.Equal(1, list!["meta"]!["total"]!.GetValue<int>());

        var audit = await client.GetFromJsonAsync<JsonObject>($"/api/r/audit-logs?entityType=Unit&entityId={id}");
        Assert.Equal(2, audit!["meta"]!["total"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/r/units/{id}")).StatusCode);
    }

    [Fact]
    public async Task Tenant_isolation_between_companies()
    {
        var client = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var me = await client.GetFromJsonAsync<JsonObject>("/api/auth/me");
        var logistics = me!["companies"]!.AsArray().First(c => c!["code"]!.ToString() == "PULSE-LOJ")!["id"]!.ToString();

        var pulseBranches = await client.GetFromJsonAsync<JsonObject>("/api/r/branches");
        var hq = pulseBranches!["data"]!.AsArray().First(b => b!["code"]!.ToString() == "MERKEZ")!["id"]!.ToString();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/r/branches/{hq}");
        request.Headers.Add("x-company-id", logistics);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Refresh_token_reuse_revokes_family()
    {
        var client = api.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new { email = "manager@enterprisepulse.dev", password = "Manager123!" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var first = login!["refreshToken"]!.ToString();
        var rotated = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first });
        rotated.EnsureSuccessStatusCode();
        var second = (await rotated.Content.ReadFromJsonAsync<JsonObject>())!["refreshToken"]!.ToString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = second })).StatusCode);
    }

    [Fact]
    public async Task Two_step_approval_flow()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var manager = await api.LoginAsync("manager@enterprisepulse.dev", "Manager123!");
        var employee = await api.LoginAsync("employee@enterprisepulse.dev", "Employee123!");

        var start = await admin.PostAsJsonAsync("/api/workflow/start", new { definitionCode = "PURCHASE_REQUEST", entityId = Guid.NewGuid().ToString(), title = "Test" });
        start.EnsureSuccessStatusCode();
        var id = (await start.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.ToString();

        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync($"/api/workflow/instances/{id}/approve", new { })).StatusCode);
        (await manager.PostAsJsonAsync($"/api/workflow/instances/{id}/approve", new { comment = "ok" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/workflow/instances/{id}/reject", new { comment = "x" })).StatusCode);

        var done = await admin.PostAsJsonAsync($"/api/workflow/instances/{id}/approve", new { });
        Assert.Equal("APPROVED", (await done.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/workflow/instances/{id}/approve", new { })).StatusCode);
    }
}
