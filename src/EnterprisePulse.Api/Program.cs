using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using EnterprisePulse.Api.Endpoints;
using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Identity;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Seeding;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Default")!;
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
var app0 = builder.Configuration.GetSection("App").Get<AppOptions>() ?? new AppOptions();

builder.Services.AddEnterprisePulseCore(connection);
builder.Services.AddSingleton<INotificationPusher, SignalRPusher>();
builder.Services.AddScoped<IBackgroundJobs, HangfireJobs>();
builder.Services.AddScoped<ScheduledJobRunner>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    o.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TokenService>((o, tokens) =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = tokens.ValidationParameters;
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(app0.CorsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    var testing = builder.Environment.IsEnvironment("Testing");
    var globalLimit = testing ? 100_000 : 300;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = globalLimit, Window = TimeSpan.FromMinutes(1) }));
    var authLimit = testing ? 1000 : 20;
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authLimit, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddHangfire(h => h
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connection), new PostgreSqlStorageOptions { SchemaName = "hangfire", PrepareSchemaIfNecessary = true }));
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHangfireServer(o => o.WorkerCount = 4);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "EnterprisePulse API (.NET)", Version = "v1" });
    o.CustomSchemaIds(t => t.FullName!.Replace('+', '.'));
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (app0.SeedOnStartup) await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync();
    if (!app.Environment.IsEnvironment("Testing"))
    {
        var jobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
        foreach (var job in scope.ServiceProvider.GetServices<IScheduledJob>())
        {
            jobs.AddOrUpdate<ScheduledJobRunner>(job.Id, r => r.RunAsync(job.Id), job.Cron, new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul") });
        }
    }
}

app.UseMiddleware<ErrorMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<RequestContextMiddleware>();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(o => o.DocumentTitle = "EnterprisePulse API");
app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [new LocalDashboardFilter()] });

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapAuth();
app.MapPlatform();
app.MapResources();
app.MapModules();
app.MapHub<NotificationsHub>("/hubs/notifications");

app.Run();

public partial class Program;
