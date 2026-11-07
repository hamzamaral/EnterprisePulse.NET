using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Assets;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Support;

public sealed record SaveTicket(string Subject, string Description, Guid? CategoryId, TicketPriority? Priority, Guid? RequesterId);
public sealed record UpdateTicket(string? Subject, Guid? CategoryId, TicketPriority? Priority, Guid? AssigneeId);
public sealed record ChangeTicketStatus(TicketStatus Status, string? Comment);
public sealed record TicketCommentBody(string Body, bool IsInternal);
public sealed record CloseTicket(int? Rating, string? Comment);
public sealed record RateTicket(int Rating, string? Comment);
public sealed record ReopenTicket(string Comment);

public sealed record TicketCommentDto(Guid Id, Guid AuthorId, string AuthorName, string Body, bool IsInternal, DateTime CreatedAt);
public sealed record TicketDto(
    Guid Id, string Number, string Subject, string Description, TicketStatus Status, TicketPriority Priority,
    Guid? CategoryId, string? CategoryName, Guid RequesterId, string RequesterName, Guid? AssigneeId, string? AssigneeName,
    DateTime FirstResponseDueAt, DateTime ResolutionDueAt, DateTime? FirstRespondedAt, DateTime? ResolvedAt, DateTime? ClosedAt,
    string ResponseSla, string ResolutionSla, int? SatisfactionRating, bool CanManage, bool CanComment, bool CanClose, bool CanReopen, bool CanRate,
    List<TicketStatus> NextStatuses, List<TicketCommentDto> Comments);

public sealed record TicketList(List<TicketDto> Items);

/// <summary>Destek talebi. SLA takvimi saat cinsinden; WAITING çözüm saatini dondurur.</summary>
[ModuleService]
public sealed class SupportService(AppDbContext db, NumberingService numbering, NotificationService notifications)
{
    private static readonly Dictionary<TicketPriority, (int First, int Resolve)> DefaultSla = new()
    {
        [TicketPriority.URGENT] = (1, 4),
        [TicketPriority.HIGH] = (4, 24),
        [TicketPriority.MEDIUM] = (8, 72),
        [TicketPriority.LOW] = (24, 120),
    };

    private static readonly Dictionary<TicketStatus, TicketStatus[]> Transitions = new()
    {
        [TicketStatus.OPEN] = [TicketStatus.IN_PROGRESS, TicketStatus.WAITING, TicketStatus.RESOLVED, TicketStatus.CLOSED],
        [TicketStatus.IN_PROGRESS] = [TicketStatus.OPEN, TicketStatus.WAITING, TicketStatus.RESOLVED, TicketStatus.CLOSED],
        [TicketStatus.WAITING] = [TicketStatus.OPEN, TicketStatus.IN_PROGRESS, TicketStatus.RESOLVED, TicketStatus.CLOSED],
        [TicketStatus.RESOLVED] = [TicketStatus.IN_PROGRESS, TicketStatus.CLOSED],
        [TicketStatus.CLOSED] = [],
    };

    private CurrentUser Ctx => db.User;
    private bool IsAgent => Ctx.Has("support:update");
    private bool IsStaff => IsAgent || Ctx.Has("support:read");

    public async Task EnsureDefaultsAsync()
    {
        foreach (var (priority, targets) in DefaultSla)
        {
            if (await db.TicketSlaPolicies.AnyAsync(p => p.Priority == priority)) continue;
            db.TicketSlaPolicies.Add(new TicketSlaPolicy { Priority = priority, FirstResponseHours = targets.First, ResolutionHours = targets.Resolve });
        }
        await db.SaveChangesAsync();
    }

