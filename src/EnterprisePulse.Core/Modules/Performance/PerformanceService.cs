using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Performance;

public sealed record SaveCycle(string Name, DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly SelfReviewDue, DateOnly ManagerReviewDue, int GoalWeight, List<Guid> CompetencyIds);
public sealed record SaveGoal(string Title, int Weight);
public sealed record SelfReview(string? Comment, List<RatingInput> Goals, List<RatingInput> Competencies);
public sealed record ManagerReview(string? Comment, string? Strengths, string? Improvements, List<RatingInput> Goals, List<RatingInput> Competencies);
public sealed record RatingInput(Guid Id, int Rating);
public sealed record ReviewDto(Guid Id, Guid CycleId, string CycleName, Guid EmployeeId, string EmployeeName, Guid? ReviewerId, string? ReviewerName, PerformanceReviewStatus Status, decimal? GoalScore, decimal? CompetencyScore, decimal? OverallScore, int GoalWeight);
public sealed record ReviewList(List<ReviewDto> Items);

/// <summary>Değerlendirme dönemi. Puan hedefin ağırlığı ile yetkinlik ortalamasının karışımıdır.</summary>
[ModuleService]
public sealed class PerformanceService(AppDbContext db)
{
    private CurrentUser Ctx => db.User;

    public async Task<ReviewCycle> CreateCycleAsync(SaveCycle dto)
    {
        Guard.That(Ctx.Has("performance:create"), "Dönem oluşturma yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Name), "Dönem adı gerekli");
        Guard.That(dto.GoalWeight is >= 0 and <= 100, "Hedef ağırlığı 0 ile 100 arasında olmalı");
        Guard.That(dto.CompetencyIds is { Count: > 0 }, "En az bir yetkinlik seçilmeli");
        foreach (var id in dto.CompetencyIds.Distinct()) Guard.That(await db.Competencies.AnyAsync(c => c.Id == id && c.IsActive), "Yetkinlik bulunamadı");
        var cycle = new ReviewCycle
        {
            Name = dto.Name.Trim(), PeriodStart = dto.PeriodStart, PeriodEnd = dto.PeriodEnd, SelfReviewDue = dto.SelfReviewDue,
            ManagerReviewDue = dto.ManagerReviewDue, GoalWeight = dto.GoalWeight, CreatedById = Ctx.RequireUserId(),
        };
        foreach (var id in dto.CompetencyIds.Distinct()) cycle.Competencies.Add(new ReviewCycleCompetency { CompetencyId = id });
        db.ReviewCycles.Add(cycle);
        await db.SaveChangesAsync();
        return cycle;
    }

    public async Task LaunchAsync(Guid id)
    {
        Guard.That(Ctx.Has("performance:update"), "Dönem başlatma yetkiniz yok");
        var cycle = await db.ReviewCycles.Include(c => c.Competencies).FirstOrDefaultAsync(c => c.Id == id) ?? throw new NotFoundAppException("Değerlendirme dönemi bulunamadı");
        Guard.State(cycle.Status == ReviewCycleStatus.DRAFT, "Dönem zaten başlamış");
        Guard.That(cycle.Competencies.Count > 0, "Yetkinlik seçilmeden dönem başlamaz");
        var employees = await db.Employees.Where(e => e.Status == EmployeeStatus.ACTIVE).ToListAsync();
        Guard.That(employees.Count > 0, "Aktif personel yok");
        foreach (var employee in employees)
        {
            var review = new PerformanceReview { CycleId = cycle.Id, EmployeeId = employee.Id, ReviewerId = employee.ManagerId };
            foreach (var competency in cycle.Competencies) review.Ratings.Add(new CompetencyRating { CompetencyId = competency.CompetencyId });
            db.PerformanceReviews.Add(review);
        }
        cycle.Status = ReviewCycleStatus.ACTIVE;
        cycle.LaunchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<ReviewDto> AddGoalAsync(Guid reviewId, SaveGoal dto)
    {
        var review = await EditableAsync(reviewId, self: true);
        Guard.State(review.Status == PerformanceReviewStatus.SELF_REVIEW, "Hedef yalnızca öz değerlendirmede eklenir");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Title), "Hedef başlığı gerekli");
        Guard.That(dto.Weight is > 0 and <= 100, "Ağırlık 1 ile 100 arasında olmalı");
        var sort = (await db.PerformanceGoals.Where(g => g.ReviewId == reviewId).MaxAsync(g => (int?)g.SortOrder) ?? 0) + 1;
        db.PerformanceGoals.Add(new PerformanceGoal { ReviewId = reviewId, Title = dto.Title.Trim(), Weight = dto.Weight, SortOrder = sort });
        await db.SaveChangesAsync();
        return await GetAsync(reviewId);
    }

