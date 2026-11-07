using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class OpsApiTests(ApiFactory api)
{
    [Fact]
    public async Task Support_contract_budget_and_fleet_are_seeded()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var tickets = (await admin.GetFromJsonAsync<JsonObject>("/api/r/tickets"))!["data"]!.AsArray();
        Assert.NotEmpty(tickets);
        var ticket = (await admin.GetFromJsonAsync<JsonObject>($"/api/support/tickets/{tickets[0]!["id"]}"))!;
        Assert.Equal("IN_PROGRESS", ticket["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/tickets", new { })).StatusCode);

        var contracts = (await admin.GetFromJsonAsync<JsonObject>("/api/r/contracts"))!["data"]!.AsArray();
        var service = contracts.First(c => c!["title"]!.ToString().Contains("bakım"))!;
        var detail = (await admin.GetFromJsonAsync<JsonObject>($"/api/contracts/{service["id"]}"))!;
        Assert.Equal("ACTIVE", detail["status"]!.ToString());
        Assert.True(detail["state"]!.ToString() is "ACTIVE" or "EXPIRING");

        var budgets = (await admin.GetFromJsonAsync<JsonObject>("/api/r/budgets"))!["data"]!.AsArray();
        var budget = (await admin.GetFromJsonAsync<JsonObject>($"/api/budgets/{budgets[0]!["id"]}"))!;
        Assert.Equal("APPROVED", budget["status"]!.ToString());
        Assert.True(budget["rows"]!.AsArray().Count > 0);

        var vehicles = (await admin.GetFromJsonAsync<JsonObject>("/api/r/vehicles"))!["data"]!.AsArray();
        var vehicle = (await admin.GetFromJsonAsync<JsonObject>($"/api/fleet/vehicles/{vehicles[0]!["id"]}"))!;
        Assert.Equal("34 EP 001", vehicle["plate"]!.ToString());
        Assert.Equal("Kerem Polat", vehicle["driverName"]!.ToString());
    }
}
