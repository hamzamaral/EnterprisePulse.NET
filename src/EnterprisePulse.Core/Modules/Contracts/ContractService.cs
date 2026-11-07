using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Contracts;

public sealed record SaveContract(string Title, Guid TypeId, Guid? PartnerId, Guid? EmployeeId, Guid? OwnerId, DateOnly StartDate, DateOnly? EndDate, DateOnly? SignedDate, decimal? Amount, string? CurrencyCode, ContractBillingCycle BillingCycle, bool AutoRenew, int? RenewalMonths, int? NoticeDays, int? ReminderDays, string? Description);
public sealed record RenewContract(DateOnly EndDate, decimal? Amount, string? Note);
public sealed record TerminateContract(DateOnly TerminatedOn, string? Reason);
public sealed record ContractDto(Guid Id, string Number, string Title, ContractStatus Status, string State, ContractParty Party, Guid TypeId, string TypeName, Guid? PartnerId, string? PartnerTitle, Guid? EmployeeId, string? EmployeeName, Guid OwnerId, string OwnerName, DateOnly StartDate, DateOnly? EndDate, decimal? Amount, string CurrencyCode, bool AutoRenew, int RenewalCount, bool CanActivate, bool CanRenew, bool CanTerminate);

public sealed record ContractRunResult(int Renewed, int Expired, int Terminated, int Reminded);

/// <summary>Sözleşme. Günlük iş otomatik yeniler, sona erdirir ve hatırlatır.</summary>
[ModuleService]
public sealed class ContractService(AppDbContext db, NumberingService numbering, NotificationService notifications) : IScheduledJob
{
    public string Id => "contracts-daily";
    public string Cron => "0 7 * * *";

    private CurrentUser Ctx => db.User;

    public async Task RunAsync()
    {
        using var bypass = Ctx.WithoutTenant();
        foreach (var companyId in await db.Companies.Select(c => c.Id).ToListAsync())
        {
            using var _ = Ctx.ActAs(companyId, superAdmin: true);
            await RunCompanyAsync();
        }
    }

