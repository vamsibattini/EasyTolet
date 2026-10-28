namespace EasyTolet.AdminService.Domain;

public sealed class AdminOutboxMessage
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Payload { get; set; } = "";
    public DateTime? PublishedAtUtc { get; set; }
}
