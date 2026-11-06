using System.Linq.Expressions;
using EnterprisePulse.Core.Common;
using EnterprisePulse.Core.Data;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace EnterprisePulse.Core.Modules.Platform;

/// <summary>Durable background work (Hangfire in the API host).</summary>
public interface IBackgroundJobs
{
    void Enqueue<T>(Expression<Func<T, Task>> job) where T : notnull;
}

/// <summary>Real-time delivery to connected browsers (SignalR in the API host).</summary>
public interface INotificationPusher
{
    Task PushAsync(IEnumerable<Guid> userIds, string eventName, object payload);
}

public sealed class NullPusher : INotificationPusher
{
    public Task PushAsync(IEnumerable<Guid> userIds, string eventName, object payload) => Task.CompletedTask;
}

public sealed class MailService(IOptions<AppOptions> options, ILogger<MailService> logger)
{
    public async Task SendAsync(string to, string subject, string html)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(o.MailFrom));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = Layout(subject, html) };
        using var client = new SmtpClient();
        await client.ConnectAsync(o.MailHost, o.MailPort, MailKit.Security.SecureSocketOptions.None);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
        logger.LogInformation("Mail sent to {To}: {Subject}", to, subject);
    }

    private static string Layout(string title, string body) => $"""
        <div style="font-family:Segoe UI,Arial,sans-serif;max-width:560px;margin:auto">
          <h2 style="color:#4338ca">EnterprisePulse</h2>
          <h3>{System.Net.WebUtility.HtmlEncode(title)}</h3>
          <div>{body}</div>
        </div>
        """;
}

public sealed class MailQueue(IBackgroundJobs jobs)
{
    public void Send(string to, string subject, string html) =>
        jobs.Enqueue<MailService>(m => m.SendAsync(to, subject, html));
}

public sealed record NotifyRequest(
    IReadOnlyCollection<Guid> UserIds,
    Guid? CompanyId,
    string Type,
    string Title,
    string Body,
    string? Link = null,
    bool Email = false);

public sealed class NotificationService(AppDbContext db, INotificationPusher pusher, MailQueue mail, IOptions<AppOptions> options)
{
    public async Task NotifyAsync(NotifyRequest request)
    {
        var ids = request.UserIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return;
        var rows = ids.Select(id => new Notification
        {
            CompanyId = request.CompanyId,
            UserId = id,
            Type = request.Type,
            Title = request.Title,
            Body = request.Body,
            Link = request.Link,
        }).ToList();
        db.Notifications.AddRange(rows);
        await db.SaveChangesAsync();

        foreach (var row in rows) await pusher.PushAsync([row.UserId], "notification", row);

        if (request.Email)
        {
            var emails = await db.Users.Where(u => ids.Contains(u.Id) && u.IsActive).Select(u => u.Email).ToListAsync();
            var link = request.Link == null ? "" : $"<p><a href=\"{options.Value.WebAppUrl}{request.Link}\">Kaydı aç</a></p>";
            foreach (var email in emails) mail.Send(email, request.Title, $"<p>{System.Net.WebUtility.HtmlEncode(request.Body)}</p>{link}");
        }
    }

    /// <summary>Active users holding a permission in a company (role based), plus super admins excluded.</summary>
    public async Task<List<Guid>> UsersWithPermissionAsync(Guid companyId, string code)
    {
        return await db.UserRoles
            .Where(ur => ur.CompanyId == companyId && ur.User.IsActive && ur.User.DeletedAt == null
                && ur.Role.Permissions.Any(rp => rp.Permission.Code == code))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync();
    }
}
