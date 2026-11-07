using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class WorkApiTests(ApiFactory api)
{
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private static async Task<JsonObject> OkAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task Lead_converts_expense_hits_project_and_time_is_costed()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var code = "CRM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var category = (await OkAsync(await admin.PostAsJsonAsync("/api/r/expense-categories", new
        {
            code, name = "Test fiş", kind = "RECEIPT", requiresReceipt = true, isActive = true,
        })))["data"]!;
        var perDiem = (await OkAsync(await admin.PostAsJsonAsync("/api/r/expense-categories", new
        {
            code = code + "-H", name = "Test harcırah", kind = "PER_DIEM", rate = 100, requiresReceipt = false, isActive = true,
        })))["data"]!;
        var people = (await admin.GetFromJsonAsync<JsonObject>("/api/r/employees?pageSize=100"))!["data"]!.AsArray();
        var deniz = people.First(p => p!["firstName"]!.ToString() == "Deniz" && p["lastName"]!.ToString() == "Aksoy");
        var project = await OkAsync(await admin.PostAsJsonAsync("/api/projects", new
        {
            name = code, managerId = deniz["id"]!.ToString(), budgetAmount = 1000, managerHourlyCost = 50,
        }));
        await OkAsync(await admin.PostAsJsonAsync($"/api/projects/{project["id"]}/status", new { status = "ACTIVE" }));

        var blocked = await admin.PostAsJsonAsync("/api/expenses/reports", new
        {
            employeeId = deniz["id"]!.ToString(), title = code, lines = new[]
            {
                new { categoryId = category["id"]!.ToString(), expenseDate = Today, description = "Fişsiz", amount = 10 },
            },
        });
        var draft = await OkAsync(blocked);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/expenses/reports/{draft["id"]}/submit", null)).StatusCode);

        var report = await OkAsync(await admin.PostAsJsonAsync("/api/expenses/reports", new
        {
            employeeId = deniz["id"]!.ToString(), projectId = project["id"]!.ToString(), title = code + " harcırah",
            lines = new[] { new { categoryId = perDiem["id"]!.ToString(), expenseDate = Today, description = "İki gün", quantity = 2 } },
        }));
        Assert.Equal(200m, report["totalAmount"]!.GetValue<decimal>());
        var submitted = await OkAsync(await admin.PostAsync($"/api/expenses/reports/{report["id"]}/submit", null));
        Assert.Equal("PENDING", submitted["status"]!.ToString());
        await OkAsync(await admin.PostAsJsonAsync($"/api/workflow/instances/{submitted["workflowInstanceId"]}/approve", new { comment = "uygun" }));
        var empty = (await OkAsync(await admin.PostAsJsonAsync("/api/r/treasury-accounts", new { code, name = "Test kasa", type = "CASH", currencyCode = "TRY" })))["data"]!;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/expenses/reports/{report["id"]}/pay", new { accountId = empty["id"]!.ToString() })).StatusCode);
        var kasa = (await admin.GetFromJsonAsync<JsonObject>("/api/r/treasury-accounts?code=KASA-MRK"))!["data"]!.AsArray().First()!;
        var paid = await OkAsync(await admin.PostAsJsonAsync($"/api/expenses/reports/{report["id"]}/pay", new { accountId = kasa["id"]!.ToString() }));
        Assert.Equal("PAID", paid["status"]!.ToString());
        var costed = await OkAsync(await admin.GetAsync($"/api/projects/{project["id"]}"));
        Assert.Equal(200m, costed["expenseCost"]!.GetValue<decimal>());

        var lead = await OkAsync(await admin.PostAsJsonAsync("/api/crm/leads", new { companyName = code, source = "WEBSITE", estimatedValue = 80, currencyCode = "TRY" }));
        var converted = await OkAsync(await admin.PostAsJsonAsync($"/api/crm/leads/{lead["id"]}/convert", new { createOpportunity = true, amount = 80 }));
        Assert.Equal("CONVERTED", converted["status"]!.ToString());
        Assert.False(string.IsNullOrEmpty(converted["partnerId"]?.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/leads", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/projects", new { })).StatusCode);

        var timed = await OkAsync(await admin.PostAsJsonAsync($"/api/projects/{project["id"]}/time", new { employeeId = deniz["id"]!.ToString(), workDate = Today, hours = 3 }));
        var entry = timed["time"]!.AsArray().First()!;
        Assert.Equal(150m, entry["costAmount"]!.GetValue<decimal>());
    }
}
