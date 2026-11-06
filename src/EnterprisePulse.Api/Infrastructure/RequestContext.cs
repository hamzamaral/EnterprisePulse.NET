using System.Text.Json;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EnterprisePulse.Api.Infrastructure;

/// <summary>Fills <see cref="CurrentUser"/> from the JWT and the x-company-id / x-branch-id headers.</summary>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, CurrentUser ctx, AppDbContext db, AuthService auth, PermissionResolver permissions)
    {
        ctx.Ip = http.Connection.RemoteIpAddress?.ToString();
        ctx.UserAgent = http.Request.Headers.UserAgent.ToString();
        ctx.RequestId = http.TraceIdentifier;
        var lang = http.Request.Headers.AcceptLanguage.ToString();
        ctx.Locale = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";

        var sub = http.User.FindFirst("sub")?.Value;
        if (sub != null && Guid.TryParse(sub, out var userId))
        {
            using (ctx.WithoutTenant())
            {
                var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName, u.IsActive, u.IsSuperAdmin })
                    .FirstOrDefaultAsync();
                if (user == null || !user.IsActive) throw new UnauthorizedAppException("Hesap devre dışı");
                ctx.UserId = user.Id;
                ctx.Email = user.Email;
                ctx.FullName = $"{user.FirstName} {user.LastName}";
                ctx.IsSuperAdmin = user.IsSuperAdmin;
                var (companyId, branchId) = await auth.ResolveCompanyAsync(user.Id, user.IsSuperAdmin,
                    HeaderGuid(http, "x-company-id"), HeaderGuid(http, "x-branch-id"));
                ctx.CompanyId = companyId;
                ctx.BranchId = branchId;
                ctx.Permissions = await permissions.ResolveAsync(user.Id, companyId);
            }
        }
        await next(http);
    }

    private static Guid? HeaderGuid(HttpContext http, string name) =>
        Guid.TryParse(http.Request.Headers[name].ToString(), out var id) ? id : null;
}

public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext http)
    {
        try
        {
            await next(http);
        }
        catch (Exception ex) when (!http.Response.HasStarted)
        {
            var (status, message) = ex switch
            {
                AppException app => (app.Status, app.Message),
                DbUpdateConcurrencyException => (409, "Kayıt başka biri tarafından değiştirildi, sayfayı yenileyin"),
                DbUpdateException { InnerException: PostgresException pg } => pg.SqlState switch
                {
                    "23505" => (409, "Bu değer zaten kullanılıyor"),
                    "23503" => (409, "Kayıt başka kayıtlarda kullanıldığı için işlem yapılamadı"),
                    _ => (400, "Kayıt kaydedilemedi"),
                },
                JsonException or BadHttpRequestException or FormatException => (400, "Geçersiz istek gövdesi"),
                _ => (500, "Beklenmeyen bir hata oluştu"),
            };
            if (status >= 500) log.LogError(ex, "Unhandled error on {Path}", http.Request.Path);
            http.Response.StatusCode = status;
            await http.Response.WriteAsJsonAsync(new { statusCode = status, message, requestId = http.TraceIdentifier });
        }
    }
}

public static class PermissionFilters
{
    /// <summary>Endpoint requires login and every listed permission code.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params string[] codes) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            foreach (var code in codes) ctx.Require(code);
            return await next(context);
        });
        return builder;
    }

    public static TBuilder RequireAnyPermission<TBuilder>(this TBuilder builder, params string[] codes) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
            if (!ctx.HasAny(codes)) throw new ForbiddenAppException();
            return await next(context);
        });
        return builder;
    }
}
