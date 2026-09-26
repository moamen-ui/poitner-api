using Pointer.Application.Resources;

namespace Pointer.Application.Common;

/// <summary>
/// DB-19 §3.4 task 6: the workspace-name rule in one place — the rename path
/// (<c>WorkspaceService.RenameAsync</c>) and the signed-in new-workspace path
/// (<c>WorkspaceCreationService</c>) validate identically. Same DB check:
/// <c>ck_workspaces_name_not_blank</c> (WorkspaceMapping).
/// </summary>
public static class WorkspaceNameRules
{
    public const int MaxLength = 120;

    /// <summary>
    /// Trims <paramref name="raw"/> into <paramref name="trimmed"/> and returns the failure
    /// message key, or <c>null</c> when the name is valid: <see cref="MessageKeys.Workspace.NameRequired"/>,
    /// <see cref="MessageKeys.Workspace.NameTooLong"/>, <see cref="MessageKeys.Workspace.NameInvalid"/>.
    /// </summary>
    public static string? Validate(string? raw, out string trimmed)
    {
        trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return MessageKeys.Workspace.NameRequired;
        if (trimmed.Length > MaxLength)
            return MessageKeys.Workspace.NameTooLong;
        if (trimmed.Any(char.IsControl))
            return MessageKeys.Workspace.NameInvalid;
        return null;
    }
}
