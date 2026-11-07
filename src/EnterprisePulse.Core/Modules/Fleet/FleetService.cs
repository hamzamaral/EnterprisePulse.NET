using System.Text.RegularExpressions;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Fleet;

public sealed record SaveVehicle(string Plate, string Brand, string Model, int? ModelYear, string? Color, VehicleFuelType FuelType, VehicleOwnership Ownership, DateOnly? ContractEndsOn, Guid? DepartmentId, int? ServiceIntervalKm, int? ServiceIntervalMonths, string? Notes);
public sealed record AssignVehicle(Guid EmployeeId, DateOnly StartDate, int StartOdometer, string? Note);
public sealed record ReturnVehicle(DateOnly EndDate, int EndOdometer, string? Note);
public sealed record SaveFuel(DateOnly EntryDate, int Odometer, decimal Liters, decimal Amount, bool FullTank, string? Station, string? Note);
public sealed record SaveService(VehicleServiceType Type, DateOnly ServiceDate, int? Odometer, Guid? PartnerId, string Description, decimal Cost);
public sealed record SaveDocument(VehicleDocumentType Type, DateOnly? StartDate, DateOnly EndDate, string? Provider, string? PolicyNumber, decimal Cost, string? Note);
public sealed record VehicleDto(Guid Id, string Plate, string Brand, string Model, VehicleStatus Status, int Odometer, Guid? DriverId, string? DriverName, DateOnly? NextServiceDate, int? NextServiceKm, string ServiceState, int OpenDocumentWarnings);
public sealed record VehicleList(List<VehicleDto> Items);

/// <summary>Filo. Plaka TR formatında; yakıt, bakım ve belge hatırlatmaları günlük işte gider.</summary>
[ModuleService]
public sealed class FleetService(AppDbContext db, NotificationService notifications) : IScheduledJob
{
    private static readonly Regex Plate = new(@"^(0[1-9]|[1-7]\d|8[01])([A-Z]{1,3})(\d{2,4})$", RegexOptions.Compiled);
    public const int WarnDays = 30;
    public const int WarnKm = 1000;

    public string Id => "fleet-daily";
    public string Cron => "0 8 * * *";

    private CurrentUser Ctx => db.User;

    public async Task RunAsync()
    {
        using var bypass = Ctx.WithoutTenant();
        foreach (var companyId in await db.Companies.Select(c => c.Id).ToListAsync())
        {
            using var _ = Ctx.ActAs(companyId, superAdmin: true);
            await RemindAsync();
        }
    }

