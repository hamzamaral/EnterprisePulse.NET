using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Metadata;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EnterprisePulse.Core.Resources;

/// <summary>Lets a module validate or enrich generic create / update / delete of one resource.</summary>
public interface IResourceHook
{
    string Resource { get; }
    /// <summary>Extra row restriction for list, lookup and find (rows that are not tenant-owned).</summary>
    IQueryable<T> Scope<T>(IQueryable<T> query) where T : class => query;
    Task BeforeSaveAsync(object entity, bool isNew) => Task.CompletedTask;
    Task AfterSaveAsync(object entity, bool isNew) => Task.CompletedTask;
    Task BeforeDeleteAsync(object entity) => Task.CompletedTask;
}

public sealed class ListQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string? Search { get; set; }
    public string? Sort { get; set; }
    public string? Order { get; set; }
    /// <summary>field=value, field__gte, field__lte, field__in (comma separated), field__null (true/false).</summary>
    public Dictionary<string, string> Filters { get; set; } = [];
}

public sealed record LookupItem(string Id, string Label);

public sealed record ListResult(IReadOnlyList<object> Data, PageMeta Meta, Dictionary<string, Dictionary<string, string>> Labels);

public sealed record OneResult(object Data, Dictionary<string, Dictionary<string, string>> Labels);

