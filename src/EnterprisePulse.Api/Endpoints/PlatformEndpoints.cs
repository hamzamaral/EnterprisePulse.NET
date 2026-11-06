using System.Text.Json;
using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Metadata;
using EnterprisePulse.Core.Modules.Identity;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Workflow;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Api.Endpoints;

public sealed record CommentRequest(string? Comment);
public sealed record SendNotificationRequest(List<Guid> UserIds, string Title, string Body, string? Link, bool Email);

public static class PlatformEndpoints
{
    public static void MapPlatform(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow })).WithTags("Platform");
        app.MapGet("/api/meta", (MetadataStore meta) => Results.Text(meta.RawJson, "application/json")).RequireAuthorization().WithTags("Platform");

        app.MapGet("/api/dashboard", async (IEnumerable<IDashboardProvider> providers) =>
        {
            var sections = new List<DashboardSection>();
            foreach (var p in providers.OrderBy(p => p.Order))
            {
                if (await p.SectionAsync() is { Cards.Count: > 0 } section) sections.Add(section);
            }
            return sections;
        }).RequireAuthorization().WithTags("Platform");

        MapUsers(app.MapGroup("/api").WithTags("Users & Roles"));
        MapWorkflow(app.MapGroup("/api/workflow").WithTags("Workflow").RequireAuthorization());
        MapNotifications(app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization());
        MapFiles(app.MapGroup("/api/files").WithTags("Files"));

        app.MapGet("/api/settings/{key}", (string key, OrganizationService org) => org.GetSettingAsync(key))
            .RequirePermission("settings:read").WithTags("Settings");
        app.MapPut("/api/settings/{key}", (string key, JsonElement body, OrganizationService org) => org.SaveSettingAsync(key, body))
            .RequirePermission("settings:update").WithTags("Settings");
        app.MapPost("/api/companies", (CreateCompanyRequest body, OrganizationService org) => org.CreateCompanyAsync(body))
            .RequirePermission("companies:create").WithTags("Settings");
    }

    private static void MapUsers(RouteGroupBuilder g)
    {
        g.MapPost("/users", async (CreateUserRequest body, UsersService users) => Results.Ok(await users.CreateAsync(body)))
            .RequirePermission("users:create");
        g.MapGet("/users/{id:guid}/roles", (Guid id, UsersService users) => users.RoleIdsAsync(id)).RequirePermission("users:read");
        g.MapPut("/users/{id:guid}/roles", async (Guid id, UserRolesRequest body, UsersService users) =>
        {
            await users.UpdateRolesAsync(id, body);
            return Results.NoContent();
        }).RequirePermission("users:update");
        g.MapDelete("/users/{id:guid}/membership", async (Guid id, UsersService users) =>
        {
            await users.RemoveFromCompanyAsync(id);
            return Results.NoContent();
        }).RequirePermission("users:delete");

        g.MapGet("/roles/details", (UsersService users) => users.RolesAsync()).RequirePermission("roles:read");
        g.MapPost("/roles/details", (RoleRequest body, UsersService users) => users.SaveRoleAsync(null, body)).RequirePermission("roles:create");
        g.MapPut("/roles/details/{id:guid}", (Guid id, RoleRequest body, UsersService users) => users.SaveRoleAsync(id, body)).RequirePermission("roles:update");
        g.MapDelete("/roles/details/{id:guid}", async (Guid id, UsersService users) =>
        {
            await users.DeleteRoleAsync(id);
            return Results.NoContent();
        }).RequirePermission("roles:delete");
    }

    private static void MapWorkflow(RouteGroupBuilder g)
    {
        g.MapGet("/inbox", (int? page, int? pageSize, WorkflowService wf) => wf.InboxAsync(page ?? 1, pageSize ?? 25));
        g.MapGet("/inbox/count", async (WorkflowService wf) => new { count = await wf.InboxCountAsync() });
        g.MapGet("/instances", (WorkflowStatus? status, string? entityType, bool? mine, string? search, int? page, int? pageSize, WorkflowService wf) =>
            wf.ListAsync(status, entityType, mine ?? false, search, page ?? 1, pageSize ?? 25));
        g.MapGet("/instances/{id:guid}", (Guid id, WorkflowService wf) => wf.GetAsync(id));
        g.MapGet("/entity/{entityType}/{entityId}", async (string entityType, string entityId, WorkflowService wf) =>
            await wf.ForEntityAsync(entityType, entityId) is { } dto ? Results.Ok(dto) : Results.NoContent());
        g.MapPost("/start", (StartWorkflowRequest body, WorkflowService wf) => wf.StartAsync(body)).RequirePermission("workflow:start");
        g.MapPost("/instances/{id:guid}/approve", (Guid id, CommentRequest body, WorkflowService wf) => wf.ApproveAsync(id, body.Comment));
        g.MapPost("/instances/{id:guid}/reject", (Guid id, CommentRequest body, WorkflowService wf) => wf.RejectAsync(id, body.Comment ?? ""));
        g.MapPost("/instances/{id:guid}/cancel", (Guid id, CommentRequest body, WorkflowService wf) => wf.CancelAsync(id, body.Comment));
    }

    private static void MapNotifications(RouteGroupBuilder g)
    {
        g.MapGet("", async (bool? unread, int? page, int? pageSize, AppDbContext db, CurrentUser ctx) =>
        {
            var (p, size) = (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, 100));
            var q = Mine(db, ctx);
            if (unread == true) q = q.Where(n => n.ReadAt == null);
            var total = await q.CountAsync();
            var rows = await q.OrderByDescending(n => n.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync();
            return Paged<Notification>.Of(rows, total, p, size);
        });
        g.MapGet("/unread-count", async (AppDbContext db, CurrentUser ctx) => new { count = await Mine(db, ctx).CountAsync(n => n.ReadAt == null) });
        g.MapPost("/{id:guid}/read", async (Guid id, AppDbContext db, CurrentUser ctx) =>
        {
            await Mine(db, ctx).Where(n => n.Id == id && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow));
            return Results.NoContent();
        });
        g.MapPost("/read-all", async (AppDbContext db, CurrentUser ctx) =>
        {
            await Mine(db, ctx).Where(n => n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow));
            return Results.NoContent();
        });
        g.MapPost("/send", async (SendNotificationRequest body, NotificationService notifications, CurrentUser ctx) =>
        {
            Guard.That(body.UserIds.Count > 0 && !string.IsNullOrWhiteSpace(body.Title), "Alıcı ve başlık zorunludur");
            await notifications.NotifyAsync(new NotifyRequest(body.UserIds, ctx.CompanyId, "announcement", body.Title, body.Body, body.Link, body.Email));
            return Results.NoContent();
        }).RequirePermission("notifications:send");
    }

    private static IQueryable<Notification> Mine(AppDbContext db, CurrentUser ctx)
    {
        var userId = ctx.RequireUserId();
        var companyId = ctx.CompanyId;
        return db.Notifications.Where(n => n.UserId == userId && (n.CompanyId == null || n.CompanyId == companyId));
    }

    private static void MapFiles(RouteGroupBuilder g)
    {
        g.MapPost("", async (HttpRequest req, FilesService files) =>
        {
            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? throw new BadRequestAppException("Dosya seçilmedi");
            await using var stream = file.OpenReadStream();
            var saved = await files.UploadAsync(stream, file.FileName, file.ContentType, file.Length,
                form["entityType"].ToString() is { Length: > 0 } t ? t : null, form["entityId"].ToString() is { Length: > 0 } e ? e : null);
            return Results.Ok(saved);
        }).RequirePermission("files:create").DisableAntiforgery();
        g.MapGet("", (string entityType, string entityId, FilesService files) => files.ForEntityAsync(entityType, entityId)).RequirePermission("files:read");
        g.MapGet("/{id:guid}/download", async (Guid id, FilesService files) =>
        {
            var (file, content) = await files.DownloadAsync(id);
            return Results.File(content, file.MimeType, file.OriginalName);
        }).RequirePermission("files:read");
        g.MapDelete("/{id:guid}", async (Guid id, FilesService files) =>
        {
            await files.DeleteAsync(id);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
