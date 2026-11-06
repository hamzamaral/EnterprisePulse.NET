using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Modules.Assets;

namespace EnterprisePulse.Api.Endpoints;

public static class AssetEndpoints
{
    public static void MapAssets(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/assets").WithTags("Assets").RequireAuthorization();

        g.MapGet("/assignments/mine", (AssignmentService s) => s.MineAsync());
        g.MapGet("/assignments/{id:guid}", (Guid id, AssignmentService s) => s.DocumentAsync(id));
        g.MapPost("/assignments/{id:guid}/acknowledge", (Guid id, AssignmentService s) => s.AcknowledgeAsync(id));

        g.MapPost("/counts", (CreateAssetCountRequest body, AssetCountService s) => s.CreateAsync(body)).RequirePermission("assets:count");
        g.MapGet("/counts/{id:guid}", (Guid id, AssetCountService s) => s.DetailAsync(id)).RequirePermission("assets:read");
        g.MapPost("/counts/{id:guid}/scan", (Guid id, ScanAssetRequest body, AssetCountService s) => s.ScanAsync(id, body)).RequirePermission("assets:count");
        g.MapPatch("/counts/{id:guid}/lines/{lineId:guid}", (Guid id, Guid lineId, UpdateCountLineRequest body, AssetCountService s) => s.UpdateLineAsync(id, lineId, body))
            .RequirePermission("assets:count");
        g.MapDelete("/counts/{id:guid}/lines/{lineId:guid}", async (Guid id, Guid lineId, AssetCountService s) =>
        {
            await s.RemoveLineAsync(id, lineId);
            return Results.NoContent();
        }).RequirePermission("assets:count");
        g.MapPost("/counts/{id:guid}/complete", (Guid id, CompleteAssetCountRequest body, AssetCountService s) => s.CompleteAsync(id, body)).RequirePermission("assets:count");
        g.MapPost("/counts/{id:guid}/cancel", (Guid id, AssetCountService s) => s.CancelAsync(id)).RequirePermission("assets:count");

        g.MapGet("/{id:guid}", (Guid id, AssetService s) => s.DetailAsync(id)).RequirePermission("assets:read");
        g.MapGet("/{id:guid}/label", async (Guid id, AssetService s) =>
        {
            var (png, name) = await s.LabelAsync(id);
            return Results.File(png, "image/png", name);
        }).RequirePermission("assets:read");
        g.MapPost("/{id:guid}/retire", (Guid id, RetireAssetRequest body, AssetService s) => s.RetireAsync(id, body)).RequirePermission("assets:update");
        g.MapGet("/{id:guid}/maintenances", (Guid id, AssetService s) => s.MaintenancesAsync(id)).RequirePermission("assets:read");
        g.MapPost("/{id:guid}/maintenances", (Guid id, CreateMaintenanceRequest body, AssetService s) => s.CreateMaintenanceAsync(id, body)).RequirePermission("assets:update");
        g.MapPost("/{id:guid}/maintenances/{maintenanceId:guid}/complete", (Guid id, Guid maintenanceId, CompleteMaintenanceRequest body, AssetService s) =>
            s.CompleteMaintenanceAsync(id, maintenanceId, body)).RequirePermission("assets:update");
        g.MapGet("/{id:guid}/assignments", (Guid id, AssignmentService s) => s.ForAssetAsync(id)).RequirePermission("assets:read");
        g.MapPost("/{id:guid}/assign", (Guid id, AssignAssetRequest body, AssignmentService s) => s.AssignAsync(id, body)).RequirePermission("assets:assign");
        g.MapPost("/{id:guid}/return", (Guid id, ReturnAssetRequest body, AssignmentService s) => s.ReturnAsync(id, body)).RequirePermission("assets:assign");
        g.MapPost("/{id:guid}/transfer", (Guid id, AssignAssetRequest body, AssignmentService s) => s.TransferAsync(id, body)).RequirePermission("assets:assign");
    }
}
