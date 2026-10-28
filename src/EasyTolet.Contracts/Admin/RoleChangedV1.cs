namespace EasyTolet.Contracts.Admin;

// Shared integration contract only: never share EF entities across services.
public sealed record RoleChangedV1(Guid EventId, long Sequence, DateTime OccurredAtUtc,
    string Operation, int? ActorId, RoleSnapshot Role);

public sealed record RoleSnapshot(int RoleId, string RoleCode, string RoleName, int? ParentRoleId,
    string? Description, bool IsActive, DateTime CreatedDate, DateTime? UpdatedDate,
    int? CreatedBy, int? UpdatedBy);
