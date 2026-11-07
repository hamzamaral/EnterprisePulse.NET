using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Expenses;
using EnterprisePulse.Core.Modules.Projects;
using EnterprisePulse.Core.Modules.Workflow;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Crm;

/// <summary>
/// A converted lead with a proposal, an open negotiation, an overdue meeting,
/// an active customer project and one paid per-diem plus one pending mileage report.
/// </summary>
public sealed class WorkSeed(
    AppDbContext db, LeadService leads, OpportunityService opportunities, ActivityService activities,
    ProjectService projects, ExpenseReportService expenses, WorkflowService workflow) : ISeedStep
{
    public int Order => 50;

    public async Task RunAsync(SeedContext ctx)
    {
        using var _ = db.User.ActAs(ctx.PulseId, ctx.AdminId, superAdmin: true);
        var categories = await CategoriesAsync();
        var deniz = await db.Employees.FirstOrDefaultAsync(e => e.FirstName == "Deniz" && e.LastName == "Aksoy");
        var kerem = await db.Employees.FirstOrDefaultAsync(e => e.FirstName == "Kerem" && e.LastName == "Polat");
        var customer = await db.BusinessPartners.FirstOrDefaultAsync(p => p.Code == "MUS-001");
        var baskent = await db.BusinessPartners.FirstOrDefaultAsync(p => p.Code == "MUS-002");
        if (deniz == null || kerem == null || customer == null || baskent == null) return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (!await db.Leads.AnyAsync(l => l.CompanyName == "Ege Lojistik"))
        {
            var lead = await leads.CreateAsync(new SaveLead("Ege Lojistik", "Selin Acar", "selin.acar@ege-lojistik.example", "0232 000 00 00", "İzmir",
                LeadSource.REFERRAL, 120000, "TRY", "Depo yazılımı için yönlendirildi.", ctx.AdminId));
            await leads.ConvertAsync(lead.Id, new ConvertLead(true, "Depo yazılımı", 120000));
            var proposal = await db.Opportunities.Where(o => o.LeadId == lead.Id).Select(o => o.Id).FirstAsync();
            await opportunities.ChangeStageAsync(proposal, OpportunityStage.PROPOSAL, null);
            await opportunities.CreateAsync(new SaveOpportunity("Şube yenileme", baskent.Id, null, 85000, "TRY", today.AddDays(21), ctx.AdminId, "İkinci şube donanımı."));
            var negotiation = await db.Opportunities.Where(o => o.PartnerId == baskent.Id && o.Title == "Şube yenileme").Select(o => o.Id).FirstAsync();
            await opportunities.ChangeStageAsync(negotiation, OpportunityStage.NEGOTIATION, null);
            await activities.CreateAsync(new SaveActivity(CrmActivityType.MEETING, "Şube keşfi", "Yerinde keşif toplantısı.", DateTime.UtcNow.AddDays(-1), ctx.AdminId, null, negotiation, baskent.Id));
        }
        if (!await db.Leads.AnyAsync(l => l.CompanyName == "Marmara Tekstil"))
        {
            await leads.CreateAsync(new SaveLead("Marmara Tekstil", "Can Demir", "can.demir@marmara-tekstil.example", null, "Bursa",
                LeadSource.WEBSITE, 45000, "TRY", "Web formundan geldi, henüz aranmadı.", ctx.AdminId));
        }

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Name == "Boğaziçi mağaza kurulumu");
        if (project == null)
        {
            var created = await projects.CreateAsync(new SaveProject("Boğaziçi mağaza kurulumu", "Monitör ve kurulum işinin saha takibi.", customer.Id, deniz.Id,
                today.AddDays(-10), today.AddDays(20), 100000, 600));
            await projects.ChangeStatusAsync(created.Id, ProjectStatus.ACTIVE);
            await projects.AddMemberAsync(created.Id, new AddMember(kerem.Id, ProjectMemberRole.MEMBER, 450));
            await projects.AddTaskAsync(created.Id, new SaveTask("Monitör montajı", "Mağaza katındaki monitörler.", ProjectTaskPriority.HIGH, kerem.Id, today.AddDays(3), 8));
            var taskId = await db.ProjectTasks.Where(t => t.ProjectId == created.Id).Select(t => t.Id).FirstAsync();
            await projects.MoveTaskAsync(taskId, ProjectTaskStatus.IN_PROGRESS);
            await projects.LogTimeAsync(created.Id, new LogTime(taskId, kerem.Id, today.AddDays(-1), 4, "Montaj ve kablo."));
            project = await db.Projects.FirstAsync(p => p.Id == created.Id);
        }

        if (await db.ExpenseReports.AnyAsync()) return;
        var cash = await db.TreasuryAccounts.FirstOrDefaultAsync(a => a.Code == "KASA-MRK");
        if (cash == null) return;
        var perDiem = await expenses.CreateAsync(new SaveExpenseReport(kerem.Id, project.Id, "İzmir kurulum harcırahı", "İki günlük saha.",
            [new ExpenseLineInput(categories["HARCI"], today.AddDays(-2), "Saha harcırahı", null, 2, null, null, "TRY", null, null)]));
        var submitted = await expenses.SubmitAsync(perDiem.Id);
        if (submitted.WorkflowInstanceId is { } instanceId) await workflow.ApproveAsync(instanceId, "Uygun");
        await expenses.PayAsync(perDiem.Id, new PayExpense(cash.Id, today.AddDays(-1)));
        var mileage = await expenses.CreateAsync(new SaveExpenseReport(kerem.Id, project.Id, "Saha kilometresi", null,
            [new ExpenseLineInput(categories["KM"], today.AddDays(-1), "İzmir gidiş-dönüş", null, 40, null, null, "TRY", null, null)]));
        await expenses.SubmitAsync(mileage.Id);
    }

    private async Task<Dictionary<string, Guid>> CategoriesAsync()
    {
        await EnsureAsync("YEMEK", "Yemek fişi", ExpenseCategoryKind.RECEIPT, null, 400, true);
        await EnsureAsync("HARCI", "Harcırah", ExpenseCategoryKind.PER_DIEM, 750, null, false);
        await EnsureAsync("KM", "Kilometre", ExpenseCategoryKind.MILEAGE, 6.50m, null, false);
        return await db.ExpenseCategories.Where(c => c.Code == "YEMEK" || c.Code == "HARCI" || c.Code == "KM").ToDictionaryAsync(c => c.Code, c => c.Id);
    }

    private async Task EnsureAsync(string code, string name, ExpenseCategoryKind kind, decimal? rate, decimal? limit, bool receipt)
    {
        var row = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Code == code);
        if (row != null) return;
        db.ExpenseCategories.Add(new ExpenseCategory { Code = code, Name = name, Kind = kind, Rate = rate, LimitAmount = limit, RequiresReceipt = receipt });
        await db.SaveChangesAsync();
    }
}
