namespace EasyTolet.AdminService.Domain;

public sealed class Role
{
    public int RoleId { get; set; }
    public string RoleCode { get; set; } = "";
    public string RoleName { get; set; } = "";
    public int? ParentRoleId { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public int? CreatedBy { get; set; }
    public int? UpdatedBy { get; set; }
    public Role? ParentRole { get; set; }
    public ICollection<Role> Children { get; set; } = new List<Role>();
}
