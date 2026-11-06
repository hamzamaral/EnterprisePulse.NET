using System.Text.Json.Nodes;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Metadata;
using EnterprisePulse.Core.Resources;

namespace EnterprisePulse.Api.Endpoints;

/// <summary>Generic metadata-driven CRUD for every resource: /api/r/{resource}.</summary>
public static class ResourceEndpoints
{
    private static readonly HashSet<string> Reserved = ["page", "pageSize", "search", "sort", "order", "format", "ids", "take"];

    public static ListQuery ToListQuery(this HttpRequest req)
    {
        var q = req.Query;
        var query = new ListQuery
        {
            Page = int.TryParse(q["page"], out var page) ? page : 1,
            PageSize = int.TryParse(q["pageSize"], out var size) ? size : 25,
            Search = q["search"].ToString() is { Length: > 0 } s ? s : null,
            Sort = q["sort"].ToString() is { Length: > 0 } sort ? sort : null,
            Order = q["order"].ToString() is { Length: > 0 } order ? order : null,
        };
        foreach (var (key, value) in q.Where(kv => !Reserved.Contains(kv.Key) && kv.Value.ToString().Length > 0))
        {
            query.Filters[key] = value.ToString();
        }
        return query;
    }

    public static void MapResources(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/r/{resource}").WithTags("Resources").RequireAuthorization();

        g.MapGet("", (string resource, HttpRequest req, ResourceService svc, MetadataStore meta, CurrentUser ctx) =>
        {
            Require(meta, ctx, resource, "read");
            return svc.ListAsync(resource, req.ToListQuery());
        });

        g.MapGet("/lookup", (string resource, HttpRequest req, ResourceService svc) =>
        {
            var query = req.ToListQuery();
            var ids = req.Query["ids"].ToString() is { Length: > 0 } raw ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries) : null;
            var take = int.TryParse(req.Query["take"], out var t) ? Math.Clamp(t, 1, 200) : 20;
            return svc.LookupAsync(resource, query.Search, ids, take, query.Filters);
        });

        g.MapGet("/export", async (string resource, HttpRequest req, ResourceService svc, ExportService export, MetadataStore meta, CurrentUser ctx) =>
        {
            Require(meta, ctx, resource, "read");
            var (r, rows, labels) = await svc.AllAsync(resource, req.ToListQuery());
            var file = export.Export(r, rows, labels, req.Query["format"] == "pdf" ? "pdf" : "xlsx", ctx.Locale);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });

        g.MapGet("/{id}", (string resource, string id, ResourceService svc, MetadataStore meta, CurrentUser ctx) =>
        {
            Require(meta, ctx, resource, "read");
            return svc.GetAsync(resource, id);
        });

        g.MapPost("", async (string resource, JsonObject body, ResourceService svc, MetadataStore meta, CurrentUser ctx) =>
        {
            Require(meta, ctx, resource, "create");
            var entity = await svc.CreateAsync(resource, body);
            if (entity is Core.Data.IEntity e) return Results.Ok(await svc.GetAsync(resource, e.Id.ToString()));
            return Results.Ok(new OneResult(entity, await svc.LabelsFor(meta.Get(resource), [entity])));
        });

        g.MapPatch("/{id}", async (string resource, string id, JsonObject body, ResourceService svc, MetadataStore meta, CurrentUser ctx) =>
        {
            Require(meta, ctx, resource, "update");
            await svc.UpdateAsync(resource, id, body);
            return Results.Ok(await svc.GetAsync(resource, id));
        });

        g.MapDelete("/{id}", async (string resource, string id, ResourceService svc, MetadataStore meta, CurrentUser ctx) =>
        {
            Require(meta, ctx, resource, "delete");
            await svc.DeleteAsync(resource, id);
            return Results.NoContent();
        });
    }

    private static void Require(MetadataStore meta, CurrentUser ctx, string resource, string action) =>
        ctx.Require(meta.Get(resource).PermissionFor(action, meta.Registry));
}
