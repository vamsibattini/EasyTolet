namespace EasyTolet.Contracts.Notifications;

/// <summary>Internal broker command. NotificationService alone renders templates and contacts providers.</summary>
public sealed record NotificationRequestedV1(
    Guid NotificationId,
    string Source,
    string Channel,
    string Recipient,
    string Template,
    Dictionary<string, string> Data,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc);

public static class NotificationQueues
{
    public const string Delivery = "easytolet-notifications-v1";
}