    public async Task<TicketDto> CreateAsync(SaveTicket dto)
    {
        Guard.That(!string.IsNullOrWhiteSpace(dto.Subject) && !string.IsNullOrWhiteSpace(dto.Description), "Konu ve açıklama gerekli");
        var requesterId = dto.RequesterId ?? Ctx.RequireUserId();
        if (requesterId != Ctx.UserId) Guard.That(Ctx.Has("support:create"), "Başkası adına talep açma yetkiniz yok");
        TicketCategory? category = null;
        if (dto.CategoryId != null)
        {
            category = await db.TicketCategories.FirstOrDefaultAsync(c => c.Id == dto.CategoryId && c.IsActive) ?? throw new BadRequestAppException("Kategori bulunamadı");
        }
        var priority = dto.Priority ?? TicketPriority.MEDIUM;
        var now = DateTime.UtcNow;
        var (first, resolve) = await TargetsAsync(priority);
        var ticket = new Ticket
        {
            Number = await numbering.NextAsync("TICKET"), Subject = dto.Subject.Trim(), Description = dto.Description.Trim(),
            CategoryId = category?.Id, Priority = priority, RequesterId = requesterId, AssigneeId = category?.DefaultAssigneeId,
            FirstResponseDueAt = now.AddHours(first), ResolutionDueAt = now.AddHours(resolve), CreatedAt = now,
        };
        db.Tickets.Add(ticket);
        db.TicketEvents.Add(new TicketEvent { TicketId = ticket.Id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.CREATED });
        if (category?.DefaultAssigneeId != null)
            db.TicketEvents.Add(new TicketEvent { TicketId = ticket.Id, Type = TicketEventType.ASSIGNED, ToValue = await UserNameAsync(category.DefaultAssigneeId) });
        await db.SaveChangesAsync();
        var recipients = ticket.AssigneeId != null
            ? [ticket.AssigneeId.Value]
            : await notifications.UsersWithPermissionAsync(Ctx.RequireCompanyId(), "support:update");
        await notifications.NotifyAsync(new NotifyRequest(recipients, ticket.CompanyId, "support.ticket_created", $"Yeni destek talebi: {ticket.Number}", ticket.Subject, $"/support/tickets/{ticket.Id}"));
        return await GetAsync(ticket.Id);
    }

