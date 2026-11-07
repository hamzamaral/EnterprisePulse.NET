using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Recruitment;

public sealed record SavePosting(string Title, Guid? DepartmentId, Guid? PositionId, Guid? BranchId, EmploymentType EmploymentType, string? Location, string? Description, int Openings, Guid? HiringManagerId, decimal? SalaryMin, decimal? SalaryMax, DateOnly? ClosesAt);
public sealed record SaveCandidate(string FirstName, string LastName, string Email, string? Phone, string? City, CandidateSource Source, string? CurrentTitle, decimal? ExpectedSalary);
public sealed record MoveApplication(ApplicationStage Stage, string? RejectReason);
public sealed record ScheduleInterview(InterviewType Type, DateTime ScheduledAt, int DurationMinutes, string? Location, List<Guid> InterviewerIds);
public sealed record InterviewFeedback(int Rating, InterviewRecommendation Recommendation, string? Feedback);
public sealed record SaveOffer(decimal GrossSalary, DateOnly StartDate, DateOnly? ExpiresAt, string? Notes);
public sealed record PostingDto(Guid Id, string Number, string Title, JobPostingStatus Status, int Openings, int Hired, Guid? DepartmentId, Guid? PositionId, Guid? HiringManagerId);
public sealed record ApplicationDto(Guid Id, Guid PostingId, string PostingTitle, Guid CandidateId, string CandidateName, ApplicationStage Stage, int? Rating, Guid? EmployeeId, string? OfferStatus, decimal? OfferSalary, bool CanHire);

/// <summary>İlan, aday ve başvuru. İşe alım kabul edilmiş tekliften personel ve ücret kaydı açar.</summary>
[ModuleService]
public sealed class RecruitmentService(AppDbContext db, NumberingService numbering)
{
    private CurrentUser Ctx => db.User;

