using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Assets;

/// <summary>
/// Asset categories, locations, 16 sample assets with assignments and maintenance, two counts, and the admin account's
/// employee profile (the general manager) with its own assets.
/// </summary>
public sealed class AssetSeed(AppDbContext db, NumberingService numbering, AssetService assets) : ISeedStep
{
    public int Order => 20;

    private static readonly (string Code, string Name, int Life)[] Categories =
    [
        ("BLG", "Bilgisayar", 36), ("TEL", "Mobil Cihaz", 24), ("MON", "Monitör ve Ekran", 48),
        ("OFS", "Ofis Ekipmanı", 60), ("MOB", "Mobilya", 60), ("ARC", "Araç", 60),
    ];

    private static readonly (string Code, string Name, string Branch)[] Locations =
    [
        ("DEPO", "Merkez Depo", "MERKEZ"), ("BT-ODA", "BT Sistem Odası", "MERKEZ"), ("TOPLANTI", "Toplantı Salonu", "MERKEZ"), ("ANK-DEPO", "Ankara Depo", "ANK"),
    ];

    /// <summary>Holder: "employee:Name", "department:CODE", "location:CODE" or "stock:LOCATION".</summary>
    private sealed record Sample(
        string Name, string Category, string Brand, string Model, string Serial, string PurchaseDate, decimal Cost, string Holder,
        string? Since = null, bool Acknowledged = false, (string Employee, string From, string To)? Previous = null,
        (string Title, string Vendor, string StartedAt, decimal Cost)? Maintenance = null, string? Retired = null);

    private static Sample[] Samples(int year) =>
    [
        new("Dizüstü Bilgisayar", "BLG", "Dell", "Latitude 7440", "DL7440-8KX21", "2024-04-15", 48500, "employee:Kerem Polat", "2024-05-02", true),
        new("Cep Telefonu", "TEL", "Apple", "iPhone 15", "F2LXK9P1Q7", "2025-09-20", 52000, "employee:Kerem Polat", "2025-10-01"),
        new("Dizüstü Bilgisayar", "BLG", "Apple", "MacBook Pro 14", "C02FK3MBP14", "2024-01-10", 89000, "employee:Deniz Aksoy", "2024-01-15", true),
        new("Dizüstü Bilgisayar", "BLG", "Lenovo", "ThinkPad T14 Gen 4", "PF4T14-22981", "2023-01-03", 39500, "employee:Elif Şahin", "2023-01-09", true),
        new("Monitör 27\"", "MON", "Dell", "P2723DE", "CN0P27-55K1", "2023-01-03", 11800, "employee:Elif Şahin", "2023-01-09", true),
        new("Cep Telefonu", "TEL", "Samsung", "Galaxy S23", "R5CW31ABX9", "2023-08-14", 31000, "employee:Ahmet Yıldız", "2023-08-21", true),
        new("Akıllı Ekran 65\"", "MON", "Samsung", "QM65R", "SQM65-7781", "2024-06-05", 42000, "location:TOPLANTI", "2024-06-10"),
        new("Ağ Anahtarı", "OFS", "Cisco", "Catalyst 9200", "FOC2419X0AB", "2022-11-01", 64000, "location:BT-ODA", "2022-11-07"),
        new("Çok Fonksiyonlu Yazıcı", "OFS", "HP", "LaserJet Pro MFP 4103", "VNB3K40112", "2024-02-12", 18500, "department:FIN", "2024-02-15"),
        new("Hafif Ticari Araç", "ARC", "Ford", "Transit Connect", "WF0XXXTTGXNA12345", "2022-03-01", 685000, "department:OPS", "2022-03-10"),
        new("Dizüstü Bilgisayar", "BLG", "Lenovo", "ThinkPad E14", "PF4E14-10457", "2024-02-01", 27500, "stock:DEPO",
            Previous: ("Burak Doğan", "2024-02-01", "2025-12-19")),
        new("Dizüstü Bilgisayar", "BLG", "HP", "EliteBook 840 G9", "5CG2310QWE", "2022-09-12", 34000, "stock:BT-ODA",
            Maintenance: ("Ekran değişimi", "HP Yetkili Servis", $"{year}-10-02", 4200)),
        new("Tablet", "TEL", "Apple", "iPad Air 11", "DMPXK2IPAD", "2025-03-17", 28000, "stock:ANK-DEPO"),
        new("Ergonomik Ofis Koltuğu", "MOB", "Herman Miller", "Aeron", "HM-AER-30219", "2023-05-02", 36000, "stock:DEPO"),
        new("Masaüstü Bilgisayar", "BLG", "Dell", "OptiPlex 7070", "DLOPT7070-991", "2019-06-10", 14500, "stock:DEPO",
            Retired: "Ekonomik ömrünü tamamladı, anakart arızası"),
    ];

