using System.Text.Json.Nodes;

namespace EnterprisePulse.Web.Services;

public sealed class LocalizedText
{
    public string Tr { get; set; } = "";
    public string En { get; set; } = "";
    public string For(string locale) => locale == "en" ? En : Tr;
}

public sealed class FieldMeta
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "string";
    public bool Required { get; set; }
    public bool ReadOnly { get; set; }
    public bool Hidden { get; set; }
    public LocalizedText Label { get; set; } = new();
    public string? Enum { get; set; }
    public List<string>? Values { get; set; }
    public int? Scale { get; set; }
    public string? Ref { get; set; }
    public string? Default { get; set; }

    public static readonly HashSet<string> System = ["id", "companyId", "createdAt", "updatedAt", "deletedAt", "createdById"];
    public bool IsSystem => System.Contains(Name);
}

public sealed class ParentMeta
{
    public string Field { get; set; } = "";
    public string Resource { get; set; } = "";
}

public sealed class ChildMeta
{
    public string Resource { get; set; } = "";
    public string Field { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ResourceMeta
{
    public string Resource { get; set; } = "";
    public string Model { get; set; } = "";
    public string Module { get; set; } = "";
    public LocalizedText Label { get; set; } = new();
    public List<string> LabelFields { get; set; } = [];
    public bool Tenant { get; set; }
    public bool SoftDelete { get; set; }
    public bool ReadOnly { get; set; }
    public bool CustomCreate { get; set; }
    public string? Sequence { get; set; }
    public bool DraftOnly { get; set; }
    public ParentMeta? Parent { get; set; }
    public List<string>? CompositeKey { get; set; }
    public List<FieldMeta> Fields { get; set; } = [];
    public List<ChildMeta> Children { get; set; } = [];

    public FieldMeta? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);
    public string KeyField => Field("id") != null ? "id" : Field("code") != null ? "code" : Fields[0].Name;
    public bool DraftLocked => DraftOnly && Children.Count > 0;
    public string? SequenceField => Sequence == null ? null : new[] { "number", "employeeNo", "assetTag", "code" }.FirstOrDefault(n => Field(n) != null);

    public string PermissionFor(string action, IReadOnlyDictionary<string, string[]> registry)
    {
        if (!registry.TryGetValue(Module, out var actions) || actions.Contains(action)) return $"{Module}:{action}";
        return action == "read" ? $"{Module}:read" : $"{Module}:update";
    }
}

public sealed class NavItemMeta
{
    public LocalizedText Label { get; set; } = new();
    public string? Resource { get; set; }
    public string? Page { get; set; }
    public string? Query { get; set; }
    public List<string> Permission { get; set; } = [];

    public string Href => Resource != null ? $"/r/{Resource}{(Query != null ? "?" + Query : "")}" : "/" + Page;
}

public sealed class NavGroupMeta
{
    public LocalizedText Label { get; set; } = new();
    public List<NavItemMeta> Items { get; set; } = [];
}

public sealed class PermissionActionMeta
{
    public string Code { get; set; } = "";
    public string Action { get; set; } = "";
    public LocalizedText Label { get; set; } = new();
}

public sealed class PermissionModuleMeta
{
    public string Module { get; set; } = "";
    public LocalizedText Label { get; set; } = new();
    public List<PermissionActionMeta> Actions { get; set; } = [];
}

public sealed class MetadataDocument
{
    public List<ResourceMeta> Resources { get; set; } = [];
    public List<NavGroupMeta> Navigation { get; set; } = [];
    public List<PermissionModuleMeta> Permissions { get; set; } = [];
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Enums { get; set; } = [];
    public JsonObject Common { get; set; } = [];

    private Dictionary<string, ResourceMeta>? _byName;
    private Dictionary<string, string[]>? _registry;

    public ResourceMeta? Get(string resource) =>
        (_byName ??= Resources.ToDictionary(r => r.Resource)).GetValueOrDefault(resource);

    public IReadOnlyDictionary<string, string[]> Registry =>
        _registry ??= Permissions.ToDictionary(p => p.Module, p => p.Actions.Select(a => a.Action).ToArray());

    public string EnumLabel(string? enumName, string? value, string locale) =>
        enumName != null && value != null && Enums.TryGetValue(enumName, out var byLocale)
            ? byLocale.GetValueOrDefault(locale)?.GetValueOrDefault(value) ?? value
            : value ?? "";
}
