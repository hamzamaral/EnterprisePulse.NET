using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class InventoryApiTests(ApiFactory api)
{
    private static string Today => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private static async Task<JsonObject> FirstAsync(HttpClient client, string url)
    {
        var res = await client.GetFromJsonAsync<JsonObject>(url);
        return res!["data"]!.AsArray().First()!.AsObject();
    }

    private static async Task<JsonObject> OkAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task Movement_count_request_receipt_and_invoice_stay_consistent()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var employee = await api.LoginAsync("employee@enterprisepulse.dev", "Employee123!");
        var me = (await employee.GetFromJsonAsync<JsonObject>("/api/auth/me"))!;
        var code = "TST-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var unit = await FirstAsync(admin, "/api/r/units?code=ADET");
        var warehouse = await FirstAsync(admin, "/api/r/warehouses?code=MRK");
        var supplier = await FirstAsync(admin, "/api/r/business-partners?code=TED-001");

        var product = (await OkAsync(await admin.PostAsJsonAsync("/api/r/products", new { code, name = "Test kalemi", unitId = unit["id"]!.ToString(), purchasePrice = 10 })))["data"]!;
        var stock = (await admin.GetFromJsonAsync<JsonObject>($"/api/stock/products/{product["id"]}"))!;
        var variantId = stock["variants"]!.AsArray().First()!["id"]!.ToString();
        await OkAsync(await admin.PatchAsJsonAsync($"/api/stock/products/{product["id"]}/variants/{variantId}", new { minStock = 5 }));

        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync("/api/stock/movements", new { type = "RECEIPT", movementDate = Today, targetWarehouseId = warehouse["id"]!.ToString(), lines = new[] { new { variantId, quantity = 1 } } })).StatusCode);

        var receipt = await OkAsync(await admin.PostAsJsonAsync("/api/stock/movements", new
        {
            type = "RECEIPT", movementDate = Today, targetWarehouseId = warehouse["id"]!.ToString(),
            lines = new[] { new { variantId, quantity = 10, unitCost = 12.5m } },
        }));
        Assert.StartsWith("STH", receipt["number"]!.ToString());
        var level = await FirstAsync(admin, $"/api/stock/levels?search={code}");
        Assert.Equal(10m, level["quantity"]!.GetValue<decimal>());
        Assert.False(level["lowStock"]!.GetValue<bool>());

        await OkAsync(await admin.PostAsJsonAsync("/api/stock/movements", new
        {
            type = "ISSUE", movementDate = Today, sourceWarehouseId = warehouse["id"]!.ToString(),
            lines = new[] { new { variantId, quantity = 6 } },
        }));
        level = await FirstAsync(admin, $"/api/stock/levels?search={code}&lowStock=true");
        Assert.Equal(4m, level["quantity"]!.GetValue<decimal>());
        Assert.True(level["lowStock"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/stock/movements", new
        {
            type = "ISSUE", movementDate = Today, sourceWarehouseId = warehouse["id"]!.ToString(),
            lines = new[] { new { variantId, quantity = 100 } },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/stock-movements", new { })).StatusCode);

        var count = await OkAsync(await admin.PostAsJsonAsync("/api/stock/counts", new { warehouseId = warehouse["id"]!.ToString(), countDate = Today }));
        var line = count["lines"]!.AsArray().First(l => l!["variantId"]!.ToString() == variantId)!;
        await OkAsync(await admin.PutAsJsonAsync($"/api/stock/counts/{count["id"]}/lines", new[] { new { id = line["id"]!.ToString(), countedQty = 2 } }));
        var done = await OkAsync(await admin.PostAsync($"/api/stock/counts/{count["id"]}/complete", null));
        Assert.Equal("COMPLETED", done["status"]!.ToString());
        Assert.Equal(2m, (await FirstAsync(admin, $"/api/stock/levels?search={code}"))["quantity"]!.GetValue<decimal>());

        var request = await OkAsync(await employee.PostAsJsonAsync("/api/purchasing/requests", new
        {
            title = "Test talebi " + code, neededBy = Today,
            lines = new[] { new { variantId, quantity = 2, estimatedPrice = 100 } },
        }));
        Assert.Equal("PENDING", request["status"]!.ToString());
        var visible = (await employee.GetFromJsonAsync<JsonObject>("/api/r/purchase-requests?pageSize=100"))!["data"]!.AsArray();
        Assert.All(visible, row => Assert.Equal(me["user"]!["id"]!.ToString(), row!["requestedById"]!.ToString()));
        var instance = request["workflowInstanceId"]!.ToString();
        await OkAsync(await admin.PostAsJsonAsync($"/api/workflow/instances/{instance}/approve", new { comment = "uygun" }));
        await OkAsync(await admin.PostAsJsonAsync($"/api/workflow/instances/{instance}/approve", new { comment = "uygun" }));
        request = (await admin.GetFromJsonAsync<JsonObject>($"/api/purchasing/requests/{request["id"]}"))!;
        Assert.Equal("APPROVED", request["status"]!.ToString());

        var quote = await OkAsync(await admin.PostAsJsonAsync("/api/purchasing/quotes", new
        {
            partnerId = supplier["id"]!.ToString(), requestId = request["id"]!.ToString(), quoteDate = Today,
            lines = new[] { new { variantId, quantity = 2, unitPrice = 100, discountRate = 10, vatRate = 20, requestLineId = request["lines"]!.AsArray().First()!["id"]!.ToString() } },
        }));
        Assert.Equal(216m, quote["totals"]!["grandTotal"]!.GetValue<decimal>());
        var orderId = await (await admin.PostAsJsonAsync($"/api/purchasing/quotes/{quote["id"]}/accept", new { warehouseId = warehouse["id"]!.ToString(), expectedDate = Today })).Content.ReadFromJsonAsync<Guid>();
        await OkAsync(await admin.PostAsync($"/api/purchasing/orders/{orderId}/approve", null));
        var order = (await admin.GetFromJsonAsync<JsonObject>($"/api/purchasing/orders/{orderId}"))!;
        var orderLine = order["lines"]!.AsArray().First()!["id"]!.ToString();
        await OkAsync(await admin.PostAsJsonAsync("/api/purchasing/receipts", new
        {
            orderId, warehouseId = warehouse["id"]!.ToString(), receiptDate = Today,
            lines = new[] { new { orderLineId = orderLine, quantity = 2 } },
        }));
        Assert.Equal(4m, (await FirstAsync(admin, $"/api/stock/levels?search={code}"))["quantity"]!.GetValue<decimal>());

        var before = (await admin.GetFromJsonAsync<decimal>($"/api/partners/{supplier["id"]}/balance"))!;
        var invoice = await OkAsync(await admin.PostAsJsonAsync("/api/purchasing/invoices", new
        {
            partnerId = supplier["id"]!.ToString(), orderId, supplierInvoiceNo = code, invoiceDate = Today,
            lines = new[] { new { variantId, quantity = 2, unitPrice = 100, discountRate = 10, vatRate = 20, orderLineId = orderLine } },
        }));
        Assert.Equal(216m, invoice["totals"]!["grandTotal"]!.GetValue<decimal>());
        var after = await admin.GetFromJsonAsync<decimal>($"/api/partners/{supplier["id"]}/balance");
        Assert.Equal(before - 216m, after);
        var cancelled = await OkAsync(await admin.PostAsJsonAsync($"/api/purchasing/invoices/{invoice["id"]}/cancel", new { reason = "test" }));
        Assert.Equal("CANCELLED", cancelled["status"]!.ToString());
        Assert.Equal(before, await admin.GetFromJsonAsync<decimal>($"/api/partners/{supplier["id"]}/balance"));
    }
}
