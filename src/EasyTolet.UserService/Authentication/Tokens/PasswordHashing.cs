using Microsoft.AspNetCore.Identity;

namespace EasyTolet.UserService.Authentication.Tokens;

public static class PasswordHashing
{
    // ASP.NET Core's versioned PBKDF2 hash format includes its random salt and work factor.
    private static readonly PasswordHasher<object> Hasher = new();
    private static readonly object User = new();
    private static readonly string DummyHash = Hasher.HashPassword(User, Guid.NewGuid().ToString("N"));
    public static string Hash(string password) => Hasher.HashPassword(User, password);
    public static bool Verify(string? hash, string password)
    {
        try
        {
            var result = Hasher.VerifyHashedPassword(User, hash ?? DummyHash, password);
            return hash is not null && result != PasswordVerificationResult.Failed;
        }
        catch (FormatException) { return false; }
    }
}
