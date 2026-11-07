using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Platform;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Crm;

public sealed record SaveLead(
    string CompanyName, string? ContactName, string? Email, string? Phone, string? City, LeadSource Source,
    decimal? EstimatedValue, string? CurrencyCode, string? Notes, Guid? OwnerId);

public sealed record ConvertLead(bool CreateOpportunity, string? OpportunityTitle, decimal? Amount);

public sealed record DisqualifyLead(string? Reason);

public sealed record LeadDto(
    Guid Id, string Number, string CompanyName, string? ContactName, string? Email, string? Phone, string? City,
    LeadSource Source, LeadStatus Status, decimal? EstimatedValue, string CurrencyCode, string? Notes,
    Guid? OwnerId, string? OwnerName, string? DisqualifyReason, Guid? PartnerId, string? PartnerTitle, DateTime? ConvertedAt);

public sealed record SaveOpportunity(
    string Title, Guid PartnerId, Guid? LeadId, decimal Amount, string? CurrencyCode, DateOnly? ExpectedCloseDate, Guid? OwnerId, string? Description);

public sealed record ChangeStage(OpportunityStage Stage, string? LostReason);

public sealed record OpportunityDto(
    Guid Id, string Number, string Title, Guid PartnerId, string PartnerTitle, Guid? LeadId, string? LeadNumber,
    OpportunityStage Stage, decimal Amount, string CurrencyCode, int Probability, decimal WeightedAmount,
    DateOnly? ExpectedCloseDate, Guid? OwnerId, string? OwnerName, string? Description, string? LostReason, DateTime? ClosedAt);

public sealed record SaveActivity(
    CrmActivityType Type, string Subject, string? Description, DateTime? DueAt, Guid? OwnerId, Guid? LeadId, Guid? OpportunityId, Guid? PartnerId);

public sealed record ActivityDto(
    Guid Id, CrmActivityType Type, string Subject, string? Description, DateTime? DueAt, DateTime? CompletedAt, bool Overdue,
    Guid OwnerId, string OwnerName, Guid? LeadId, Guid? OpportunityId, Guid? PartnerId);

/// <summary>Potansiyel müşteri. Dönüşüm cari kart (ve istenirse fırsat) açar.</summary>
[ModuleService]
public sealed class LeadService(AppDbContext db, NumberingService numbering, OpportunityService opportunities, NotificationService notifications)
{
    private CurrentUser Ctx => db.User;

    public static bool IsOpen(LeadStatus status) => status is LeadStatus.NEW or LeadStatus.CONTACTED or LeadStatus.QUALIFIED;

    public async Task<LeadDto> CreateAsync(SaveLead dto)
    {
        Guard.That(Ctx.Has("crm:create"), "Potansiyel müşteri oluşturma yetkiniz yok");
        var lead = new Lead { Number = await numbering.NextAsync("LEAD"), CreatedById = Ctx.RequireUserId() };
        await ApplyAsync(lead, dto);
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return await GetAsync(lead.Id);
    }

