using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Trade;
using EnterprisePulse.Core.Modules.Workflow;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Purchasing;

public sealed record RequestLineInput(Guid? VariantId, string? Description, decimal Quantity, decimal? EstimatedPrice);

public sealed record CreatePurchaseRequest(string Title, DateOnly? NeededBy, Guid? DepartmentId, string? Notes, List<RequestLineInput> Lines);

public sealed record RequestLineDto(Guid Id, int LineNo, Guid? VariantId, Guid? ProductId, string? Sku, string? UnitCode, string Description, decimal Quantity, decimal? EstimatedPrice);

public sealed record LinkedDocumentDto(Guid Id, string Number, DateOnly Date, string Status, decimal Total);

public sealed record PurchaseRequestDetailDto(
    Guid Id, string Number, string Title, DateOnly RequestDate, DateOnly? NeededBy, Guid? DepartmentId, string? DepartmentName,
    PurchaseRequestStatus Status, Guid RequestedById, string RequestedByName, decimal EstimatedTotal, int QuoteCount, Guid? WorkflowInstanceId,
    DateTime? DecidedAt, string? Notes, bool CanCancel, List<RequestLineDto> Lines, List<LinkedDocumentDto> Orders);

/// <summary>Satın alma talepleri, approved through the PURCHASE_REQUEST workflow.</summary>
[ModuleService]
public sealed class PurchaseRequestService(AppDbContext db, NumberingService numbering, WorkflowService workflow, TradeService trade, NotificationService notifications)
{
    public const string WorkflowCode = "PURCHASE_REQUEST";
    public const string EntityType = "PurchaseRequest";

    private CurrentUser Ctx => db.User;

    /// <summary>Without <c>purchasing:read</c> a user only sees their own requests.</summary>
    public bool OwnOnly => !Ctx.Has("purchasing:read");

    public async Task<PurchaseRequestDetailDto> CreateAsync(CreatePurchaseRequest dto)
    {
        Guard.That(Ctx.HasAny("workflow:start", "purchasing:create"), "Satın alma talebi oluşturma yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(dto.Title), "Talep başlığı gerekli");
        Guard.That(dto.Lines is { Count: > 0 }, "En az bir satır gerekli");
        Guard.That(dto.DepartmentId == null || await db.Departments.AnyAsync(d => d.Id == dto.DepartmentId), "Departman bulunamadı");
        var variants = await trade.LoadVariantsAsync(dto.Lines.Select(l => l.VariantId));
        var hasWorkflow = await db.WorkflowDefinitions.AnyAsync(d => d.Code == WorkflowCode && d.IsActive && d.Steps.Any());

        await using var tx = await db.Database.BeginTransactionAsync();
        var request = new PurchaseRequest
        {
            Number = await numbering.NextAsync("PURCHASE_REQUEST"),
            Title = dto.Title.Trim(),
            RequestDate = DateOnly.FromDateTime(DateTime.UtcNow),
            NeededBy = dto.NeededBy,
            DepartmentId = dto.DepartmentId,
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            RequestedById = Ctx.RequireUserId(),
            Status = hasWorkflow ? PurchaseRequestStatus.PENDING : PurchaseRequestStatus.APPROVED,
            DecidedAt = hasWorkflow ? null : DateTime.UtcNow,
        };
        for (var i = 0; i < dto.Lines.Count; i++)
        {
            var l = dto.Lines[i];
            var v = l.VariantId is { } vid ? variants[vid] : null;
            var description = !string.IsNullOrWhiteSpace(l.Description) ? l.Description.Trim() : v != null ? TradeService.Label(v) : "";
            Guard.That(description.Length > 0, $"Satır {i + 1}: açıklama gerekli");
            Guard.That(l.Quantity > 0, $"Satır {i + 1}: miktar sıfırdan büyük olmalı");
            Guard.That(l.EstimatedPrice is null or >= 0, $"Satır {i + 1}: tahmini fiyat negatif olamaz");
            request.Lines.Add(new PurchaseRequestLine { LineNo = i + 1, VariantId = v?.Id, Description = description, Quantity = l.Quantity, EstimatedPrice = l.EstimatedPrice });
        }
        db.PurchaseRequests.Add(request);
        await db.SaveChangesAsync();
        if (hasWorkflow)
        {
            var instance = await workflow.StartAsync(new StartWorkflowRequest(WorkflowCode, request.Id.ToString(), $"{request.Number} — {request.Title}"));
            request.WorkflowInstanceId = instance.Id;
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
        return await GetAsync(request.Id);
    }

    /// <summary>The requester (or purchasing) withdraws a pending or approved, not yet ordered request.</summary>
    public async Task<PurchaseRequestDetailDto> CancelAsync(Guid id, string? reason)
    {
        var request = await VisibleAsync(id);
        if (request.RequestedById != Ctx.UserId && !Ctx.Has("purchasing:update"))
        {
            throw new ForbiddenAppException("Talebi yalnızca talep sahibi iptal edebilir");
        }
        Guard.State(request.Status is PurchaseRequestStatus.PENDING or PurchaseRequestStatus.APPROVED, "Talep zaten kapanmış");
        await using var tx = await db.Database.BeginTransactionAsync();
        if (request.Status == PurchaseRequestStatus.PENDING && request.WorkflowInstanceId is { } instanceId)
        {
            await workflow.CancelAsync(instanceId, reason);
        }
        await db.PurchaseRequests.Where(r => r.Id == id && (r.Status == PurchaseRequestStatus.PENDING || r.Status == PurchaseRequestStatus.APPROVED))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, PurchaseRequestStatus.CANCELLED).SetProperty(r => r.DecidedAt, DateTime.UtcNow));
        await tx.CommitAsync();
        return await GetAsync(id);
    }

