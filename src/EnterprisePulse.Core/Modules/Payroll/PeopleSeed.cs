using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Performance;
using EnterprisePulse.Core.Modules.Recruitment;
using EnterprisePulse.Core.Modules.Training;
using EnterprisePulse.Core.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Payroll;

/// <summary>October payroll for two salaries, one hired candidate, one completed review and a mandatory course.</summary>
public sealed class PeopleSeed(AppDbContext db, PayrollService payroll, RecruitmentService recruitment, PerformanceService performance, TrainingService training) : ISeedStep
{
    public int Order => 60;

    public async Task RunAsync(SeedContext ctx)
    {
        using var _ = db.User.ActAs(ctx.PulseId, ctx.AdminId, superAdmin: true);
        var deniz = await db.Employees.FirstOrDefaultAsync(e => e.FirstName == "Deniz" && e.LastName == "Aksoy");
        var kerem = await db.Employees.FirstOrDefaultAsync(e => e.FirstName == "Kerem" && e.LastName == "Polat");
        if (deniz == null || kerem == null) return;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await PayrollAsync(deniz, kerem, today);
        await RecruitmentAsync(deniz, today);
        await PerformanceAsync(kerem, today);
        await TrainingAsync(kerem, today);
    }

    private async Task PayrollAsync(Employee deniz, Employee kerem, DateOnly today)
    {
        if (await db.PayrollPeriods.AnyAsync(p => p.Year == today.Year && p.Month == 10)) return;
        await payroll.SaveParameterAsync(new SavePayrollParameter(today.Year, 26005.50m, 195041.40m, 0.14m, 0.01m, 0.1675m, 0.02m, 0.00759m, 1.5m, 225,
            [new TaxBracket(158000, 0.15m), new TaxBracket(null, 0.20m)]));
        await payroll.SaveSalaryAsync(new SaveSalary(deniz.Id, new DateOnly(today.Year, 10, 1), 90000, "Yıllık ücret"));
        await payroll.SaveSalaryAsync(new SaveSalary(kerem.Id, new DateOnly(today.Year, 10, 1), 55000, "Yıllık ücret"));
        var period = await payroll.OpenAsync(today.Year, 10);
        await payroll.ApproveAsync(period.Id);
        var bank = await db.TreasuryAccounts.FirstOrDefaultAsync(a => a.Code == "BANK-TRY");
        if (bank != null) await payroll.PayAsync(period.Id, new PayPayroll(bank.Id, today.AddDays(-1)));
    }

    private async Task RecruitmentAsync(Employee deniz, DateOnly today)
    {
        if (await db.JobPostings.AnyAsync(p => p.Title == "Kıdemli yazılım geliştirici")) return;
        var department = await db.Departments.FirstOrDefaultAsync(d => d.Code == "BT");
        var position = await db.Positions.FirstOrDefaultAsync(p => p.Code == "YAZ");
        var posting = await recruitment.CreatePostingAsync(new SavePosting("Kıdemli yazılım geliştirici", department?.Id, position?.Id, null, EmploymentType.FULL_TIME,
            "İstanbul", "Mağaza ve depo yazılımı.", 1, deniz.Id, 70000, 90000, today.AddDays(30)));
        await recruitment.PublishAsync(posting.Id);
        var candidate = await recruitment.CreateCandidateAsync(new SaveCandidate("Ece", "Yılmaz", "ece.yilmaz@example", "0532 000 00 00", "İstanbul", CandidateSource.LINKEDIN, "Yazılım geliştirici", 75000));
        var application = await recruitment.ApplyAsync(posting.Id, candidate.Id);
        await recruitment.MoveAsync(application.Id, ApplicationStage.SCREENING, null);
        await recruitment.ScheduleInterviewAsync(application.Id, new ScheduleInterview(InterviewType.TECHNICAL, DateTime.UtcNow.AddDays(1), 60, "Levent", [deniz.Id]));
        var interviewId = await db.Interviews.Where(i => i.ApplicationId == application.Id).Select(i => i.Id).FirstAsync();
        await recruitment.SubmitFeedbackAsync(interviewId, new InterviewFeedback(4, InterviewRecommendation.YES, "Teknik görüşme olumlu."));
        await recruitment.OfferAsync(application.Id, new SaveOffer(72000, today, today.AddDays(7), null));
        var offerId = await db.JobOffers.Where(o => o.ApplicationId == application.Id).Select(o => o.Id).FirstAsync();
        await recruitment.RespondOfferAsync(offerId, true);
        await recruitment.HireAsync(application.Id);

        if (!await db.JobPostings.AnyAsync(p => p.Title == "Sistem yöneticisi"))
        {
            var open = await recruitment.CreatePostingAsync(new SavePosting("Sistem yöneticisi", department?.Id, null, null, EmploymentType.FULL_TIME, "İstanbul", "Altyapı ve destek.", 1, deniz.Id, 60000, 80000, today.AddDays(45)));
            await recruitment.PublishAsync(open.Id);
        }
    }