/// <summary>
/// Metadata-driven CRUD for every generated entity: paging, search, filters, foreign-key labels,
/// tenant-safe references, draft locks, document numbers and soft delete.
/// </summary>
public sealed class ResourceService(AppDbContext db, MetadataStore meta, NumberingService numbering, IEnumerable<IResourceHook> hooks)
{
    public static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly MethodInfo ILike = typeof(NpgsqlDbFunctionsExtensions)
        .GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string)])!;

    private IResourceHook? Hook(string resource) => hooks.FirstOrDefault(h => h.Resource == resource);

    private IQueryable<T> Scoped<T>(ResourceMeta r, IQueryable<T> q) where T : class =>
        Hook(r.Resource) is { } hook ? hook.Scope(q) : q;

    public IQueryable<T> Query<T>() where T : class => db.Set<T>();

    private IQueryable Set(ResourceMeta r) =>
        (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(r.ClrType).Invoke(db, null)!;

    // ---------- read ----------

    public Task<ListResult> ListAsync(string resource, ListQuery query) =>
        (Task<ListResult>)GetType().GetMethod(nameof(ListTyped), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(meta.Get(resource).ClrType).Invoke(this, [meta.Get(resource), query])!;

    private async Task<ListResult> ListTyped<T>(ResourceMeta r, ListQuery query) where T : class
    {
        var q = Filtered(Scoped(r, db.Set<T>().AsNoTracking()), r, query);
        var total = await q.CountAsync();
        q = Sorted(q, r, query.Sort, query.Order);
        var pageSize = Math.Clamp(query.PageSize, 1, 5000);
        var page = Math.Max(1, query.Page);
        var rows = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var data = rows.Cast<object>().ToList();
        return new ListResult(data, Paged<object>.Of(data, total, page, pageSize).Meta, await LabelsFor(r, data));
    }

    /// <summary>All rows matching the query (export, reports); capped to keep memory bounded.</summary>
    public async Task<(ResourceMeta Meta, List<object> Rows, Dictionary<string, Dictionary<string, string>> Labels)> AllAsync(string resource, ListQuery query)
    {
        var r = meta.Get(resource);
        query.Page = 1;
        query.PageSize = 5000;
        var result = await ListAsync(resource, query);
        return (r, result.Data.ToList(), result.Labels);
    }

    public IQueryable<T> Filtered<T>(IQueryable<T> q, ResourceMeta r, ListQuery query) where T : class
    {
        var p = Expression.Parameter(typeof(T), "x");
        foreach (var (rawKey, rawValue) in query.Filters)
        {
            var parts = rawKey.Split("__");
            var field = r.Field(parts[0]);
            if (field == null || field.Hidden) continue;
            var op = parts.Length > 1 ? parts[1] : "eq";
            var member = Expression.Property(p, field.Property);
            Expression body;
            if (op == "null")
            {
                var isNull = Expression.Equal(member, Expression.Constant(null, field.Property.PropertyType));
                body = rawValue == "true" ? isNull : Expression.Not(isNull);
            }
            else if (op == "in")
            {
                var type = field.Property.PropertyType;
                var listType = typeof(List<>).MakeGenericType(type);
                var list = (IList)Activator.CreateInstance(listType)!;
                foreach (var v in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries)) list.Add(Convert(v, type));
                body = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [type], Expression.Constant(list), member);
            }
            else
            {
                var value = Expression.Constant(Convert(rawValue, field.Property.PropertyType), field.Property.PropertyType);
                body = op switch
                {
                    "gte" => Expression.GreaterThanOrEqual(member, value),
                    "lte" => Expression.LessThanOrEqual(member, value),
                    "gt" => Expression.GreaterThan(member, value),
                    "lt" => Expression.LessThan(member, value),
                    "ne" => Expression.NotEqual(member, value),
                    _ => Expression.Equal(member, value),
                };
            }
            q = q.Where(Expression.Lambda<Func<T, bool>>(body, p));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = Expression.Constant($"%{query.Search.Trim().Replace("%", "\\%").Replace("_", "\\_")}%");
            Expression? any = null;
            foreach (var f in r.Fields.Where(f => f.Kind is "string" or "text" && !f.Hidden))
            {
                var call = Expression.Call(ILike, Expression.Constant(EF.Functions), Expression.Property(p, f.Property), pattern);
                any = any == null ? call : Expression.OrElse(any, call);
            }
            if (any != null) q = q.Where(Expression.Lambda<Func<T, bool>>(any, p));
        }
        return q;
    }

    private static IQueryable<T> Sorted<T>(IQueryable<T> q, ResourceMeta r, string? sort, string? order)
    {
        var field = (sort == null ? null : r.Field(sort))
            ?? r.Field("createdAt") ?? r.Field(r.LabelFields[0]) ?? r.Fields[0];
        var desc = order == null ? field.Name is "createdAt" or "date" : order.Equals("desc", StringComparison.OrdinalIgnoreCase);
        var p = Expression.Parameter(typeof(T), "x");
        var lambda = Expression.Lambda(Expression.Property(p, field.Property), p);
        var method = desc ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy);
        var sorted = (IQueryable<T>)typeof(Queryable).GetMethods().First(m => m.Name == method && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(T), field.Property.PropertyType).Invoke(null, [q, lambda])!;
        var id = r.Field("id");
        if (id == null || id == field) return sorted;
        var idLambda = Expression.Lambda(Expression.Property(p, id.Property), p);
        return (IQueryable<T>)typeof(Queryable).GetMethods().First(m => m.Name == nameof(Queryable.ThenBy) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(T), id.Property.PropertyType).Invoke(null, [sorted, idLambda])!;
    }

    public async Task<OneResult> GetAsync(string resource, string id)
    {
        var r = meta.Get(resource);
        var entity = await FindAsync(r, id, tracked: false);
        return new OneResult(entity, await LabelsFor(r, [entity]));
    }

    public async Task<object> FindAsync(ResourceMeta r, string id, bool tracked = true)
    {
        var key = KeyField(r);
        var value = Convert(id, key.Property.PropertyType);
        var method = GetType().GetMethod(nameof(FindTyped), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(r.ClrType);
        var entity = await (Task<object?>)method.Invoke(this, [r, key, value, tracked])!;
        return entity ?? throw new NotFoundAppException();
    }

    private async Task<object?> FindTyped<T>(ResourceMeta r, FieldMeta key, object value, bool tracked) where T : class
    {
        var p = Expression.Parameter(typeof(T), "x");
        var body = Expression.Equal(Expression.Property(p, key.Property), Expression.Constant(value, key.Property.PropertyType));
        IQueryable<T> q = Scoped(r, db.Set<T>());
        if (!tracked) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(Expression.Lambda<Func<T, bool>>(body, p));
    }

    private static FieldMeta KeyField(ResourceMeta r) =>
        r.Field("id") ?? r.Field("code") ?? throw new BadRequestAppException("Bu kaynak tekil anahtarla okunamaz");

    // ---------- labels / lookup ----------

    public string LabelOf(ResourceMeta r, object entity)
    {
        var parts = r.LabelFields.Select(name => r.Field(name)?.Property.GetValue(entity)?.ToString()).Where(s => !string.IsNullOrEmpty(s));
        var label = string.Join(" ", parts);
        return label.Length > 0 ? label : KeyField(r).Property.GetValue(entity)?.ToString() ?? "";
    }

    public async Task<Dictionary<string, Dictionary<string, string>>> LabelsFor(ResourceMeta r, IReadOnlyCollection<object> rows)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var f in r.Fields.Where(f => f.Ref != null))
        {
            var ids = rows.Select(row => f.Property.GetValue(row)).Where(v => v != null).Select(v => v!.ToString()!).Distinct().ToList();
            if (ids.Count == 0) continue;
            var items = await LookupAsync(f.Ref!, null, ids);
            result[f.Name] = items.ToDictionary(i => i.Id, i => i.Label);
        }
        return result;
    }

    public async Task<List<LookupItem>> LookupAsync(string resource, string? search, IReadOnlyCollection<string>? ids = null, int take = 20, Dictionary<string, string>? filters = null)
    {
        var r = meta.Get(resource);
        var method = GetType().GetMethod(nameof(LookupTyped), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(r.ClrType);
        return await (Task<List<LookupItem>>)method.Invoke(this, [r, search, ids, take, filters])!;
    }

    private async Task<List<LookupItem>> LookupTyped<T>(ResourceMeta r, string? search, IReadOnlyCollection<string>? ids, int take, Dictionary<string, string>? filters) where T : class
    {
        var key = KeyField(r);
        IQueryable<T> q = db.Set<T>().AsNoTracking();
        if (ids != null)
        {
            var type = key.Property.PropertyType;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
            foreach (var id in ids) list.Add(Convert(id, type));
            var p = Expression.Parameter(typeof(T), "x");
            var body = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [type], Expression.Constant(list), Expression.Property(p, key.Property));
            q = q.Where(Expression.Lambda<Func<T, bool>>(body, p));
            // Labels of referenced rows from other tenants are never shown; deleted rows still get a label.
            q = q.IgnoreQueryFilters();
            if (r.Tenant && !db.User.BypassTenant)
            {
                var company = db.User.CompanyId;
                q = q.Where(x => EF.Property<Guid>(x, "CompanyId") == company);
            }
        }
        else
        {
            q = Filtered(Scoped(r, q), r, new ListQuery { Search = search, Filters = filters ?? [] });
            if (r.Field("isActive") != null) q = q.Where(x => EF.Property<bool>(x, "IsActive"));
            q = Sorted(q, r, r.LabelFields[0] == "id" ? null : r.LabelFields[0], "asc").Take(take);
        }
        var rows = await q.ToListAsync();
        return rows.Select(row => new LookupItem(key.Property.GetValue(row)!.ToString()!, LabelOf(r, row))).ToList();
    }

    // ---------- write ----------

    public async Task<object> CreateAsync(string resource, JsonObject body)
    {
        var r = meta.Get(resource);
        Guard.State(!r.ReadOnly && !r.CustomCreate, "Bu kayıt türü buradan oluşturulamaz");
        var entity = Activator.CreateInstance(r.ClrType)!;
        await ApplyAsync(r, entity, body, isNew: true);
        await AssertParentEditable(r, entity);
        if (r.Sequence != null && r.Field("number") is { } number && string.IsNullOrEmpty(number.Property.GetValue(entity) as string))
        {
            number.Property.SetValue(entity, await numbering.NextAsync(r.Sequence));
        }
        if (r.Field("createdById") is { } createdBy && createdBy.Property.GetValue(entity) == null)
        {
            createdBy.Property.SetValue(entity, db.User.UserId);
        }
        ValidateRequired(r, entity);
        if (Hook(resource) is { } hook) await hook.BeforeSaveAsync(entity, true);
        db.Add(entity);
        await SaveAsync();
        if (Hook(resource) is { } after) await after.AfterSaveAsync(entity, true);
        return entity;
    }

    public async Task<object> UpdateAsync(string resource, string id, JsonObject body)
    {
        var r = meta.Get(resource);
        Guard.State(!r.ReadOnly, "Bu kayıt türü değiştirilemez");
        var entity = await FindAsync(r, id);
        AssertDraft(r, entity);
        await AssertParentEditable(r, entity);
        await ApplyAsync(r, entity, body, isNew: false);
        ValidateRequired(r, entity);
        if (Hook(resource) is { } hook) await hook.BeforeSaveAsync(entity, false);
        await SaveAsync();
        if (Hook(resource) is { } after) await after.AfterSaveAsync(entity, false);
        return entity;
    }

    public async Task DeleteAsync(string resource, string id)
    {
        var r = meta.Get(resource);
        Guard.State(!r.ReadOnly, "Bu kayıt türü silinemez");
        var entity = await FindAsync(r, id);
        AssertDraft(r, entity);
        await AssertParentEditable(r, entity);
        if (Hook(resource) is { } hook) await hook.BeforeDeleteAsync(entity);
        db.Remove(entity);
        await SaveAsync();
    }

    public async Task SaveAsync()
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            throw pg.SqlState switch
            {
                "23505" => new ConflictAppException("Bu değer zaten kullanılıyor (tekil alan)"),
                "23503" => new ConflictAppException("Kayıt başka kayıtlarda kullanıldığı için bu işlem yapılamaz"),
                "23502" => new BadRequestAppException($"Zorunlu alan eksik: {pg.ColumnName}"),
                _ => ex,
            };
        }
    }

    private static void AssertDraft(ResourceMeta r, object entity)
    {
        if (!r.DraftLocked) return;
        var status = r.Field("status")!.Property.GetValue(entity)?.ToString();
        Guard.State(status == "DRAFT", "Yalnızca taslak belgeler düzenlenebilir veya silinebilir");
    }

    private async Task AssertParentEditable(ResourceMeta r, object entity)
    {
        if (r.Parent == null || !meta.Resources.TryGetValue(r.Parent.Resource, out var parent) || !parent.DraftLocked) return;
        var parentId = r.Field(r.Parent.Field)?.Property.GetValue(entity)?.ToString();
        if (parentId == null) return;
        var owner = await FindAsync(parent, parentId, tracked: false);
        var status = parent.Field("status")!.Property.GetValue(owner)?.ToString();
        Guard.State(status == "DRAFT", "Belge taslak değil; kalemleri değiştirilemez");
    }

    private async Task ApplyAsync(ResourceMeta r, object entity, JsonObject body, bool isNew)
    {
        foreach (var (key, node) in body)
        {
            var field = r.Field(key);
            if (field == null || field.Hidden) continue;
            if (field.ReadOnly && !(isNew && r.CompositeKey?.Contains(field.Name) == true)) continue;
            if (!isNew && r.DraftLocked && field.Name == "status") continue;
            if (!isNew && r.CompositeKey?.Contains(field.Name) == true) continue;
            var type = field.Property.PropertyType;
            object? value;
            try
            {
                value = node == null ? null : node.Deserialize(type, BodyOptions);
                if (value is string s && Nullable.GetUnderlyingType(type) == null && type == typeof(string)) value = s.Trim();
                if (value is string empty && empty.Length == 0 && field.Kind is not ("string" or "text")) value = null;
            }
            catch (Exception)
            {
                throw new BadRequestAppException($"Geçersiz değer: {field.Label.Tr}");
            }
            if (value == null && type.IsValueType && Nullable.GetUnderlyingType(type) == null)
            {
                throw new BadRequestAppException($"Zorunlu alan: {field.Label.Tr}");
            }
            if (field.Ref != null && value != null) await AssertReference(field, value);
            field.Property.SetValue(entity, value);
        }
    }

    /// <summary>A reference must point at a row the caller can see (same company, not deleted).</summary>
    private async Task AssertReference(FieldMeta field, object value)
    {
        if (!meta.Resources.TryGetValue(field.Ref!, out var target)) return;
        var found = await LookupAsync(target.Resource, null, [value.ToString()!]);
        if (found.Count == 0) throw new BadRequestAppException($"Geçersiz seçim: {field.Label.Tr}");
    }

    private static void ValidateRequired(ResourceMeta r, object entity)
    {
        foreach (var f in r.Fields.Where(f => f.Required && !f.ReadOnly))
        {
            var v = f.Property.GetValue(entity);
            var missing = v == null || (v is string s && s.Length == 0) || (v is Guid g && g == Guid.Empty);
            if (missing) throw new BadRequestAppException($"Zorunlu alan: {f.Label.Tr}");
        }
    }

    public static object? Convert(string raw, Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (raw == "null" && (Nullable.GetUnderlyingType(type) != null || !t.IsValueType)) return null;
        if (t == typeof(string)) return raw;
        if (t == typeof(Guid)) return Guid.TryParse(raw, out var g) ? g : throw new BadRequestAppException($"Geçersiz kimlik: {raw}");
        if (t.IsEnum) return Enum.TryParse(t, raw, true, out var e) ? e : throw new BadRequestAppException($"Geçersiz değer: {raw}");
        if (t == typeof(bool)) return raw is "true" or "1";
        if (t == typeof(DateOnly)) return DateOnly.Parse(raw[..Math.Min(10, raw.Length)]);
        if (t == typeof(DateTime)) return DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        return System.Convert.ChangeType(raw, t, System.Globalization.CultureInfo.InvariantCulture);
    }
}
