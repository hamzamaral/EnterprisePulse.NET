using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Training;

public sealed record SaveSession(Guid CourseId, DateTime StartsAt, DateTime EndsAt, string? Location, string? Instructor, int? Capacity, decimal? Cost, bool SelfEnrollment);
public sealed record AttendanceLine(Guid EmployeeId, TrainingEnrollmentStatus Status, int? Score);
public sealed record EnrollmentDto(Guid Id, Guid SessionId, string CourseTitle, DateTime StartsAt, TrainingEnrollmentStatus Status, int? Score, DateOnly? CertificateExpiresAt);
public sealed record ComplianceGap(Guid EmployeeId, string EmployeeName, Guid CourseId, string CourseTitle);
public sealed record EnrollmentList(List<EnrollmentDto> Items);
public sealed record ComplianceList(List<ComplianceGap> Items);

/// <summary>Eğitim oturumu ve yoklama. Zorunlu eğitimde geçerli sertifikası olmayan personel açıkta kalır.</summary>
[ModuleService]
public sealed class TrainingService(AppDbContext db)
{
    private CurrentUser Ctx => db.User;

    public async Task<TrainingSession> CreateSessionAsync(SaveSession dto)
    {
        Guard.That(Ctx.Has("training:create"), "Oturum oluşturma yetkiniz yok");
        Guard.That(dto.EndsAt > dto.StartsAt, "Bitiş başlangıçtan sonra olmalı");
        Guard.That(dto.Capacity is null or > 0, "Kontenjan sıfırdan büyük olmalı");
        var course = await db.TrainingCourses.FirstOrDefaultAsync(c => c.Id == dto.CourseId && c.IsActive) ?? throw new NotFoundAppException("Eğitim bulunamadı");
        var session = new TrainingSession
        {
            CourseId = course.Id, StartsAt = dto.StartsAt, EndsAt = dto.EndsAt, Location = Blank(dto.Location), Instructor = Blank(dto.Instructor),
            Capacity = dto.Capacity, Cost = dto.Cost, SelfEnrollment = dto.SelfEnrollment, CreatedById = Ctx.RequireUserId(),
        };
        db.TrainingSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    public async Task EnrollAsync(Guid sessionId, Guid? employeeId)
    {
        var session = await db.TrainingSessions.Include(s => s.Enrollments).FirstOrDefaultAsync(s => s.Id == sessionId) ?? throw new NotFoundAppException("Oturum bulunamadı");
        Guard.State(session.Status == TrainingSessionStatus.PLANNED, "Yalnızca planlı oturuma kayıt olunur");
        var target = employeeId;
        if (target == null)
        {
            Guard.That(session.SelfEnrollment, "Bu oturuma kendi kendine kayıt kapalı");
            target = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync() ?? throw new NotFoundAppException("Personel kaydınız yok");
        }
        else Guard.That(Ctx.Has("training:update"), "Başkası adına kayıt yetkiniz yok");
        Guard.That(await db.Employees.AnyAsync(e => e.Id == target && e.Status == EmployeeStatus.ACTIVE), "Personel bulunamadı");
        Guard.That(session.Enrollments.All(e => e.EmployeeId != target), "Personel zaten kayıtlı");
        var taken = session.Enrollments.Count(e => e.Status is TrainingEnrollmentStatus.ENROLLED or TrainingEnrollmentStatus.ATTENDED);
        Guard.That(session.Capacity == null || taken < session.Capacity, "Oturum kontenjanı dolu");
        db.TrainingEnrollments.Add(new TrainingEnrollment { SessionId = session.Id, EmployeeId = target.Value });
        await db.SaveChangesAsync();
    }

    public async Task CompleteAsync(Guid sessionId, List<AttendanceLine> lines)
    {
        Guard.That(Ctx.Has("training:update"), "Yoklama yetkiniz yok");
        var session = await db.TrainingSessions.Include(s => s.Course).Include(s => s.Enrollments).FirstOrDefaultAsync(s => s.Id == sessionId)
            ?? throw new NotFoundAppException("Oturum bulunamadı");
        Guard.State(session.Status == TrainingSessionStatus.PLANNED, "Oturum zaten kapanmış");
        Guard.That(lines is { Count: > 0 }, "Yoklama satırı gerekli");
        foreach (var line in lines)
        {
            Guard.That(line.Status is TrainingEnrollmentStatus.ATTENDED or TrainingEnrollmentStatus.NO_SHOW, "Yoklama sonucu katıldı veya katılmadı olmalı");
            Guard.That(line.Score is null or (>= 0 and <= 100), "Puan 0 ile 100 arasında olmalı");
            var enrollment = session.Enrollments.FirstOrDefault(e => e.EmployeeId == line.EmployeeId) ?? throw new NotFoundAppException("Kayıt bulunamadı");
            enrollment.Status = line.Status;
            enrollment.Score = line.Score;
            if (line.Status == TrainingEnrollmentStatus.ATTENDED && session.Course.ValidityMonths is { } months)
                enrollment.CertificateExpiresAt = DateOnly.FromDateTime(session.EndsAt.Date).AddMonths(months);
        }
        session.Status = TrainingSessionStatus.COMPLETED;
        session.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<EnrollmentList> MineAsync()
    {
        var employeeId = await db.Employees.Where(e => e.UserId == Ctx.UserId).Select(e => (Guid?)e.Id).FirstOrDefaultAsync();
        if (employeeId == null) return new EnrollmentList([]);
        var rows = await db.TrainingEnrollments.AsNoTracking().Include(e => e.Session).ThenInclude(s => s.Course).Where(e => e.EmployeeId == employeeId).ToListAsync();
        return new EnrollmentList(rows.Select(e => new EnrollmentDto(e.Id, e.SessionId, e.Session.Course.Title, e.Session.StartsAt, e.Status, e.Score, e.CertificateExpiresAt)).ToList());
    }

    public async Task<ComplianceList> ComplianceAsync()
    {
        Guard.That(Ctx.Has("training:read"), "Eğitim görme yetkiniz yok");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var courses = await db.TrainingCourses.AsNoTracking().Where(c => c.IsMandatory && c.IsActive).ToListAsync();
        var employees = await db.Employees.AsNoTracking().Where(e => e.Status == EmployeeStatus.ACTIVE).ToListAsync();
        var attended = await db.TrainingEnrollments.AsNoTracking().Include(e => e.Session)
            .Where(e => e.Status == TrainingEnrollmentStatus.ATTENDED).ToListAsync();
        var gaps = new List<ComplianceGap>();
        foreach (var employee in employees)
        {
            foreach (var course in courses)
            {
                var covered = attended.Any(e => e.EmployeeId == employee.Id && e.Session.CourseId == course.Id && (e.CertificateExpiresAt == null || e.CertificateExpiresAt >= today));
                if (!covered) gaps.Add(new ComplianceGap(employee.Id, $"{employee.FirstName} {employee.LastName}", course.Id, course.Title));
            }
        }
        return new ComplianceList(gaps);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class TrainingSessionsHook() : ManagedRecordHook("training-sessions", "Oturumlar eğitim ekranından yönetilir");
public sealed class TrainingEnrollmentsHook() : ManagedRecordHook("training-enrollments", "Katılımlar oturum ekranından yazılır");