    public async Task<ReviewDto> SubmitSelfAsync(Guid reviewId, SelfReview dto)
    {
        var review = await db.PerformanceReviews.Include(r => r.Goals).Include(r => r.Ratings).FirstOrDefaultAsync(r => r.Id == reviewId) ?? throw new NotFoundAppException("Değerlendirme bulunamadı");
        await AssertPartyAsync(review, self: true);
        Guard.State(review.Status == PerformanceReviewStatus.SELF_REVIEW, "Öz değerlendirme kapalı");
        ApplyRatings(review.Goals, dto.Goals, self: true);
        ApplyCompetencies(review.Ratings, dto.Competencies, self: true);
        Guard.That(review.Goals.Sum(g => g.Weight) == 100, "Hedef ağırlıkları 100 olmalı");
        Guard.That(review.Goals.All(g => g.SelfRating is >= 1 and <= 5), "Her hedefin öz puanı 1 ile 5 arasında olmalı");
        Guard.That(review.Ratings.All(r => r.SelfRating is >= 1 and <= 5), "Her yetkinliğin öz puanı gerekli");
        review.SelfComment = Blank(dto.Comment);
        review.SelfSubmittedAt = DateTime.UtcNow;
        review.Status = PerformanceReviewStatus.MANAGER_REVIEW;
        await db.SaveChangesAsync();
        return await GetAsync(reviewId);
    }

    public async Task<ReviewDto> SubmitManagerAsync(Guid reviewId, ManagerReview dto)
    {
        var review = await db.PerformanceReviews.Include(r => r.Goals).Include(r => r.Ratings).Include(r => r.Cycle).FirstOrDefaultAsync(r => r.Id == reviewId)
            ?? throw new NotFoundAppException("Değerlendirme bulunamadı");
        await AssertPartyAsync(review, self: false);
        Guard.State(review.Status == PerformanceReviewStatus.MANAGER_REVIEW, "Yönetici değerlendirmesi kapalı");
        ApplyRatings(review.Goals, dto.Goals, self: false);
        ApplyCompetencies(review.Ratings, dto.Competencies, self: false);
        Guard.That(review.Goals.All(g => g.ManagerRating is >= 1 and <= 5), "Her hedefin yönetici puanı gerekli");
        Guard.That(review.Ratings.All(r => r.ManagerRating is >= 1 and <= 5), "Her yetkinliğin yönetici puanı gerekli");
        review.GoalScore = Math.Round(review.Goals.Sum(g => g.Weight * g.ManagerRating!.Value) / 100m, 2, MidpointRounding.AwayFromZero);
        review.CompetencyScore = Math.Round(review.Ratings.Average(r => (decimal)r.ManagerRating!.Value), 2, MidpointRounding.AwayFromZero);
        var weight = review.Cycle.GoalWeight;
        review.OverallScore = Math.Round((review.GoalScore.Value * weight + review.CompetencyScore.Value * (100 - weight)) / 100m, 2, MidpointRounding.AwayFromZero);
        review.ManagerComment = Blank(dto.Comment);
        review.Strengths = Blank(dto.Strengths);
        review.Improvements = Blank(dto.Improvements);
        review.ManagerSubmittedAt = DateTime.UtcNow;
        review.Status = PerformanceReviewStatus.COMPLETED;
        await db.SaveChangesAsync();
        return await GetAsync(reviewId);
    }

