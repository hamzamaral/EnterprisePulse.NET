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
    /// <summary>Custom record page, e.g. "/assets/counts/{id}", instead of the generic detail.</summary>
    public string? DetailHref { get; set; }
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
        Assets();
        Inventory();
        Sales();
        Work();
        People();
        Ops();
    }

    static void Assets()
    {
        Configure("assets", v =>
        {
            v.Columns = ["assetTag", "name", "categoryId", "brand", "model", "status", "condition", "locationId"];
            v.FilterFields = ["status", "condition", "categoryId", "locationId", "purchaseDate", "warrantyEndDate"];
            v.ReadOnlyFields = ["status", "retiredAt", "retireReason"];
            v.Actions.Add(new ResourceAction
            {
                Label = "Zimmetle", LabelEn = "Assign", Icon = Icons.Material.Filled.AssignmentInd, Permission = "assets:assign",
                Visible = ResourceAction.StatusIn("IN_STOCK"), Run = Components.Pages.Assets.AssignDialog.Action(transfer: false),
            });
            v.Actions.Add(new ResourceAction
            {
                Label = "Devret", LabelEn = "Transfer", Icon = Icons.Material.Filled.SwapHoriz, Permission = "assets:assign",
                Visible = ResourceAction.StatusIn("ASSIGNED"), Run = Components.Pages.Assets.AssignDialog.Action(transfer: true),
            });
            v.Actions.Add(new ResourceAction
            {
                Label = "İade al", LabelEn = "Take back", Icon = Icons.Material.Filled.AssignmentReturn, Permission = "assets:assign",
                Visible = ResourceAction.StatusIn("ASSIGNED"), Run = Components.Pages.Assets.ReturnDialog.RunAsync,
            });
            v.Actions.Add(new ResourceAction
            {
                Label = "Hizmet dışı bırak", LabelEn = "Retire", Icon = Icons.Material.Filled.Block, Color = Color.Error, Permission = "assets:update",
                Visible = ResourceAction.StatusIn("IN_STOCK"), Run = Components.Pages.Assets.RetireDialog.RunAsync,
            });
            v.Actions.Add(new ResourceAction
            {
                Label = "QR etiketi", LabelEn = "QR label", Icon = Icons.Material.Filled.QrCode2, Color = Color.Default,
                Endpoint = "/api/assets/{id}/label", Download = true,
            });
            v.Tabs.Add(new ResourceTab { Title = "Amortisman ve etiket", Component = typeof(Components.Pages.Assets.AssetOverviewTab) });
            v.Tabs.Add(new ResourceTab { Title = "Zimmet geçmişi", Component = typeof(Components.Pages.Assets.AssetAssignmentsTab) });
            v.Tabs.Add(new ResourceTab { Title = "Bakım", Component = typeof(Components.Pages.Assets.AssetMaintenanceTab) });
        });
        Configure("asset-assignments", v =>
        {
            v.Columns = ["number", "assetId", "assigneeType", "employeeId", "departmentId", "locationId", "assignedAt", "returnedAt", "acknowledgedAt", "status"];
            v.FilterFields = ["status", "assigneeType", "assetId", "employeeId", "departmentId", "assignedAt"];
            v.HideCreate = v.HideEdit = v.HideDelete = true;
            v.DetailHref = "/assets/assignments/{id}";
        });
        Configure("asset-counts", v =>
        {
            v.Columns = ["number", "countDate", "locationId", "categoryId", "status", "createdById", "completedAt"];
            v.FilterFields = ["status", "locationId", "categoryId", "countDate"];
            v.CreateDialog = typeof(Components.Pages.Assets.AssetCountDialog);
            v.HideEdit = v.HideDelete = true;
            v.DetailHref = "/assets/counts/{id}";
        });
        Configure("asset-categories", v =>
        {
            v.Columns = ["code", "name", "usefulLifeMonths", "isActive"];
            v.Links.Add(new HeaderLink("Konumlar", "Locations", "/r/asset-locations", Icons.Material.Filled.Place));
        });
        Configure("asset-locations", v =>
        {
            v.Columns = ["code", "name", "branchId", "description", "isActive"];
            v.Links.Add(new HeaderLink("Kategoriler", "Categories", "/r/asset-categories", Icons.Material.Filled.Category));
        });
        Configure("asset-maintenances", v => v.HideCreate = v.HideEdit = v.HideDelete = true);
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

    static void Inventory()
    {
        Configure("products", v =>
        {
            v.Columns = ["code", "name", "categoryId", "unitId", "type", "trackingType", "salePrice", "isActive"];
            v.FilterFields = ["type", "trackingType", "categoryId", "isActive"];
            v.Links.Add(new HeaderLink("Stok durumu", "Stock levels", "/inventory", Icons.Material.Filled.Warehouse));
            v.Tabs.Add(new ResourceTab { Title = "Varyantlar", Component = typeof(Components.Pages.Inventory.ProductVariantsTab) });
            v.Tabs.Add(new ResourceTab { Title = "Stok", Component = typeof(Components.Pages.Inventory.ProductStockTab) });
            v.Tabs.Add(new ResourceTab { Title = "Stok kartı", Component = typeof(Components.Pages.Inventory.ProductLedgerTab) });
        });
        Configure("product-categories", v =>
        {
            v.Columns = ["code", "name", "parentId", "isActive"];
            v.Links.Add(new HeaderLink("Depolar", "Warehouses", "/r/warehouses", Icons.Material.Filled.Warehouse));
        });
        Configure("warehouses", v =>
        {
            v.Columns = ["code", "name", "branchId", "address", "isActive"];
            v.Links.Add(new HeaderLink("Kategoriler", "Categories", "/r/product-categories", Icons.Material.Filled.Category));
            v.Tabs.Add(new ResourceTab { Title = "Stok değeri", Component = typeof(Components.Pages.Inventory.WarehouseStockTab) });
        });
        Configure("stock-movements", v =>
        {
            v.Columns = ["number", "type", "movementDate", "sourceWarehouseId", "targetWarehouseId", "reference", "createdAt"];
            v.FilterFields = ["type", "movementDate", "sourceWarehouseId", "targetWarehouseId"];
            v.CreateHref = "/inventory/movements/new";
            v.DetailHref = "/inventory/movements/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("stock-counts", v =>
        {
            v.Columns = ["number", "warehouseId", "countDate", "status", "createdById", "completedAt"];
            v.FilterFields = ["status", "warehouseId", "countDate"];
            v.CreateDialog = typeof(Components.Pages.Inventory.StockCountDialog);
            v.DetailHref = "/inventory/counts/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("business-partners", v =>
        {
            v.Columns = ["code", "title", "type", "city", "phone", "paymentTermDays", "currencyCode", "isActive"];
            v.FilterFields = ["type", "city", "isActive"];
            v.Tabs.Add(new ResourceTab { Title = "Ekstre", Component = typeof(Components.Pages.Partners.PartnerStatementTab) });
        });
        Configure("purchase-requests", v =>
        {
            v.Columns = ["number", "title", "requestDate", "neededBy", "status", "requestedById"];
            v.FilterFields = ["status", "requestDate", "departmentId"];
            v.CreateHref = "/purchasing/requests/new";
            v.HideEdit = v.HideDelete = true;
            v.HiddenChildren = ["purchase-request-lines"];
            v.WorkflowEntity = "PurchaseRequest";
            v.Tabs.Add(new ResourceTab { Title = "Satırlar", Component = typeof(Components.Pages.Purchasing.RequestLinesTab) });
            v.Tabs.Add(new ResourceTab { Title = "Teklif karşılaştırma", Component = typeof(Components.Pages.Purchasing.QuoteCompareTab), Permission = "purchasing:read" });
            v.Actions.Add(new ResourceAction
            {
                Label = "İptal et", LabelEn = "Cancel", Icon = Icons.Material.Filled.Cancel, Color = Color.Error,
                Visible = ResourceAction.StatusIn("PENDING", "APPROVED"), Endpoint = "/api/purchasing/requests/{id}/cancel", Prompt = "Gerekçe",
            });
        });
        Configure("supplier-quotes", v =>
        {
            v.Columns = ["number", "partnerId", "requestId", "quoteDate", "validUntil", "currencyCode", "grandTotal", "status"];
            v.FilterFields = ["status", "partnerId", "quoteDate"];
            v.CreateHref = "/purchasing/quotes/new";
            v.HideEdit = v.HideDelete = true;
            v.HiddenChildren = ["supplier-quote-lines"];
            v.Tabs.Add(new ResourceTab { Title = "Satırlar", Component = typeof(Components.Pages.Purchasing.QuoteLinesTab) });
            v.Actions.Add(new ResourceAction
            {
                Label = "Kabul et", LabelEn = "Accept", Icon = Icons.Material.Filled.Check, Permission = "purchasing:create",
                Visible = ResourceAction.StatusIn("OPEN"), Run = Components.Pages.Purchasing.AcceptQuoteDialog.Open,
            });
            v.Actions.Add(new ResourceAction
            {
                Label = "Reddet", LabelEn = "Reject", Icon = Icons.Material.Filled.Close, Color = Color.Error, Permission = "purchasing:update",
                Visible = ResourceAction.StatusIn("OPEN"), Endpoint = "/api/purchasing/quotes/{id}/reject", Confirm = "Teklif reddedilecek.",
            });
        });
        Configure("purchase-orders", v =>
        {
            v.Columns = ["number", "partnerId", "orderDate", "expectedDate", "warehouseId", "currencyCode", "grandTotal", "status"];
            v.FilterFields = ["status", "partnerId", "warehouseId", "orderDate"];
            v.CreateHref = "/purchasing/orders/new";
            v.DetailHref = "/purchasing/orders/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("goods-receipts", v =>
        {
            v.Columns = ["number", "orderId", "partnerId", "warehouseId", "receiptDate", "deliveryNoteNo"];
            v.FilterFields = ["partnerId", "warehouseId", "receiptDate"];
            v.CreateHref = "/purchasing/receipts/new";
            v.DetailHref = "/purchasing/receipts/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("purchase-invoices", v =>
        {
            v.Columns = ["number", "supplierInvoiceNo", "partnerId", "invoiceDate", "dueDate", "grandTotal", "paymentStatus", "status"];
            v.FilterFields = ["status", "paymentStatus", "partnerId", "invoiceDate"];
            v.CreateHref = "/purchasing/invoices/new";
            v.DetailHref = "/purchasing/invoices/{id}";
            v.HideEdit = v.HideDelete = true;
        });
    }

    static void Sales()
    {
        Configure("sales-quotes", v =>
        {
            v.Columns = ["number", "partnerId", "quoteDate", "validUntil", "currencyCode", "grandTotal", "status"];
            v.FilterFields = ["status", "partnerId", "quoteDate"];
            v.CreateHref = "/sales/quotes/new";
            v.DetailHref = "/sales/quotes/{id}";
            v.HideEdit = v.HideDelete = true;
            v.HiddenChildren = ["sales-quote-lines"];
            v.Actions.Add(new ResourceAction
            {
                Label = "Kabul et", LabelEn = "Accept", Icon = Icons.Material.Filled.Check, Permission = "sales:create",
                Visible = ResourceAction.StatusIn("OPEN"), Run = Components.Pages.Sales.AcceptSalesQuoteDialog.Open,
            });
            v.Actions.Add(new ResourceAction
            {
                Label = "Reddet", LabelEn = "Reject", Icon = Icons.Material.Filled.Close, Color = Color.Error, Permission = "sales:update",
                Visible = ResourceAction.StatusIn("OPEN"), Endpoint = "/api/sales/quotes/{id}/reject", Confirm = "Teklif reddedilecek.",
            });
        });
        Configure("sales-orders", v =>
        {
            v.Columns = ["number", "partnerId", "orderDate", "deliveryDate", "warehouseId", "currencyCode", "grandTotal", "status"];
            v.FilterFields = ["status", "partnerId", "warehouseId", "orderDate"];
            v.CreateHref = "/sales/orders/new";
            v.DetailHref = "/sales/orders/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("delivery-notes", v =>
        {
            v.Columns = ["number", "orderId", "partnerId", "warehouseId", "deliveryDate", "carrier"];
            v.FilterFields = ["partnerId", "warehouseId", "deliveryDate"];
            v.CreateHref = "/sales/deliveries/new";
            v.DetailHref = "/sales/deliveries/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("sales-invoices", v =>
        {
            v.Columns = ["number", "partnerId", "invoiceDate", "dueDate", "grandTotal", "paidAmount", "paymentStatus", "status"];
            v.FilterFields = ["status", "paymentStatus", "partnerId", "invoiceDate"];
            v.CreateHref = "/sales/invoices/new";
            v.DetailHref = "/sales/invoices/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("sales-returns", v =>
        {
            v.Columns = ["number", "invoiceId", "partnerId", "returnDate", "grandTotal", "reason"];
            v.FilterFields = ["partnerId", "returnDate"];
            v.CreateHref = "/sales/returns/new";
            v.DetailHref = "/sales/returns/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("treasury-accounts", v =>
        {
            v.Columns = ["code", "name", "type", "currencyCode", "bankName", "iban", "isActive"];
            v.FilterFields = ["type", "currencyCode", "isActive"];
            v.Tabs.Add(new ResourceTab { Title = "Hareketler", Component = typeof(Components.Pages.Finance.TreasuryTxTab) });
        });
        Configure("partner-payments", v =>
        {
            v.Columns = ["number", "direction", "method", "partnerId", "paymentDate", "amount", "currencyCode", "status"];
            v.FilterFields = ["direction", "method", "status", "partnerId", "paymentDate"];
            v.CreateHref = "/finance/payments/new";
            v.DetailHref = "/finance/payments/{id}";
            v.HideEdit = v.HideDelete = true;
        });
        Configure("cheques", v =>
        {
            v.Columns = ["number", "kind", "direction", "status", "serialNo", "partnerId", "dueDate", "amount", "currencyCode"];
            v.FilterFields = ["kind", "direction", "status", "partnerId", "dueDate"];
            v.DetailHref = "/finance/cheques/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
    }

    static void Work()
    {
        Configure("leads", v =>
        {
            v.Columns = ["number", "companyName", "contactName", "city", "source", "status", "estimatedValue", "ownerId"];
            v.FilterFields = ["status", "source", "ownerId"];
            v.DetailHref = "/crm/leads/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("opportunities", v =>
        {
            v.Columns = ["number", "title", "partnerId", "stage", "amount", "probability", "expectedCloseDate", "ownerId"];
            v.FilterFields = ["stage", "partnerId", "ownerId"];
            v.DetailHref = "/crm/opportunities/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("crm-activities", v =>
        {
            v.Columns = ["type", "subject", "dueAt", "completedAt", "ownerId", "opportunityId", "partnerId"];
            v.FilterFields = ["type", "ownerId"];
            v.DetailHref = "/crm/activities/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("expense-reports", v =>
        {
            v.Columns = ["number", "employeeId", "projectId", "title", "totalAmount", "status", "paymentDate"];
            v.FilterFields = ["status", "employeeId", "projectId"];
            v.DetailHref = "/expenses/reports/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("expense-categories", v =>
        {
            v.Columns = ["code", "name", "kind", "rate", "limitAmount", "requiresReceipt", "isActive"];
            v.FilterFields = ["kind", "isActive"];
        });
        Configure("projects", v =>
        {
            v.Columns = ["number", "name", "partnerId", "managerId", "status", "startDate", "dueDate", "budgetAmount", "expenseCost"];
            v.FilterFields = ["status", "managerId", "partnerId"];
            v.DetailHref = "/projects/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
    }

    static void People()
    {
        Configure("payroll-periods", v =>
        {
            v.Columns = ["number", "year", "month", "status", "employeeCount", "grossTotal", "netTotal", "employerCostTotal"];
            v.FilterFields = ["status", "year"];
            v.DetailHref = "/payroll/periods/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("salary-records", v =>
        {
            v.Columns = ["employeeId", "effectiveFrom", "grossSalary", "note"];
            v.FilterFields = ["employeeId"];
            v.HideEdit = v.HideDelete = true;
        });
        Configure("job-postings", v =>
        {
            v.Columns = ["number", "title", "status", "openings", "location", "salaryMin", "salaryMax"];
            v.FilterFields = ["status"];
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("job-applications", v =>
        {
            v.Columns = ["postingId", "candidateId", "stage", "appliedAt", "hiredAt"];
            v.FilterFields = ["stage", "postingId"];
            v.DetailHref = "/recruitment/applications/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("candidates", v =>
        {
            v.Columns = ["firstName", "lastName", "email", "city", "source", "expectedSalary"];
            v.FilterFields = ["source"];
            v.HideEdit = v.HideDelete = true;
        });
        Configure("review-cycles", v =>
        {
            v.Columns = ["name", "status", "periodStart", "periodEnd", "goalWeight"];
            v.FilterFields = ["status"];
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("competencies", v =>
        {
            v.Columns = ["name", "sortOrder", "isActive"];
            v.FilterFields = ["isActive"];
        });
        Configure("training-courses", v =>
        {
            v.Columns = ["code", "title", "category", "format", "durationHours", "isMandatory", "validityMonths", "isActive"];
            v.FilterFields = ["category", "isMandatory", "isActive"];
        });
        Configure("training-sessions", v =>
        {
            v.Columns = ["courseId", "startsAt", "status", "location", "instructor", "capacity"];
            v.FilterFields = ["status", "courseId"];
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
    }

    static void Ops()
    {
        Configure("tickets", v =>
        {
            v.Columns = ["number", "subject", "priority", "status", "requesterId", "assigneeId", "resolutionDueAt"];
            v.FilterFields = ["status", "priority", "assigneeId"];
            v.DetailHref = "/support/tickets/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("ticket-categories", v =>
        {
            v.Columns = ["name", "defaultAssigneeId", "isActive", "sortOrder"];
            v.FilterFields = ["isActive"];
        });
        Configure("contracts", v =>
        {
            v.Columns = ["number", "title", "status", "party", "partnerId", "employeeId", "startDate", "endDate", "amount"];
            v.FilterFields = ["status", "party", "typeId"];
            v.DetailHref = "/contracts/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("contract-types", v =>
        {
            v.Columns = ["name", "party", "flow", "defaultTermMonths", "defaultNoticeDays", "isActive"];
            v.FilterFields = ["party", "isActive"];
        });
        Configure("budgets", v =>
        {
            v.Columns = ["year", "name", "status", "departmentId", "ownerId"];
            v.FilterFields = ["year", "status"];
            v.DetailHref = "/budgets/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("budget-items", v =>
        {
            v.Columns = ["code", "name", "kind", "source", "isActive"];
            v.FilterFields = ["kind", "source", "isActive"];
        });
        Configure("budget-entries", v =>
        {
            v.Columns = ["itemId", "entryDate", "amount", "description", "departmentId"];
            v.FilterFields = ["itemId"];
            v.HideEdit = v.HideDelete = true;
        });
        Configure("vehicles", v =>
        {
            v.Columns = ["plate", "brand", "model", "status", "odometer", "fuelType", "ownership"];
            v.FilterFields = ["status", "fuelType"];
            v.DetailHref = "/fleet/vehicles/{id}";
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
        Configure("vehicle-fuel-entries", v =>
        {
            v.Columns = ["vehicleId", "entryDate", "odometer", "liters", "amount", "consumption"];
            v.FilterFields = ["vehicleId"];
            v.HideCreate = v.HideEdit = v.HideDelete = true;
        });
    }
}
