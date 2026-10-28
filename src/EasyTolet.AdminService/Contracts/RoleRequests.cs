using System.ComponentModel.DataAnnotations;

namespace EasyTolet.AdminService.Contracts;

public sealed class SaveRoleRequest
{
    [Required, StringLength(10), RegularExpression("^[A-Za-z0-9_-]+$")]
    public string RoleCode { get; set; } = "";
    [Required, StringLength(100)]
    public string RoleName { get; set; } = "";
    [Range(1, int.MaxValue)]
    public int? ParentRoleId { get; set; }
    [StringLength(500)]
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
