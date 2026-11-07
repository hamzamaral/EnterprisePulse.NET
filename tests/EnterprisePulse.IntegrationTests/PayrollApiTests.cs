using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class PayrollApiTests(ApiFactory api)
{
    private static async Task<JsonObject> OkAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task Period_calculates_net_then_pays()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        await OkAsync(await admin.PutAsJsonAsync("/api/payroll/parameters", new
        {
            year = 2026, minimumWage = 26005.50m, sgkCeiling = 195041.40m,
            sgkEmployeeRate = 0.14m, unemploymentEmployeeRate = 0.01m, sgkEmployerRate = 0.1675m, unemploymentEmployerRate = 0.02m,
            stampTaxRate = 0.00759m, overtimeRate = 1.5m, monthlyHours = 225,
            taxBrackets = new object[] { new { upTo = 158000m, rate = 0.15m }, new { rate = 0.20m } },
        }));
        var people = (await admin.GetFromJsonAsync<JsonObject>("/api/r/employees?pageSize=100"))!["data"]!.AsArray();
        var deniz = people.First(p => p!["firstName"]!.ToString() == "Deniz" && p["lastName"]!.ToString() == "Aksoy");
        await OkAsync(await admin.PostAsJsonAsync("/api/payroll/salaries", new { employeeId = deniz["id"]!.ToString(), effectiveFrom = "2026-01-01", grossSalary = 45000 }));
        var period = await OkAsync(await admin.PostAsJsonAsync("/api/payroll/periods", new { year = 2026, month = 8 }));
        var slip = period["payslips"]!.AsArray().First(s => s!["employeeId"]!.ToString() == deniz["id"]!.ToString())!;
        Assert.True(slip["netPay"]!.GetValue<decimal>() < slip["grossPay"]!.GetValue<decimal>());
        Assert.True(slip["sgkEmployee"]!.GetValue<decimal>() > 0);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/payroll-periods", new { })).StatusCode);
        var approved = await OkAsync(await admin.PostAsync($"/api/payroll/periods/{period["id"]}/approve", null));
        Assert.Equal("APPROVED", approved["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/payroll/payslips/{slip["id"]}", new { missingDays = 1 })).StatusCode);
        var bank = (await admin.GetFromJsonAsync<JsonObject>("/api/r/treasury-accounts?code=BANK-TRY"))!["data"]!.AsArray().First()!;
        var paid = await OkAsync(await admin.PostAsJsonAsync($"/api/payroll/periods/{period["id"]}/pay", new { accountId = bank["id"]!.ToString() }));
        Assert.Equal("PAID", paid["status"]!.ToString());
    }
}
