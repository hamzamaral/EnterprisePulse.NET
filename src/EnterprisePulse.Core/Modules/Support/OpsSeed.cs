using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Budgets;
using EnterprisePulse.Core.Modules.Contracts;
using EnterprisePulse.Core.Modules.Fleet;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Support;

/// <summary>Open support ticket, expiring service contract, approved company budget and one assigned vehicle.</summary>
public sealed class OpsSeed(AppDbContext db, SupportService support, ContractService contracts, BudgetService budgets, FleetService fleet) : ISeedStep
{
    public int Order => 70;

    public async Task RunAsync(SeedContext ctx)
    {
        using var _ = db.User.ActAs(ctx.PulseId, ctx.AdminId, superAdmin: true);
        var kerem = await db.Employees.FirstOrDefaultAsync(e => e.FirstName == "Kerem" && e.LastName == "Polat");
        var keremUser = kerem?.UserId;
        var customer = await db.BusinessPartners.FirstOrDefaultAsync(p => p.Code == "MUS-001");
        if (kerem == null || keremUser == null || customer == null) return;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await SupportAsync(ctx.PulseId, ctx.AdminId, keremUser.Value);
        await ContractsAsync(customer.Id, kerem.Id, ctx.AdminId, today);
        await BudgetsAsync(today.Year, ctx.AdminId);
        await FleetAsync(kerem.Id, today);
    }

    private async Task SupportAsync(Guid pulseId, Guid adminId, Guid keremUserId)
    {
        await support.EnsureDefaultsAsync();
        var category = await db.TicketCategories.FirstOrDefaultAsync(c => c.Name == "BT Destek");
        if (category == null)
        {
            category = new TicketCategory { Name = "BT Destek", DefaultAssigneeId = adminId, SortOrder = 1 };
            db.TicketCategories.Add(category);
            await db.SaveChangesAsync();
        }
        if (await db.Tickets.AnyAsync()) return;
        using (db.User.ActAs(pulseId, keremUserId))
            await support.CreateAsync(new SaveTicket("Monitör montajında ses yok", "Kurulum sonrası hoparlör çıkışı gelmiyor.", category.Id, TicketPriority.HIGH, null));
        var ticket = await db.Tickets.OrderByDescending(t => t.CreatedAt).FirstAsync();
        await support.ChangeStatusAsync(ticket.Id, new ChangeTicketStatus(TicketStatus.IN_PROGRESS, "İncelemeye alındı"));
    }

    private async Task ContractsAsync(Guid partnerId, Guid keremId, Guid adminId, DateOnly today)
    {
        if (await db.ContractTypes.AnyAsync()) return;
        var service = new ContractType { Name = "Hizmet", Party = ContractParty.PARTNER, Flow = ContractFlow.INCOME, DefaultTermMonths = 12, DefaultNoticeDays = 30, SortOrder = 1 };
        var employment = new ContractType { Name = "İş sözleşmesi", Party = ContractParty.EMPLOYEE, Flow = ContractFlow.NONE, DefaultTermMonths = 12, DefaultNoticeDays = 30, SortOrder = 2 };
        db.ContractTypes.AddRange(service, employment);
        await db.SaveChangesAsync();

        var active = await contracts.CreateAsync(new SaveContract("Boğaziçi bakım hizmeti", service.Id, partnerId, null, adminId, today.AddMonths(-10), today.AddDays(20), today.AddMonths(-10), 15000, "TRY", ContractBillingCycle.MONTHLY, true, 12, 30, 30, "Yıllık bakım"));
        await contracts.ActivateAsync(active.Id, null);

        var job = await contracts.CreateAsync(new SaveContract("Kerem Polat iş sözleşmesi", employment.Id, null, keremId, adminId, today.AddYears(-1), today.AddMonths(2), today.AddYears(-1), null, "TRY", ContractBillingCycle.ONE_TIME, false, null, 30, 30, null));
        await contracts.ActivateAsync(job.Id, null);
        await contracts.RunCompanyAsync();
    }

    private async Task BudgetsAsync(int year, Guid adminId)
    {
        if (await db.Budgets.AnyAsync(b => b.Year == year)) return;
        var payroll = await budgets.SaveItemAsync(new SaveBudgetItem("PERSONEL", "Personel maliyeti", BudgetItemKind.EXPENSE, BudgetSource.PAYROLL, null));
        var sales = await budgets.SaveItemAsync(new SaveBudgetItem("SATIS", "Satış geliri", BudgetItemKind.INCOME, BudgetSource.SALES, null));
        var expense = await budgets.SaveItemAsync(new SaveBudgetItem("MASRAF", "Masraflar", BudgetItemKind.EXPENSE, BudgetSource.EXPENSES, null));
        var other = await budgets.SaveItemAsync(new SaveBudgetItem("DIGER", "Diğer gider", BudgetItemKind.EXPENSE, BudgetSource.MANUAL, null));
        var budget = await budgets.CreateAsync(new SaveBudget(year, null, $"{year} şirket bütçesi", adminId, null));
        var lines = new List<PlanLine>();
        for (var month = 1; month <= 12; month++)
        {
            lines.Add(new PlanLine(payroll.Id, month, 200000));
            lines.Add(new PlanLine(sales.Id, month, 350000));
            lines.Add(new PlanLine(expense.Id, month, 25000));
            lines.Add(new PlanLine(other.Id, month, 10000));
        }
        await budgets.SavePlanAsync(budget.Id, new SavePlan(lines));
        await budgets.ApproveAsync(budget.Id);
        await budgets.AddEntryAsync(new SaveEntry(other.Id, null, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5), 8500, "Ofis kirası"));
    }

    private async Task FleetAsync(Guid keremId, DateOnly today)
    {
        if (await db.Vehicles.AnyAsync()) return;
        var vehicle = await fleet.CreateAsync(new SaveVehicle("34 EP 001", "Toyota", "Corolla", 2022, "Beyaz", VehicleFuelType.HYBRID, VehicleOwnership.OWNED, null, null, 15000, 12, null));
        await fleet.AssignAsync(vehicle.Id, new AssignVehicle(keremId, today.AddDays(-40), 42000, "Saha aracı"));
        await fleet.AddFuelAsync(vehicle.Id, new SaveFuel(today.AddDays(-30), 42350, 40, 1800, true, "Shell Levent", null));
        await fleet.AddFuelAsync(vehicle.Id, new SaveFuel(today.AddDays(-5), 42800, 38, 1710, true, "Opet Maslak", null));
        await fleet.AddServiceAsync(vehicle.Id, new SaveService(VehicleServiceType.PERIODIC, today.AddDays(-60), 41000, null, "Periyodik bakım", 4500));
        await fleet.AddDocumentAsync(vehicle.Id, new SaveDocument(VehicleDocumentType.INSPECTION, today.AddMonths(-6), today.AddDays(25), "TÜVTÜRK", null, 2500, null));
        await fleet.AddDocumentAsync(vehicle.Id, new SaveDocument(VehicleDocumentType.TRAFFIC_INSURANCE, today.AddMonths(-3), today.AddMonths(9), "Anadolu Sigorta", "POL-7781", 12000, null));
    }
}
