using EnterprisePulse.Api.Infrastructure;
using EnterprisePulse.Core.Modules.Performance;
using EnterprisePulse.Core.Modules.Recruitment;
using EnterprisePulse.Core.Modules.Training;

namespace EnterprisePulse.Api.Endpoints;

public static class PeopleEndpoints
{
    public static void MapPeople(this IEndpointRouteBuilder app)
    {
        var payroll = app.MapGroup("/api/payroll").WithTags("Payroll").RequireAuthorization();
        payroll.MapGet("/mine", (EnterprisePulse.Core.Modules.Payroll.PayrollService s) => s.MineAsync());

        var jobs = app.MapGroup("/api/recruitment").WithTags("Recruitment").RequireAuthorization();
        jobs.MapPost("/postings", (SavePosting body, RecruitmentService s) => s.CreatePostingAsync(body)).RequirePermission("recruitment:create");
        jobs.MapPost("/postings/{id:guid}/publish", (Guid id, RecruitmentService s) => s.PublishAsync(id)).RequirePermission("recruitment:update");
        jobs.MapPost("/candidates", (SaveCandidate body, RecruitmentService s) => s.CreateCandidateAsync(body)).RequirePermission("recruitment:create");
        jobs.MapPost("/postings/{id:guid}/applications", (Guid id, ApplyBody body, RecruitmentService s) => s.ApplyAsync(id, body.CandidateId)).RequirePermission("recruitment:create");
        jobs.MapGet("/applications/{id:guid}", (Guid id, RecruitmentService s) => s.ApplicationAsync(id)).RequirePermission("recruitment:read");
        jobs.MapPost("/applications/{id:guid}/stage", (Guid id, MoveApplication body, RecruitmentService s) => s.MoveAsync(id, body.Stage, body.RejectReason)).RequirePermission("recruitment:update");
        jobs.MapPost("/applications/{id:guid}/interviews", (Guid id, ScheduleInterview body, RecruitmentService s) => s.ScheduleInterviewAsync(id, body)).RequirePermission("recruitment:update");
        jobs.MapPost("/interviews/{id:guid}/feedback", (Guid id, InterviewFeedback body, RecruitmentService s) => s.SubmitFeedbackAsync(id, body)).RequirePermission("recruitment:update");
        jobs.MapPost("/applications/{id:guid}/offers", (Guid id, SaveOffer body, RecruitmentService s) => s.OfferAsync(id, body)).RequirePermission("recruitment:update");
        jobs.MapPost("/offers/{id:guid}/accept", (Guid id, RecruitmentService s) => s.RespondOfferAsync(id, true)).RequirePermission("recruitment:update");
        jobs.MapPost("/applications/{id:guid}/hire", (Guid id, RecruitmentService s) => s.HireAsync(id)).RequirePermission("recruitment:hire");
        jobs.MapGet("/interviews/mine", (RecruitmentService s) => s.MyInterviewsAsync());

        var reviews = app.MapGroup("/api/performance").WithTags("Performance").RequireAuthorization();
        reviews.MapPost("/cycles", (SaveCycle body, PerformanceService s) => s.CreateCycleAsync(body)).RequirePermission("performance:create");
        reviews.MapPost("/cycles/{id:guid}/launch", (Guid id, PerformanceService s) => s.LaunchAsync(id)).RequirePermission("performance:update");
        reviews.MapGet("/reviews/{id:guid}", (Guid id, PerformanceService s) => s.GetAsync(id)).RequirePermission("performance:read");
        reviews.MapPost("/reviews/{id:guid}/goals", (Guid id, SaveGoal body, PerformanceService s) => s.AddGoalAsync(id, body));
        reviews.MapPost("/reviews/{id:guid}/self", (Guid id, SelfReview body, PerformanceService s) => s.SubmitSelfAsync(id, body));
        reviews.MapPost("/reviews/{id:guid}/manager", (Guid id, ManagerReview body, PerformanceService s) => s.SubmitManagerAsync(id, body));
        reviews.MapGet("/mine", (PerformanceService s) => s.MineAsync());

        var training = app.MapGroup("/api/training").WithTags("Training").RequireAuthorization();
        training.MapPost("/sessions", (SaveSession body, TrainingService s) => s.CreateSessionAsync(body)).RequirePermission("training:create");
        training.MapPost("/sessions/{id:guid}/enroll", (Guid id, EnrollBody body, TrainingService s) => s.EnrollAsync(id, body.EmployeeId)).RequirePermission("training:update");
        training.MapPost("/sessions/{id:guid}/complete", (Guid id, CompleteBody body, TrainingService s) => s.CompleteAsync(id, body.Lines)).RequirePermission("training:update");
        training.MapGet("/mine", (TrainingService s) => s.MineAsync());
        training.MapGet("/compliance", (TrainingService s) => s.ComplianceAsync()).RequirePermission("training:read");
    }
}

public sealed record ApplyBody(Guid CandidateId);
public sealed record EnrollBody(Guid? EmployeeId);
public sealed record CompleteBody(List<AttendanceLine> Lines);
