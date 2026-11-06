using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnterprisePulse.Core.Data;

namespace EnterprisePulse.Core.Metadata;

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

    [System.Text.Json.Serialization.JsonIgnore] public PropertyInfo Property { get; set; } = null!;
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
    public string Table { get; set; } = "";
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

    [System.Text.Json.Serialization.JsonIgnore] public Type ClrType { get; set; } = null!;

    /// <summary>Documents with lines are edited only while in draft; later steps go through actions.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool DraftLocked => DraftOnly && Children.Count > 0;

    public FieldMeta? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);

    /// <summary>Field filled from <see cref="Sequence"/> when left empty on create.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public FieldMeta? SequenceField => Sequence == null ? null : SequenceFieldNames.Select(Field).FirstOrDefault(f => f != null);

    public static readonly string[] SequenceFieldNames = ["number", "employeeNo", "assetTag", "code"];

    public string PermissionFor(string action, IReadOnlyDictionary<string, string[]> registry)
    {
        if (!registry.TryGetValue(Module, out var actions)) return $"{Module}:{action}";
        if (actions.Contains(action)) return $"{Module}:{action}";
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

public sealed class RoleSeedMeta
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Codes { get; set; } = [];
}

public sealed class SequenceMeta
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public bool Yearly { get; set; }
}

public sealed class MetadataDocument
{
    public List<ResourceMeta> Resources { get; set; } = [];
    public List<NavGroupMeta> Navigation { get; set; } = [];
    public List<PermissionModuleMeta> Permissions { get; set; } = [];
    public Dictionary<string, RoleSeedMeta> Roles { get; set; } = [];
    public List<SequenceMeta> Sequences { get; set; } = [];
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> Enums { get; set; } = [];
    public JsonObject Common { get; set; } = [];
}

/// <summary>Generated resource metadata (tools/codegen) bound to the CLR entity types.</summary>
public sealed class MetadataStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MetadataDocument Document { get; }
    public IReadOnlyDictionary<string, ResourceMeta> Resources { get; }
    public IReadOnlyDictionary<Type, ResourceMeta> ByType { get; }
    public IReadOnlyDictionary<string, string[]> Registry { get; }
    public string RawJson { get; }

    public MetadataStore()
    {
        using var stream = typeof(MetadataStore).Assembly.GetManifestResourceStream("metadata.json")
            ?? throw new InvalidOperationException("metadata.json is not embedded");
        using var reader = new StreamReader(stream);
        RawJson = reader.ReadToEnd();
        Document = JsonSerializer.Deserialize<MetadataDocument>(RawJson, JsonOptions)!;

        var assembly = typeof(AppDbContext).Assembly;
        foreach (var r in Document.Resources)
        {
            r.ClrType = assembly.GetType($"{typeof(AppDbContext).Namespace}.{r.Model}")
                ?? throw new InvalidOperationException($"Entity type {r.Model} not found");
            foreach (var f in r.Fields)
            {
                var pascal = char.ToUpperInvariant(f.Name[0]) + f.Name[1..];
                f.Property = r.ClrType.GetProperty(pascal) ?? r.ClrType.GetProperty(pascal + "Value")
                    ?? throw new InvalidOperationException($"{r.Model}.{pascal} not found");
            }
        }
        Resources = Document.Resources.ToDictionary(r => r.Resource);
        ByType = Document.Resources.ToDictionary(r => r.ClrType);
        Registry = Document.Permissions.ToDictionary(p => p.Module, p => p.Actions.Select(a => a.Action).ToArray());
    }

    public ResourceMeta Get(string resource) =>
        Resources.TryGetValue(resource, out var meta) ? meta : throw new Common.NotFoundAppException($"Bilinmeyen kaynak: {resource}");

    public IEnumerable<string> AllPermissionCodes => Document.Permissions.SelectMany(p => p.Actions.Select(a => a.Code));
}
