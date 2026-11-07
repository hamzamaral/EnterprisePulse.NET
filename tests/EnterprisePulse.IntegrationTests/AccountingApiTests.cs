using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class AccountingApiTests(ApiFactory api)
{
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private static async Task<JsonObject> OkAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<JsonArray> ListAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<JsonObject>(url))!["data"]!.AsArray();

    private static async Task<JsonArray> ArrayAsync(HttpClient client, string url) => (await client.GetFromJsonAsync<JsonArray>(url))!;

    private static decimal Dec(JsonNode? node) => decimal.Parse(node!.ToString(), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<HttpClient> AdminAsync() => await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");

    // ---------- Muhasebe ----------

    [Fact]
    public async Task Chart_of_accounts_and_manual_entries_are_seeded()
    {
        var admin = await AdminAsync();
        var accounts = await ArrayAsync(admin, "/api/ledger-accounts");
        Assert.True(accounts.Count >= 60);
        JsonObject Account(string code) => accounts.First(a => a!["code"]!.ToString() == code)!.AsObject();
        Assert.True(Account("100")["isSystem"]!.GetValue<bool>());
        Assert.Equal("ASSET", Account("120")["type"]!.ToString());
        Assert.Equal("LIABILITY", Account("320")["type"]!.ToString());
        Assert.Equal("INCOME", Account("600")["type"]!.ToString());
        Assert.Equal("EXPENSE", Account("770")["type"]!.ToString());

        var entries = await ListAsync(admin, "/api/r/journal-entries?pageSize=100");
        Assert.True(entries.Count(e => e!["status"]!.ToString() == "POSTED") >= 3);
        Assert.True(entries.Count(e => e!["status"]!.ToString() == "DRAFT") >= 1);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/journal-entries", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/ledger-accounts", new { })).StatusCode);

        // Sub-accounts need a known main account and a valid code.
        var created = await OkAsync(await admin.PostAsJsonAsync("/api/ledger-accounts", new { code = "120.77", name = "Test müşterileri" }));
        Assert.Equal("ASSET", created["type"]!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/ledger-accounts", new { code = "ABC", name = "Geçersiz" })).StatusCode);
        Assert.True((await admin.PostAsJsonAsync("/api/ledger-accounts", new { code = "120.77", name = "Yinelenen" })).StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/ledger-accounts/{created["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/ledger-accounts/{Account("100")["id"]}")).StatusCode);

        var employee = await api.LoginAsync("employee@enterprisepulse.dev", "Employee123!");
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/accounting/summary")).StatusCode);
    }

    [Fact]
    public async Task Posting_run_creates_balanced_entries_and_statements()
    {
        var admin = await AdminAsync();
        var pending = await ArrayAsync(admin, "/api/accounting/pending");
        Assert.NotEmpty(pending);
        Assert.Contains(pending, p => p!["source"]!.ToString() == "SALES_INVOICE");
        Assert.Contains(pending, p => p!["source"]!.ToString() == "PRODUCTION");

        var run = await OkAsync(await admin.PostAsync("/api/accounting/run", null));
        Assert.True(run["posted"]!.GetValue<int>() > 0);
        var again = await OkAsync(await admin.PostAsync("/api/accounting/run", null));
        Assert.Equal(0, again["posted"]!.GetValue<int>());
        Assert.Equal(0, again["reversed"]!.GetValue<int>());
        Assert.Empty(await ArrayAsync(admin, "/api/accounting/pending"));

        // Every automatic entry balances.
        var entries = await ListAsync(admin, "/api/r/journal-entries?pageSize=100");
        var sales = entries.First(e => e!["source"]!.ToString() == "SALES_INVOICE")!;
        var detail = (await admin.GetFromJsonAsync<JsonObject>($"/api/journal-entries/{sales["id"]}"))!;
        Assert.Equal("POSTED", detail["status"]!.ToString());
        var lines = detail["lines"]!.AsArray();
        Assert.Equal(lines.Sum(l => Dec(l!["debit"])), lines.Sum(l => Dec(l!["credit"])));
        Assert.Contains(lines, l => l!["accountCode"]!.ToString().StartsWith("120"));
        Assert.Contains(lines, l => l!["accountCode"]!.ToString().StartsWith("600"));
        Assert.False(detail["canEdit"]!.GetValue<bool>());

        var production = entries.First(e => e!["source"]!.ToString() == "PRODUCTION")!;
        var productionLines = (await admin.GetFromJsonAsync<JsonObject>($"/api/journal-entries/{production["id"]}"))!["lines"]!.AsArray();
        Assert.Contains(productionLines, l => l!["accountCode"]!.ToString().StartsWith("152") || l!["accountCode"]!.ToString().StartsWith("153"));
        Assert.Equal(productionLines.Sum(l => Dec(l!["debit"])), productionLines.Sum(l => Dec(l!["credit"])));

        var trial = (await admin.GetFromJsonAsync<JsonObject>("/api/accounting/trial-balance"))!;
        var totals = trial["totals"]!;
        Assert.True(Dec(totals["totalDebit"]) > 0);
        Assert.Equal(Dec(totals["totalDebit"]), Dec(totals["totalCredit"]));
        Assert.Equal(Dec(totals["periodDebit"]), Dec(totals["periodCredit"]));
        Assert.Equal(Dec(totals["balanceDebit"]), Dec(totals["balanceCredit"]));

        var sheet = (await admin.GetFromJsonAsync<JsonObject>("/api/accounting/balance-sheet"))!;
        Assert.True(Dec(sheet["totalAssets"]) > 0);
        Assert.Equal(Dec(sheet["totalAssets"]), Dec(sheet["totalLiabilities"]));

        var income = (await admin.GetFromJsonAsync<JsonObject>("/api/accounting/income-statement"))!;
        Assert.True(Dec(income["netSales"]) > 0);
        Assert.NotEmpty(income["sections"]!.AsArray());

        var summary = (await admin.GetFromJsonAsync<JsonObject>("/api/accounting/summary"))!;
        Assert.True(summary["chartInstalled"]!.GetValue<bool>());
        Assert.Equal(0, summary["pendingCount"]!.GetValue<int>());
        Assert.Equal(12, summary["months"]!.AsArray().Count);
    }

    [Fact]
    public async Task Manual_entries_validate_post_reverse_and_respect_the_period_lock()
    {
        var admin = await AdminAsync();
        var accounts = await ArrayAsync(admin, "/api/ledger-accounts");
        string Id(string code) => accounts.First(a => a!["code"]!.ToString() == code)!["id"]!.ToString();
        var bank = Id("102");
        var expense = Id("770");

        // Drafts may be unbalanced while they are being prepared; posting them is refused.
        var unbalanced = await OkAsync(await admin.PostAsJsonAsync("/api/journal-entries", new
        {
            entryDate = Today, description = "Dengesiz",
            lines = new[] { new { accountId = expense, debit = 100, credit = 0 }, new { accountId = bank, debit = 0, credit = 90 } },
        }));
        Assert.False(unbalanced["canPost"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/journal-entries/{unbalanced["id"]}/post", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/journal-entries/{unbalanced["id"]}")).StatusCode);

        var draft = await OkAsync(await admin.PostAsJsonAsync("/api/journal-entries", new
        {
            entryDate = Today, description = "Test gideri",
            lines = new[] { new { accountId = expense, debit = 250, credit = 0 }, new { accountId = bank, debit = 0, credit = 250 } },
        }));
        Assert.Equal("DRAFT", draft["status"]!.ToString());
        Assert.StartsWith("YEV-", draft["number"]!.ToString());
        var id = draft["id"]!.ToString();

        var updated = await OkAsync(await admin.PatchAsJsonAsync($"/api/journal-entries/{id}", new
        {
            entryDate = Today, description = "Test gideri (düzeltildi)",
            lines = new[] { new { accountId = expense, debit = 300, credit = 0 }, new { accountId = bank, debit = 0, credit = 300 } },
        }));
        Assert.Equal(300m, Dec(updated["total"]));

        var posted = await OkAsync(await admin.PostAsync($"/api/journal-entries/{id}/post", null));
        Assert.Equal("POSTED", posted["status"]!.ToString());
        Assert.False(posted["canEdit"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/journal-entries/{id}")).StatusCode);

        var reversal = await OkAsync(await admin.PostAsJsonAsync($"/api/journal-entries/{id}/reverse", new { date = Today, note = "Hatalı giriş" }));
        Assert.True(reversal["isReversal"]!.GetValue<bool>());
        Assert.Equal(id, reversal["reverses"]!["id"]!.ToString());
        var original = (await admin.GetFromJsonAsync<JsonObject>($"/api/journal-entries/{id}"))!;
        Assert.True(original["isReversed"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/journal-entries/{id}/reverse", new { date = Today })).StatusCode);

        // Draft entries can be deleted.
        var another = await OkAsync(await admin.PostAsJsonAsync("/api/journal-entries", new
        {
            entryDate = Today, description = "Silinecek",
            lines = new[] { new { accountId = expense, debit = 10, credit = 0 }, new { accountId = bank, debit = 0, credit = 10 } },
        }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/journal-entries/{another["id"]}")).StatusCode);

        // Entries cannot be posted or reversed into a locked period.
        var lockedDraft = await OkAsync(await admin.PostAsJsonAsync("/api/journal-entries", new
        {
            entryDate = Today, description = "Kilitli dönem",
            lines = new[] { new { accountId = expense, debit = 5, credit = 0 }, new { accountId = bank, debit = 0, credit = 5 } },
        }));
        await OkAsync(await admin.PatchAsJsonAsync("/api/accounting/settings", new { lockedUntil = Today }));
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/journal-entries/{lockedDraft["id"]}/post", null)).StatusCode);
            Assert.True((await admin.GetFromJsonAsync<JsonObject>($"/api/journal-entries/{lockedDraft["id"]}"))!["locked"]!.GetValue<bool>());
        }
        finally
        {
            await OkAsync(await admin.PatchAsJsonAsync("/api/accounting/settings", new { lockedUntil = (string?)null }));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/journal-entries/{lockedDraft["id"]}")).StatusCode);
        Assert.Null((await admin.GetFromJsonAsync<JsonObject>("/api/accounting/settings"))!["lockedUntil"]);
    }

    // ---------- e-Fatura ----------

    [Fact]
    public async Task Outgoing_einvoices_are_queued_validated_and_delivered()
    {
        var admin = await AdminAsync();
        var unsent = await ArrayAsync(admin, "/api/einvoices/unsent");
        Assert.NotEmpty(unsent);
        var sent = new List<JsonObject>();
        foreach (var invoice in unsent.Take(5))
        {
            var res = await admin.PostAsJsonAsync("/api/einvoices", new { salesInvoiceId = invoice!["id"]!.ToString() });
            if (!res.IsSuccessStatusCode) continue;
            var created = (await res.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal("QUEUED", created["status"]!.ToString());
            Assert.Matches(new Regex(@"^[A-Z0-9]{3}\d{4}\d{9}$"), created["number"]!.ToString());
            Assert.Contains("<Invoice", created["xml"]!.ToString());
            Assert.Contains(created["number"]!.ToString(), created["xml"]!.ToString());
            Assert.Single(created["events"]!.AsArray());
            sent.Add(created);
        }
        Assert.NotEmpty(sent);

        // The same invoice cannot be sent twice.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/einvoices", new { salesInvoiceId = sent[0]["salesInvoiceId"]!.ToString() })).StatusCode);

        // First run validates, the second delivers.
        Assert.True((await OkAsync(await admin.PostAsync("/api/einvoices/sync", null)))["processed"]!.GetValue<int>() >= sent.Count);
        await OkAsync(await admin.PostAsync("/api/einvoices/sync", null));
        var states = new List<JsonObject>();
        foreach (var s in sent) states.Add((await admin.GetFromJsonAsync<JsonObject>($"/api/einvoices/{s["id"]}"))!);
        Assert.True(states.Any(s => s["status"]!.ToString() == "DELIVERED"), string.Join(" | ", states.Select(s => $"{s["status"]} {s["statusCode"]} {s["statusMessage"]}")));
        Assert.All(states, s => Assert.Contains(s["status"]!.ToString(), new[] { "DELIVERED", "ERROR" }));
        var delivered = states.First(s => s["status"]!.ToString() == "DELIVERED");
        Assert.True(delivered["events"]!.AsArray().Count >= 3);

        if (delivered["profile"]!.ToString() == "TICARIFATURA")
        {
            // A delivered e-Fatura cannot be cancelled; the receiver has to answer first.
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/sales/invoices/{delivered["salesInvoiceId"]}/cancel", new { text = "Hata" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/einvoices/{delivered["id"]}/simulate-response", new { action = "REJECT" })).StatusCode);
            var rejected = await OkAsync(await admin.PostAsJsonAsync($"/api/einvoices/{delivered["id"]}/simulate-response", new { action = "REJECT", note = "Fiyat uyuşmazlığı" }));
            Assert.Equal("REJECTED", rejected["status"]!.ToString());
        }
        else
        {
            // e-Arşiv invoices may be cancelled (unless other tests already collected a payment on the shared invoice).
            var cancelRes = await admin.PostAsJsonAsync($"/api/sales/invoices/{delivered["salesInvoiceId"]}/cancel", new { text = "Hata" });
            if (cancelRes.StatusCode != HttpStatusCode.Conflict)
            {
                var cancelled = await OkAsync(cancelRes);
                Assert.Equal("CANCELLED", cancelled["status"]!.ToString());
                Assert.Equal("CANCELLED", (await admin.GetFromJsonAsync<JsonObject>($"/api/einvoices/{delivered["id"]}"))!["status"]!.ToString());
            }
        }

        var summary = (await admin.GetFromJsonAsync<JsonObject>("/api/einvoices/summary"))!;
        Assert.Equal(unsent.Count - sent.Count, summary["unsent"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/e-invoices", new { })).StatusCode);
    }

    [Fact]
    public async Task Incoming_einvoice_is_answered_and_recorded_as_purchase_invoice()
    {
        var admin = await AdminAsync();
        var incoming = await OkAsync(await admin.PostAsJsonAsync("/api/einvoices/simulate-incoming", new { }));
        var id = incoming["id"]!.ToString();
        Assert.Equal("INCOMING", incoming["direction"]!.ToString());
        Assert.Equal("DELIVERED", incoming["status"]!.ToString());
        Assert.True(incoming["canRespond"]!.GetValue<bool>());
        Assert.Matches(new Regex(@"^[A-Z0-9]{3}\d{4}\d{9}$"), incoming["number"]!.ToString());

        // A ticari fatura has to be accepted before it is recorded.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/einvoices/{id}/record", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/einvoices/{id}/respond", new { action = "REJECT" })).StatusCode);
        var accepted = await OkAsync(await admin.PostAsJsonAsync($"/api/einvoices/{id}/respond", new { action = "ACCEPT" }));
        Assert.Equal("ACCEPTED", accepted["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/einvoices/{id}/respond", new { action = "ACCEPT" })).StatusCode);

        var recorded = await OkAsync(await admin.PostAsync($"/api/einvoices/{id}/record", null));
        Assert.NotNull(recorded["purchaseInvoiceId"]);
        Assert.False(recorded["canRecord"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/einvoices/{id}/record", null)).StatusCode);

        var second = await OkAsync(await admin.PostAsJsonAsync("/api/einvoices/simulate-incoming", new { }));
        var rejected = await OkAsync(await admin.PostAsJsonAsync($"/api/einvoices/{second["id"]}/respond", new { action = "REJECT", note = "Sipariş edilmedi" }));
        Assert.Equal("REJECTED", rejected["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/einvoices/{second["id"]}/record", null)).StatusCode);

        var settings = await OkAsync(await admin.PatchAsJsonAsync("/api/einvoices/settings", new { autoSend = true }));
        Assert.True(settings["autoSend"]!.GetValue<bool>());
        await OkAsync(await admin.PatchAsJsonAsync("/api/einvoices/settings", new { autoSend = false }));
    }

    // ---------- Üretim ----------

    [Fact]
    public async Task Bills_of_materials_and_work_orders_are_seeded()
    {
        var admin = await AdminAsync();
        var boms = await ListAsync(admin, "/api/r/bill-of-materials?pageSize=100");
        Assert.True(boms.Count >= 3);
        var orders = await ListAsync(admin, "/api/r/work-orders?pageSize=100");
        foreach (var status in new[] { "DRAFT", "RELEASED", "IN_PROGRESS", "COMPLETED" }) Assert.Contains(orders, o => o!["status"]!.ToString() == status);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/work-orders", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/bill-of-materials", new { })).StatusCode);

        var kit = boms.First(b => b!["code"]!.ToString() == "PRD-KIT-R1")!;
        var kitDetail = (await admin.GetFromJsonAsync<JsonObject>($"/api/production/boms/{kit["id"]}"))!;
        Assert.True(kitDetail["isDefault"]!.GetValue<bool>());
        Assert.Contains(kitDetail["lines"]!.AsArray(), l => l!["made"]!.GetValue<bool>());
        Assert.True(Dec(kitDetail["unitCost"]) > 160m); // at least labour + overhead

        // Multi-level plan: the starter kit needs the workstation set, which needs its own parts.
        var plan = (await admin.GetFromJsonAsync<JsonObject>($"/api/production/boms/{kit["id"]}/plan?quantity=12"))!;
        var planLines = plan["lines"]!.AsArray();
        Assert.Contains(planLines, l => l!["sku"]!.ToString() == "PRD-ISTASYON" && l["level"]!.GetValue<int>() == 1);
        Assert.Contains(planLines, l => l!["sku"]!.ToString() == "PRD-MONITOR" && l["level"]!.GetValue<int>() == 2);
        Assert.Equal(12m, Dec(planLines.First(l => l!["sku"]!.ToString() == "PRD-POLO-LCV-M")!["required"]));
        Assert.Equal(7m, Dec(planLines.First(l => l!["sku"]!.ToString() == "PRD-MONITOR")!["required"])); // 12 sets needed, 5 already built
        Assert.True(Dec(plan["totalCost"]) > 0);

        // The scrap rate raises the planned use: 5 kg coffee at 4% scrap per 10 packages.
        var catering = boms.First(b => b!["code"]!.ToString() == "PRD-IKRAM-R1")!;
        var coffee = (await admin.GetFromJsonAsync<JsonObject>($"/api/production/boms/{catering["id"]}"))!["lines"]!.AsArray().First(l => l!["variant"]!["sku"]!.ToString() == "PRD-KAHVE")!;
        Assert.Equal(0.52m, Dec(coffee["quantityPer"]));

        var done = orders.First(o => o!["reference"]?.ToString() == "Bilgi İşlem talebi #131")!;
        Assert.Equal("COMPLETED", done["status"]!.ToString());
        var detail = (await admin.GetFromJsonAsync<JsonObject>($"/api/production/work-orders/{done["id"]}"))!;
        Assert.Equal(3m, Dec(detail["order"]!["producedQty"]));
        Assert.Single(detail["records"]!.AsArray());
        Assert.All(detail["components"]!.AsArray(), c => Assert.Equal(Dec(c!["requiredQty"]), Dec(c["consumedQty"])));
        var record = detail["records"]![0]!;
        Assert.True(Dec(record["materialCost"]) > 0);
        Assert.Equal(75m * 3, Dec(record["overheadCost"]));
        Assert.Equal(250m * 3, Dec(record["laborCost"]));
        Assert.Equal(Math.Round((Dec(record["materialCost"]) + 750m + 225m) / 3m, 4), Dec(record["unitCost"]));
        Assert.True(Dec(detail["actualUnitCost"]) > 0);
        Assert.False(detail["canRecord"]!.GetValue<bool>());

        var summary = (await admin.GetFromJsonAsync<JsonObject>("/api/production/summary"))!;
        Assert.True(summary["activeBoms"]!.GetValue<int>() >= 3);
        Assert.True(summary["draft"]!.GetValue<int>() >= 1);
        Assert.True(summary["released"]!.GetValue<int>() >= 1);
        Assert.True(summary["inProgress"]!.GetValue<int>() >= 1);
        Assert.True(summary["late"]!.GetValue<int>() >= 1);
        Assert.True(summary["completedThisMonth"]!.GetValue<int>() >= 0);

        var requirements = await ArrayAsync(admin, "/api/production/requirements");
        Assert.NotEmpty(requirements);
        Assert.Contains(requirements, r => r!["variant"]!["sku"]!.ToString() == "PRD-ISTASYON" && r["made"]!.GetValue<bool>());
        Assert.All(requirements, r => Assert.Equal(Dec(r!["productionDemand"]) + Dec(r["salesDemand"]), Dec(r["gross"])));

        var employee = await api.LoginAsync("employee@enterprisepulse.dev", "Employee123!");
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync($"/api/production/work-orders/{done["id"]}/release", new { })).StatusCode);
    }

    [Fact]
    public async Task Work_order_lifecycle_records_output_and_posts_stock()
    {
        var admin = await AdminAsync();
        var boms = await ListAsync(admin, "/api/r/bill-of-materials?pageSize=100");
        var catering = boms.First(b => b!["code"]!.ToString() == "PRD-IKRAM-R1")!;
        var warehouse = (await ListAsync(admin, "/api/r/warehouses?code=MRK")).First()!;
        var wh = warehouse["id"]!.ToString();

        async Task<decimal> Stock(string sku)
        {
            var levels = await ListAsync(admin, $"/api/stock/levels?search={sku}");
            return levels.Where(l => l!["sku"]!.ToString() == sku).Sum(l => Dec(l!["quantity"]));
        }

        var order = await OkAsync(await admin.PostAsJsonAsync("/api/production/work-orders", new
        {
            bomId = catering["id"]!.ToString(), quantity = 10, plannedStart = Today, plannedEnd = Today, sourceWarehouseId = wh, targetWarehouseId = wh, reference = "Test",
        }));
        var id = order["order"]!["id"]!.ToString();
        Assert.Equal("DRAFT", order["order"]!["status"]!.ToString());
        Assert.StartsWith("IEM-", order["order"]!["number"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/production/work-orders/{id}/records", new { quantity = 1 })).StatusCode);

        var edited = await OkAsync(await admin.PatchAsJsonAsync($"/api/production/work-orders/{id}", new { quantity = 20 }));
        var water = edited["components"]!.AsArray().First(c => c!["variant"]!["sku"]!.ToString() == "PRD-SU")!;
        Assert.Equal(40m, Dec(water["requiredQty"]));
        await OkAsync(await admin.PatchAsJsonAsync($"/api/production/work-orders/{id}", new { quantity = 10 }));

        var released = await OkAsync(await admin.PostAsync($"/api/production/work-orders/{id}/release", null));
        Assert.Equal("RELEASED", released["order"]!["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/production/work-orders/{id}", new { quantity = 5 })).StatusCode);

        var packagesBefore = await Stock("PRD-IKRAM");
        var waterBefore = await Stock("PRD-SU");
        var coffeeBefore = await Stock("PRD-KAHVE");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/production/work-orders/{id}/records", new { quantity = 11 })).StatusCode);

        var partial = await OkAsync(await admin.PostAsJsonAsync($"/api/production/work-orders/{id}/records", new { quantity = 4, recordDate = Today }));
        Assert.Equal("IN_PROGRESS", partial["order"]!["status"]!.ToString());
        Assert.Equal(4m, Dec(partial["order"]!["producedQty"]));
        Assert.Equal(40, partial["order"]!["progress"]!.GetValue<int>());
        Assert.False(partial["canCancel"]!.GetValue<bool>());
        Assert.Equal(packagesBefore + 4, await Stock("PRD-IKRAM"));
        Assert.Equal(waterBefore - 8m, await Stock("PRD-SU"));
        Assert.Equal(coffeeBefore - 2.08m, await Stock("PRD-KAHVE")); // 0.52 kg per package, first-expiring lot first
        var first = partial["records"]![0]!;
        Assert.True(Dec(first["unitCost"]) > 0);
        Assert.StartsWith("URT-", first["number"]!.ToString());
        Assert.NotNull(first["issueMovementId"]);

        // The record that finishes the order uses up the rest of every component.
        var finished = await OkAsync(await admin.PostAsJsonAsync($"/api/production/work-orders/{id}/records", new { quantity = 6 }));
        Assert.Equal("COMPLETED", finished["order"]!["status"]!.ToString());
        Assert.All(finished["components"]!.AsArray(), c => Assert.Equal(Dec(c!["requiredQty"]), Dec(c["consumedQty"])));
        Assert.Equal(2, finished["records"]!.AsArray().Count);
        Assert.Equal(packagesBefore + 10, await Stock("PRD-IKRAM"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/production/work-orders/{id}/records", new { quantity = 1 })).StatusCode);

        // Cancel only works before output; cancelled orders stop counting as demand.
        var spare = await OkAsync(await admin.PostAsJsonAsync("/api/production/work-orders", new
        {
            bomId = catering["id"]!.ToString(), quantity = 10, plannedStart = Today, plannedEnd = Today, sourceWarehouseId = wh, targetWarehouseId = wh,
        }));
        var cancelled = await OkAsync(await admin.PostAsync($"/api/production/work-orders/{spare["order"]!["id"]}/cancel", null));
        Assert.Equal("CANCELLED", cancelled["order"]!["status"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/production/work-orders/{spare["order"]!["id"]}/release", null)).StatusCode);

        // BOM safety: loops and used BOMs.
        var bomDetail = (await admin.GetFromJsonAsync<JsonObject>($"/api/production/boms/{catering["id"]}"))!;
        Assert.False(bomDetail["canDelete"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/production/boms/{catering["id"]}")).StatusCode);
        var station = boms.First(b => b!["code"]!.ToString() == "PRD-ISTASYON-R1")!;
        var stationDetail = (await admin.GetFromJsonAsync<JsonObject>($"/api/production/boms/{station["id"]}"))!;
        var kitVariant = (await ArrayAsync(admin, "/api/stock/items?search=PRD-KIT")).First(i => i!["sku"]!.ToString() == "PRD-KIT")!["variantId"]!.ToString();
        var loop = await admin.PatchAsJsonAsync($"/api/production/boms/{station["id"]}", new
        {
            lines = new[] { new { variantId = kitVariant, quantity = 1 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, loop.StatusCode);
        Assert.Equal(3, stationDetail["lines"]!.AsArray().Count);

        // Shortages of bought items become a purchase request.
        var monitor = (await ArrayAsync(admin, "/api/stock/items?search=PRD-MONITOR")).First(i => i!["sku"]!.ToString() == "PRD-MONITOR")!["variantId"]!.ToString();
        var request = await OkAsync(await admin.PostAsJsonAsync("/api/production/requirements/purchase-request", new { lines = new[] { new { variantId = monitor, quantity = 5 } } }));
        Assert.False(string.IsNullOrEmpty(request["number"]!.ToString()));
    }
}
