using System.Globalization;
using System.Text.Json;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using EnterprisePulse.Core.Modules.Accounting;
using EnterprisePulse.Core.Modules.Partners;
using EnterprisePulse.Core.Modules.Platform;
using EnterprisePulse.Core.Modules.Purchasing;
using EnterprisePulse.Core.Modules.Trade;
using Microsoft.EntityFrameworkCore;

namespace EnterprisePulse.Core.Modules.Einvoice;

public sealed record SendEInvoice(Guid SalesInvoiceId, EInvoiceProfile? Profile);
public sealed record RespondEInvoice(string Action, string? Note);
public sealed record SimulateIncoming(Guid? PartnerId, EInvoiceProfile? Profile);
public sealed record EInvoiceSettings(string SenderAlias, EInvoiceProfile DefaultProfile, bool AutoSend);
public sealed record UpdateEInvoiceSettings(string? SenderAlias, EInvoiceProfile? DefaultProfile, bool? AutoSend);

public sealed record EInvoiceSummaryDto(
    int Unsent, int InProgress, int Errors, int AwaitingResponse, int ToRespond, int ToRecord, int OutgoingThisMonth, decimal OutgoingAmountThisMonth, int IncomingThisMonth);

public sealed record UnsentInvoiceDto(
    Guid Id, string Number, Guid PartnerId, string PartnerTitle, DateOnly InvoiceDate, string CurrencyCode, decimal GrandTotal, EInvoiceProfile Profile);

public sealed record SyncResultDto(int Processed);

public sealed record EInvoiceEventDto(Guid Id, EInvoiceStatus Status, string? Code, string Message, string? UserName, DateTime CreatedAt);

public sealed record EInvoiceDetailDto(
    Guid Id, EInvoiceDirection Direction, EInvoiceProfile Profile, EInvoiceStatus Status, string Number, Guid Uuid, Guid PartnerId, string PartnerCode, string PartnerTitle,
    DateOnly IssueDate, string CurrencyCode, decimal TaxTotal, decimal PayableAmount, Guid? SalesInvoiceId, string? SalesInvoiceNumber, Guid? PurchaseInvoiceId,
    string? PurchaseInvoiceNumber, string? StatusCode, string? StatusMessage, DateTime? SentAt, DateTime? DeliveredAt, DateTime? RespondedAt, bool CanRespond,
    decimal ExchangeRate, decimal LineExtensionAmount, decimal AllowanceTotal, string? SenderAlias, string? ReceiverAlias, string? SenderTaxId, string? ReceiverTaxId,
    string? ResponseNote, DateOnly? ResponseDeadline, List<EInvoiceLine> Lines, List<VatBreakdown> Taxes, List<EInvoiceEventDto> Events, string Xml, string? CreatedByName,
    bool CanResend, bool CanRecord);

/// <summary>
/// e-Fatura / e-Arşiv simulation. Outgoing invoices are queued from sales invoices and moved through GİB's states by the integrator
/// run (<see cref="SyncAsync"/>, every minute); incoming ones are simulated supplier invoices that are answered and recorded as
/// purchase invoices.
/// </summary>
[ModuleService]
public sealed class EInvoiceService(AppDbContext db, NumberingService numbering, NotificationService notifications, PurchaseInvoiceService purchaseInvoices) : IScheduledJob
{
    public const string SettingsKey = "einvoice";
    public const string InvoiceSequence = "EINVOICE";
    public const string ArchiveSequence = "EARCHIVE";

    public string Id => "einvoice-sync";
    public string Cron => "* * * * *";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private CurrentUser Ctx => db.User;

    private sealed record Step(EInvoiceStatus Status, string Code, string Message);

    private static Step Queued => new(EInvoiceStatus.QUEUED, EInvoiceRules.Codes.Queued, "Zarf kuyruğa eklendi");

    public async Task RunAsync()
    {
        using var bypass = Ctx.WithoutTenant();
        foreach (var companyId in await db.Companies.Select(c => c.Id).ToListAsync())
        {
            using var _ = Ctx.ActAs(companyId, superAdmin: true);
            db.ChangeTracker.Clear();
            await SyncAsync();
        }
    }

    // ---------- Queries ----------

