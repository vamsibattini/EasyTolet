using System.Net;
using System.Net.Mail;
using System.Text.Json;
using EasyTolet.Contracts.Notifications;
using EasyTolet.NotificationService.Notifications;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EasyTolet.NotificationService.Tests.Notifications;

public sealed class NotificationDeliveryTests
{
    public static NotificationRequestedV1 Message(string channel = "email", string template = "account.reset") => new(
        Guid.NewGuid(), "UserService", channel, channel == "sms" ? "+919876543210" : channel == "push" ? "device-token" : "recipient@example.com",
        template, template == "message" ? new() { ["subject"] = "Booking update", ["body"] = "Your booking was confirmed." } : new() { ["token"] = new string('a', 43) },
        DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15));
    public static IConfiguration Config(bool sms = false, bool push = false) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Notifications:Email:Host"] = "smtp.gmail.com", ["Notifications:Email:Port"] = "587",
        ["Notifications:Email:Username"] = "vamsib508@gmail.com", ["Notifications:Email:FromAddress"] = "vamsib508@gmail.com",
        ["Notifications:Sms:Enabled"] = sms.ToString(), ["Notifications:Push:Enabled"] = push.ToString(),
        ["Notifications:Sms:Endpoint"] = "https://sms.example.test/send", ["Notifications:Sms:ApiKey"] = "test-secret",
        ["Notifications:Push:Endpoint"] = "https://push.example.test/send", ["Notifications:Push:ApiKey"] = "test-secret"
    }).Build();

    [Theory]
    [InlineData("account.reset", "Reset your EasyTolet password")]
    [InlineData("account.email", "Verify your EasyTolet email address")]
    [InlineData("account.delete", "Confirm your EasyTolet account deletion")]
    public async Task EmailTemplateUsesConfiguredSenderRequestedRecipientAndSingleUseToken(string template, string subject)
    {
        var configuration = Config(); var email = new CaptureEmail(); var handler = new Handler(); using var client = new HttpClient(handler);
        var sender = new NotificationSender(email, new(client, configuration), configuration, TimeProvider.System);
        var message = Message(template: template);
        await sender.SendAsync(message, default);
        Assert.Equal("vamsib508@gmail.com", email.From); Assert.Equal(message.Recipient, email.To);
        Assert.Equal(subject, email.Subject); Assert.Contains(message.Data["token"], email.Body);
        Assert.Contains("UTC", email.Body); Assert.Contains(message.NotificationId.ToString("D"), email.MessageId); Assert.Null(handler.Body);
    }

    [Theory]
    [InlineData("sms", "account.phone")]
    [InlineData("push", "message")]
    public async Task EnabledSmsAndPushUseAuthenticatedProviderAndStableIdempotencyKey(string channel, string template)
    {
        var configuration = Config(true, true); var email = new CaptureEmail(); var handler = new Handler(); using var client = new HttpClient(handler);
        var sender = new NotificationSender(email, new(client, configuration), configuration, TimeProvider.System);
        var message = Message(channel, template);
        await sender.SendAsync(message, default);
        Assert.Null(email.To); Assert.Equal("Bearer test-secret", handler.Authorization);
        Assert.Equal(message.NotificationId.ToString("D"), handler.Idempotency);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(message.Recipient, json.RootElement.GetProperty("recipient").GetString());
        Assert.Equal(channel, json.RootElement.GetProperty("channel").GetString());
        Assert.Equal(NotificationTemplates.Render(message).Body, json.RootElement.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData("sms", "account.phone")]
    [InlineData("push", "message")]
    public async Task DisabledProvidersNeverMakeNetworkRequests(string channel, string template)
    {
        var config = Config(); var handler = new Handler(); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ChannelDisabledException>(() => new NotificationSender(new CaptureEmail(), new(client, config), config, TimeProvider.System).SendAsync(Message(channel, template), default));
        Assert.Null(handler.Body);
    }

    [Theory]
    [InlineData("recipient")]
    [InlineData("channel")]
    [InlineData("purpose")]
    [InlineData("token")]
    [InlineData("expired")]
    [InlineData("header")]
    [InlineData("data")]
    public async Task InvalidOrExpiredMessagesNeverReachProvider(string invalid)
    {
        var message = Message();
        message = invalid switch
        {
            "recipient" => message with { Recipient = "invalid\r\nBcc: other@example.com" },
            "channel" => message with { Channel = "other" }, "purpose" => message with { Template = "other" },
            "token" => message with { Data = new() { ["token"] = "bad" } },
            "expired" => message with { CreatedAtUtc = DateTime.UtcNow.AddHours(-1), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) },
            "header" => message with { Template = "message", Data = new() { ["subject"] = "bad\r\nheader", ["body"] = "text" } },
            _ => message with { Data = null! }
        };
        var config = Config(); var email = new CaptureEmail(); var handler = new Handler(); using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new NotificationSender(email, new(client, config), config, TimeProvider.System).SendAsync(message, default));
        Assert.Null(email.To); Assert.Null(handler.Body);
    }

    [Fact]
    public async Task MissingSmtpSecretFailsBeforeNetworkAccess()
    {
        using var mail = new MailMessage("sender@example.com", "recipient@example.com", "test", "test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SmtpEmailTransport(Config()).SendAsync(mail, default));
    }

    [Theory]
    [InlineData("http://sms.example.test")]
    [InlineData("https://user:secret@sms.example.test")]
    [InlineData("")]
    public async Task InvalidProviderConfigurationNeverSends(string endpoint)
    {
        var config = Config(true); config["Notifications:Sms:Endpoint"] = endpoint;
        var handler = new Handler(); using var client = new HttpClient(handler);
        var message = Message("sms", "account.phone");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HttpNotificationTransport(client, config).SendAsync(message, NotificationTemplates.Render(message), default));
        Assert.Null(handler.Body);
    }

    [Fact]
    public async Task ProviderFailureDoesNotExposeProviderResponse()
    {
        var config = Config(true); var handler = new Handler { Status = HttpStatusCode.BadGateway }; using var client = new HttpClient(handler);
        var message = Message("sms", "account.phone");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new HttpNotificationTransport(client, config).SendAsync(message, NotificationTemplates.Render(message), default));
        Assert.Equal("Notification provider delivery failed.", error.Message);
    }

    private sealed class CaptureEmail : IEmailTransport
    {
        public string? From, To, Subject, Body, MessageId;
        public Task SendAsync(MailMessage message, CancellationToken ct)
        {
            From = message.From!.Address; To = Assert.Single(message.To).Address; Subject = message.Subject; Body = message.Body;
            MessageId = message.Headers["Message-ID"]; Assert.Empty(message.CC); Assert.Empty(message.Bcc); Assert.False(message.IsBodyHtml);
            return Task.CompletedTask;
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string? Body, Authorization, Idempotency;
        public HttpStatusCode Status = HttpStatusCode.Accepted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization = request.Headers.Authorization?.ToString(); Idempotency = request.Headers.GetValues("Idempotency-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(Status) { Content = new StringContent("private provider response") };
        }
    }
}