    public async Task<ReviewList> MineAsync()
    {
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        var query = db.PerformanceReviews.AsNoTracking().Include(r => r.Cycle).Include(r => r.Employee).Include(r => r.Reviewer).AsQueryable();
        if (!Ctx.Has("performance:read")) query = query.Where(r => r.EmployeeId == employeeId || r.ReviewerId == employeeId);
        return new ReviewList((await query.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync()).Select(Map).ToList());
    }

    public async Task<ReviewDto> GetAsync(Guid id)
    {
        var review = await db.PerformanceReviews.AsNoTracking().Include(r => r.Cycle).Include(r => r.Employee).Include(r => r.Reviewer).FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new NotFoundAppException("Değerlendirme bulunamadı");
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (!Ctx.Has("performance:read") && review.EmployeeId != employeeId && review.ReviewerId != employeeId) throw new NotFoundAppException("Değerlendirme bulunamadı");
        return Map(review);
    }

    private async Task<PerformanceReview> EditableAsync(Guid id, bool self)
    {
        var review = await db.PerformanceReviews.FirstOrDefaultAsync(r => r.Id == id) ?? throw new NotFoundAppException("Değerlendirme bulunamadı");
        await AssertPartyAsync(review, self);
        return review;
    }

    private async Task AssertPartyAsync(PerformanceReview review, bool self)
    {
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        var allowed = self ? review.EmployeeId == employeeId || Ctx.Has("performance:update") : review.ReviewerId == employeeId || Ctx.Has("performance:update");
        Guard.That(allowed, self ? "Öz değerlendirme yetkiniz yok" : "Yönetici değerlendirmesi yetkiniz yok");
    }

    private static void ApplyRatings(IEnumerable<PerformanceGoal> goals, List<RatingInput> inputs, bool self)
    {
        foreach (var input in inputs)
        {
            Guard.That(input.Rating is >= 1 and <= 5, "Puan 1 ile 5 arasında olmalı");
            var goal = goals.FirstOrDefault(g => g.Id == input.Id) ?? throw new NotFoundAppException("Hedef bulunamadı");
            if (self) goal.SelfRating = input.Rating; else goal.ManagerRating = input.Rating;
        }
    }

    private static void ApplyCompetencies(IEnumerable<CompetencyRating> ratings, List<RatingInput> inputs, bool self)
    {
        foreach (var input in inputs)
        {
            Guard.That(input.Rating is >= 1 and <= 5, "Puan 1 ile 5 arasında olmalı");
            var rating = ratings.FirstOrDefault(r => r.Id == input.Id) ?? throw new NotFoundAppException("Yetkinlik puanı bulunamadı");
            if (self) rating.SelfRating = input.Rating; else rating.ManagerRating = input.Rating;
        }
    }

    private static ReviewDto Map(PerformanceReview r) => new(r.Id, r.CycleId, r.Cycle.Name, r.EmployeeId, $"{r.Employee.FirstName} {r.Employee.LastName}",
        r.ReviewerId, r.Reviewer == null ? null : $"{r.Reviewer.FirstName} {r.Reviewer.LastName}", r.Status, r.GoalScore, r.CompetencyScore, r.OverallScore, r.Cycle.GoalWeight);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ReviewCyclesHook() : ManagedRecordHook("review-cycles", "Değerlendirme dönemleri performans ekranından yönetilir");
public sealed class PerformanceReviewsHook() : ManagedRecordHook("performance-reviews", "Değerlendirmeler dönem başlayınca oluşur");
