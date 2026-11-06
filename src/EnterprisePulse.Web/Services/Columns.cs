namespace EnterprisePulse.Web.Services;

public static class Columns
{
    private static readonly string[] Preferred = ["number", "code", "name", "title", "firstName", "lastName", "status", "date", "type"];

    /// <summary>Default list columns: label fields first, then the most informative scalar fields.</summary>
    public static List<FieldMeta> For(ResourceMeta r, string? exclude = null, int max = 8)
    {
        var view = ResourceViews.For(r.Resource);
        if (view.Columns != null) return view.Columns.Select(r.Field).Where(f => f != null && f.Name != exclude).Cast<FieldMeta>().ToList();

        var candidates = r.Fields.Where(f => !f.Hidden && f.Kind is not ("json" or "text") && f.Name != exclude
            && !view.HiddenFields.Contains(f.Name) && f.Name is not ("id" or "companyId" or "deletedAt" or "updatedAt" or "createdById" or "passwordHash")).ToList();
        var ordered = candidates
            .OrderBy(f => r.LabelFields.Contains(f.Name) ? 0 : Array.IndexOf(Preferred, f.Name) is var i and >= 0 ? 1 + i : 20)
            .ThenBy(f => f.Name == "createdAt" ? 1 : 0)
            .ToList();
        return ordered.Take(max).ToList();
    }

    public static bool AlignRight(FieldMeta f) => f.Kind is "decimal" or "int" or "long";
}
