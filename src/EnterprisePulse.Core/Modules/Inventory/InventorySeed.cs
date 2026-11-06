using System.Text.Json;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Purchasing;
using EnterprisePulse.Core.Modules.Trade;
using EnterprisePulse.Core.Modules.Workflow;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Inventory;

/// <summary>
/// Demo stock, partners and purchasing: opening movements, a completed and an open count, competing quotes and
/// orders whose receipts are real stock receipts.
/// </summary>
public sealed class InventorySeed(
    AppDbContext db, StockMovementService movements, StockCountService counts, PurchaseRequestService requests, SupplierQuoteService quotes,
    PurchaseOrderService orders, GoodsReceiptService receipts, PurchaseInvoiceService invoices, WorkflowService workflow) : ISeedStep
{
    public int Order => 30;

    private sealed record Line(string Sku, decimal Qty, decimal? Cost = null, string? Lot = null, int? ExpiryInDays = null, string[]? Serials = null);

    public async Task RunAsync(SeedContext ctx)
    {
        using var _ = db.User.ActAs(ctx.PulseId, ctx.AdminId, superAdmin: true);
        var categories = await CategoriesAsync();
        var warehouses = await WarehousesAsync(ctx.PulseId);
        if (await db.Products.AnyAsync()) return;

        var variants = await ProductsAsync(categories);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly Day(int days) => today.AddDays(-days);

        await Move(StockMovementType.RECEIPT, Day(60), null, warehouses["MRK"], "İRS-A-104288", "Yıllık tedarik anlaşması kapsamında ilk teslimat",
        [
            new("PRD-LAPTOP", 6, 32000, Serials: Range("LTP7-", 1, 6)), new("PRD-PHONE", 4, 18000, Serials: Range("PHN-", 1, 4)),
            new("PRD-MONITOR", 20, 6500), new("PRD-MOUSE-SYH", 60, 350), new("PRD-MOUSE-BYZ", 40, 360),
            new("PRD-KLAVYE-Q", 25, 1200), new("PRD-KLAVYE-F", 10, 1250),
            new("PRD-POLO-LCV-S", 20, 220), new("PRD-POLO-LCV-M", 40, 220), new("PRD-POLO-LCV-L", 40, 220), new("PRD-POLO-LCV-XL", 20, 220),
            new("PRD-A4", 80, 450), new("PRD-TONER", 12, 1400), new("PRD-KAHVE", 30, 520, "KHV-2401", 200), new("PRD-SU", 50, 70),
            new("PRD-DEZ", 40, 180, "DZN-0915", 300),
        ], variants);

        var partners = await PartnersAsync();
        var ankara = await orders.CreateAsync(new SavePurchaseOrder(partners["CAR-001"], warehouses["ANK"], Day(50), Day(43), "TRY", null,
            "Ankara ofisi açılış ihtiyaçları",
            [
                Item(variants, "PRD-MONITOR", 8, 6700), Item(variants, "PRD-MOUSE-SYH", 20, 355), Item(variants, "PRD-A4", 30, 460),
                Item(variants, "PRD-KAHVE", 15, 540, vat: 10), Item(variants, "PRD-SU", 30, 72, vat: 10),
            ]));
        await orders.ApproveAsync(ankara.Id);
        var ankaraDetail = await orders.GetAsync(ankara.Id);
        await receipts.CreateAsync(new CreateGoodsReceipt(ankara.Id, warehouses["ANK"], Day(45), "İRS-B-55120", null,
            ankaraDetail.Lines.Select(l => new ReceiptLineInput(l.Id, l.Quantity, l.Sku == "PRD-KAHVE" ? "KHV-2402" : null, l.Sku == "PRD-KAHVE" ? today.AddDays(240) : null, null)).ToList()));
        await invoices.CreateAsync(new CreatePurchaseInvoice(partners["CAR-001"], ankara.Id, $"KRD{today.Year}000418", Day(44), Day(14), "TRY", null, null,
            ankaraDetail.Lines.Select(l => new TradeLineInput(l.VariantId, l.Description, l.Quantity, l.UnitPrice, l.DiscountRate, l.VatRate, OrderLineId: l.Id)).ToList()));

        await Move(StockMovementType.TRANSFER, Day(30), warehouses["MRK"], warehouses["IZM"], null, "İzmir şubesi açılış sevkiyatı",
        [
            new("PRD-LAPTOP", 2, Serials: ["LTP7-0005", "LTP7-0006"]), new("PRD-MONITOR", 4), new("PRD-MOUSE-SYH", 15), new("PRD-A4", 20), new("PRD-DEZ", 10, Lot: "DZN-0915"),
        ], variants);
        await Move(StockMovementType.ISSUE, Day(20), warehouses["MRK"], null, "Bilgi İşlem talebi #118", "Yeni başlayan personel için ekipman çıkışı",
        [
            new("PRD-LAPTOP", 2, Serials: ["LTP7-0001", "LTP7-0002"]), new("PRD-MONITOR", 3), new("PRD-MOUSE-SYH", 10), new("PRD-KLAVYE-Q", 5),
            new("PRD-TONER", 6), new("PRD-KAHVE", 8, Lot: "KHV-2401"), new("PRD-POLO-LCV-M", 34),
        ], variants);

        await PurchasingAsync(ctx, variants, warehouses, partners, today);
        await Move(StockMovementType.ISSUE, Day(5), warehouses["ANK"], null, "Ofis tüketimi", null, [new("PRD-SU", 12), new("PRD-KAHVE", 5, Lot: "KHV-2402")], variants);
        await CountsAsync(warehouses, variants);
    }

    private async Task PurchasingAsync(SeedContext ctx, Dictionary<string, Guid> variants, Dictionary<string, Guid> warehouses, Dictionary<string, Guid> partners, DateOnly today)
    {
        var employee = ctx.UsersByName["Kerem Polat"];
        var manager = ctx.UsersByName["Deniz Aksoy"];
        using (db.User.ActAs(ctx.PulseId, employee))
        {
            db.User.Permissions.Add("workflow:start");
            await requests.CreateAsync(new CreatePurchaseRequest("5 adet dizüstü bilgisayar talebi", today.AddDays(14), null, "Yeni katılan yazılım ekibi için.",
                [new RequestLineInput(variants["PRD-LAPTOP"], null, 5, 32000)]));
        }
        using var admin = db.User.ActAs(ctx.PulseId, ctx.AdminId, superAdmin: true);

        Guid stationeryId;
        using (db.User.ActAs(ctx.PulseId, manager))
        {
            db.User.Permissions.Add("purchasing:create");
            stationeryId = (await requests.CreateAsync(new CreatePurchaseRequest("Ankara ofisi kırtasiye ihtiyacı", today.AddDays(10), null, null,
            [
                new RequestLineInput(variants["PRD-A4"], null, 40, 450), new RequestLineInput(variants["PRD-TONER"], null, 10, 1400),
                new RequestLineInput(null, "Ergonomik ayak desteği", 6, 750),
            ]))).Id;
        }
        var stationery = await db.PurchaseRequests.FirstAsync(r => r.Id == stationeryId);
        await workflow.ApproveAsync(stationery.WorkflowInstanceId!.Value, "Uygun");
        await workflow.ApproveAsync(stationery.WorkflowInstanceId.Value, "Uygun");
        var stationeryLines = (await requests.GetAsync(stationeryId)).Lines;
        await quotes.CreateAsync(Quote(partners["TED-002"], stationeryId, today.AddDays(-3), 3,
            [QuoteLine(variants, stationeryLines[0].Id, "PRD-A4", 40, 440), QuoteLine(variants, stationeryLines[1].Id, "PRD-TONER", 10, 1380)]));
        await quotes.CreateAsync(Quote(partners["CAR-001"], stationeryId, today.AddDays(-2), 7,
            [QuoteLine(variants, stationeryLines[0].Id, "PRD-A4", 40, 455, 5), QuoteLine(variants, stationeryLines[1].Id, "PRD-TONER", 10, 1350, 5)]));

        var restock = await requests.CreateAsync(new CreatePurchaseRequest("Merkez depo monitör ve kahve ikmali", null, null, null,
            [new RequestLineInput(variants["PRD-MONITOR"], null, 10, 6500), new RequestLineInput(variants["PRD-KAHVE"], null, 20, 550)]));
        var restockRow = await db.PurchaseRequests.FirstAsync(r => r.Id == restock.Id);
        await workflow.ApproveAsync(restockRow.WorkflowInstanceId!.Value, "Uygun");
        await workflow.ApproveAsync(restockRow.WorkflowInstanceId.Value, "Uygun");
        var restockLines = (await requests.GetAsync(restock.Id)).Lines;
        await quotes.CreateAsync(Quote(partners["CAR-001"], restock.Id, today.AddDays(-15), 10,
            [QuoteLine(variants, restockLines[0].Id, "PRD-MONITOR", 10, 6750), QuoteLine(variants, restockLines[1].Id, "PRD-KAHVE", 20, 545, vat: 10)]));
        var accepted = await quotes.CreateAsync(Quote(partners["TED-001"], restock.Id, today.AddDays(-15), 5,
            [QuoteLine(variants, restockLines[0].Id, "PRD-MONITOR", 10, 6600), QuoteLine(variants, restockLines[1].Id, "PRD-KAHVE", 20, 560, vat: 10)]));
        var orderId = await quotes.AcceptAsync(accepted.Id, new AcceptQuote(warehouses["MRK"], today.AddDays(7)));
        await orders.ApproveAsync(orderId);
        var order = await orders.GetAsync(orderId);
        await receipts.CreateAsync(new CreateGoodsReceipt(orderId, warehouses["MRK"], today.AddDays(-10), "İRS-A-104731", null,
        [
            new ReceiptLineInput(order.Lines[0].Id, 5, null, null, null),
            new ReceiptLineInput(order.Lines[1].Id, 20, "KHV-2403", today.AddDays(260), null),
        ]));
        await invoices.CreateAsync(new CreatePurchaseInvoice(partners["TED-001"], orderId, $"ANB{today.Year}001204", today.AddDays(-9), today.AddDays(21), "TRY", null, null,
        [
            new TradeLineInput(order.Lines[0].VariantId, order.Lines[0].Description, 5, 6600, OrderLineId: order.Lines[0].Id),
            new TradeLineInput(order.Lines[1].VariantId, order.Lines[1].Description, 20, 560, VatRate: 10, OrderLineId: order.Lines[1].Id),
        ]));

        var eur = await db.ExchangeRates.Where(r => r.CurrencyCode == "EUR").OrderByDescending(r => r.RateDate).Select(r => (decimal?)r.Selling).FirstAsync();
        var euro = await orders.CreateAsync(new SavePurchaseOrder(partners["TED-003"], warehouses["MRK"], today.AddDays(-3), today.AddDays(4), "EUR", eur,
            null, [Item(variants, "PRD-LAPTOP", 4, 690, discount: 3), Item(variants, "PRD-PHONE", 2, 380)]));
        var euroRow = await db.PurchaseOrders.FirstAsync(o => o.Id == euro.Id);
        euroRow.Status = PurchaseOrderStatus.APPROVED;
        euroRow.ApprovedById = ctx.AdminId;
        euroRow.ApprovedAt = DateTime.UtcNow;
        await orders.CreateAsync(new SavePurchaseOrder(partners["TED-004"], warehouses["MRK"], today.AddDays(-1), today.AddDays(6), null, null, null,
            [Item(variants, "PRD-DEZ", 30, 185), Item(variants, "PRD-SU", 60, 68, vat: 10)]));
        await db.SaveChangesAsync();
    }

    private async Task CountsAsync(Dictionary<string, Guid> warehouses, Dictionary<string, Guid> variants)
    {
        var completed = await counts.CreateAsync(new CreateStockCountRequest(warehouses["MRK"], null, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3), "Ay sonu sayım farkları"));
        var updates = completed.Lines.Select(l => new CountLineUpdate(l.Id, l.Sku switch { "PRD-A4" => l.ExpectedQty - 2, "PRD-SU" => l.ExpectedQty + 1, _ => l.ExpectedQty })).ToList();
        await counts.UpdateLinesAsync(completed.Id, updates);
        await counts.CompleteAsync(completed.Id);

        var open = await counts.CreateAsync(new CreateStockCountRequest(warehouses["IZM"], null, DateOnly.FromDateTime(DateTime.UtcNow), "Çeyrek sonu sayımı"));
        await counts.UpdateLinesAsync(open.Id, open.Lines.Where(l => l.Sku == "PRD-MONITOR").Select(l => new CountLineUpdate(l.Id, l.ExpectedQty)).ToList());
    }

    private async Task Move(StockMovementType type, DateOnly date, Guid? source, Guid? target, string? reference, string? notes, IReadOnlyList<Line> lines, Dictionary<string, Guid> variants)
    {
        var inputs = new List<MovementLineInput>();
        foreach (var l in lines)
        {
            var variantId = variants[l.Sku];
            List<Guid>? serialIds = null;
            if (l.Serials != null && type != StockMovementType.RECEIPT)
            {
                serialIds = await db.SerialNumbers.Where(s => s.VariantId == variantId && l.Serials.Contains(s.SerialNo)).Select(s => s.Id).ToListAsync();
            }
            inputs.Add(new MovementLineInput(variantId, l.Qty, l.Cost, LotNo: l.Lot, ExpiryDate: l.ExpiryInDays == null ? null : DateOnly.FromDateTime(DateTime.UtcNow).AddDays(l.ExpiryInDays.Value),
                SerialNos: type == StockMovementType.RECEIPT ? l.Serials?.ToList() : null, SerialIds: serialIds));
        }
        await movements.PostAsync(new CreateMovementRequest(type, date, source, target, reference, notes, inputs));
    }

    private static TradeLineInput Item(Dictionary<string, Guid> variants, string sku, decimal qty, decimal price, decimal discount = 0, decimal vat = 20) =>
        new(variants[sku], null, qty, price, discount, vat);

    private static TradeLineInput QuoteLine(Dictionary<string, Guid> variants, Guid requestLineId, string sku, decimal qty, decimal price, decimal discount = 0, decimal vat = 20) =>
        new(variants[sku], null, qty, price, discount, vat, requestLineId);

    private static CreateSupplierQuote Quote(Guid partnerId, Guid requestId, DateOnly date, int deliveryDays, List<TradeLineInput> lines) =>
        new(partnerId, requestId, date, date.AddDays(30), deliveryDays, "TRY", 1, null, lines);

    private static string[] Range(string prefix, int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => $"{prefix}{n:0000}").ToArray();

    private async Task<Dictionary<string, Guid>> CategoriesAsync()
    {
        (string Code, string Name, string? Parent)[] rows =
        [
            ("ELK", "Elektronik", null), ("BLG", "Bilgisayar ve Aksesuar", "ELK"), ("TEL", "Telefon ve Tablet", "ELK"),
            ("OFS", "Ofis Malzemeleri", null), ("KRT", "Kırtasiye", "OFS"), ("TKS", "Tekstil", null), ("GDA", "Gıda ve İçecek", null), ("TMZ", "Temizlik", null),
        ];
        var ids = new Dictionary<string, Guid>();
        foreach (var (code, name, parent) in rows)
        {
            var row = await db.ProductCategories.FirstOrDefaultAsync(c => c.Code == code);
            if (row == null)
            {
                row = new ProductCategory { Code = code, Name = name, ParentId = parent == null ? null : ids[parent] };
                db.ProductCategories.Add(row);
                await db.SaveChangesAsync();
            }
            ids[code] = row.Id;
        }
        return ids;
    }

    private async Task<Dictionary<string, Guid>> WarehousesAsync(Guid companyId)
    {
        (string Code, string Name, string Branch, string Address)[] rows =
        [
            ("MRK", "Merkez Depo", "MERKEZ", "Tuzla Organize Sanayi Bölgesi, İstanbul"),
            ("ANK", "Ankara Depo", "ANK", "Ostim OSB, Ankara"),
            ("IZM", "İzmir Depo", "IZM", "Atatürk OSB, Çiğli, İzmir"),
        ];
        var ids = new Dictionary<string, Guid>();
        foreach (var (code, name, branch, address) in rows)
        {
            var row = await db.Warehouses.FirstOrDefaultAsync(w => w.Code == code);
            if (row == null)
            {
                var branchId = await db.Branches.Where(b => b.CompanyId == companyId && b.Code == branch).Select(b => (Guid?)b.Id).FirstOrDefaultAsync();
                row = new Warehouse { Code = code, Name = name, Address = address, BranchId = branchId };
                db.Warehouses.Add(row);
                await db.SaveChangesAsync();
            }
            ids[code] = row.Id;
        }
        return ids;
    }

    private async Task<Dictionary<string, Guid>> ProductsAsync(Dictionary<string, Guid> categories)
    {
        var units = await db.Units.ToDictionaryAsync(u => u.Code, u => u.Id);
        var vat = await db.TaxRates.Where(t => t.Code == "KDV20").Select(t => (Guid?)t.Id).FirstOrDefaultAsync();
        var ids = new Dictionary<string, Guid>();
        void Add(Product product, params (string Sku, string? Name, decimal Min, string? Barcode, Dictionary<string, string>? Attr)[] variants)
        {
            var first = true;
            foreach (var v in variants)
            {
                product.Variants.Add(new ProductVariant
                {
                    Sku = v.Sku, Name = v.Name, Barcode = v.Barcode, MinStock = v.Min, IsDefault = first,
                    Attributes = v.Attr == null ? null : JsonSerializer.SerializeToDocument(v.Attr),
                });
                ids[v.Sku] = Guid.Empty;
                first = false;
            }
            db.Products.Add(product);
        }
        Add(Good("PRD-LAPTOP", "Dizüstü Bilgisayar 14\" i7 / 16 GB", "BLG", "ADET", 32000, 39999, TrackingType.SERIAL), ("PRD-LAPTOP", null, 3, "8690000000017", null));
        Add(Good("PRD-MONITOR", "27\" IPS Monitör", "BLG", "ADET", 6500, 8499), ("PRD-MONITOR", null, 5, "8690000000024", null));
        Add(Good("PRD-MOUSE", "Kablosuz Mouse", "BLG", "ADET", 350, 549), ("PRD-MOUSE-SYH", "Siyah", 20, null, Attr("Renk", "Siyah")), ("PRD-MOUSE-BYZ", "Beyaz", 10, null, Attr("Renk", "Beyaz")));
        Add(Good("PRD-KLAVYE", "Mekanik Klavye", "BLG", "ADET", 1200, 1799), ("PRD-KLAVYE-Q", "Türkçe Q", 10, null, Attr("Düzen", "Q")), ("PRD-KLAVYE-F", "Türkçe F", 3, null, Attr("Düzen", "F")));
        Add(Good("PRD-PHONE", "Akıllı Telefon 128 GB", "TEL", "ADET", 18000, 22999, TrackingType.SERIAL), ("PRD-PHONE", null, 2, null, null));
        Add(Good("PRD-POLO", "Kurumsal Polo Tişört", "TKS", "ADET", 220, null),
            ("PRD-POLO-LCV-S", "Lacivert / S", 5, null, Attr("Renk", "Lacivert", "Beden", "S")), ("PRD-POLO-LCV-M", "Lacivert / M", 10, null, Attr("Renk", "Lacivert", "Beden", "M")),
            ("PRD-POLO-LCV-L", "Lacivert / L", 10, null, Attr("Renk", "Lacivert", "Beden", "L")), ("PRD-POLO-LCV-XL", "Lacivert / XL", 5, null, Attr("Renk", "Lacivert", "Beden", "XL")));
        Add(Good("PRD-A4", "A4 Fotokopi Kağıdı (5 top)", "KRT", "KOLI", 450, 590), ("PRD-A4", null, 30, "8690000000031", null));
        Add(Good("PRD-TONER", "Lazer Yazıcı Toneri", "OFS", "ADET", 1400, 1900), ("PRD-TONER", null, 8, null, null));
        Add(Good("PRD-KAHVE", "Filtre Kahve 1 kg", "GDA", "KG", 520, 690, TrackingType.LOT), ("PRD-KAHVE", null, 10, null, null));
        Add(Good("PRD-SU", "Damacana Su 19 L", "GDA", "ADET", 70, 95), ("PRD-SU", null, 20, null, null));
        Add(Good("PRD-DEZ", "Yüzey Dezenfektanı 5 L", "TMZ", "LT", 180, 260, TrackingType.LOT), ("PRD-DEZ", null, 15, null, null));
        Add(new Product { Code = "SRV-KURULUM", Name = "Kurulum ve Yapılandırma Hizmeti", CategoryId = categories["BLG"], UnitId = units["SAAT"], Type = ProductType.SERVICE, SalePrice = 1500, VatRateId = vat },
            ("SRV-KURULUM", null, 0, null, null));
        await db.SaveChangesAsync();

        Product Good(string code, string name, string category, string unit, decimal purchase, decimal? sale, TrackingType tracking = TrackingType.NONE) => new()
        {
            Code = code, Name = name, CategoryId = categories[category], UnitId = units[unit], PurchasePrice = purchase, SalePrice = sale,
            TrackingType = tracking, VatRateId = vat,
        };
        var saved = await db.ProductVariants.Select(v => new { v.Sku, v.Id }).ToListAsync();
        return saved.ToDictionary(v => v.Sku, v => v.Id);
    }

    private static Dictionary<string, string> Attr(params string[] pairs)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i < pairs.Length; i += 2) d[pairs[i]] = pairs[i + 1];
        return d;
    }

    private async Task<Dictionary<string, Guid>> PartnersAsync()
    {
        (string Code, string Title, PartnerType Type, string City, string Tax, int Term, string Currency, decimal? Limit)[] rows =
        [
            ("TED-001", "Anadolu Bilişim Ltd. Şti.", PartnerType.SUPPLIER, "İstanbul", "1112223334", 30, "TRY", null),
            ("TED-002", "Ege Kırtasiye San. Tic. A.Ş.", PartnerType.SUPPLIER, "İzmir", "2223334445", 45, "TRY", null),
            ("TED-003", "TechSource Europe GmbH", PartnerType.SUPPLIER, "München", "3334445556", 60, "EUR", null),
            ("TED-004", "Marmara Ambalaj A.Ş.", PartnerType.SUPPLIER, "Kocaeli", "4445556667", 30, "TRY", null),
            ("MUS-001", "Boğaziçi Perakende A.Ş.", PartnerType.CUSTOMER, "İstanbul", "5556667778", 30, "TRY", 500000),
            ("MUS-002", "Başkent Ofis Çözümleri Ltd.", PartnerType.CUSTOMER, "Ankara", "6667778889", 15, "TRY", 150000),
            ("MUS-003", "Akdeniz Otelcilik A.Ş.", PartnerType.CUSTOMER, "Antalya", "7778889990", 45, "TRY", 250000),
            ("MUS-004", "Nordic Retail AB", PartnerType.CUSTOMER, "Stockholm", "8889990001", 30, "USD", 40000),
            ("CAR-001", "Karadeniz Ticaret ve Dağıtım", PartnerType.BOTH, "Trabzon", "9990001112", 30, "TRY", 100000),
        ];
        var ids = new Dictionary<string, Guid>();
        foreach (var r in rows)
        {
            var row = new BusinessPartner
            {
                Code = r.Code, Title = r.Title, Type = r.Type, City = r.City, TaxNumber = r.Tax, TaxOffice = r.City, PaymentTermDays = r.Term,
                CurrencyCode = r.Currency, CreditLimit = r.Limit, Email = $"info@{r.Code.ToLowerInvariant()}.example.com", Phone = "+90 212 555 00 00",
                ContactName = r.Type == PartnerType.SUPPLIER ? "Satış Temsilcisi" : "Satın Alma Sorumlusu",
            };
            db.BusinessPartners.Add(row);
            ids[r.Code] = row.Id;
        }
        await db.SaveChangesAsync();
        return ids;
    }
}
