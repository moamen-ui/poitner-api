namespace Pointer.Infrastructure.Auth;

public class LoginLockoutOptions
{
    public const string Section = "Auth:LoginLockout";
    public int Threshold { get; set; } = 10;
    public int WindowMinutes { get; set; } = 15;
}
