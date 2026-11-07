using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace EnterprisePulse.IntegrationTests;

[Collection("api")]
public sealed class SalesApiTests(ApiFactory api)
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
    public async Task Quote_reserves_stock_invoices_and_collects()
    {
        var admin = await api.LoginAsync("admin@enterprisepulse.dev", "Admin123!");
        var code = "SAT-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var unit = await FirstAsync(admin, "/api/r/units?code=ADET");
        var warehouse = await FirstAsync(admin, "/api/r/warehouses?code=MRK");
        var product = (await OkAsync(await admin.PostAsJsonAsync("/api/r/products", new { code, name = "Satış test kalemi", unitId = unit["id"]!.ToString(), purchasePrice = 10, salePrice = 100 })))["data"]!;
        var stock = (await admin.GetFromJsonAsync<JsonObject>($"/api/stock/products/{product["id"]}"))!;
        var variantId = stock["variants"]!.AsArray().First()!["id"]!.ToString();
        await OkAsync(await admin.PostAsJsonAsync("/api/stock/movements", new
        {
            type = "RECEIPT", movementDate = Today, targetWarehouseId = warehouse["id"]!.ToString(),
            lines = new[] { new { variantId, quantity = 10, unitCost = 10 } },
        }));
        var customer = (await OkAsync(await admin.PostAsJsonAsync("/api/r/business-partners", new
        {
            code, title = "Satış test müşterisi", type = "CUSTOMER", currencyCode = "TRY", paymentTermDays = 15, creditLimit = 500,
        })))["data"]!;
        var account = (await OkAsync(await admin.PostAsJsonAsync("/api/r/treasury-accounts", new
        {
            code, name = "Test kasa", type = "CASH", currencyCode = "TRY",
        })))["data"]!;

        var quote = await OkAsync(await admin.PostAsJsonAsync("/api/sales/quotes", new
        {
            partnerId = customer["id"]!.ToString(), quoteDate = Today,
            lines = new[] { new { variantId, quantity = 6, unitPrice = 100, vatRate = 0 } },
        }));
        var orderId = await (await admin.PostAsJsonAsync($"/api/sales/quotes/{quote["id"]}/accept", new { warehouseId = warehouse["id"]!.ToString() })).Content.ReadFromJsonAsync<Guid>();
        var denied = await admin.PostAsync($"/api/sales/orders/{orderId}/approve", null);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        await OkAsync(await admin.PutAsJsonAsync($"/api/sales/orders/{orderId}", new
        {
            partnerId = customer["id"]!.ToString(), warehouseId = warehouse["id"]!.ToString(), orderDate = Today, currencyCode = "TRY",
            lines = new[] { new { variantId, quantity = 4, unitPrice = 100, vatRate = 0 } },
        }));
        var approved = await OkAsync(await admin.PostAsync($"/api/sales/orders/{orderId}/approve", null));
        Assert.Equal("APPROVED", approved["status"]!.ToString());
        var lineId = approved["lines"]!.AsArray().First()!["id"]!.ToString();

        var extra = await OkAsync(await admin.PostAsJsonAsync("/api/sales/orders", new
        {
            partnerId = customer["id"]!.ToString(), warehouseId = warehouse["id"]!.ToString(), orderDate = Today, currencyCode = "TRY",
            lines = new[] { new { variantId, quantity = 8, unitPrice = 10, vatRate = 0 } },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/sales/orders/{extra["id"]}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/r/sales-orders", new { })).StatusCode);

        await OkAsync(await admin.PostAsJsonAsync("/api/sales/deliveries", new
        {
            orderId, deliveryDate = Today, lines = new[] { new { orderLineId = lineId, quantity = 2 } },
        }));
        Assert.Equal(8m, (await FirstAsync(admin, $"/api/stock/levels?search={code}"))["quantity"]!.GetValue<decimal>());

        var before = await admin.GetFromJsonAsync<decimal>($"/api/partners/{customer["id"]}/balance");
        var invoice = await OkAsync(await admin.PostAsJsonAsync("/api/sales/invoices", new
        {
            partnerId = customer["id"]!.ToString(), orderId, invoiceDate = Today,
            lines = new[] { new { variantId, quantity = 2, unitPrice = 100, vatRate = 0, orderLineId = lineId } },
        }));
        Assert.Equal(200m, invoice["totals"]!["grandTotal"]!.GetValue<decimal>());
        Assert.Equal(before + 200m, await admin.GetFromJsonAsync<decimal>($"/api/partners/{customer["id"]}/balance"));

        await OkAsync(await admin.PostAsJsonAsync("/api/finance/payments", new
        {
            direction = "IN", method = "CASH", partnerId = customer["id"]!.ToString(), paymentDate = Today, accountId = account["id"]!.ToString(),
            amount = 200, currencyCode = "TRY", allocations = new[] { new { salesInvoiceId = invoice["id"]!.ToString(), amount = 200 } },
        }));
        Assert.Equal(before, await admin.GetFromJsonAsync<decimal>($"/api/partners/{customer["id"]}/balance"));
        var accounts = await admin.GetFromJsonAsync<List<JsonObject>>("/api/finance/accounts");
        Assert.Equal(200m, accounts!.First(a => a["id"]!.ToString() == account["id"]!.ToString())["balance"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/sales/invoices/{invoice["id"]}/cancel", new { reason = "tahsil edildi" })).StatusCode);

        var invoiceLine = invoice["lines"]!.AsArray().First()!["id"]!.ToString();
        var returned = await OkAsync(await admin.PostAsJsonAsync("/api/sales/returns", new
        {
            invoiceId = invoice["id"]!.ToString(), warehouseId = warehouse["id"]!.ToString(), returnDate = Today, reason = "test iade",
            lines = new[] { new { invoiceLineId = invoiceLine, quantity = 1 } },
        }));
        Assert.Equal(100m, returned["grandTotal"]!.GetValue<decimal>());
        Assert.Equal(9m, (await FirstAsync(admin, $"/api/stock/levels?search={code}"))["quantity"]!.GetValue<decimal>());
        Assert.Equal(before - 100m, await admin.GetFromJsonAsync<decimal>($"/api/partners/{customer["id"]}/balance"));
    }
}
