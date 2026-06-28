namespace Pointer.Application.DTOs.Profile;

public class UserProfileResponse
{
    public ProfileUser User { get; set; } = new();
    public ProfileTotals Totals { get; set; } = new();
    public List<ProfileProject> Projects { get; set; } = new();
}

public class ProfileUser
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
}

public class ProfileCounts
{
    public int Comments { get; set; }
    public int Replies { get; set; }
    public int Open { get; set; }
    public int ReadyToApply { get; set; }
    public int Applied { get; set; }
    public int Archived { get; set; }
}

public class ProfileTotals : ProfileCounts
{
    public int ProjectsInvolved { get; set; }
}

public class ProfileEnvironment : ProfileCounts
{
    public int Environment { get; set; } // EnvironmentTag int
}

public class ProfileProject : ProfileCounts
{
    public int ProjectId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public List<ProfileEnvironment> Environments { get; set; } = new();
}
