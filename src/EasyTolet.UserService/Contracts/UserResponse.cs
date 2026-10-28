namespace EasyTolet.UserService.Contracts;

public sealed class UserResponse
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public string? RoleCode { get; set; }
    public string? RoleName { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string Address1 { get; set; } = string.Empty;
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string Country { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PinNumber { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? AlternateMobileNumber { get; set; }
    public string EmailId { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public DateTime? UpdatedDate { get; set; }
    public long? CreatedBy { get; set; }
    public long? UpdatedBy { get; set; }
    public bool IsActive { get; set; }
}
