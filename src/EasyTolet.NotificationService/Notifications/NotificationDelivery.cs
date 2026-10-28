using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using EasyTolet.Contracts.Notifications;

namespace EasyTolet.NotificationService.Notifications;

public interface IEmailTransport { Task SendAsync(MailMessage message, CancellationToken ct); }
public interface INotificationSender { Task SendAsync(NotificationRequestedV1 message, CancellationToken ct); }
public sealed class ChannelDisabledException() : Exception("The notification channel is disabled.");

public sealed class SmtpEmailTransport(IConfiguration configuration) : IEmailTransport
{
    public async Task SendAsync(MailMessage message, CancellationToken ct)
    {
        var host = configuration["Notifications:Email:Host"];
        var username = configuration["Notifications:Email:Username"];
        var password = configuration["Notifications:Email:Password"];
        var port = configuration.GetValue<int?>("Notifications:Email:Port") ?? 587;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || port is <= 0 or > 65535)
            throw new InvalidOperationException("Email delivery requires SMTP configuration and a password secret.");
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = true, UseDefaultCredentials = false, Credentials = new NetworkCredential(username, password),
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await client.SendMailAsync(message, timeout.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is SmtpException or OperationCanceledException)
        { throw new InvalidOperationException("Notification email delivery failed."); }
    }
}

/// <summary>Provider adapters accept rendered text and use the notification ID for idempotency.</summary>
public sealed class HttpNotificationTransport(HttpClient client, IConfiguration configuration)
{
    public async Task SendAsync(NotificationRequestedV1 message, NotificationContent content, CancellationToken ct)
    {
        var section = message.Channel switch { "sms" => "Sms", "push" => "Push", _ => throw new ArgumentException("Invalid HTTP notification channel.") };
        if (!configuration.GetValue<bool>($"Notifications:{section}:Enabled")) throw new ChannelDisabledException();
        var endpoint = configuration[$"Notifications:{section}:Endpoint"];
        var key = configuration[$"Notifications:{section}:ApiKey"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Notification provider requires an HTTPS endpoint and API key.");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new { message.NotificationId, message.Channel, message.Recipient, content.Subject, content.Body, message.ExpiresAtUtc })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("Idempotency-Key", message.NotificationId.ToString("D"));
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Notification provider delivery failed.");
    }
}

public sealed class NotificationSender(IEmailTransport email, HttpNotificationTransport http,
    IConfiguration configuration, TimeProvider clock) : INotificationSender
{
    public async Task SendAsync(NotificationRequestedV1 message, CancellationToken ct)
    {
        NotificationTemplates.Validate(message);
        if (message.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime) throw new InvalidOperationException("Notification has expired.");
        var content = NotificationTemplates.Render(message);
        if (message.Channel != "email") { await http.SendAsync(message, content, ct); return; }
        var address = configuration["Notifications:Email:FromAddress"];
        if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("An email sender address is required.");
        using var mail = new MailMessage
        {
            From = new MailAddress(address, configuration["Notifications:Email:FromName"] ?? "EasyTolet"),
            Subject = content.Subject, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false, Body = content.Body
        };
        mail.To.Add(new MailAddress(message.Recipient));
        mail.Headers.Add("Message-ID", $"<{message.NotificationId:D}@{mail.From.Host}>");
        await email.SendAsync(mail, ct);
    }
}
