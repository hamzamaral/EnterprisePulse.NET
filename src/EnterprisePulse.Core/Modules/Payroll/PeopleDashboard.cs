using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Payroll;

public sealed class PayrollDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 70;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("payroll:read")) return null;
        var period = await db.PayrollPeriods.AsNoTracking().OrderByDescending(p => p.Year).ThenByDescending(p => p.Month).FirstOrDefaultAsync();
        var net = period?.NetTotal ?? 0;
        var label = period == null ? "Dönem yok" : $"{period.Year}-{period.Month:00} {period.Status}";
        return new DashboardSection("payroll", "Bordro",
        [
            new("last-payroll", "Son bordro net", net.ToString("N2"), "Payments", label, "/r/payroll-periods"),
        ]);
    }
}

public sealed class RecruitmentDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 75;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("recruitment:read")) return null;
        var open = await db.JobPostings.CountAsync(p => p.Status == JobPostingStatus.OPEN);
        var pipeline = await db.JobApplications.CountAsync(a => a.Stage != ApplicationStage.HIRED && a.Stage != ApplicationStage.REJECTED && a.Stage != ApplicationStage.WITHDRAWN);
        return new DashboardSection("recruitment", "İşe Alım",
        [
            new("open-postings", "Açık ilan", open.ToString(), "Work", Link: "/r/job-postings"),
            new("open-applications", "Süren başvuru", pipeline.ToString(), "PersonSearch", Link: "/r/job-applications"),
        ]);
    }
}

public sealed class PerformanceDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 80;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("performance:read")) return null;
        var open = await db.PerformanceReviews.CountAsync(r => r.Status != PerformanceReviewStatus.COMPLETED);
        var done = await db.PerformanceReviews.CountAsync(r => r.Status == PerformanceReviewStatus.COMPLETED);
        return new DashboardSection("performance", "Performans",
        [
            new("open-reviews", "Açık değerlendirme", open.ToString(), "RateReview", Link: "/performance/my"),
            new("done-reviews", "Tamamlanan", done.ToString(), "TaskAlt", Link: "/r/review-cycles"),
        ]);
    }
}

public sealed class TrainingDashboard(AppDbContext db) : IDashboardProvider
{
    public int Order => 85;

    public async Task<DashboardSection?> SectionAsync()
    {
        if (!db.User.Has("training:read")) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var courses = await db.TrainingCourses.AsNoTracking().Where(c => c.IsMandatory && c.IsActive).Select(c => c.Id).ToListAsync();
        var employees = await db.Employees.CountAsync(e => e.Status == EmployeeStatus.ACTIVE);
        var covered = 0;
        if (courses.Count > 0)
        {
            var attended = await db.TrainingEnrollments.AsNoTracking().Include(e => e.Session)
                .Where(e => e.Status == TrainingEnrollmentStatus.ATTENDED && courses.Contains(e.Session.CourseId) && (e.CertificateExpiresAt == null || e.CertificateExpiresAt >= today))
                .Select(e => new { e.EmployeeId, e.Session.CourseId }).Distinct().CountAsync();
            covered = attended;
        }
        var gaps = Math.Max(0, employees * Math.Max(courses.Count, 0) - covered);
        return new DashboardSection("training", "Eğitim",
        [
            new("training-gaps", "Zorunlu eğitim açığı", gaps.ToString(), "School", Link: "/training/compliance", Color: gaps > 0 ? "warning" : "primary"),
        ]);
    }
}