    private async Task PerformanceAsync(Employee kerem, DateOnly today)
    {
        if (await db.ReviewCycles.AnyAsync(c => c.Name == "2026 Değerlendirme")) return;
        var communication = await CompetencyAsync("İletişim");
        var technical = await CompetencyAsync("Teknik yetkinlik");
        var cycle = await performance.CreateCycleAsync(new SaveCycle("2026 Değerlendirme", new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31),
            today.AddDays(14), today.AddDays(28), 60, [communication, technical]));
        await performance.LaunchAsync(cycle.Id);
        var reviewId = await db.PerformanceReviews.Where(r => r.CycleId == cycle.Id && r.EmployeeId == kerem.Id).Select(r => r.Id).FirstAsync();
        await performance.AddGoalAsync(reviewId, new SaveGoal("Mağaza kurulumunu zamanında teslim etmek", 60));
        await performance.AddGoalAsync(reviewId, new SaveGoal("Saha hatalarını azaltmak", 40));
        var goals = await db.PerformanceGoals.Where(g => g.ReviewId == reviewId).OrderBy(g => g.SortOrder).Select(g => g.Id).ToListAsync();
        var ratings = await db.CompetencyRatings.Where(r => r.ReviewId == reviewId).Select(r => r.Id).ToListAsync();
        await performance.SubmitSelfAsync(reviewId, new SelfReview("Kurulum sahada ilerledi.", goals.Select(id => new RatingInput(id, 4)).ToList(), ratings.Select(id => new RatingInput(id, 4)).ToList()));
        await performance.SubmitManagerAsync(reviewId, new ManagerReview("Hedefler tuttu.", "Saha disiplini", "Dokümantasyon", goals.Select(id => new RatingInput(id, id == goals[0] ? 5 : 4)).ToList(), ratings.Select(id => new RatingInput(id, 4)).ToList()));
    }

    private async Task<Guid> CompetencyAsync(string name)
    {
        var row = await db.Competencies.FirstOrDefaultAsync(c => c.Name == name);
        if (row != null) return row.Id;
        row = new Competency { Name = name, SortOrder = name == "İletişim" ? 1 : 2 };
        db.Competencies.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task TrainingAsync(Employee kerem, DateOnly today)
    {
        if (await db.TrainingCourses.AnyAsync(c => c.Code == "ISG")) return;
        var course = new TrainingCourse
        {
            Code = "ISG", Title = "İş güvenliği", Category = TrainingCategory.OCCUPATIONAL_SAFETY, Format = TrainingFormat.CLASSROOM,
            DurationHours = 8, IsMandatory = true, ValidityMonths = 12,
        };
        db.TrainingCourses.Add(course);
        await db.SaveChangesAsync();
        var done = await training.CreateSessionAsync(new SaveSession(course.Id, DateTime.UtcNow.AddDays(-20), DateTime.UtcNow.AddDays(-20).AddHours(8), "Levent", "Deniz Aksoy", 12, 0, false));
        await training.EnrollAsync(done.Id, kerem.Id);
        await training.CompleteAsync(done.Id, [new AttendanceLine(kerem.Id, TrainingEnrollmentStatus.ATTENDED, 90)]);
        var planned = await training.CreateSessionAsync(new SaveSession(course.Id, DateTime.UtcNow.AddDays(7), DateTime.UtcNow.AddDays(7).AddHours(8), "Levent", "Deniz Aksoy", 12, 0, true));
        await training.EnrollAsync(planned.Id, kerem.Id);
    }
}