    public async Task<PostingDto> CreatePostingAsync(SavePosting dto)
    {
        Guard.That(Ctx.Has("recruitment:create"), "İlan oluşturma yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Title), "İlan başlığı gerekli");
        Guard.That(dto.Openings > 0, "Kontenjan sıfırdan büyük olmalı");
        Guard.That(dto.SalaryMin is null || dto.SalaryMax is null || dto.SalaryMax >= dto.SalaryMin, "Ücret aralığı geçersiz");
        var posting = new JobPosting
        {
            Number = await numbering.NextAsync("JOB_POSTING"), Title = dto.Title.Trim(), DepartmentId = dto.DepartmentId, PositionId = dto.PositionId,
            BranchId = dto.BranchId, EmploymentType = dto.EmploymentType, Location = Blank(dto.Location), Description = Blank(dto.Description),
            Openings = dto.Openings, HiringManagerId = dto.HiringManagerId, SalaryMin = dto.SalaryMin, SalaryMax = dto.SalaryMax, ClosesAt = dto.ClosesAt,
            CreatedById = Ctx.RequireUserId(),
        };
        db.JobPostings.Add(posting);
        await db.SaveChangesAsync();
        return await PostingAsync(posting.Id);
    }

    public async Task<PostingDto> PublishAsync(Guid id)
    {
        Guard.That(Ctx.Has("recruitment:update"), "İlan güncelleme yetkiniz yok");
        var posting = await FindPostingAsync(id);
        Guard.State(posting.Status is JobPostingStatus.DRAFT or JobPostingStatus.ON_HOLD, "İlan yayına alınamaz");
        posting.Status = JobPostingStatus.OPEN;
        posting.PublishedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await PostingAsync(id);
    }

    public async Task<Candidate> CreateCandidateAsync(SaveCandidate dto)
    {
        Guard.That(Ctx.Has("recruitment:create"), "Aday oluşturma yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(dto.FirstName) && !string.IsNullOrWhiteSpace(dto.LastName) && !string.IsNullOrWhiteSpace(dto.Email), "Ad, soyad ve e-posta gerekli");
        Guard.That(!await db.Candidates.AnyAsync(c => c.Email == dto.Email.Trim()), "Bu e-posta ile aday zaten var");
        var candidate = new Candidate
        {
            FirstName = dto.FirstName.Trim(), LastName = dto.LastName.Trim(), Email = dto.Email.Trim(), Phone = Blank(dto.Phone), City = Blank(dto.City),
            Source = dto.Source, CurrentTitle = Blank(dto.CurrentTitle), ExpectedSalary = dto.ExpectedSalary, CreatedById = Ctx.RequireUserId(),
        };
        db.Candidates.Add(candidate);
        await db.SaveChangesAsync();
        return candidate;
    }

    public async Task<ApplicationDto> ApplyAsync(Guid postingId, Guid candidateId)
    {
        Guard.That(Ctx.Has("recruitment:create"), "Başvuru oluşturma yetkiniz yok");
        var posting = await FindPostingAsync(postingId);
        Guard.State(posting.Status == JobPostingStatus.OPEN, "Yalnızca yayındaki ilana başvurulur");
        Guard.That(await db.Candidates.AnyAsync(c => c.Id == candidateId), "Aday bulunamadı");
        Guard.That(!await db.JobApplications.AnyAsync(a => a.PostingId == postingId && a.CandidateId == candidateId), "Aday bu ilana zaten başvurmuş");
        var sort = (await db.JobApplications.Where(a => a.PostingId == postingId && a.Stage == ApplicationStage.APPLIED).MaxAsync(a => (int?)a.SortOrder) ?? 0) + 1;
        var application = new JobApplication { PostingId = postingId, CandidateId = candidateId, SortOrder = sort, CreatedById = Ctx.RequireUserId() };
        db.JobApplications.Add(application);
        await db.SaveChangesAsync();
        await LogAsync(application, ApplicationActivityType.STAGE, null, ApplicationStage.APPLIED, "Başvuru alındı");
        return await ApplicationAsync(application.Id);
    }

    public async Task<ApplicationDto> MoveAsync(Guid id, ApplicationStage stage, string? reason)
    {
        Guard.That(Ctx.Has("recruitment:update"), "Başvuru güncelleme yetkiniz yok");
        var application = await FindApplicationAsync(id);
        Guard.State(application.Stage is not (ApplicationStage.HIRED or ApplicationStage.REJECTED or ApplicationStage.WITHDRAWN), "Kapanmış başvuru taşınamaz");
        Guard.That(stage != ApplicationStage.HIRED, "İşe alım teklif kabulünden yapılır");
        if (stage is ApplicationStage.REJECTED or ApplicationStage.WITHDRAWN) Guard.That(!string.IsNullOrWhiteSpace(reason), "Gerekçe gerekli");
        var from = application.Stage;
        application.Stage = stage;
        application.StageChangedAt = DateTime.UtcNow;
        application.RejectReason = stage == ApplicationStage.REJECTED ? reason!.Trim() : null;
        await LogAsync(application, ApplicationActivityType.STAGE, from, stage, reason);
        await db.SaveChangesAsync();
        return await ApplicationAsync(id);
    }

    public async Task<ApplicationDto> ScheduleInterviewAsync(Guid applicationId, ScheduleInterview dto)
    {
        Guard.That(Ctx.Has("recruitment:update"), "Mülakat planlama yetkiniz yok");
        Guard.That(dto.InterviewerIds is { Count: > 0 }, "En az bir görüşmeci gerekli");
        Guard.That(dto.DurationMinutes > 0, "Süre sıfırdan büyük olmalı");
        var application = await FindApplicationAsync(applicationId);
        Guard.State(application.Stage is ApplicationStage.SCREENING or ApplicationStage.INTERVIEW, "Mülakat bu aşamada planlanamaz");
        foreach (var employeeId in dto.InterviewerIds.Distinct()) Guard.That(await db.Employees.AnyAsync(e => e.Id == employeeId), "Görüşmeci bulunamadı");
        var interview = new Interview
        {
            ApplicationId = application.Id, Type = dto.Type, ScheduledAt = dto.ScheduledAt, DurationMinutes = dto.DurationMinutes,
            Location = Blank(dto.Location), CreatedById = Ctx.RequireUserId(),
        };
        foreach (var employeeId in dto.InterviewerIds.Distinct()) interview.Participants.Add(new InterviewParticipant { EmployeeId = employeeId });
        db.Interviews.Add(interview);
        if (application.Stage == ApplicationStage.SCREENING)
        {
            application.Stage = ApplicationStage.INTERVIEW;
            application.StageChangedAt = DateTime.UtcNow;
            await LogAsync(application, ApplicationActivityType.STAGE, ApplicationStage.SCREENING, ApplicationStage.INTERVIEW, null);
        }
        await LogAsync(application, ApplicationActivityType.INTERVIEW, application.Stage, application.Stage, dto.Type.ToString());
        await db.SaveChangesAsync();
        return await ApplicationAsync(application.Id);
    }

    public async Task SubmitFeedbackAsync(Guid interviewId, InterviewFeedback dto)
    {
        Guard.That(dto.Rating is >= 1 and <= 5, "Puan 1 ile 5 arasında olmalı");
        var interview = await db.Interviews.Include(i => i.Participants).FirstOrDefaultAsync(i => i.Id == interviewId) ?? throw new NotFoundAppException("Mülakat bulunamadı");
        Guard.State(interview.Status == InterviewStatus.SCHEDULED, "Mülakat kapalı");
        var me = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        var participant = interview.Participants.FirstOrDefault(p => p.EmployeeId == me);
        if (participant == null && Ctx.Has("recruitment:update") && interview.Participants.Count == 1) participant = interview.Participants[0];
        Guard.That(participant != null, "Bu mülakatın görüşmecisi değilsiniz");
        participant!.Rating = dto.Rating;
        participant.Recommendation = dto.Recommendation;
        participant.Feedback = Blank(dto.Feedback);
        participant.SubmittedAt = DateTime.UtcNow;
        if (interview.Participants.All(p => p.SubmittedAt != null)) interview.Status = InterviewStatus.COMPLETED;
        await db.SaveChangesAsync();
    }

    public async Task<ApplicationDto> OfferAsync(Guid applicationId, SaveOffer dto)
    {
        Guard.That(Ctx.Has("recruitment:update"), "Teklif yetkiniz yok");
        Guard.That(dto.GrossSalary > 0, "Teklif ücreti sıfırdan büyük olmalı");
        var application = await FindApplicationAsync(applicationId);
        Guard.State(application.Stage is ApplicationStage.INTERVIEW or ApplicationStage.OFFER, "Teklif bu aşamada verilemez");
        Guard.That(!await db.JobOffers.AnyAsync(o => o.ApplicationId == applicationId && o.Status == JobOfferStatus.PENDING), "Bekleyen bir teklif zaten var");
        db.JobOffers.Add(new JobOffer
        {
            ApplicationId = application.Id, GrossSalary = dto.GrossSalary, StartDate = dto.StartDate, ExpiresAt = dto.ExpiresAt,
            Notes = Blank(dto.Notes), CreatedById = Ctx.RequireUserId(),
        });
        await LogAsync(application, ApplicationActivityType.OFFER, application.Stage, application.Stage, dto.GrossSalary.ToString("N2"));
        await db.SaveChangesAsync();
        return await ApplicationAsync(application.Id);
    }

    public async Task<ApplicationDto> RespondOfferAsync(Guid offerId, bool accept)
    {
        Guard.That(Ctx.Has("recruitment:update"), "Teklif yanıtlama yetkiniz yok");
        var offer = await db.JobOffers.FirstOrDefaultAsync(o => o.Id == offerId) ?? throw new NotFoundAppException("Teklif bulunamadı");
        Guard.State(offer.Status == JobOfferStatus.PENDING, "Teklif zaten yanıtlanmış");
        var application = await FindApplicationAsync(offer.ApplicationId);
        offer.Status = accept ? JobOfferStatus.ACCEPTED : JobOfferStatus.DECLINED;
        offer.RespondedAt = DateTime.UtcNow;
        if (accept && application.Stage != ApplicationStage.OFFER)
        {
            var from = application.Stage;
            application.Stage = ApplicationStage.OFFER;
            application.StageChangedAt = DateTime.UtcNow;
            await LogAsync(application, ApplicationActivityType.STAGE, from, ApplicationStage.OFFER, "Teklif kabul edildi");
        }
        await db.SaveChangesAsync();
        return await ApplicationAsync(application.Id);
    }

    public async Task<ApplicationDto> HireAsync(Guid applicationId)
    {
        Guard.That(Ctx.Has("recruitment:hire"), "İşe alım yetkiniz yok");
        var application = await db.JobApplications.Include(a => a.Posting).Include(a => a.Candidate).FirstOrDefaultAsync(a => a.Id == applicationId)
            ?? throw new NotFoundAppException("Başvuru bulunamadı");
        Guard.State(application.Stage == ApplicationStage.OFFER && application.EmployeeId == null, "İşe alım için kabul edilmiş teklif gerekli");
        var offer = await db.JobOffers.Where(o => o.ApplicationId == applicationId && o.Status == JobOfferStatus.ACCEPTED).OrderByDescending(o => o.RespondedAt).FirstOrDefaultAsync()
            ?? throw new ConflictAppException("Kabul edilmiş teklif yok");
        var candidate = application.Candidate;
        var posting = application.Posting;
        var employee = new Employee
        {
            EmployeeNo = await numbering.NextAsync("EMPLOYEE"), FirstName = candidate.FirstName, LastName = candidate.LastName, Email = candidate.Email,
            Phone = candidate.Phone, HireDate = offer.StartDate, EmploymentType = posting.EmploymentType, DepartmentId = posting.DepartmentId,
            PositionId = posting.PositionId, BranchId = posting.BranchId, ManagerId = posting.HiringManagerId,
        };
        db.Employees.Add(employee);
        db.SalaryRecords.Add(new SalaryRecord
        {
            EmployeeId = employee.Id, EffectiveFrom = offer.StartDate, GrossSalary = offer.GrossSalary, Note = "İşe alım teklifi", CreatedById = Ctx.RequireUserId(),
        });
        await db.SaveChangesAsync();
        application.EmployeeId = employee.Id;
        application.Stage = ApplicationStage.HIRED;
        application.HiredAt = DateTime.UtcNow;
        application.StageChangedAt = DateTime.UtcNow;
        await LogAsync(application, ApplicationActivityType.HIRED, ApplicationStage.OFFER, ApplicationStage.HIRED, employee.EmployeeNo);
        var hired = await db.JobApplications.CountAsync(a => a.PostingId == posting.Id && a.Stage == ApplicationStage.HIRED);
        if (hired >= posting.Openings) { posting.Status = JobPostingStatus.CLOSED; posting.ClosedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
        return await ApplicationAsync(application.Id);
    }

    public async Task<InterviewList> MyInterviewsAsync()
    {
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (employeeId == null) return new InterviewList([]);
        var rows = await db.InterviewParticipants.AsNoTracking().Include(p => p.Interview).ThenInclude(i => i.Application).ThenInclude(a => a.Candidate)
            .Where(p => p.EmployeeId == employeeId && p.Interview.Status != InterviewStatus.CANCELLED).OrderBy(p => p.Interview.ScheduledAt).ToListAsync();
        return new InterviewList(rows.Select(p => new InterviewDto(p.Interview.Id, p.Interview.ApplicationId, $"{p.Interview.Application.Candidate.FirstName} {p.Interview.Application.Candidate.LastName}",
            p.Interview.Type, p.Interview.Status, p.Interview.ScheduledAt, p.SubmittedAt == null)).ToList());
    }

    public async Task<ApplicationDto> ApplicationAsync(Guid id)
    {
        Guard.That(Ctx.Has("recruitment:read"), "Başvuru görme yetkiniz yok");
        var application = await db.JobApplications.AsNoTracking().Include(a => a.Posting).Include(a => a.Candidate).Include(a => a.Offers).FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new NotFoundAppException("Başvuru bulunamadı");
        var offer = application.Offers.OrderByDescending(o => o.CreatedAt).FirstOrDefault();
        var accepted = application.Offers.Any(o => o.Status == JobOfferStatus.ACCEPTED);
        return new ApplicationDto(application.Id, application.PostingId, application.Posting.Title, application.CandidateId,
            $"{application.Candidate.FirstName} {application.Candidate.LastName}", application.Stage, application.Rating, application.EmployeeId,
            offer?.Status.ToString(), offer?.GrossSalary, application.Stage == ApplicationStage.OFFER && accepted && application.EmployeeId == null);
    }

    private async Task<PostingDto> PostingAsync(Guid id)
    {
        var posting = await db.JobPostings.AsNoTracking().FirstAsync(p => p.Id == id);
        var hired = await db.JobApplications.CountAsync(a => a.PostingId == id && a.Stage == ApplicationStage.HIRED);
        return new PostingDto(posting.Id, posting.Number, posting.Title, posting.Status, posting.Openings, hired, posting.DepartmentId, posting.PositionId, posting.HiringManagerId);
    }

    private async Task LogAsync(JobApplication application, ApplicationActivityType type, ApplicationStage? from, ApplicationStage? to, string? body)
    {
        db.ApplicationActivities.Add(new ApplicationActivity { ApplicationId = application.Id, Type = type, FromStage = from, ToStage = to, Body = Blank(body), CreatedById = Ctx.RequireUserId() });
        await db.SaveChangesAsync();
    }

    private async Task<JobPosting> FindPostingAsync(Guid id) => await db.JobPostings.FirstOrDefaultAsync(p => p.Id == id) ?? throw new NotFoundAppException("İlan bulunamadı");
    private async Task<JobApplication> FindApplicationAsync(Guid id) => await db.JobApplications.FirstOrDefaultAsync(a => a.Id == id) ?? throw new NotFoundAppException("Başvuru bulunamadı");
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record InterviewDto(Guid Id, Guid ApplicationId, string CandidateName, InterviewType Type, InterviewStatus Status, DateTime ScheduledAt, bool NeedsFeedback);
public sealed record InterviewList(List<InterviewDto> Items);

public sealed class JobPostingsHook() : ManagedRecordHook("job-postings", "İlanlar işe alım ekranından yönetilir");
public sealed class JobApplicationsHook() : ManagedRecordHook("job-applications", "Başvurular işe alım ekranından yönetilir");
public sealed class JobOffersHook() : ManagedRecordHook("job-offers", "Teklifler başvuru ekranından verilir");
public sealed class InterviewsHook() : ManagedRecordHook("interviews", "Mülakatlar başvuru ekranından planlanır");