    public async Task<TicketDto> UpdateAsync(Guid id, UpdateTicket dto)
    {
        Guard.That(IsAgent, "Talep güncelleme yetkiniz yok");
        var ticket = await OpenAsync(id);
        if (dto.Subject != null) ticket.Subject = dto.Subject.Trim();
        if (dto.CategoryId != null && dto.CategoryId != ticket.CategoryId)
        {
            Guard.That(await db.TicketCategories.AnyAsync(c => c.Id == dto.CategoryId && c.IsActive), "Kategori bulunamadı");
            db.TicketEvents.Add(new TicketEvent { TicketId = id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.CATEGORY_CHANGED, FromValue = ticket.CategoryId?.ToString(), ToValue = dto.CategoryId.ToString() });
            ticket.CategoryId = dto.CategoryId;
        }
        if (dto.Priority != null && dto.Priority != ticket.Priority)
        {
            var (first, resolve) = await TargetsAsync(dto.Priority.Value);
            ticket.FirstResponseDueAt = ticket.CreatedAt.AddHours(first);
            ticket.ResolutionDueAt = ticket.CreatedAt.AddHours(resolve).AddMinutes(ticket.PausedMinutes);
            db.TicketEvents.Add(new TicketEvent { TicketId = id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.PRIORITY_CHANGED, FromValue = ticket.Priority.ToString(), ToValue = dto.Priority.ToString() });
            ticket.Priority = dto.Priority.Value;
        }
        if (dto.AssigneeId != null && dto.AssigneeId != ticket.AssigneeId)
        {
            db.TicketEvents.Add(new TicketEvent { TicketId = id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.ASSIGNED, FromValue = await UserNameAsync(ticket.AssigneeId), ToValue = await UserNameAsync(dto.AssigneeId) });
            ticket.AssigneeId = dto.AssigneeId;
            if (dto.AssigneeId != Ctx.UserId)
                await notifications.NotifyAsync(new NotifyRequest([dto.AssigneeId.Value], ticket.CompanyId, "support.ticket_assigned", $"Size destek talebi atandı: {ticket.Number}", ticket.Subject, $"/support/tickets/{ticket.Id}"));
        }
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<TicketDto> ChangeStatusAsync(Guid id, ChangeTicketStatus dto)
    {
        Guard.That(IsAgent, "Durum değiştirme yetkiniz yok");
        var ticket = await OpenAsync(id);
        Guard.State(Transitions[ticket.Status].Contains(dto.Status), $"{ticket.Status} talebi {dto.Status} olamaz");
        var now = DateTime.UtcNow;
        ApplyStatus(ticket, dto.Status, now, byAgent: true);
        db.TicketEvents.Add(new TicketEvent { TicketId = id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.STATUS_CHANGED, FromValue = ticket.Status.ToString(), ToValue = dto.Status.ToString() });
        if (dto.Status == TicketStatus.IN_PROGRESS && ticket.AssigneeId == null) ticket.AssigneeId = Ctx.RequireUserId();
        if (!string.IsNullOrWhiteSpace(dto.Comment))
        {
            db.TicketComments.Add(new TicketComment { TicketId = id, AuthorId = Ctx.RequireUserId(), Body = dto.Comment.Trim() });
            ticket.FirstRespondedAt ??= now;
        }
        ticket.Status = dto.Status;
        await db.SaveChangesAsync();
        if (ticket.RequesterId != Ctx.UserId)
            await notifications.NotifyAsync(new NotifyRequest([ticket.RequesterId], ticket.CompanyId, "support.ticket_status", $"{ticket.Number} durumu güncellendi", dto.Comment?.Trim() ?? ticket.Subject, $"/support/tickets/{ticket.Id}"));
        return await GetAsync(id);
    }

    public async Task<TicketDto> CommentAsync(Guid id, TicketCommentBody dto)
    {
        Guard.That(!string.IsNullOrWhiteSpace(dto.Body), "Yanıt boş olamaz");
        if (dto.IsInternal) Guard.That(IsAgent, "İç not yalnızca destek ekibine açık");
        var ticket = await OpenAsync(id);
        var isRequester = ticket.RequesterId == Ctx.UserId;
        Guard.That(IsAgent || isRequester, "Talebi görme yetkiniz yok");
        if (!IsAgent) Guard.State(ticket.Status is TicketStatus.OPEN or TicketStatus.IN_PROGRESS or TicketStatus.WAITING, "Yanıt için talep yeniden açılmalı");
        var now = DateTime.UtcNow;
        db.TicketComments.Add(new TicketComment { TicketId = id, AuthorId = Ctx.RequireUserId(), Body = dto.Body.Trim(), IsInternal = dto.IsInternal });
        if (!dto.IsInternal && !isRequester) ticket.FirstRespondedAt ??= now;
        if (isRequester && ticket.Status == TicketStatus.WAITING)
        {
            var next = ticket.AssigneeId == null ? TicketStatus.OPEN : TicketStatus.IN_PROGRESS;
            ApplyStatus(ticket, next, now, byAgent: false);
            db.TicketEvents.Add(new TicketEvent { TicketId = id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.STATUS_CHANGED, FromValue = ticket.Status.ToString(), ToValue = next.ToString() });
            ticket.Status = next;
        }
        await db.SaveChangesAsync();
        if (!dto.IsInternal)
        {
            var recipients = isRequester
                ? (ticket.AssigneeId != null ? [ticket.AssigneeId.Value] : await notifications.UsersWithPermissionAsync(Ctx.RequireCompanyId(), "support:update"))
                : new List<Guid> { ticket.RequesterId };
            await notifications.NotifyAsync(new NotifyRequest(recipients, ticket.CompanyId, "support.ticket_comment", $"{ticket.Number} talebine yanıt geldi", dto.Body.Trim(), $"/support/tickets/{ticket.Id}"));
        }
        return await GetAsync(id);
    }

    public async Task<TicketDto> CloseAsync(Guid id, CloseTicket dto)
    {
        var ticket = await RequesterAsync(id);
        Guard.State(ticket.Status != TicketStatus.CLOSED, "Talep zaten kapalı");
        var now = DateTime.UtcNow;
        var from = ticket.Status;
        ApplyStatus(ticket, TicketStatus.CLOSED, now, byAgent: false);
        ticket.Status = TicketStatus.CLOSED;
        if (dto.Rating is >= 1 and <= 5) { ticket.SatisfactionRating = dto.Rating; ticket.SatisfactionComment = Blank(dto.Comment); }
        db.TicketEvents.Add(new TicketEvent { TicketId = id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.STATUS_CHANGED, FromValue = from.ToString(), ToValue = "CLOSED" });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<TicketDto> RateAsync(Guid id, RateTicket dto)
    {
        Guard.That(dto.Rating is >= 1 and <= 5, "Puan 1 ile 5 arasında olmalı");
        var ticket = await RequesterAsync(id);
        Guard.State(ticket.Status == TicketStatus.CLOSED, "Yalnızca kapalı talep puanlanır");
        Guard.State(ticket.SatisfactionRating == null, "Talep zaten puanlanmış");
        ticket.SatisfactionRating = dto.Rating;
        ticket.SatisfactionComment = Blank(dto.Comment);
        db.TicketEvents.Add(new TicketEvent { TicketId = id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.RATED, ToValue = dto.Rating.ToString() });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<TicketDto> ReopenAsync(Guid id, ReopenTicket dto)
    {
        Guard.That(!string.IsNullOrWhiteSpace(dto.Comment), "Gerekçe gerekli");
        var ticket = await RequesterAsync(id);
        Guard.State(ticket.Status == TicketStatus.RESOLVED, "Yalnızca çözülen talep yeniden açılır");
        var now = DateTime.UtcNow;
        var next = ticket.AssigneeId == null ? TicketStatus.OPEN : TicketStatus.IN_PROGRESS;
        db.TicketComments.Add(new TicketComment { TicketId = id, AuthorId = Ctx.RequireUserId(), Body = dto.Comment.Trim() });
        ApplyStatus(ticket, next, now, byAgent: false);
        db.TicketEvents.Add(new TicketEvent { TicketId = id, ActorId = Ctx.RequireUserId(), Type = TicketEventType.REOPENED, FromValue = ticket.Status.ToString(), ToValue = next.ToString() });
        ticket.Status = next;
        ticket.ResolvedAt = null;
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<TicketList> MineAsync()
    {
        var rows = await db.Tickets.AsNoTracking().Include(t => t.Category).Include(t => t.Requester).Include(t => t.Assignee).Include(t => t.Comments).ThenInclude(c => c.Author)
            .Where(t => t.RequesterId == Ctx.UserId).OrderByDescending(t => t.CreatedAt).ToListAsync();
        return new TicketList(rows.Select(t => Map(t, DateTime.UtcNow)).ToList());
    }

    public async Task<TicketDto> GetAsync(Guid id)
    {
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Category).Include(t => t.Requester).Include(t => t.Assignee).Include(t => t.Comments).ThenInclude(c => c.Author)
            .FirstOrDefaultAsync(t => t.Id == id) ?? throw new NotFoundAppException("Talep bulunamadı");
        Guard.That(IsStaff || ticket.RequesterId == Ctx.UserId, "Talebi görme yetkiniz yok");
        return Map(ticket, DateTime.UtcNow);
    }

    private TicketDto Map(Ticket t, DateTime now)
    {
        var open = t.Status != TicketStatus.CLOSED;
        var isRequester = t.RequesterId == Ctx.UserId;
        var comments = t.Comments.Where(c => IsStaff || !c.IsInternal).OrderBy(c => c.CreatedAt)
            .Select(c => new TicketCommentDto(c.Id, c.AuthorId, $"{c.Author.FirstName} {c.Author.LastName}", c.Body, c.IsInternal, c.CreatedAt)).ToList();
        return new TicketDto(t.Id, t.Number, t.Subject, t.Description, t.Status, t.Priority, t.CategoryId, t.Category?.Name, t.RequesterId, $"{t.Requester.FirstName} {t.Requester.LastName}",
            t.AssigneeId, t.Assignee == null ? null : $"{t.Assignee.FirstName} {t.Assignee.LastName}", t.FirstResponseDueAt, t.ResolutionDueAt, t.FirstRespondedAt, t.ResolvedAt, t.ClosedAt,
            Sla(t.CreatedAt, t.FirstResponseDueAt, t.FirstRespondedAt ?? (t.Status == TicketStatus.CLOSED ? t.ClosedAt : null), null, now),
            Sla(t.CreatedAt, t.ResolutionDueAt, t.ResolvedAt ?? (t.Status == TicketStatus.CLOSED ? t.ClosedAt : null), t.PausedAt, now),
            t.SatisfactionRating, IsAgent && open, open && (IsAgent || (isRequester && t.Status is TicketStatus.OPEN or TicketStatus.IN_PROGRESS or TicketStatus.WAITING)),
            isRequester && open, isRequester && t.Status == TicketStatus.RESOLVED, isRequester && t.Status == TicketStatus.CLOSED && t.SatisfactionRating == null,
            IsAgent ? Transitions[t.Status].ToList() : [], comments);
    }

    private static void ApplyStatus(Ticket ticket, TicketStatus to, DateTime now, bool byAgent)
    {
        if (ticket.PausedAt != null && to != TicketStatus.WAITING)
        {
            var minutes = Math.Max(0, (int)Math.Round((now - ticket.PausedAt.Value).TotalMinutes));
            ticket.PausedMinutes += minutes;
            ticket.ResolutionDueAt = ticket.ResolutionDueAt.AddMinutes(minutes);
            ticket.PausedAt = null;
        }
        if (to == TicketStatus.WAITING) ticket.PausedAt ??= now;
        if (byAgent && to is TicketStatus.RESOLVED or TicketStatus.CLOSED)
        {
            ticket.ResolvedAt ??= now;
            ticket.FirstRespondedAt ??= now;
        }
        if (to == TicketStatus.CLOSED) ticket.ClosedAt = now;
        if (ticket.Status == TicketStatus.RESOLVED && to is TicketStatus.OPEN or TicketStatus.IN_PROGRESS or TicketStatus.WAITING) ticket.ResolvedAt = null;
    }

    private static string Sla(DateTime started, DateTime due, DateTime? done, DateTime? paused, DateTime now)
    {
        if (done != null) return done <= due ? "MET" : "BREACHED";
        var at = paused ?? now;
        if (at > due) return "BREACHED";
        if (paused != null) return "PAUSED";
        var window = (due - started).TotalMilliseconds;
        var left = (due - now).TotalMilliseconds;
        return window > 0 && left < window * 0.25 ? "AT_RISK" : "ON_TRACK";
    }

    private async Task<(int First, int Resolve)> TargetsAsync(TicketPriority priority)
    {
        var policy = await db.TicketSlaPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Priority == priority);
        return policy == null ? DefaultSla[priority] : (policy.FirstResponseHours, policy.ResolutionHours);
    }

    private async Task<Ticket> OpenAsync(Guid id)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id) ?? throw new NotFoundAppException("Talep bulunamadı");
        Guard.State(ticket.Status != TicketStatus.CLOSED, "Kapalı talep değiştirilemez");
        return ticket;
    }

    private async Task<Ticket> RequesterAsync(Guid id)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id) ?? throw new NotFoundAppException("Talep bulunamadı");
        Guard.That(ticket.RequesterId == Ctx.UserId || Ctx.IsSuperAdmin, "Yalnızca talep sahibi işlem yapabilir");
        return ticket;
    }

    private async Task<string?> UserNameAsync(Guid? id)
    {
        if (id == null) return null;
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => new { u.FirstName, u.LastName }).FirstOrDefaultAsync();
        return user == null ? null : $"{user.FirstName} {user.LastName}";
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class TicketsHook() : ManagedRecordHook("tickets", "Destek talepleri destek ekranından yönetilir");
public sealed class TicketCommentsHook() : ManagedRecordHook("ticket-comments", "Yorumlar talep ekranından yazılır");