    /// <summary>Applies an approval outcome; only pending requests change, so repeated events are harmless.</summary>
    public async Task DecideAsync(Guid id, PurchaseRequestStatus status)
    {
        var request = await db.PurchaseRequests.FirstOrDefaultAsync(r => r.Id == id && r.Status == PurchaseRequestStatus.PENDING);
        if (request == null) return;
        request.Status = status;
        request.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (status != PurchaseRequestStatus.CANCELLED && request.RequestedById != Ctx.UserId)
        {
            await notifications.NotifyAsync(new NotifyRequest([request.RequestedById], request.CompanyId, "purchasing.request_decided",
                status == PurchaseRequestStatus.APPROVED ? "Satın alma talebiniz onaylandı" : "Satın alma talebiniz reddedildi",
                $"{request.Number} — {request.Title}", $"/r/purchase-requests/{request.Id}"));
        }
    }

    public async Task<PurchaseRequestDetailDto> GetAsync(Guid id)
    {
        var r = await db.PurchaseRequests.AsNoTracking().Include(x => x.Department).Include(x => x.RequestedBy)
            .Include(x => x.Lines).ThenInclude(l => l.Variant).ThenInclude(v => v!.Product).ThenInclude(p => p.Unit)
            .Include(x => x.Orders).AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id);
        if (r == null || (OwnOnly && r.RequestedById != Ctx.UserId)) throw new NotFoundAppException("Satın alma talebi bulunamadı");
        var quotes = await db.SupplierQuotes.CountAsync(q => q.RequestId == id);
        var canCancel = r.Status is PurchaseRequestStatus.PENDING or PurchaseRequestStatus.APPROVED && (r.RequestedById == Ctx.UserId || Ctx.Has("purchasing:update"));
        return new PurchaseRequestDetailDto(r.Id, r.Number, r.Title, r.RequestDate, r.NeededBy, r.DepartmentId, r.Department?.Name, r.Status, r.RequestedById,
            $"{r.RequestedBy.FirstName} {r.RequestedBy.LastName}", Math.Round(r.Lines.Sum(l => l.Quantity * (l.EstimatedPrice ?? 0)), 2), quotes,
            r.WorkflowInstanceId, r.DecidedAt, r.Notes, canCancel,
            r.Lines.OrderBy(l => l.LineNo).Select(l => new RequestLineDto(l.Id, l.LineNo, l.VariantId, l.Variant?.ProductId, l.Variant?.Sku, l.Variant?.Product.Unit.Code,
                l.Description, l.Quantity, l.EstimatedPrice)).ToList(),
            r.Orders.OrderBy(o => o.Number).Select(o => new LinkedDocumentDto(o.Id, o.Number, o.OrderDate, o.Status.ToString(), o.GrandTotal)).ToList());
    }

    private async Task<PurchaseRequest> VisibleAsync(Guid id)
    {
        var r = await db.PurchaseRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null || (OwnOnly && r.RequestedById != Ctx.UserId)) throw new NotFoundAppException("Satın alma talebi bulunamadı");
        return r;
    }
}

/// <summary>Keeps purchase request status in sync with the approval flow.</summary>
public sealed class PurchaseRequestWorkflowListener(IServiceProvider services) : IWorkflowListener
{
    public string EntityType => PurchaseRequestService.EntityType;

    public Task OnFinishedAsync(WorkflowFinished e)
    {
        // Resolved lazily: the service depends on WorkflowService, which depends on the listeners.
        var requests = (PurchaseRequestService)services.GetService(typeof(PurchaseRequestService))!;
        if (!Guid.TryParse(e.EntityId, out var id)) return Task.CompletedTask;
        var status = e.Status switch
        {
            WorkflowStatus.APPROVED => PurchaseRequestStatus.APPROVED,
            WorkflowStatus.REJECTED => PurchaseRequestStatus.REJECTED,
            _ => PurchaseRequestStatus.CANCELLED,
        };
        return requests.DecideAsync(id, status);
    }
}
