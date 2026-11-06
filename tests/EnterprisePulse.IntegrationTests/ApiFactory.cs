using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Linq.Expressions;

namespace EnterprisePulse.IntegrationTests;

/// <summary>Runs the API against a throw-away database on the .NET compose Postgres (port 5442).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly string Database = "ep_test_" + Guid.NewGuid().ToString("N")[..8];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"Host=localhost;Port=5442;Database={Database};Username=erp;Password=erp");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBackgroundJobs>();
            services.AddScoped<IBackgroundJobs, NoJobs>();
        });
    }

    public async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonObject>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!["accessToken"]!.ToString());
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await using var conn = new Npgsql.NpgsqlConnection("Host=localhost;Port=5442;Database=postgres;Username=erp;Password=erp");
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS {Database} WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class NoJobs : IBackgroundJobs
    {
        public void Enqueue<T>(Expression<Func<T, Task>> job) where T : notnull { }
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
