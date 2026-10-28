using System.ComponentModel.DataAnnotations;

namespace EasyTolet.UserService.Contracts;

public class RegisterUserRequest
{
    // Required for ordinary registration; optional for SSO and profile updates. Never persisted verbatim.
    [StringLength(128, MinimumLength = 12)]
    public string? Password { get; set; }

    [Range(1, int.MaxValue)]
    public int RoleId { get; set; }
    [Required, StringLength(150)]
    public string FirstName { get; set; } = string.Empty;
    [StringLength(150)]
    public string? LastName { get; set; }
    [Required, StringLength(500)]
    public string Address1 { get; set; } = string.Empty;
    [StringLength(500)]
    public string? Address2 { get; set; }
    [StringLength(500)]
    public string? Address3 { get; set; }
    [Required, StringLength(100)]
    public string Country { get; set; } = string.Empty;
    [Required, StringLength(150)]
    public string State { get; set; } = string.Empty;
    [Required, StringLength(150)]
    public string City { get; set; } = string.Empty;
    [Required, StringLength(20)]
    public string PinNumber { get; set; } = string.Empty;
    [Required, StringLength(30)]
    public string MobileNumber { get; set; } = string.Empty;
    [StringLength(30)]
    public string? AlternateMobileNumber { get; set; }
    [Required, StringLength(320), EmailAddress]
    public string EmailId { get; set; } = string.Empty;
}

// Full profile update; nullable address/contact fields are cleared when omitted.
public sealed class SaveUserRequest : RegisterUserRequest
{
    public bool IsActive { get; set; } = true;
}
