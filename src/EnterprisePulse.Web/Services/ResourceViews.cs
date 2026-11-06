using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace EnterprisePulse.Web.Services;

public sealed class ActionContext
{
    public required ApiClient Api { get; init; }
    public required JsonObject Record { get; init; }
    public required ISnackbar Snackbar { get; init; }
    public required IDialogService Dialogs { get; init; }
    public required NavigationManager Nav { get; init; }
    public required Func<Task> Reload { get; init; }
    public string Id => Record["id"]?.ToString() ?? "";
    public string Locale => Api.Session.Locale;
}

/// <summary>A button on a record (submit, approve, post, print...). Default run: POST Endpoint with {comment}.</summary>
public sealed class ResourceAction
{
    public required string Label { get; init; }
    public string LabelEn { get; init; } = "";
    public string Icon { get; init; } = Icons.Material.Filled.PlayArrow;
    public Color Color { get; init; } = Color.Primary;
    public string? Permission { get; init; }
    public Func<JsonObject, bool> Visible { get; init; } = _ => true;
    public string? Endpoint { get; init; }
    public string? Prompt { get; init; }
    public bool PromptRequired { get; init; }
    public string? Confirm { get; init; }
    public bool Download { get; init; }
    public Func<ActionContext, Task>? Run { get; init; }

    public string Text(string locale) => locale == "en" && LabelEn.Length > 0 ? LabelEn : Label;

    public static Func<JsonObject, bool> StatusIn(params string[] statuses) =>
        r => statuses.Contains(r["status"]?.ToString());
}

/// <summary>Button in the list header that opens a related page (balances, weekly plan...).</summary>
public sealed record HeaderLink(string Label, string LabelEn, string Href, string Icon, string? Permission = null);

public sealed class ResourceTab
{
    public required string Title { get; init; }
    public required Type Component { get; init; }
    public string? Permission { get; init; }
}

/// <summary>Per-resource tweaks over the generic metadata screens; business phases register theirs here.</summary>
public sealed class ResourceView
{
    public string[]? Columns { get; set; }
    public string[] HiddenFields { get; set; } = [];
    public string[] ReadOnlyFields { get; set; } = [];
    public string[]? FilterFields { get; set; }
    public bool HideCreate { get; set; }
    public bool HideEdit { get; set; }
    public bool HideDelete { get; set; }
    public string? CreateHref { get; set; }
    public Type? CreateDialog { get; set; }
    public List<ResourceAction> Actions { get; } = [];
    public List<ResourceTab> Tabs { get; } = [];
    public List<HeaderLink> Links { get; } = [];
    public string[] HiddenChildren { get; set; } = [];
    public string? WorkflowEntity { get; set; }
}

public static partial class ResourceViews
{
    private static readonly Dictionary<string, ResourceView> Map = [];

    public static ResourceView For(string resource) => Map.TryGetValue(resource, out var v) ? v : new ResourceView();

    public static ResourceView Configure(string resource, Action<ResourceView> configure)
    {
        if (!Map.TryGetValue(resource, out var view)) Map[resource] = view = new ResourceView();
        configure(view);
        return view;
    }

    static ResourceViews()
    {
        Platform();
        Hr();
    }

