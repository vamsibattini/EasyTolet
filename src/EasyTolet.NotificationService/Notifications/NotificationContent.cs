using System.Net.Mail;
using System.Text.RegularExpressions;
using EasyTolet.Contracts.Notifications;

namespace EasyTolet.NotificationService.Notifications;

public sealed record NotificationContent(string Subject, string Body);

public static class NotificationTemplates
{
    public static void Validate(NotificationRequestedV1 message)
    {
        if (message.NotificationId == Guid.Empty || string.IsNullOrWhiteSpace(message.Source) || message.Source.Length > 80 ||
            message.Channel is not ("email" or "sms" or "push") || string.IsNullOrWhiteSpace(message.Recipient) || message.Recipient.Length > 4096 ||
            message.Data is null || message.Data.Count > 10 || message.Data.Any(pair => string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 80 || pair.Value is null || pair.Value.Length > 8000) ||
            message.CreatedAtUtc.Kind != DateTimeKind.Utc || message.ExpiresAtUtc.Kind != DateTimeKind.Utc ||
            message.ExpiresAtUtc <= message.CreatedAtUtc || message.ExpiresAtUtc - message.CreatedAtUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Invalid notification command.");
        if (message.Channel == "email" && (!MailAddress.TryCreate(message.Recipient, out var address) || address.Address != message.Recipient || message.Recipient.Length > 320))
            throw new ArgumentException("Invalid notification recipient.");
        if (message.Channel == "sms" && !Regex.IsMatch(message.Recipient, @"\A\+[1-9][0-9]{7,14}\z"))
            throw new ArgumentException("Invalid notification recipient.");
        _ = Render(message);
    }

    public static NotificationContent Render(NotificationRequestedV1 message)
    {
        if (message.Template == "message")
        {
            var subject = Required(message, "subject");
            var body = Required(message, "body");
            if (subject.Length > 200 || subject.Contains('\r') || subject.Contains('\n')) throw new ArgumentException("Invalid notification subject.");
            return new(subject, body);
        }
        var action = message.Template switch
        {
            "account.reset" when message.Channel == "email" => "Reset your EasyTolet password",
            "account.email" when message.Channel == "email" => "Verify your EasyTolet email address",
            "account.delete" when message.Channel == "email" => "Confirm your EasyTolet account deletion",
            "account.phone" when message.Channel == "sms" => "Verify your EasyTolet phone number",
            _ => throw new ArgumentException("Unsupported notification template or channel.")
        };
        var token = Required(message, "token");
        if (!Regex.IsMatch(token, @"\A[A-Za-z0-9_-]{43}\z")) throw new ArgumentException("Invalid confirmation token format.");
        return new(action, $"{action}\n\nYour single-use confirmation token is:\n{token}\n\n" +
            $"Enter this token in the matching EasyTolet account flow. It expires at {message.ExpiresAtUtc:yyyy-MM-dd HH:mm:ss} UTC.\n\n" +
            "If you did not request this, ignore this message. Never share this token with anyone.");
    }

    private static string Required(NotificationRequestedV1 message, string key) =>
        message.Data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("Missing notification template data.");
}