    public async Task<LeadDto> UpdateAsync(Guid id, SaveLead dto)
    {
        Guard.That(Ctx.Has("crm:update"), "Potansiyel müşteri güncelleme yetkiniz yok");
        var lead = await FindAsync(id);
        Guard.State(IsOpen(lead.Status), "Kapanmış potansiyel müşteri güncellenemez");
        await ApplyAsync(lead, dto);
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<LeadDto> DisqualifyAsync(Guid id, string? reason)
    {
        Guard.That(Ctx.Has("crm:update"), "Potansiyel müşteri güncelleme yetkiniz yok");
        Guard.That(!string.IsNullOrWhiteSpace(reason), "Eleme gerekçesi gerekli");
        var lead = await FindAsync(id);
        Guard.State(IsOpen(lead.Status), "Kapanmış potansiyel müşteri elenemez");
        lead.Status = LeadStatus.DISQUALIFIED;
        lead.DisqualifyReason = reason!.Trim();
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    /// <summary>Opens a customer account (or links an existing one) and optionally a prospecting opportunity.</summary>
    public async Task<LeadDto> ConvertAsync(Guid id, ConvertLead dto)
    {
        Guard.That(Ctx.Has("crm:update"), "Potansiyel müşteri güncelleme yetkiniz yok");
        var lead = await FindAsync(id);
        Guard.State(IsOpen(lead.Status), "Yalnızca açık potansiyel müşteri dönüştürülür");
        await using var tx = await db.Database.BeginTransactionAsync();
        var partner = await MatchPartnerAsync(lead);
        if (partner == null)
        {
            partner = new BusinessPartner
            {
                Code = await numbering.NextAsync("PARTNER"),
                Title = lead.CompanyName,
                Type = PartnerType.CUSTOMER,
                Email = lead.Email,
                Phone = lead.Phone,
                City = lead.City,
                ContactName = lead.ContactName,
                CurrencyCode = lead.CurrencyCode,
            };
            db.BusinessPartners.Add(partner);
            await db.SaveChangesAsync();
        }
        lead.PartnerId = partner.Id;
        lead.Status = LeadStatus.CONVERTED;
        lead.ConvertedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (dto.CreateOpportunity)
        {
            await opportunities.CreateAsync(new SaveOpportunity(
                string.IsNullOrWhiteSpace(dto.OpportunityTitle) ? lead.CompanyName : dto.OpportunityTitle.Trim(),
                partner.Id, lead.Id, dto.Amount ?? lead.EstimatedValue ?? 0, lead.CurrencyCode, null, lead.OwnerId, lead.Notes));
        }
        await tx.CommitAsync();
        if (lead.OwnerId is { } owner && owner != Ctx.UserId)
        {
            await notifications.NotifyAsync(new NotifyRequest([owner], lead.CompanyId, "crm.lead_converted",
                "Potansiyel müşteri carisi açıldı", $"{lead.Number} — {lead.CompanyName}", $"/crm/leads/{lead.Id}"));
        }
        return await GetAsync(id);
    }

    public async Task<LeadDto> GetAsync(Guid id)
    {
        Guard.That(Ctx.Has("crm:read"), "Potansiyel müşteri görme yetkiniz yok");
        var lead = await db.Leads.AsNoTracking().Include(l => l.Owner).Include(l => l.Partner).FirstOrDefaultAsync(l => l.Id == id)
            ?? throw new NotFoundAppException("Potansiyel müşteri bulunamadı");
        return Map(lead);
    }

    private async Task<BusinessPartner?> MatchPartnerAsync(Lead lead)
    {
        if (string.IsNullOrWhiteSpace(lead.Email)) return null;
        return await db.BusinessPartners.FirstOrDefaultAsync(p => p.Email == lead.Email);
    }

    private async Task ApplyAsync(Lead lead, SaveLead dto)
    {
        Guard.That(!string.IsNullOrWhiteSpace(dto.CompanyName), "Firma adı gerekli");
        Guard.That(dto.EstimatedValue is null or >= 0, "Tahmini tutar negatif olamaz");
        if (dto.OwnerId != null) Guard.That(await db.Users.AnyAsync(u => u.Id == dto.OwnerId), "Sorumlu kullanıcı bulunamadı");
        lead.CompanyName = dto.CompanyName.Trim();
        lead.ContactName = Blank(dto.ContactName);
        lead.Email = Blank(dto.Email);
        lead.Phone = Blank(dto.Phone);
        lead.City = Blank(dto.City);
        lead.Source = dto.Source;
        lead.EstimatedValue = dto.EstimatedValue;
        lead.CurrencyCode = string.IsNullOrWhiteSpace(dto.CurrencyCode) ? "TRY" : dto.CurrencyCode.Trim().ToUpperInvariant();
        lead.Notes = Blank(dto.Notes);
        lead.OwnerId = dto.OwnerId ?? Ctx.UserId;
    }

    private async Task<Lead> FindAsync(Guid id) =>
        await db.Leads.FirstOrDefaultAsync(l => l.Id == id) ?? throw new NotFoundAppException("Potansiyel müşteri bulunamadı");

    private static LeadDto Map(Lead l) => new(
        l.Id, l.Number, l.CompanyName, l.ContactName, l.Email, l.Phone, l.City, l.Source, l.Status, l.EstimatedValue, l.CurrencyCode, l.Notes,
        l.OwnerId, l.Owner == null ? null : $"{l.Owner.FirstName} {l.Owner.LastName}", l.DisqualifyReason, l.PartnerId, l.Partner?.Title, l.ConvertedAt);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Satış fırsatı. Aşama değişince olasılık aşamanın varsayılanına döner.</summary>
[ModuleService]
public sealed class OpportunityService(AppDbContext db, NumberingService numbering)
{
    private CurrentUser Ctx => db.User;

    public static int ProbabilityOf(OpportunityStage stage) => stage switch
    {
        OpportunityStage.PROSPECTING => 10,
        OpportunityStage.QUALIFICATION => 25,
        OpportunityStage.PROPOSAL => 50,
        OpportunityStage.NEGOTIATION => 75,
        OpportunityStage.WON => 100,
        _ => 0,
    };

    public static bool IsOpen(OpportunityStage stage) => stage is not (OpportunityStage.WON or OpportunityStage.LOST);

    public async Task<OpportunityDto> CreateAsync(SaveOpportunity dto)
    {
        Guard.That(Ctx.Has("crm:create"), "Fırsat oluşturma yetkiniz yok");
        var row = new Opportunity
        {
            Number = await numbering.NextAsync("OPPORTUNITY"),
            Stage = OpportunityStage.PROSPECTING,
            Probability = ProbabilityOf(OpportunityStage.PROSPECTING),
            CreatedById = Ctx.RequireUserId(),
        };
        await ApplyAsync(row, dto);
        db.Opportunities.Add(row);
        await db.SaveChangesAsync();
        return await GetAsync(row.Id);
    }

    public async Task<OpportunityDto> ChangeStageAsync(Guid id, OpportunityStage stage, string? lostReason)
    {
        Guard.That(Ctx.Has("crm:update"), "Fırsat güncelleme yetkiniz yok");
        var row = await db.Opportunities.FirstOrDefaultAsync(o => o.Id == id) ?? throw new NotFoundAppException("Fırsat bulunamadı");
        Guard.State(IsOpen(row.Stage), "Kapanmış fırsatın aşaması değişmez");
        if (stage == OpportunityStage.LOST) Guard.That(!string.IsNullOrWhiteSpace(lostReason), "Kaybetme gerekçesi gerekli");
        row.Stage = stage;
        row.Probability = ProbabilityOf(stage);
        row.LostReason = stage == OpportunityStage.LOST ? lostReason!.Trim() : null;
        row.ClosedAt = IsOpen(stage) ? null : DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<OpportunityDto> GetAsync(Guid id)
    {
        Guard.That(Ctx.Has("crm:read"), "Fırsat görme yetkiniz yok");
        var row = await db.Opportunities.AsNoTracking().Include(o => o.Partner).Include(o => o.Lead).Include(o => o.Owner).FirstOrDefaultAsync(o => o.Id == id)
            ?? throw new NotFoundAppException("Fırsat bulunamadı");
        return Map(row);
    }

    private async Task ApplyAsync(Opportunity row, SaveOpportunity dto)
    {
        Guard.That(!string.IsNullOrWhiteSpace(dto.Title), "Fırsat başlığı gerekli");
        Guard.That(dto.Amount >= 0, "Tutar negatif olamaz");
        var partner = await db.BusinessPartners.FirstOrDefaultAsync(p => p.Id == dto.PartnerId) ?? throw new NotFoundAppException("Cari hesap bulunamadı");
        Guard.That(partner.Type is PartnerType.CUSTOMER or PartnerType.BOTH, "Fırsat yalnızca müşteri carisine açılır");
        if (dto.LeadId != null)
        {
            var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == dto.LeadId) ?? throw new NotFoundAppException("Potansiyel müşteri bulunamadı");
            Guard.That(lead.PartnerId == null || lead.PartnerId == partner.Id, "Fırsat, potansiyel müşterinin carisiyle eşleşmiyor");
        }
        if (dto.OwnerId != null) Guard.That(await db.Users.AnyAsync(u => u.Id == dto.OwnerId), "Sorumlu kullanıcı bulunamadı");
        row.Title = dto.Title.Trim();
        row.PartnerId = partner.Id;
        row.LeadId = dto.LeadId;
        row.Amount = dto.Amount;
        row.CurrencyCode = string.IsNullOrWhiteSpace(dto.CurrencyCode) ? partner.CurrencyCode : dto.CurrencyCode.Trim().ToUpperInvariant();
        row.ExpectedCloseDate = dto.ExpectedCloseDate;
        row.OwnerId = dto.OwnerId ?? Ctx.UserId;
        row.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
    }

    internal static OpportunityDto Map(Opportunity o) => new(
        o.Id, o.Number, o.Title, o.PartnerId, o.Partner.Title, o.LeadId, o.Lead?.Number, o.Stage, o.Amount, o.CurrencyCode, o.Probability,
        Math.Round(o.Amount * o.Probability / 100m, 2, MidpointRounding.AwayFromZero), o.ExpectedCloseDate, o.OwnerId,
        o.Owner == null ? null : $"{o.Owner.FirstName} {o.Owner.LastName}", o.Description, o.LostReason, o.ClosedAt);
}

/// <summary>Arama, toplantı, e-posta, görev veya not.</summary>
[ModuleService]
public sealed class ActivityService(AppDbContext db)
{
    private CurrentUser Ctx => db.User;

    public async Task<ActivityDto> CreateAsync(SaveActivity dto)
    {
        Guard.That(Ctx.Has("crm:create"), "Aktivite oluşturma yetkiniz yok");
        var row = new CrmActivity { CreatedById = Ctx.RequireUserId() };
        await ApplyAsync(row, dto);
        db.CrmActivities.Add(row);
        await db.SaveChangesAsync();
        return await GetAsync(row.Id);
    }

    public async Task<ActivityDto> CompleteAsync(Guid id)
    {
        Guard.That(Ctx.Has("crm:update"), "Aktivite güncelleme yetkiniz yok");
        var row = await db.CrmActivities.FirstOrDefaultAsync(a => a.Id == id) ?? throw new NotFoundAppException("Aktivite bulunamadı");
        Guard.State(row.CompletedAt == null, "Aktivite zaten tamamlanmış");
        row.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    public async Task<ActivityDto> GetAsync(Guid id)
    {
        Guard.That(Ctx.Has("crm:read"), "Aktivite görme yetkiniz yok");
        var row = await db.CrmActivities.AsNoTracking().Include(a => a.Owner).FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new NotFoundAppException("Aktivite bulunamadı");
        return Map(row);
    }

    private async Task ApplyAsync(CrmActivity row, SaveActivity dto)
    {
        Guard.That(!string.IsNullOrWhiteSpace(dto.Subject), "Konu gerekli");
        Guard.That(dto.LeadId != null || dto.OpportunityId != null || dto.PartnerId != null, "Aktivite bir kayıtla ilişkilendirilmeli");
        if (dto.LeadId != null) Guard.That(await db.Leads.AnyAsync(l => l.Id == dto.LeadId), "Potansiyel müşteri bulunamadı");
        if (dto.OpportunityId != null) Guard.That(await db.Opportunities.AnyAsync(o => o.Id == dto.OpportunityId), "Fırsat bulunamadı");
        if (dto.PartnerId != null) Guard.That(await db.BusinessPartners.AnyAsync(p => p.Id == dto.PartnerId), "Cari hesap bulunamadı");
        var owner = dto.OwnerId ?? Ctx.RequireUserId();
        Guard.That(await db.Users.AnyAsync(u => u.Id == owner), "Sorumlu kullanıcı bulunamadı");
        row.Type = dto.Type;
        row.Subject = dto.Subject.Trim();
        row.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        row.DueAt = dto.DueAt;
        row.OwnerId = owner;
        row.LeadId = dto.LeadId;
        row.OpportunityId = dto.OpportunityId;
        row.PartnerId = dto.PartnerId;
    }

    internal static ActivityDto Map(CrmActivity a) => new(
        a.Id, a.Type, a.Subject, a.Description, a.DueAt, a.CompletedAt, a.CompletedAt == null && a.DueAt != null && a.DueAt < DateTime.UtcNow,
        a.OwnerId, $"{a.Owner.FirstName} {a.Owner.LastName}", a.LeadId, a.OpportunityId, a.PartnerId);
}