    static void Hr()
    {
        Configure("employees", v =>
        {
            v.Columns = ["employeeNo", "firstName", "lastName", "departmentId", "positionId", "managerId", "hireDate", "status"];
            v.FilterFields = ["status", "departmentId", "positionId", "branchId", "employmentType", "managerId"];
            v.ReadOnlyFields = ["status", "terminationDate", "terminationReason"];
            v.HiddenChildren = ["shift-assignments", "attendance-records", "salary-records"];
            v.Links.Add(new HeaderLink("Organizasyon şeması", "Org chart", "/hr/org-chart", Icons.Material.Filled.AccountTree));
            v.Actions.Add(new ResourceAction
            {
                Label = "İşten çıkış", LabelEn = "Terminate", Icon = Icons.Material.Filled.PersonOff, Color = Color.Error, Permission = "employees:update",
                Visible = ResourceAction.StatusIn("ACTIVE"), Run = Components.Pages.Hr.TerminateDialog.OpenAsync,
            });
        });
        Configure("departments", v => v.Columns = ["code", "name", "parentId", "managerId", "isActive"]);
        Configure("positions", v => v.Columns = ["code", "name", "departmentId", "isActive"]);
        Configure("leave-requests", v =>
        {
            v.Columns = ["number", "employeeId", "leaveTypeId", "startDate", "endDate", "days", "status", "createdAt"];
            v.FilterFields = ["status", "employeeId", "leaveTypeId", "startDate"];
            v.CreateDialog = typeof(Components.Pages.Hr.LeaveRequestDialog);
            v.HideEdit = v.HideDelete = true;
            v.WorkflowEntity = "LeaveRequest";
            v.Actions.Add(new ResourceAction
            {
                Label = "İptal et", LabelEn = "Cancel", Icon = Icons.Material.Filled.Cancel, Color = Color.Error,
                Visible = ResourceAction.StatusIn("PENDING", "APPROVED"), Endpoint = "/api/hr/leave-requests/{id}/cancel",
                Prompt = "Açıklama", Confirm = "Talep iptal edilecek; onaylanmış izinlerde günler bakiyeye iade edilir.",
            });
        });
        Configure("leave-types", v =>
        {
            v.Columns = ["code", "name", "defaultDays", "isPaid", "requiresBalance", "color", "isActive"];
            v.Links.Add(new HeaderLink("İzin bakiyeleri", "Balances", "/hr/leave-balances", Icons.Material.Filled.AccountBalanceWallet));
            v.Links.Add(new HeaderLink("Resmi tatiller", "Public holidays", "/r/holidays", Icons.Material.Filled.Celebration));
        });
        Configure("holidays", v => v.Columns = ["date", "name"]);
        Configure("leave-balances", v => v.Columns = ["employeeId", "leaveTypeId", "year", "entitledDays", "usedDays"]);
        Configure("shifts", v =>
        {
            v.Columns = ["code", "name", "startTime", "endTime", "breakMinutes", "color", "isActive"];
            v.Links.Add(new HeaderLink("Haftalık plan", "Weekly plan", "/hr/shift-plan", Icons.Material.Filled.CalendarViewWeek));
            v.Links.Add(new HeaderLink("Puantaj", "Timesheet", "/hr/timesheet", Icons.Material.Filled.GridOn));
        });
    }

    static void Platform()
    {
        Configure("users", v =>
        {
            v.Columns = ["firstName", "lastName", "email", "phone", "isActive", "isSuperAdmin", "lastLoginAt"];
            v.CreateDialog = typeof(Components.Pages.Platform.UserCreateDialog);
            v.HiddenFields = ["failedLoginCount", "lockedUntil", "twoFactorEnabled", "avatarUrl"];
            v.Tabs.Add(new ResourceTab { Title = "Roller", Component = typeof(Components.Pages.Platform.UserRolesTab), Permission = "users:update" });
            v.HiddenChildren = ["user-roles"];
        });
        Configure("roles", v => v.CreateHref = "/roles");
        Configure("companies", v => v.CreateDialog = typeof(Components.Pages.Platform.CompanyCreateDialog));
        Configure("audit-logs", v =>
        {
            v.Columns = ["createdAt", "userEmail", "action", "entityType", "entityId", "ip"];
            v.FilterFields = ["action", "entityType", "userEmail"];
        });
        Configure("workflow-instances", v =>
        {
            v.Columns = ["title", "definitionId", "entityType", "status", "currentStepOrder", "startedById", "startedAt"];
            v.Actions.Add(new ResourceAction { Label = "Süreci aç", LabelEn = "Open", Icon = Icons.Material.Filled.OpenInNew, Run = c => { c.Nav.NavigateTo($"/workflow/instances/{c.Id}"); return Task.CompletedTask; } });
        });
        Configure("number-sequences", v => v.HideCreate = v.HideDelete = true);
    }
}