    public async Task<VehicleDto> CreateAsync(SaveVehicle dto)
    {
        Guard.That(Ctx.Has("fleet:update"), "Araç kaydı yetkiniz yok");
        var plate = Normalize(dto.Plate) ?? throw new BadRequestAppException("Plaka geçersiz");
        Guard.That(!await db.Vehicles.AnyAsync(v => v.Plate == plate), "Plaka zaten kayıtlı");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Brand) && !string.IsNullOrWhiteSpace(dto.Model), "Marka ve model gerekli");
        var vehicle = new Vehicle
        {
            Plate = plate, Brand = dto.Brand.Trim(), Model = dto.Model.Trim(), ModelYear = dto.ModelYear, Color = Blank(dto.Color),
            FuelType = dto.FuelType, Ownership = dto.Ownership, ContractEndsOn = dto.ContractEndsOn, DepartmentId = dto.DepartmentId,
            ServiceIntervalKm = dto.ServiceIntervalKm, ServiceIntervalMonths = dto.ServiceIntervalMonths, Notes = Blank(dto.Notes),
            CreatedById = Ctx.RequireUserId(),
        };
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync();
        return await GetAsync(vehicle.Id);
    }

    public async Task<VehicleDto> AssignAsync(Guid id, AssignVehicle dto)
    {
        Guard.That(Ctx.Has("fleet:update"), "Zimmet yetkiniz yok");
        var vehicle = await ActiveAsync(id);
        Guard.That(!await db.VehicleAssignments.AnyAsync(a => a.VehicleId == id && a.EndDate == null), "Araç zaten zimmetli");
        Guard.That(await db.Employees.AnyAsync(e => e.Id == dto.EmployeeId && e.Status == EmployeeStatus.ACTIVE), "Personel bulunamadı");
        Guard.That(dto.StartOdometer >= vehicle.Odometer, "Kilometre mevcut okumadan küçük olamaz");
        db.VehicleAssignments.Add(new VehicleAssignment
        {
            VehicleId = id, EmployeeId = dto.EmployeeId, StartDate = dto.StartDate, StartOdometer = dto.StartOdometer,
            Note = Blank(dto.Note), CreatedById = Ctx.RequireUserId(),
        });
        await RecordOdometerAsync(vehicle, dto.StartDate, dto.StartOdometer, VehicleOdometerSource.ASSIGNMENT, null, null);
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<VehicleDto> ReturnAsync(Guid id, ReturnVehicle dto)
    {
        Guard.That(Ctx.Has("fleet:update"), "İade yetkiniz yok");
        var vehicle = await ActiveAsync(id);
        var assignment = await db.VehicleAssignments.FirstOrDefaultAsync(a => a.VehicleId == id && a.EndDate == null) ?? throw new ConflictAppException("Açık zimmet yok");
        Guard.That(dto.EndDate >= assignment.StartDate, "İade tarihi zimmetten önce olamaz");
        Guard.That(dto.EndOdometer >= assignment.StartOdometer, "İade kilometresı başlangıçtan küçük olamaz");
        assignment.EndDate = dto.EndDate;
        assignment.EndOdometer = dto.EndOdometer;
        if (dto.Note != null) assignment.Note = Blank(dto.Note);
        await RecordOdometerAsync(vehicle, dto.EndDate, dto.EndOdometer, VehicleOdometerSource.ASSIGNMENT, null, null);
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<VehicleDto> AddFuelAsync(Guid id, SaveFuel dto)
    {
        Guard.That(Ctx.Has("fleet:update") || await IsDriverAsync(id), "Yakıt girişi yetkiniz yok");
        var vehicle = await ActiveAsync(id);
        Guard.That(dto.Liters > 0 && dto.Amount >= 0, "Litre ve tutar geçersiz");
        await AssertOdometerAsync(vehicle.Id, dto.EntryDate, dto.Odometer);
        var driver = await db.VehicleAssignments.Where(a => a.VehicleId == id && a.StartDate <= dto.EntryDate && (a.EndDate == null || a.EndDate >= dto.EntryDate))
            .Select(a => (Guid?)a.EmployeeId).FirstOrDefaultAsync();
        var entry = new VehicleFuelEntry
        {
            VehicleId = id, EntryDate = dto.EntryDate, Odometer = dto.Odometer, Liters = dto.Liters, Amount = dto.Amount,
            FullTank = dto.FullTank, Station = Blank(dto.Station), EmployeeId = driver, Note = Blank(dto.Note), CreatedById = Ctx.RequireUserId(),
        };
        db.VehicleFuelEntries.Add(entry);
        await db.SaveChangesAsync();
        await RecordOdometerAsync(vehicle, dto.EntryDate, dto.Odometer, VehicleOdometerSource.FUEL, entry.Id, null);
        await RecalcFuelAsync(id);
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<VehicleDto> AddServiceAsync(Guid id, SaveService dto)
    {
        Guard.That(Ctx.Has("fleet:update"), "Bakım kaydı yetkiniz yok");
        var vehicle = await ActiveAsync(id);
        Guard.That(!string.IsNullOrWhiteSpace(dto.Description), "Açıklama gerekli");
        if (dto.Odometer != null) await AssertOdometerAsync(vehicle.Id, dto.ServiceDate, dto.Odometer.Value);
        var service = new VehicleService
        {
            VehicleId = id, Type = dto.Type, ServiceDate = dto.ServiceDate, Odometer = dto.Odometer, PartnerId = dto.PartnerId,
            Description = dto.Description.Trim(), Cost = dto.Cost, CreatedById = Ctx.RequireUserId(),
        };
        db.VehicleServices.Add(service);
        await db.SaveChangesAsync();
        if (dto.Odometer != null) await RecordOdometerAsync(vehicle, dto.ServiceDate, dto.Odometer.Value, VehicleOdometerSource.SERVICE, null, service.Id);
        if (dto.Type == VehicleServiceType.PERIODIC)
        {
            if (vehicle.ServiceIntervalKm is > 0 && dto.Odometer != null) vehicle.NextServiceKm = dto.Odometer + vehicle.ServiceIntervalKm;
            if (vehicle.ServiceIntervalMonths is > 0) vehicle.NextServiceDate = dto.ServiceDate.AddMonths(vehicle.ServiceIntervalMonths.Value);
            vehicle.ServiceRemindedFor = null;
        }
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<VehicleDto> AddDocumentAsync(Guid id, SaveDocument dto)
    {
        Guard.That(Ctx.Has("fleet:update"), "Belge kaydı yetkiniz yok");
        await ActiveAsync(id);
        Guard.That(dto.EndDate >= (dto.StartDate ?? dto.EndDate), "Bitiş başlangıçtan önce olamaz");
        db.VehicleDocuments.Add(new VehicleDocument
        {
            VehicleId = id, Type = dto.Type, StartDate = dto.StartDate, EndDate = dto.EndDate, Provider = Blank(dto.Provider),
            PolicyNumber = Blank(dto.PolicyNumber), Cost = dto.Cost, Note = Blank(dto.Note), CreatedById = Ctx.RequireUserId(),
        });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<VehicleList> MineAsync()
    {
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (employeeId == null) return new VehicleList([]);
        var ids = await db.VehicleAssignments.Where(a => a.EmployeeId == employeeId && a.EndDate == null).Select(a => a.VehicleId).ToListAsync();
        var list = new List<VehicleDto>();
        foreach (var id in ids) list.Add(await GetAsync(id));
        return new VehicleList(list);
    }

    public async Task<VehicleDto> GetAsync(Guid id)
    {
        Guard.That(Ctx.Has("fleet:read") || await IsDriverAsync(id), "Araç görme yetkiniz yok");
        var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id) ?? throw new NotFoundAppException("Araç bulunamadı");
        var assignment = await db.VehicleAssignments.AsNoTracking().Include(a => a.Employee).Where(a => a.VehicleId == id && a.EndDate == null).FirstOrDefaultAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var docs = await db.VehicleDocuments.AsNoTracking().Where(d => d.VehicleId == id).ToListAsync();
        var warnings = docs.GroupBy(d => d.Type).Select(g => g.OrderByDescending(d => d.EndDate).First()).Count(d => d.EndDate < today || d.EndDate <= today.AddDays(WarnDays));
        return new VehicleDto(vehicle.Id, vehicle.Plate, vehicle.Brand, vehicle.Model, vehicle.Status, vehicle.Odometer,
            assignment?.EmployeeId, assignment == null ? null : $"{assignment.Employee.FirstName} {assignment.Employee.LastName}",
            vehicle.NextServiceDate, vehicle.NextServiceKm, ServiceState(vehicle, today), warnings);
    }

    private async Task RemindAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var vehicles = await db.Vehicles.Where(v => v.Status != VehicleStatus.RETIRED).ToListAsync();
        foreach (var vehicle in vehicles)
        {
            var serviceState = ServiceState(vehicle, today);
            if (serviceState is "DUE_SOON" or "OVERDUE")
            {
                var key = $"{serviceState}:{vehicle.NextServiceKm}:{vehicle.NextServiceDate:O}";
                if (vehicle.ServiceRemindedFor != key)
                {
                    vehicle.ServiceRemindedFor = key;
                    var managers = await notifications.UsersWithPermissionAsync(Ctx.RequireCompanyId(), "fleet:update");
                    await notifications.NotifyAsync(new NotifyRequest(managers, vehicle.CompanyId, "fleet.service",
                        $"Bakım hatırlatması: {vehicle.Plate}", $"{vehicle.Brand} {vehicle.Model} · {serviceState}", $"/fleet/vehicles/{vehicle.Id}", true));
                }
            }
            var docs = await db.VehicleDocuments.Where(d => d.VehicleId == vehicle.Id).ToListAsync();
            foreach (var doc in docs.GroupBy(d => d.Type).Select(g => g.OrderByDescending(d => d.EndDate).First()))
            {
                var state = doc.EndDate < today ? "EXPIRED" : doc.EndDate <= today.AddDays(WarnDays) ? "EXPIRING" : "VALID";
                if (state == "VALID" || doc.RemindedState == state) continue;
                doc.RemindedState = state;
                var managers = await notifications.UsersWithPermissionAsync(Ctx.RequireCompanyId(), "fleet:update");
                await notifications.NotifyAsync(new NotifyRequest(managers, vehicle.CompanyId, "fleet.document",
                    $"Belge hatırlatması: {vehicle.Plate}", $"{doc.Type} · {state} · {doc.EndDate:dd.MM.yyyy}", $"/fleet/vehicles/{vehicle.Id}", true));
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task RecalcFuelAsync(Guid vehicleId)
    {
        var entries = await db.VehicleFuelEntries.Where(e => e.VehicleId == vehicleId).OrderBy(e => e.Odometer).ThenBy(e => e.EntryDate).ToListAsync();
        VehicleFuelEntry? previousFull = null;
        foreach (var entry in entries)
        {
            if (previousFull != null && entry.Odometer > previousFull.Odometer)
            {
                entry.Distance = entry.Odometer - previousFull.Odometer;
                if (entry.FullTank && entry.Distance > 0)
                    entry.Consumption = Math.Round(entry.Liters * 100m / entry.Distance.Value, 2, MidpointRounding.AwayFromZero);
                else entry.Consumption = null;
            }
            else { entry.Distance = null; entry.Consumption = null; }
            if (entry.FullTank) previousFull = entry;
        }
    }

    private async Task RecordOdometerAsync(Vehicle vehicle, DateOnly date, int odometer, VehicleOdometerSource source, Guid? fuelId, Guid? serviceId)
    {
        if (odometer > vehicle.Odometer) vehicle.Odometer = odometer;
        db.VehicleOdometerReadings.Add(new VehicleOdometerReading
        {
            VehicleId = vehicle.Id, ReadingDate = date, Odometer = odometer, Source = source, FuelEntryId = fuelId, ServiceId = serviceId, CreatedById = Ctx.UserId,
        });
    }

    private async Task AssertOdometerAsync(Guid vehicleId, DateOnly date, int odometer)
    {
        var readings = await db.VehicleOdometerReadings.AsNoTracking().Where(r => r.VehicleId == vehicleId).Select(r => new { r.ReadingDate, r.Odometer }).ToListAsync();
        var min = readings.Where(r => r.ReadingDate <= date).Select(r => r.Odometer).DefaultIfEmpty(0).Max();
        var later = readings.Where(r => r.ReadingDate > date).Select(r => (int?)r.Odometer).DefaultIfEmpty(null).Min();
        Guard.That(odometer >= min, $"Kilometre en az {min} olmalı");
        if (later != null) Guard.That(odometer <= later, $"Kilometre en fazla {later} olmalı");
    }

    private static string ServiceState(Vehicle vehicle, DateOnly today)
    {
        var overdueKm = vehicle.NextServiceKm != null && vehicle.Odometer >= vehicle.NextServiceKm;
        var overdueDate = vehicle.NextServiceDate != null && today >= vehicle.NextServiceDate;
        if (overdueKm || overdueDate) return "OVERDUE";
        var soonKm = vehicle.NextServiceKm != null && vehicle.Odometer >= vehicle.NextServiceKm - WarnKm;
        var soonDate = vehicle.NextServiceDate != null && today >= vehicle.NextServiceDate.Value.AddDays(-WarnDays);
        return soonKm || soonDate ? "DUE_SOON" : "OK";
    }

    private async Task<Vehicle> ActiveAsync(Guid id)
    {
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id) ?? throw new NotFoundAppException("Araç bulunamadı");
        Guard.State(vehicle.Status != VehicleStatus.RETIRED, "Hurdaya ayrılmış araç güncellenemez");
        return vehicle;
    }

    private async Task<bool> IsDriverAsync(Guid vehicleId)
    {
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        return employeeId != null && await db.VehicleAssignments.AnyAsync(a => a.VehicleId == vehicleId && a.EmployeeId == employeeId && a.EndDate == null);
    }

    private static string? Normalize(string value)
    {
        var compact = value.ToUpperInvariant().Replace(" ", "").Replace("-", "");
        var match = Plate.Match(compact);
        return match.Success ? $"{match.Groups[1].Value} {match.Groups[2].Value} {match.Groups[3].Value}" : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class VehiclesHook() : ManagedRecordHook("vehicles", "Araçlar filo ekranından yönetilir");
public sealed class VehicleAssignmentsHook() : ManagedRecordHook("vehicle-assignments", "Zimmetler araç ekranından yazılır");
public sealed class VehicleFuelHook() : ManagedRecordHook("vehicle-fuel-entries", "Yakıt kayıtları araç ekranından yazılır");