    public async Task<ContractRunResult> RunCompanyAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var renewed = 0; var expired = 0; var terminated = 0; var reminded = 0;
        var contracts = await db.Contracts.Where(c => c.Status == ContractStatus.ACTIVE).ToListAsync();
        foreach (var contract in contracts)
        {
            if (contract.TerminatedOn is { } end && end < today)
            {
                contract.Status = ContractStatus.TERMINATED;
                db.ContractEvents.Add(new ContractEvent { ContractId = contract.Id, Type = ContractEventType.TERMINATED, ToValue = end.ToString("O") });
                terminated++;
                continue;
            }
            if (contract.EndDate is null || contract.EndDate >= today)
            {
                if (ReminderDue(contract, today))
                {
                    contract.RemindedFor = contract.EndDate;
                    db.ContractEvents.Add(new ContractEvent { ContractId = contract.Id, Type = ContractEventType.REMINDER_SENT, ToValue = contract.EndDate?.ToString("O") });
                    await notifications.NotifyAsync(new NotifyRequest([contract.OwnerId], contract.CompanyId, "contracts.reminder",
                        $"Sözleşme hatırlatması: {contract.Number}", $"{contract.Title} · bitiş {contract.EndDate:dd.MM.yyyy}", $"/contracts/{contract.Id}", true));
                    reminded++;
                }
                continue;
            }
            if (contract.AutoRenew && contract.RenewalMonths is > 0)
            {
                var endDate = contract.EndDate.Value;
                var times = 0;
                while (endDate < today) { endDate = TermEnd(endDate.AddDays(1), contract.RenewalMonths.Value); times++; }
                var from = contract.EndDate;
                contract.EndDate = endDate;
                contract.RenewalCount += times;
                contract.RemindedFor = null;
                db.ContractEvents.Add(new ContractEvent { ContractId = contract.Id, Type = ContractEventType.AUTO_RENEWED, FromValue = from?.ToString("O"), ToValue = endDate.ToString("O") });
                renewed += times;
            }
            else
            {
                contract.Status = ContractStatus.EXPIRED;
                db.ContractEvents.Add(new ContractEvent { ContractId = contract.Id, Type = ContractEventType.EXPIRED, ToValue = contract.EndDate?.ToString("O") });
                expired++;
            }
        }
        await db.SaveChangesAsync();
        return new ContractRunResult(renewed, expired, terminated, reminded);
    }

    public async Task<ContractDto> CreateAsync(SaveContract dto)
    {
        Guard.That(Ctx.Has("contracts:create"), "Sözleşme oluşturma yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Title), "Başlık gerekli");
        var type = await db.ContractTypes.FirstOrDefaultAsync(t => t.Id == dto.TypeId && t.IsActive) ?? throw new BadRequestAppException("Sözleşme türü bulunamadı");
        await AssertPartyAsync(type.Party, dto.PartnerId, dto.EmployeeId);
        var autoRenew = dto.AutoRenew;
        var renewalMonths = dto.RenewalMonths ?? (autoRenew ? type.DefaultTermMonths : null);
        AssertTerms(dto.StartDate, dto.EndDate, autoRenew, renewalMonths);
        var company = await db.Companies.Where(c => c.Id == Ctx.CompanyId).Select(c => c.BaseCurrency).FirstAsync();
        var contract = new Contract
        {
            Number = await numbering.NextAsync("CONTRACT"), Title = dto.Title.Trim(), TypeId = type.Id, Party = type.Party,
            PartnerId = type.Party == ContractParty.PARTNER ? dto.PartnerId : null, EmployeeId = type.Party == ContractParty.EMPLOYEE ? dto.EmployeeId : null,
            OwnerId = dto.OwnerId ?? Ctx.RequireUserId(), StartDate = dto.StartDate, EndDate = dto.EndDate, SignedDate = dto.SignedDate,
            Amount = dto.Amount, CurrencyCode = string.IsNullOrWhiteSpace(dto.CurrencyCode) ? company : dto.CurrencyCode!.Trim(),
            BillingCycle = dto.BillingCycle, AutoRenew = autoRenew, RenewalMonths = renewalMonths,
            NoticeDays = dto.NoticeDays ?? type.DefaultNoticeDays, ReminderDays = dto.ReminderDays ?? 30,
            Description = Blank(dto.Description), CreatedById = Ctx.RequireUserId(),
        };
        db.Contracts.Add(contract);
        db.ContractEvents.Add(new ContractEvent { ContractId = contract.Id, ActorId = Ctx.RequireUserId(), Type = ContractEventType.CREATED });
        await db.SaveChangesAsync();
        return await GetAsync(contract.Id);
    }

    public async Task<ContractDto> ActivateAsync(Guid id, DateOnly? signedDate)
    {
        Guard.That(Ctx.Has("contracts:update"), "Sözleşme güncelleme yetkiniz yok");
        var contract = await db.Contracts.FirstOrDefaultAsync(c => c.Id == id) ?? throw new NotFoundAppException("Sözleşme bulunamadı");
        Guard.State(contract.Status == ContractStatus.DRAFT, "Yalnızca taslak sözleşme aktive edilir");
        contract.Status = ContractStatus.ACTIVE;
        contract.SignedDate = signedDate ?? contract.SignedDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        contract.ActivatedAt = DateTime.UtcNow;
        db.ContractEvents.Add(new ContractEvent { ContractId = id, ActorId = Ctx.RequireUserId(), Type = ContractEventType.ACTIVATED, ToValue = contract.SignedDate?.ToString("O") });
        await db.SaveChangesAsync();
        await RunCompanyAsync();
        return await GetAsync(id);
    }

    public async Task<ContractDto> RenewAsync(Guid id, RenewContract dto)
    {
        Guard.That(Ctx.Has("contracts:update"), "Sözleşme güncelleme yetkiniz yok");
        var contract = await db.Contracts.FirstOrDefaultAsync(c => c.Id == id) ?? throw new NotFoundAppException("Sözleşme bulunamadı");
        var renewable = (contract.Status == ContractStatus.ACTIVE && contract.TerminatedOn == null) || contract.Status == ContractStatus.EXPIRED;
        Guard.State(renewable && contract.EndDate != null, "Yalnızca süreli aktif veya bitmiş sözleşme yenilenir");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Guard.That(dto.EndDate > contract.EndDate && dto.EndDate >= today, "Yeni bitiş bugünden ve mevcut bitişten sonra olmalı");
        var from = contract.EndDate;
        contract.EndDate = dto.EndDate;
        if (dto.Amount != null) contract.Amount = dto.Amount;
        contract.Status = ContractStatus.ACTIVE;
        contract.RenewalCount += 1;
        contract.RemindedFor = null;
        db.ContractEvents.Add(new ContractEvent { ContractId = id, ActorId = Ctx.RequireUserId(), Type = ContractEventType.RENEWED, FromValue = from?.ToString("O"), ToValue = dto.EndDate.ToString("O"), Note = Blank(dto.Note) });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<ContractDto> TerminateAsync(Guid id, TerminateContract dto)
    {
        Guard.That(Ctx.Has("contracts:update"), "Sözleşme güncelleme yetkiniz yok");
        var contract = await db.Contracts.FirstOrDefaultAsync(c => c.Id == id) ?? throw new NotFoundAppException("Sözleşme bulunamadı");
        Guard.State(contract.Status == ContractStatus.ACTIVE && contract.TerminatedOn == null, "Yalnızca aktif sözleşme feshedilir");
        contract.TerminatedOn = dto.TerminatedOn;
        contract.TerminationReason = Blank(dto.Reason);
        db.ContractEvents.Add(new ContractEvent { ContractId = id, ActorId = Ctx.RequireUserId(), Type = ContractEventType.TERMINATION_SCHEDULED, ToValue = dto.TerminatedOn.ToString("O"), Note = Blank(dto.Reason) });
        await db.SaveChangesAsync();
        await RunCompanyAsync();
        return await GetAsync(id);
    }

    public async Task<ContractDto> GetAsync(Guid id)
    {
        var contract = await db.Contracts.AsNoTracking().Include(c => c.Type).Include(c => c.Partner).Include(c => c.Employee).Include(c => c.Owner)
            .FirstOrDefaultAsync(c => c.Id == id) ?? throw new NotFoundAppException("Sözleşme bulunamadı");
        Guard.That(Ctx.Has("contracts:read") || contract.OwnerId == Ctx.UserId, "Sözleşme görme yetkiniz yok");
        return Map(contract, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    private ContractDto Map(Contract c, DateOnly today)
    {
        var canUpdate = Ctx.Has("contracts:update");
        return new ContractDto(c.Id, c.Number, c.Title, c.Status, State(c, today), c.Party, c.TypeId, c.Type.Name, c.PartnerId, c.Partner?.Title,
            c.EmployeeId, c.Employee == null ? null : $"{c.Employee.FirstName} {c.Employee.LastName}", c.OwnerId, $"{c.Owner.FirstName} {c.Owner.LastName}",
            c.StartDate, c.EndDate, c.Amount, c.CurrencyCode, c.AutoRenew, c.RenewalCount,
            canUpdate && c.Status == ContractStatus.DRAFT,
            canUpdate && c.EndDate != null && ((c.Status == ContractStatus.ACTIVE && c.TerminatedOn == null) || c.Status == ContractStatus.EXPIRED),
            canUpdate && c.Status == ContractStatus.ACTIVE && c.TerminatedOn == null);
    }

    private static string State(Contract c, DateOnly today)
    {
        if (c.Status != ContractStatus.ACTIVE) return c.Status.ToString();
        if (c.TerminatedOn != null) return "TERMINATING";
        var action = ActionDate(c);
        return action != null && today >= action.Value.AddDays(-c.ReminderDays) ? "EXPIRING" : "ACTIVE";
    }

    private static DateOnly? ActionDate(Contract c)
    {
        if (c.EndDate == null) return null;
        return c.AutoRenew && c.TerminatedOn == null ? c.EndDate.Value.AddDays(-c.NoticeDays) : c.EndDate;
    }

    private static bool ReminderDue(Contract c, DateOnly today)
    {
        if (c.TerminatedOn != null || c.EndDate == null) return false;
        if (c.RemindedFor == c.EndDate) return false;
        var due = ActionDate(c);
        return due != null && today >= due.Value.AddDays(-c.ReminderDays) && today <= c.EndDate;
    }

    private static DateOnly TermEnd(DateOnly start, int months)
    {
        var next = start.AddMonths(months);
        return next.AddDays(-1);
    }

    private static void AssertTerms(DateOnly start, DateOnly? end, bool autoRenew, int? renewalMonths)
    {
        if (end != null) Guard.That(end >= start, "Bitiş başlangıçtan önce olamaz");
        if (autoRenew) Guard.That(end != null && renewalMonths is > 0, "Otomatik yenileme için süre ve ay gerekli");
    }

    private async Task AssertPartyAsync(ContractParty party, Guid? partnerId, Guid? employeeId)
    {
        if (party == ContractParty.PARTNER)
        {
            Guard.That(partnerId != null, "Cari gerekli");
            Guard.That(await db.BusinessPartners.AnyAsync(p => p.Id == partnerId), "Cari bulunamadı");
        }
        else
        {
            Guard.That(employeeId != null, "Personel gerekli");
            Guard.That(await db.Employees.AnyAsync(e => e.Id == employeeId), "Personel bulunamadı");
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ContractsHook() : ManagedRecordHook("contracts", "Sözleşmeler sözleşme ekranından yönetilir");
