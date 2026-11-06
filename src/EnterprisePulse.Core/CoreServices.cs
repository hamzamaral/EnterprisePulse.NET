using System.Reflection;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Metadata;
using EnterprisePulse.Core.Modules.Identity;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Workflow;
using EnterprisePulse.Core.Resources;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EnterprisePulse.Core;

public static class CoreServices
{
    public static IServiceCollection AddEnterprisePulseCore(this IServiceCollection services, string connectionString)
    {
        services.AddMemoryCache();
        services.AddScoped<CurrentUser>();
        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(connectionString, n => n.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
        services.AddSingleton<MetadataStore>();
        services.AddSingleton<StorageService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<MailService>();
        services.AddSingleton<TokenService>();

        services.AddScoped<NumberingService>();
        services.AddScoped<ResourceService>();
        services.AddScoped<PermissionResolver>();
        services.AddScoped<AuthService>();
        services.AddScoped<UsersService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<FilesService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<CompanyBootstrap>();
        services.AddScoped<DataSeeder>();
        services.AddScoped<MailQueue>();

        // Module services, hooks, workflow listeners and seed steps are discovered by convention.
        var types = typeof(CoreServices).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }).ToList();
        foreach (var t in types)
        {
            if (typeof(IResourceHook).IsAssignableFrom(t)) services.AddScoped(typeof(IResourceHook), t);
            if (typeof(IWorkflowListener).IsAssignableFrom(t)) services.AddScoped(typeof(IWorkflowListener), t);
            if (typeof(ISeedStep).IsAssignableFrom(t)) services.AddScoped(typeof(ISeedStep), t);
            if (typeof(IDashboardProvider).IsAssignableFrom(t)) services.AddScoped(typeof(IDashboardProvider), t);
            if (typeof(IScheduledJob).IsAssignableFrom(t))
            {
                services.AddScoped(typeof(IScheduledJob), t);
                services.AddScoped(t);
            }
            if (t.GetCustomAttribute<ModuleServiceAttribute>() != null) services.AddScoped(t);
        }
        return services;
    }
}

/// <summary>Registers a business module service in DI without touching <see cref="CoreServices"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ModuleServiceAttribute : Attribute;
