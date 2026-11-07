using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class PeopleApiTests(ApiFactory api)
{
    [Fact]
    public async Task Payroll_hire_review_and_training_gap_are_seeded()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var periods = (await admin.GetFromJsonAsync<JsonObject>("/api/r/payroll-periods"))!["data"]!.AsArray();
        var october = periods.First(p => p!["month"]!.GetValue<int>() == 10);
        Assert.Equal("PAID", october!["status"]!.ToString());
        Assert.True(october["netTotal"]!.GetValue<decimal>() > 0);

        var applications = (await admin.GetFromJsonAsync<JsonObject>("/api/r/job-applications?pageSize=100"))!["data"]!.AsArray();
        var hired = applications.First(a => a!["stage"]!.ToString() == "HIRED")!;
        var detail = (await admin.GetFromJsonAsync<JsonObject>($"/api/recruitment/applications/{hired["id"]}"))!;
        Assert.Equal("HIRED", detail["stage"]!.ToString());
        Assert.Equal(72000, detail["offerSalary"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/job-postings", new { })).StatusCode);

        var reviews = (await admin.GetFromJsonAsync<JsonObject>("/api/performance/mine"))!["items"]!.AsArray();
        var kerem = reviews.First(r => r!["employeeName"]!.ToString() == "Kerem Polat" && r["status"]!.ToString() == "COMPLETED")!;
        Assert.Equal(4.36m, kerem["overallScore"]!.GetValue<decimal>());

        var gaps = (await admin.GetFromJsonAsync<JsonObject>("/api/training/compliance"))!["items"]!.AsArray();
        Assert.True(gaps.Count > 0);
        Assert.DoesNotContain(gaps, g => g!["employeeName"]!.ToString() == "Kerem Polat");
    }
}
