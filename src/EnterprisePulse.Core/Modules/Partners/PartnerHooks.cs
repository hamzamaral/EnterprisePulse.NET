using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Resources;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Partners;

public sealed class BusinessPartnersHook(AppDbContext db) : IResourceHook
{
    public string Resource => "business-partners";

    public async Task BeforeSaveAsync(object entity, bool isNew)
    {
        var p = (BusinessPartner)entity;
        p.Title = p.Title.Trim();
        Guard.That(p.Title.Length > 0, "Unvan gerekli");
        Guard.That(p.PaymentTermDays is >= 0 and <= 365, "Vade 0-365 gün arasında olmalı");
        Guard.That(p.CreditLimit is null or >= 0, "Kredi limiti negatif olamaz");
        p.CurrencyCode = p.CurrencyCode.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(p.Code))
        {
            p.Code = p.Code.Trim();
            var taken = await db.BusinessPartners.IgnoreQueryFilters()
                .AnyAsync(x => x.CompanyId == db.User.CompanyId && x.Code == p.Code && x.Id != p.Id);
            Guard.State(!taken, $"{p.Code} cari kodu kullanımda");
        }
    }

    /// <summary>Partners are soft-deleted and deactivated; their ledger history stays intact.</summary>
    public Task BeforeDeleteAsync(object entity)
    {
        ((BusinessPartner)entity).IsActive = false;
        return Task.CompletedTask;
    }
}
