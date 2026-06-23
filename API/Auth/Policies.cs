namespace Pointer.API.Auth;

public static class Policies
{
    /// <summary>Requires the user's role to grant admin (claim <c>is_admin == "true"</c>).</summary>
    public const string Admin = "Admin";
}