    public async Task<EInvoiceSummaryDto> SummaryAsync()
    {
        var today = AccountingClock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var windowStart = DateTime.UtcNow.AddDays(-EInvoiceRules.ResponseDays);
        var outgoing = db.EInvoices.AsNoTracking().Where(e => e.Direction == EInvoiceDirection.OUTGOING);
        var incoming = db.EInvoices.AsNoTracking().Where(e => e.Direction == EInvoiceDirection.INCOMING);
        var unsent = await db.SalesInvoices.CountAsync(i => i.Status == InvoiceStatus.ISSUED && i.EInvoice == null);
        var inProgress = await outgoing.CountAsync(e => e.Status == EInvoiceStatus.QUEUED || e.Status == EInvoiceStatus.SENT);
        var errors = await outgoing.CountAsync(e => e.Status == EInvoiceStatus.ERROR);
        var awaiting = await outgoing.CountAsync(e => e.Profile == EInvoiceProfile.TICARIFATURA && e.Status == EInvoiceStatus.DELIVERED);
        var toRespond = await incoming.CountAsync(e => e.Profile == EInvoiceProfile.TICARIFATURA && e.Status == EInvoiceStatus.DELIVERED && e.DeliveredAt >= windowStart);
        var toRecord = await incoming.CountAsync(e => e.Status != EInvoiceStatus.REJECTED && (e.PurchaseInvoiceId == null || e.PurchaseInvoice!.Status == InvoiceStatus.CANCELLED));
        var month = await outgoing.Where(e => e.IssueDate >= monthStart && e.Status != EInvoiceStatus.ERROR && e.Status != EInvoiceStatus.CANCELLED)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Sum = g.Sum(e => e.PayableAmount) }).FirstOrDefaultAsync();
        var incomingMonth = await incoming.CountAsync(e => e.IssueDate >= monthStart);
        return new EInvoiceSummaryDto(unsent, inProgress, errors, awaiting, toRespond, toRecord, month?.Count ?? 0, month?.Sum ?? 0, incomingMonth);
    }

    /// <summary>Issued sales invoices that have not been sent yet, newest first.</summary>
    public async Task<List<UnsentInvoiceDto>> UnsentAsync()
    {
        var settings = await ReadSettingsAsync(Ctx.RequireCompanyId());
        var rows = await db.SalesInvoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.ISSUED && i.EInvoice == null)
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.CreatedAt).Take(50)
            .Select(i => new { i.Id, i.Number, i.PartnerId, i.Partner.Title, i.Partner.EInvoiceAlias, i.InvoiceDate, i.CurrencyCode, i.GrandTotal }).ToListAsync();
        return rows.Select(r => new UnsentInvoiceDto(r.Id, r.Number, r.PartnerId, r.Title, r.InvoiceDate, r.CurrencyCode, r.GrandTotal,
            EInvoiceRules.ResolveProfile(r.EInvoiceAlias, null, settings.DefaultProfile)!.Value)).ToList();
    }

    public async Task<EInvoiceDetailDto> GetAsync(Guid id)
    {
        var row = await db.EInvoices.AsNoTracking().AsSplitQuery()
            .Include(e => e.Partner).Include(e => e.SalesInvoice).Include(e => e.PurchaseInvoice).Include(e => e.CreatedBy)
            .Include(e => e.Events).ThenInclude(ev => ev.User)
            .FirstOrDefaultAsync(e => e.Id == id) ?? throw new NotFoundAppException("e-Fatura bulunamadı");
        var now = DateTime.UtcNow;
        var incoming = row.Direction == EInvoiceDirection.INCOMING;
        var recorded = row.PurchaseInvoice != null && row.PurchaseInvoice.Status != InvoiceStatus.CANCELLED;
        var lines = ReadLines(row);
        DateOnly? deadline = row.Profile == EInvoiceProfile.TICARIFATURA && row.DeliveredAt != null
            ? AccountingClock.LocalDate(row.DeliveredAt.Value.AddDays(EInvoiceRules.ResponseDays)) : null;
        return new EInvoiceDetailDto(row.Id, row.Direction, row.Profile, row.Status, row.Number, row.Uuid, row.PartnerId, row.Partner.Code, row.Partner.Title, row.IssueDate,
            row.CurrencyCode, row.TaxTotal, row.PayableAmount, row.SalesInvoiceId, row.SalesInvoice?.Number, row.PurchaseInvoiceId, row.PurchaseInvoice?.Number, row.StatusCode,
            row.StatusMessage, row.SentAt, row.DeliveredAt, row.RespondedAt, EInvoiceRules.CanRespond(row.Profile, row.Status, row.DeliveredAt, now), row.ExchangeRate,
            row.LineExtensionAmount, row.AllowanceTotal, row.SenderAlias, row.ReceiverAlias, row.SenderTaxId, row.ReceiverTaxId, row.ResponseNote, deadline, lines,
            TaxesOf(lines), row.Events.OrderBy(e => e.CreatedAt).Select(e => new EInvoiceEventDto(e.Id, e.Status, e.Code, e.Message,
                e.User == null ? null : $"{e.User.FirstName} {e.User.LastName}", e.CreatedAt)).ToList(), row.Xml,
            row.CreatedBy == null ? null : $"{row.CreatedBy.FirstName} {row.CreatedBy.LastName}",
            !incoming && row.Status == EInvoiceStatus.ERROR && row.SalesInvoice?.Status == InvoiceStatus.ISSUED,
            incoming && !recorded && (row.Profile == EInvoiceProfile.TICARIFATURA ? row.Status == EInvoiceStatus.ACCEPTED : row.Status != EInvoiceStatus.REJECTED));
    }

    private static List<EInvoiceLine> ReadLines(EInvoice row) => row.Lines.Deserialize<List<EInvoiceLine>>(Json) ?? [];

    private static List<VatBreakdown> TaxesOf(List<EInvoiceLine> lines) =>
        lines.GroupBy(l => l.VatRate).OrderBy(g => decimal.Parse(g.Key, CultureInfo.InvariantCulture))
            .Select(g => new VatBreakdown(g.Key, Money(g.Sum(l => Dec(l.Net))), Money(g.Sum(l => Dec(l.Vat))))).ToList();

    private static decimal Dec(string v) => decimal.Parse(v, CultureInfo.InvariantCulture);

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // ---------- Outgoing ----------

    /// <summary>Queues a sales invoice for GİB.</summary>
    public async Task<EInvoiceDetailDto> SendAsync(SendEInvoice dto)
    {
        var companyId = Ctx.RequireCompanyId();
        var userId = Ctx.RequireUserId();
        Guid id;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var salesId = dto.SalesInvoiceId;
            await db.Database.ExecuteSqlAsync($"SELECT id FROM sales_invoices WHERE id = {salesId} FOR UPDATE");
            var invoice = await LoadSourceAsync(salesId) ?? throw new NotFoundAppException("Satış faturası bulunamadı");
            Guard.State(invoice.Status == InvoiceStatus.ISSUED, "İptal edilmiş fatura gönderilemez");
            if (invoice.EInvoice != null)
            {
                throw new ConflictAppException(invoice.EInvoice.Status == EInvoiceStatus.ERROR ? "GİB bu e-faturayı reddetti; yeniden gönderin" : "Fatura zaten gönderilmiş");
            }
            var settings = await ReadSettingsAsync(companyId);
            var alias = invoice.Partner.EInvoiceAlias;
            var profile = EInvoiceRules.ResolveProfile(alias, dto.Profile, settings.DefaultProfile)
                ?? throw new BadRequestAppException(!string.IsNullOrEmpty(alias)
                    ? $"{invoice.Partner.Title} e-Fatura mükellefi; e-Arşiv kullanılamaz"
                    : $"{invoice.Partner.Title} e-Fatura mükellefi değil; e-Arşiv olarak gönderin");
            var number = await NextNumberAsync(companyId, profile);
            var data = await OutgoingDataAsync(invoice, profile, number, settings.SenderAlias);
            data.CompanyId = companyId;
            data.Direction = EInvoiceDirection.OUTGOING;
            data.SalesInvoiceId = invoice.Id;
            data.PartnerId = invoice.PartnerId;
            data.CreatedById = userId;
            data.Events.Add(new EInvoiceEvent { EInvoiceId = data.Id, Status = EInvoiceStatus.QUEUED, Code = Queued.Code, Message = Queued.Message, UserId = userId });
            db.EInvoices.Add(data);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            id = data.Id;
        }
        return await GetAsync(id);
    }

    /// <summary>Sends an envelope refused by GİB again with fresh data and a new ETTN.</summary>
    public async Task<EInvoiceDetailDto> ResendAsync(Guid id)
    {
        var companyId = Ctx.RequireCompanyId();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var current = await LockAsync(id);
            Guard.State(current.Direction == EInvoiceDirection.OUTGOING, "Yalnızca giden faturalar yeniden gönderilir");
            Guard.State(current.Status == EInvoiceStatus.ERROR, "Yalnızca GİB'in reddettiği faturalar yeniden gönderilir");
            var invoice = current.SalesInvoiceId is { } salesId ? await LoadSourceAsync(salesId) : null;
            Guard.State(invoice is { Status: InvoiceStatus.ISSUED }, "Satış faturası iptal edilmiş");
            var settings = await ReadSettingsAsync(companyId);
            var alias = invoice!.Partner.EInvoiceAlias;
            var profile = EInvoiceRules.ResolveProfile(alias, current.Profile, settings.DefaultProfile) ?? EInvoiceRules.ResolveProfile(alias, null, settings.DefaultProfile)!.Value;
            var sameSeries = (profile == EInvoiceProfile.EARSIVFATURA) == (current.Profile == EInvoiceProfile.EARSIVFATURA);
            var number = sameSeries ? current.Number : await NextNumberAsync(companyId, profile);
            var data = await OutgoingDataAsync(invoice, profile, number, settings.SenderAlias);
            current.Profile = data.Profile;
            current.Status = data.Status;
            current.Number = data.Number;
            current.Uuid = data.Uuid;
            current.IssueDate = data.IssueDate;
            current.CurrencyCode = data.CurrencyCode;
            current.ExchangeRate = data.ExchangeRate;
            current.LineExtensionAmount = data.LineExtensionAmount;
            current.AllowanceTotal = data.AllowanceTotal;
            current.TaxTotal = data.TaxTotal;
            current.PayableAmount = data.PayableAmount;
            current.SenderAlias = data.SenderAlias;
            current.ReceiverAlias = data.ReceiverAlias;
            current.SenderTaxId = data.SenderTaxId;
            current.ReceiverTaxId = data.ReceiverTaxId;
            current.Lines = data.Lines;
            current.Xml = data.Xml;
            current.StatusCode = data.StatusCode;
            current.StatusMessage = data.StatusMessage;
            current.PartnerId = invoice.PartnerId;
            current.SentAt = null;
            current.DeliveredAt = null;
            current.RespondedAt = null;
            current.ResponseNote = null;
            db.EInvoiceEvents.Add(new EInvoiceEvent
            {
                EInvoiceId = id, Status = EInvoiceStatus.QUEUED, Code = EInvoiceRules.Codes.Queued, Message = $"Yeniden gönderildi (yeni ETTN {data.Uuid})", UserId = Ctx.UserId,
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        return await GetAsync(id);
    }

    /// <summary>
    /// One integrator run: queued envelopes are validated (SENT or ERROR), sent ones reach the receiver, and unanswered ticari
    /// faturas are accepted after 8 days.
    /// </summary>
    public async Task<SyncResultDto> SyncAsync()
    {
        var now = DateTime.UtcNow;
        var rows = await db.EInvoices.AsNoTracking()
            .Where(e => e.Direction == EInvoiceDirection.OUTGOING
                && (e.Status == EInvoiceStatus.QUEUED || e.Status == EInvoiceStatus.SENT || (e.Status == EInvoiceStatus.DELIVERED && e.Profile == EInvoiceProfile.TICARIFATURA)))
            .OrderBy(e => e.CreatedAt).Select(e => new { Row = e, e.Partner.Title }).ToListAsync();
        var processed = 0;
        foreach (var item in rows)
        {
            var row = item.Row;
            var step = EInvoiceRules.NextOutgoingStep(row.Profile, row.Status, row.DeliveredAt, () => EInvoiceRules.ValidateForGib(new ValidationInput(row.Profile, row.Number,
                row.SenderTaxId, row.ReceiverTaxId, row.ReceiverAlias, row.CurrencyCode, row.ExchangeRate, ReadLines(row), row.PayableAmount)), now);
            if (step == null) continue;
            if (!await TransitionAsync(row.Id, row.Status, new Step(step.Status, step.Code, step.Message), null)) continue;
            processed++;
            if (step.Status == EInvoiceStatus.ERROR)
            {
                await NotifyCompanyAsync(row.CompanyId, "einvoice:send", $"e-Fatura reddedildi: {row.Number}", $"{item.Title} — {step.Message}", $"/e-invoices/{row.Id}");
            }
        }
        return new SyncResultDto(processed);
    }

    /// <summary>Accept or reject an incoming ticari fatura.</summary>
    public async Task<EInvoiceDetailDto> RespondAsync(Guid id, RespondEInvoice dto)
    {
        await AnswerAsync(id, dto, EInvoiceDirection.INCOMING);
        return await GetAsync(id);
    }

    /// <summary>Simulates the receiver's answer to an outgoing ticari fatura.</summary>
    public async Task<EInvoiceDetailDto> SimulateResponseAsync(Guid id, RespondEInvoice dto)
    {
        var row = await AnswerAsync(id, dto, EInvoiceDirection.OUTGOING);
        if (dto.Action == "REJECT")
            await NotifyCompanyAsync(Ctx.RequireCompanyId(), "einvoice:send", $"Ticari fatura reddedildi: {row.Number}", dto.Note ?? "", $"/e-invoices/{id}");
        return await GetAsync(id);
    }

    // ---------- Incoming ----------

    /// <summary>Simulates an e-Fatura arriving from a supplier, priced at the products' purchase prices.</summary>
    public async Task<EInvoiceDetailDto> SimulateIncomingAsync(SimulateIncoming dto)
    {
        var companyId = Ctx.RequireCompanyId();
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId);
        var partner = dto.PartnerId != null
            ? await db.BusinessPartners.FirstOrDefaultAsync(p => p.Id == dto.PartnerId && p.IsActive)
            : await RandomSupplierAsync(company.BaseCurrency);
        Guard.That(partner != null, "Tedarikçi bulunamadı");
        Guard.That(PartnerSides.Suppliers.Contains(partner!.Type), $"{partner.Title} tedarikçi değil");
        Guard.That(partner.CurrencyCode == company.BaseCurrency, "Yalnızca ana para biriminde faturalayan tedarikçiler simüle edilir");
        var variants = await db.ProductVariants.AsNoTracking().Where(v => v.IsActive && v.Product.IsActive && v.Product.PurchasePrice > 0)
            .Select(v => new
            {
                v.Id, v.Sku, v.Name, ProductName = v.Product.Name, v.Product.Type, v.Product.PurchasePrice, Unit = v.Product.Unit.Code,
                VatRate = v.Product.VatRate == null ? (decimal?)null : v.Product.VatRate.Rate,
            }).ToListAsync();
        Guard.That(variants.Count > 0, "Önce alış fiyatı olan ürün ekleyin");
        var picks = variants.OrderBy(_ => Random.Shared.Next()).Take(Random.Shared.Next(1, 4)).ToList();
        var inputs = picks.Select(v => new EInvoiceLineInput(v.Name != null ? $"{v.ProductName} {v.Name}" : v.ProductName, v.Sku, v.Id, v.Unit,
            v.Type == ProductType.SERVICE ? Random.Shared.Next(1, 4) : Random.Shared.Next(1, 11), v.PurchasePrice!.Value, 0, v.VatRate ?? 20)).ToList();
        var (lines, totals) = EInvoiceRules.BuildLines(inputs);
        var settings = await ReadSettingsAsync(companyId);
        var now = DateTime.UtcNow;
        var issueDate = AccountingClock.Today;
        var prefix = (new string(partner.Code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant() + "XXX")[..3];
        var number = $"{prefix}{issueDate.Year}{Random.Shared.Next(1, 1_000_000_000):D9}";
        var uuid = Guid.NewGuid();
        var profile = dto.Profile ?? EInvoiceProfile.TICARIFATURA;
        var senderAlias = partner.EInvoiceAlias ?? $"urn:mail:defaultgb@{partner.Code.ToLowerInvariant()}.com.tr";
        var xml = EInvoiceXml.Build(new UblInvoice(profile, number, uuid, issueDate.ToString("yyyy-MM-dd"), LocalTime(now), partner.CurrencyCode, null,
            [$"Vade: {partner.PaymentTermDays} gün"], PartnerParty(partner), CompanyParty(company), lines, totals, null));
        var invoice = new EInvoice
        {
            CompanyId = companyId, Direction = EInvoiceDirection.INCOMING, Profile = profile, Status = EInvoiceStatus.DELIVERED, Number = number, Uuid = uuid, PartnerId = partner.Id,
            IssueDate = issueDate, CurrencyCode = partner.CurrencyCode, ExchangeRate = 1, LineExtensionAmount = Dec(totals.LineExtension), AllowanceTotal = Dec(totals.Allowance),
            TaxTotal = lines.Sum(l => Dec(l.Vat)), PayableAmount = Dec(totals.Payable), SenderAlias = senderAlias, ReceiverAlias = settings.SenderAlias,
            SenderTaxId = partner.TaxNumber, ReceiverTaxId = company.TaxNumber, Lines = JsonSerializer.SerializeToDocument(lines, Json), Xml = xml,
            StatusCode = EInvoiceRules.Codes.Completed, StatusMessage = "Gelen kutusuna ulaştı", SentAt = now, DeliveredAt = now, CreatedById = Ctx.UserId,
        };
        invoice.Events.Add(new EInvoiceEvent
        {
            EInvoiceId = invoice.Id, Status = EInvoiceStatus.DELIVERED, Code = EInvoiceRules.Codes.Completed, Message = "Gelen kutusuna ulaştı (simülasyon)", UserId = Ctx.UserId,
        });
        db.EInvoices.Add(invoice);
        await db.SaveChangesAsync();
        await NotifyCompanyAsync(companyId, "einvoice:update", $"Yeni gelen e-Fatura: {partner.Title}", $"{number} · {totals.Payable} {partner.CurrencyCode}", $"/e-invoices/{invoice.Id}");
        return await GetAsync(invoice.Id);
    }

    /// <summary>Records an incoming invoice as a purchase invoice (ticari faturas must be accepted first).</summary>
    public async Task<EInvoiceDetailDto> RecordAsync(Guid id)
    {
        var companyId = Ctx.RequireCompanyId();
        var row = await db.EInvoices.Include(e => e.PurchaseInvoice).FirstOrDefaultAsync(e => e.Id == id) ?? throw new NotFoundAppException("e-Fatura bulunamadı");
        Guard.State(row.Direction == EInvoiceDirection.INCOMING, "Yalnızca gelen faturalar kaydedilir");
        Guard.State(row.PurchaseInvoice == null || row.PurchaseInvoice.Status == InvoiceStatus.CANCELLED, "Fatura zaten kaydedilmiş");
        Guard.State(row.Status != EInvoiceStatus.REJECTED, "Reddedilen fatura kaydedilemez");
        Guard.State(!(row.Profile == EInvoiceProfile.TICARIFATURA && row.Status != EInvoiceStatus.ACCEPTED), "Önce ticari faturayı kabul edin");
        var lines = ReadLines(row);
        var variantIds = lines.Where(l => l.VariantId != null).Select(l => l.VariantId!.Value).ToList();
        var known = (await db.ProductVariants.Where(v => variantIds.Contains(v.Id) && v.IsActive && v.Product.IsActive).Select(v => v.Id).ToListAsync()).ToHashSet();
        var invoice = await purchaseInvoices.CreateAsync(new CreatePurchaseInvoice(row.PartnerId, null, row.Number, row.IssueDate, null, row.CurrencyCode,
            row.ExchangeRate == 1 ? null : row.ExchangeRate, $"e-Fatura {row.Number} (ETTN {row.Uuid})",
            lines.Select(l => new TradeLineInput(l.VariantId is { } v && known.Contains(v) ? v : null, l.Description, Dec(l.Quantity), Dec(l.UnitPrice), Dec(l.DiscountRate), Dec(l.VatRate))).ToList()));
        row = await db.EInvoices.FirstAsync(e => e.Id == id);
        row.PurchaseInvoiceId = invoice.Id;
        db.EInvoiceEvents.Add(new EInvoiceEvent { EInvoiceId = id, Status = row.Status, Message = $"Alış faturası oluşturuldu: {invoice.Number}", UserId = Ctx.UserId });
        await db.SaveChangesAsync();
        return await GetAsync(id);
    }

    // ---------- Settings ----------

    public Task<EInvoiceSettings> SettingsAsync() => ReadSettingsAsync(Ctx.RequireCompanyId());

    public async Task<EInvoiceSettings> UpdateSettingsAsync(UpdateEInvoiceSettings dto)
    {
        var companyId = Ctx.RequireCompanyId();
        var current = await ReadSettingsAsync(companyId);
        var value = new EInvoiceSettings(string.IsNullOrWhiteSpace(dto.SenderAlias) ? current.SenderAlias : dto.SenderAlias.Trim(), dto.DefaultProfile ?? current.DefaultProfile,
            dto.AutoSend ?? current.AutoSend);
        Guard.That(EInvoiceRules.AliasPattern.IsMatch(value.SenderAlias), "Gönderici posta kutusu etiketi urn:mail:ad@alan.com biçiminde olmalı");
        var json = JsonSerializer.SerializeToDocument(new { senderAlias = value.SenderAlias, defaultProfile = value.DefaultProfile.ToString(), autoSend = value.AutoSend });
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingsKey);
        if (row == null) db.Settings.Add(new Setting { CompanyId = companyId, Key = SettingsKey, Value = json });
        else row.Value = json;
        await db.SaveChangesAsync();
        return value;
    }

    /// <summary>Sends a new sales invoice when automatic sending is on; failures are swallowed.</summary>
    public async Task AutoSendAsync(Guid salesInvoiceId)
    {
        var settings = await ReadSettingsAsync(Ctx.RequireCompanyId());
        if (!settings.AutoSend) return;
        try
        {
            await SendAsync(new SendEInvoice(salesInvoiceId, null));
        }
        catch (AppException)
        {
            // The invoice stays in the unsent list; the user can send it manually.
        }
    }

    // ---------- Internals ----------

    private async Task<EInvoice> AnswerAsync(Guid id, RespondEInvoice dto, EInvoiceDirection direction)
    {
        var note = dto.Note?.Trim();
        Guard.That(dto.Action is "ACCEPT" or "REJECT", "İşlem ACCEPT veya REJECT olmalı");
        Guard.That(!(dto.Action == "REJECT" && string.IsNullOrEmpty(note)), "Faturayı reddetmek için bir gerekçe girin");
        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await LockAsync(id);
        Guard.State(row.Direction == direction, direction == EInvoiceDirection.INCOMING ? "Yalnızca gelen faturalar yanıtlanır" : "Giden faturayı yalnızca alıcı yanıtlar");
        Guard.State(EInvoiceRules.CanRespond(row.Profile, row.Status, row.DeliveredAt, DateTime.UtcNow), $"Yalnızca ulaşmış ticari faturalar {EInvoiceRules.ResponseDays} gün içinde yanıtlanır");
        if (dto.Action == "REJECT" && row.PurchaseInvoiceId is { } purchaseId)
        {
            var status = await db.PurchaseInvoices.Where(p => p.Id == purchaseId).Select(p => (InvoiceStatus?)p.Status).FirstOrDefaultAsync();
            Guard.State(status != InvoiceStatus.ISSUED, "Reddetmeden önce alış faturasını iptal edin");
        }
        var accepted = dto.Action == "ACCEPT";
        var incoming = direction == EInvoiceDirection.INCOMING;
        var step = accepted
            ? new Step(EInvoiceStatus.ACCEPTED, EInvoiceRules.Codes.Accepted, incoming ? "Fatura kabul edildi" : "Alıcı faturayı kabul etti")
            : new Step(EInvoiceStatus.REJECTED, EInvoiceRules.Codes.Rejected, $"{(incoming ? "Fatura reddedildi" : "Alıcı faturayı reddetti")}: {note}");
        ApplyStep(row, step, Ctx.UserId, accepted ? null : note);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return row;
    }

    /// <summary>Moves an e-invoice from <paramref name="from"/> to the step's status; false when it changed meanwhile.</summary>
    private async Task<bool> TransitionAsync(Guid id, EInvoiceStatus from, Step step, Guid? userId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync($"SELECT id FROM e_invoices WHERE id = {id} FOR UPDATE");
        var status = await db.EInvoices.AsNoTracking().Where(e => e.Id == id).Select(e => e.Status).FirstAsync();
        if (status != from) return false;
        var row = await db.EInvoices.FirstAsync(e => e.Id == id);
        ApplyStep(row, step, userId, null);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        db.Entry(row).State = EntityState.Detached;
        return true;
    }

    private void ApplyStep(EInvoice row, Step step, Guid? userId, string? responseNote)
    {
        var now = DateTime.UtcNow;
        row.Status = step.Status;
        row.StatusCode = step.Code;
        row.StatusMessage = step.Message;
        if (step.Status == EInvoiceStatus.SENT) row.SentAt = now;
        if (step.Status == EInvoiceStatus.DELIVERED) row.DeliveredAt = now;
        if (step.Status is EInvoiceStatus.ACCEPTED or EInvoiceStatus.REJECTED)
        {
            row.RespondedAt = now;
            row.ResponseNote = responseNote;
        }
        db.EInvoiceEvents.Add(new EInvoiceEvent { EInvoiceId = row.Id, Status = step.Status, Code = step.Code, Message = step.Message, UserId = userId });
    }

    private async Task<EInvoice> LockAsync(Guid id)
    {
        await db.Database.ExecuteSqlAsync($"SELECT id FROM e_invoices WHERE id = {id} FOR UPDATE");
        return await db.EInvoices.FirstOrDefaultAsync(e => e.Id == id) ?? throw new NotFoundAppException("e-Fatura bulunamadı");
    }

    private Task<SalesInvoice?> LoadSourceAsync(Guid id) =>
        db.SalesInvoices.Include(i => i.Partner).Include(i => i.Order).Include(i => i.EInvoice)
            .Include(i => i.Lines.OrderBy(l => l.LineNo)).ThenInclude(l => l.Variant).ThenInclude(v => v!.Product).ThenInclude(p => p.Unit)
            .AsSplitQuery().FirstOrDefaultAsync(i => i.Id == id);

    private async Task<EInvoice> OutgoingDataAsync(SalesInvoice invoice, EInvoiceProfile profile, string number, string senderAlias)
    {
        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == invoice.CompanyId);
        var (lines, totals) = EInvoiceRules.BuildLines(invoice.Lines.Select(l => new EInvoiceLineInput(l.Description, l.Variant?.Sku, l.VariantId,
            l.Variant?.Product.Unit.Code, l.Quantity, l.UnitPrice, l.DiscountRate, l.VatRate)).ToList());
        var uuid = Guid.NewGuid();
        var eArchive = profile == EInvoiceProfile.EARSIVFATURA;
        var receiverAlias = eArchive ? null : invoice.Partner.EInvoiceAlias;
        var notes = new List<string> { $"Fatura no: {invoice.Number}", $"Son ödeme tarihi: {invoice.DueDate:yyyy-MM-dd}" };
        if (!string.IsNullOrEmpty(invoice.Notes)) notes.Add(invoice.Notes);
        var xml = EInvoiceXml.Build(new UblInvoice(profile, number, uuid, invoice.InvoiceDate.ToString("yyyy-MM-dd"), LocalTime(DateTime.UtcNow), invoice.CurrencyCode,
            invoice.CurrencyCode == "TRY" ? null : invoice.ExchangeRate.ToString("0.0000", CultureInfo.InvariantCulture), notes, CompanyParty(company), PartnerParty(invoice.Partner),
            lines, totals, invoice.Order == null ? null : new OrderReference(invoice.Order.Number, invoice.Order.OrderDate.ToString("yyyy-MM-dd"))));
        return new EInvoice
        {
            Profile = profile, Status = EInvoiceStatus.QUEUED, Number = number, Uuid = uuid, IssueDate = invoice.InvoiceDate, CurrencyCode = invoice.CurrencyCode,
            ExchangeRate = invoice.ExchangeRate, LineExtensionAmount = Dec(totals.LineExtension), AllowanceTotal = Dec(totals.Allowance), TaxTotal = lines.Sum(l => Dec(l.Vat)),
            PayableAmount = Dec(totals.Payable), SenderAlias = senderAlias, ReceiverAlias = receiverAlias, SenderTaxId = company.TaxNumber, ReceiverTaxId = invoice.Partner.TaxNumber,
            Lines = JsonSerializer.SerializeToDocument(lines, Json), Xml = xml, StatusCode = Queued.Code, StatusMessage = Queued.Message,
        };
    }

    /// <summary>GİB number of the next invoice, e.g. EPF2026000000001; the series are created on first use.</summary>
    private async Task<string> NextNumberAsync(Guid companyId, EInvoiceProfile profile)
    {
        var year = AccountingClock.Today.Year;
        var series = new[] { (Code: InvoiceSequence, Name: "e-Fatura", Prefix: "EPF"), (Code: ArchiveSequence, Name: "e-Arşiv Fatura", Prefix: "EPA") };
        foreach (var s in series)
        {
            var row = await db.NumberSequences.FirstOrDefaultAsync(n => n.Code == s.Code);
            if (row == null)
                db.NumberSequences.Add(new NumberSequence { CompanyId = companyId, Code = s.Code, Name = s.Name, Prefix = s.Prefix, Padding = 9, CurrentYear = year });
            else if (row.Padding != 9 && row.NextValue == 1) row.Padding = 9;
        }
        await db.SaveChangesAsync();
        var code = profile == EInvoiceProfile.EARSIVFATURA ? ArchiveSequence : InvoiceSequence;
        return EInvoiceRules.ToGibNumber(await numbering.NextAsync(code, companyId));
    }

    private async Task<EInvoiceSettings> ReadSettingsAsync(Guid companyId)
    {
        var row = await db.Settings.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Key == SettingsKey);
        var alias = "urn:mail:defaultgb@enterprisepulse.dev";
        var profile = EInvoiceProfile.TICARIFATURA;
        var auto = false;
        if (row != null)
        {
            var root = row.Value.RootElement;
            if (root.TryGetProperty("senderAlias", out var a) && a.ValueKind == JsonValueKind.String) alias = a.GetString()!;
            if (root.TryGetProperty("defaultProfile", out var p) && p.ValueKind == JsonValueKind.String && Enum.TryParse<EInvoiceProfile>(p.GetString(), out var parsed)) profile = parsed;
            if (root.TryGetProperty("autoSend", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False) auto = s.GetBoolean();
        }
        return new EInvoiceSettings(alias, profile, auto);
    }

    private async Task<BusinessPartner?> RandomSupplierAsync(string currencyCode)
    {
        var suppliers = await db.BusinessPartners.AsNoTracking()
            .Where(p => p.IsActive && PartnerSides.Suppliers.Contains(p.Type) && p.CurrencyCode == currencyCode && p.TaxNumber != null).ToListAsync();
        return suppliers.Count == 0 ? null : suppliers[Random.Shared.Next(suppliers.Count)];
    }

    private async Task NotifyCompanyAsync(Guid companyId, string permission, string title, string body, string link)
    {
        var users = await notifications.UsersWithPermissionAsync(companyId, permission);
        await notifications.NotifyAsync(new NotifyRequest(users, companyId, "einvoice", title, body, link));
    }

    private static string LocalTime(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static UblParty CompanyParty(Company c) => new(c.TaxNumber, c.LegalName ?? c.Name, c.TaxOffice, c.Address, c.City, c.Country, c.Email);

    private static UblParty PartnerParty(BusinessPartner p) => new(p.TaxNumber, p.Title, p.TaxOffice, p.Address, p.City, "TR", p.Email);
}