    private static (string Name, string Category, string Brand, string Model, string Serial, string Bought, decimal Cost, string Since, bool Acknowledged)[] ProfileAssets(int year) =>
    [
        ("Dizüstü Bilgisayar", "BLG", "Apple", "MacBook Pro 16", "C02HK7MBP16", $"{year - 1}-11-04", 124_000, $"{year - 1}-11-10", true),
        ("Cep Telefonu", "TEL", "Apple", "iPhone 16 Pro", "G7QXM2P4R9", $"{year - 1}-09-25", 78_000, $"{year - 1}-10-01", true),
        ("Monitör 27\"", "MON", "LG", "UltraFine 27UQ850", "304NTAB1K882", $"{year - 1}-11-04", 21_500, $"{year - 1}-11-10", false),
    ];

    public async Task RunAsync(SeedContext ctx)
    {
        var year = DateTime.UtcNow.Year;
        var categories = new Dictionary<string, Guid>();
        foreach (var (code, name, life) in Categories)
        {
            var c = await db.AssetCategories.FirstOrDefaultAsync(x => x.Code == code);
            if (c == null)
            {
                c = new AssetCategory { Code = code, Name = name, UsefulLifeMonths = life };
                db.AssetCategories.Add(c);
                await db.SaveChangesAsync();
            }
            categories[code] = c.Id;
        }
        var locations = new Dictionary<string, Guid>();
        foreach (var (code, name, branch) in Locations)
        {
            var l = await db.AssetLocations.FirstOrDefaultAsync(x => x.Code == code);
            if (l == null)
            {
                var branchId = await db.Branches.Where(b => b.CompanyId == ctx.PulseId && b.Code == branch).Select(b => (Guid?)b.Id).FirstOrDefaultAsync();
                l = new AssetLocation { Code = code, Name = name, BranchId = branchId };
                db.AssetLocations.Add(l);
                await db.SaveChangesAsync();
            }
            locations[code] = l.Id;
        }

        if (!await db.Assets.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == ctx.PulseId))
        {
            await SeedAssetsAsync(ctx, year, categories, locations);
        }
        await SeedCountsAsync(ctx, year);
        await SeedProfileAsync(ctx, year, categories);
    }

    private async Task SeedAssetsAsync(SeedContext ctx, int year, Dictionary<string, Guid> categories, Dictionary<string, Guid> locations)
    {
        var employees = (await db.Employees.OrderBy(e => e.EmployeeNo).Select(e => new { Name = e.FirstName + " " + e.LastName, e.Id }).ToListAsync())
            .GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First().Id);
        var departments = await db.Departments.ToDictionaryAsync(d => d.Code, d => d.Id);
        var samples = Samples(year);
        for (var index = 0; index < samples.Length; index++)
        {
            var s = samples[index];
            var (kind, key) = (s.Holder[..s.Holder.IndexOf(':')], s.Holder[(s.Holder.IndexOf(':') + 1)..]);
            var bought = DateOnly.Parse(s.PurchaseDate);
            var asset = new Asset
            {
                AssetTag = await numbering.NextAsync("ASSET"), Name = s.Name, CategoryId = categories[s.Category], Brand = s.Brand, Model = s.Model,
                SerialNumber = s.Serial,
                Status = s.Retired != null ? AssetStatus.RETIRED : s.Maintenance != null ? AssetStatus.IN_MAINTENANCE : kind == "stock" ? AssetStatus.IN_STOCK : AssetStatus.ASSIGNED,
                Condition = s.Retired != null ? AssetCondition.DAMAGED : AssetCondition.GOOD,
                LocationId = kind == "stock" ? locations[key] : null,
                PurchaseDate = bought, PurchaseCost = s.Cost, SalvageValue = Math.Round(s.Cost * 0.05m), Supplier = $"{s.Brand} Türkiye Distribütörü",
                InvoiceNo = $"FTR-{bought.Year}-{index + 101:00000}", WarrantyEndDate = bought.AddYears(2),
                RetiredAt = s.Retired != null ? DateTime.UtcNow : null, RetireReason = s.Retired,
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();

            if (s.Previous is { } p)
            {
                db.AssetAssignments.Add(new AssetAssignment
                {
                    Number = await numbering.NextAsync("ASSET_ASSIGNMENT"), AssetId = asset.Id, AssigneeType = AssigneeType.EMPLOYEE,
                    EmployeeId = employees[p.Employee], Status = AssignmentStatus.RETURNED, AssignedAt = DateOnly.Parse(p.From),
                    ConditionOut = AssetCondition.NEW, AssignedById = ctx.AdminId, AcknowledgedAt = DateTime.Parse(p.From).ToUniversalTime(),
                    ReturnedAt = DateOnly.Parse(p.To), ConditionIn = AssetCondition.GOOD, ReturnNotes = "Cihaz yenilendi, eski cihaz depoya alındı",
                    ReturnedById = ctx.AdminId,
                });
            }
            if (kind != "stock")
            {
                var since = DateOnly.Parse(s.Since!);
                db.AssetAssignments.Add(new AssetAssignment
                {
                    Number = await numbering.NextAsync("ASSET_ASSIGNMENT"), AssetId = asset.Id,
                    AssigneeType = kind switch { "employee" => AssigneeType.EMPLOYEE, "department" => AssigneeType.DEPARTMENT, _ => AssigneeType.LOCATION },
                    EmployeeId = kind == "employee" ? employees[key] : null, DepartmentId = kind == "department" ? departments[key] : null,
                    LocationId = kind == "location" ? locations[key] : null, AssignedAt = since, ConditionOut = AssetCondition.NEW,
                    AssignedById = ctx.AdminId, AcknowledgedAt = s.Acknowledged ? since.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc) : null,
                });
            }
            if (s.Maintenance is { } m)
            {
                db.AssetMaintenances.Add(new AssetMaintenance
                {
                    AssetId = asset.Id, Type = MaintenanceType.REPAIR, Title = m.Title, Vendor = m.Vendor, Cost = m.Cost,
                    StartedAt = DateOnly.Parse(m.StartedAt), CreatedById = ctx.AdminId,
                });
            }
            if (s.Category == "ARC")
            {
                db.AssetMaintenances.Add(new AssetMaintenance
                {
                    AssetId = asset.Id, Type = MaintenanceType.SERVICE, Status = MaintenanceStatus.COMPLETED, Title = "Periyodik bakım (60.000 km)",
                    Vendor = "Ford Yetkili Servis", Cost = 9800, StartedAt = new DateOnly(year, 4, 14), CompletedAt = new DateOnly(year, 4, 15),
                    CreatedById = ctx.AdminId,
                });
            }
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Links the admin account to the general manager so its self-service pages (my assets, my leave) are not empty.</summary>
    private async Task SeedProfileAsync(SeedContext ctx, int year, Dictionary<string, Guid> categories)
    {
        var profile = await db.Employees.FirstOrDefaultAsync(e => e.UserId == ctx.AdminId)
            ?? await db.Employees.FirstOrDefaultAsync(e => e.FirstName == "Hakan" && e.LastName == "Erdem" && e.UserId == null);
        if (profile == null) return;
        profile.UserId = ctx.AdminId;
        await db.SaveChangesAsync();
        if (await db.AssetAssignments.AnyAsync(a => a.EmployeeId == profile.Id)) return;
        foreach (var s in ProfileAssets(year))
        {
            var bought = DateOnly.Parse(s.Bought);
            var since = DateOnly.Parse(s.Since);
            var asset = new Asset
            {
                AssetTag = await numbering.NextAsync("ASSET"), Name = s.Name, CategoryId = categories[s.Category], Brand = s.Brand, Model = s.Model,
                SerialNumber = s.Serial, Status = AssetStatus.ASSIGNED, Condition = AssetCondition.GOOD, PurchaseDate = bought, PurchaseCost = s.Cost,
                SalvageValue = Math.Round(s.Cost * 0.05m), Supplier = $"{s.Brand} Türkiye Distribütörü", InvoiceNo = $"FTR-{bought.Year}-{s.Serial[^5..]}",
                WarrantyEndDate = bought.AddYears(2),
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            db.AssetAssignments.Add(new AssetAssignment
            {
                Number = await numbering.NextAsync("ASSET_ASSIGNMENT"), AssetId = asset.Id, AssigneeType = AssigneeType.EMPLOYEE, EmployeeId = profile.Id,
                AssignedAt = since, ConditionOut = AssetCondition.NEW, AssignedById = ctx.AdminId,
                AcknowledgedAt = s.Acknowledged ? since.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc) : null,
            });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>A completed company-wide count (one asset missing) and an open computer count that is half scanned.</summary>
    private async Task SeedCountsAsync(SeedContext ctx, int year)
    {
        if (await db.AssetCounts.AnyAsync()) return;
        var inService = await db.Assets.Where(a => !AssetService.OutOfService.Contains(a.Status)).OrderBy(a => a.AssetTag)
            .Select(a => new { a.Id, a.CategoryId, Location = a.Location == null ? null : a.Location.Name }).ToListAsync();
        if (inService.Count == 0) return;
        var holders = await assets.HoldersAsync(inService.Select(a => a.Id).ToList());
        string? HolderOf(Guid id, string? location) => holders.GetValueOrDefault(id)?.Name ?? location;

        var completedAt = new DateTime(year, 6, 30, 16, 0, 0, DateTimeKind.Utc);
        var done = new AssetCount
        {
            Number = await numbering.NextAsync("ASSET_COUNT"), Status = AssetCountStatus.COMPLETED, CountDate = new DateOnly(year, 6, 30),
            Notes = "Yıl ortası demirbaş sayımı", CreatedById = ctx.AdminId, CompletedAt = completedAt,
        };
        db.AssetCounts.Add(done);
        for (var i = 0; i < inService.Count; i++)
        {
            var missing = i == inService.Count - 1;
            db.AssetCountLines.Add(new AssetCountLine
            {
                CountId = done.Id, AssetId = inService[i].Id, ExpectedHolder = HolderOf(inService[i].Id, inService[i].Location),
                FoundAt = missing ? null : completedAt, FoundById = missing ? null : ctx.AdminId,
                Note = missing ? "Konumunda bulunamadı, sorumluya soruldu" : null,
            });
        }

        var category = inService[0].CategoryId;
        var scope = inService.Where(a => a.CategoryId == category).ToList();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var open = new AssetCount
        {
            Number = await numbering.NextAsync("ASSET_COUNT"), CountDate = new DateOnly(today.Year, today.Month, 1), CategoryId = category,
            Notes = "Bilgisayar envanteri kontrolü", CreatedById = ctx.AdminId,
        };
        db.AssetCounts.Add(open);
        for (var i = 0; i < scope.Count; i++)
        {
            var found = i < scope.Count / 2.0;
            db.AssetCountLines.Add(new AssetCountLine
            {
                CountId = open.Id, AssetId = scope[i].Id, ExpectedHolder = HolderOf(scope[i].Id, scope[i].Location),
                FoundAt = found ? DateTime.UtcNow : null, FoundById = found ? ctx.AdminId : null,
            });
        }
        await db.SaveChangesAsync();
    }
}
