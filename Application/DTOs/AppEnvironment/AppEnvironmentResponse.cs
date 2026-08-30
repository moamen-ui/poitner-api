namespace Pointer.Application.DTOs.AppEnvironment;

public class AppEnvironmentResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>True for the super-admin-seeded catalog (OwnerId == null) — visible to every tenant.</summary>
    public bool IsGlobal { get; set; }

    /// <summary>Whether the caller may rename/delete this environment: super admin manages the
    /// global catalog, a tenant manages only its own.</summary>
    public bool CanManage { get; set; }
}
