namespace Pointer.Application.Resources;

public static class MessageKeys
{
    public static class Auth
    {
        public const string InvalidCredentials = "Invalid email or password.";
        public const string Inactive = "Account is disabled.";
        public const string PendingApproval = "Your request is awaiting admin approval.";
        public const string Rejected = "Your request was rejected.";
        public const string Disabled = "Your account is disabled.";
        public const string RegistrationSubmitted = "Request submitted for approval.";
        public const string AccountExists = "An account with this email already exists.";
    }

    public static class User
    {
        public const string NotFound = "User not found.";
        public const string EmailTaken = "Email already in use.";
        public const string EmailRequired = "Email is required.";
        public const string PasswordRequired = "Password is required.";
        public const string PasswordWeak = "Password must be at least 8 characters.";
        public const string DisplayNameRequired = "Display name is required.";
    }

    public static class Project
    {
        public const string NotFound = "Project not found.";
        public const string KeyTaken = "Project key already exists.";
        public const string KeyRequired = "Project key is required.";
        public const string Disabled = "This project has been disabled.";
    }

    public static class Role
    {
        public const string NotFound = "Role not found.";
        public const string NameTaken = "A role with this name already exists.";
        public const string NameRequired = "Role name is required.";
        public const string SystemImmutable = "System roles cannot be modified or disabled.";
        public const string Invalid = "The selected role does not exist or is inactive.";
        public const string HasUsers = "This role has assigned users — choose another role to move them to.";
        public const string ReassignSame = "The reassignment role must be different from the role being deleted.";
        public const string EscalationNotAllowed = "Only a super admin may assign or approve users with an admin-tier role.";
    }

    public static class Preferences
    {
        public const string Invalid = "Invalid preference value.";
        public const string NotFound = "User not found.";
    }

    public static class Comment
    {
        public const string NotFound = "Comment not found.";
        public const string BodyRequired = "Comment body is required.";
        public const string Created = "Comment created.";
        public const string Applied = "Comment marked applied.";
        public const string InvalidPredefinedAction = "The selected action is unavailable. Please refresh and try again.";
    }

    public static class PredefinedAction
    {
        public const string NotFound = "Action not found.";
        public const string TextRequired = "Action text is required.";
        public const string PromptRequired = "Action prompt is required.";
    }

    public static class Suggestion
    {
        public const string NotFound = "Suggestion not found.";
        public const string TextRequired = "Suggestion text is required.";
        public const string PromptRequired = "Suggestion prompt is required.";
        public const string CanEditDirectly = "You can edit this project — add the predefined action directly instead of suggesting it.";
        public const string ProjectUnavailable = "The target project is no longer available.";
        public const string Created = "Suggestion sent for admin review.";
        public const string Approved = "Suggestion approved.";
        public const string Rejected = "Suggestion rejected.";
    }

    public static class Project_Delete
    {
        public const string HasComments = "This project has comments — only an admin can delete it.";
        public const string NotOwner = "You can only delete your own projects.";
    }

    public static class Invite
    {
        public const string NotFound = "Invite not found.";
        public const string Invalid = "This invite link is invalid or has expired.";
        public const string Expired = "This invite link has expired.";
        public const string Revoked = "This invite link has been revoked.";
        public const string UsedUp = "This invite link has reached its usage limit.";
        public const string EmailMismatch = "This invite is locked to a different email address.";
        public const string Forbidden = "You are not allowed to create invites.";
        public const string Created = "Invite created.";
        public const string Revoked_Ok = "Invite revoked.";
    }

    public static class Demo
    {
        public const string NotDemoUser = "This account is not a demo account.";
        public const string AlreadyUpgraded = "This demo has already been upgraded to a permanent account.";
        public const string DemoExpired = "This demo session has expired. Please start a new demo.";
        public const string EmailTaken = "That email is already registered.";
        public const string UpgradeSuccess = "Your workspace has been upgraded. Welcome to Pointer!";
    }

    public static class ExportImport
    {
        public const string Exported = "Export ready.";
        public const string Imported = "Import complete.";
        public const string UnsupportedSchemaVersion = "Unsupported export schema version.";
        public const string InvalidJson = "Invalid export file.";
        public const string TooManyComments = "Too many comments in a single import.";
        public const string FileTooLarge = "Export file too large.";
        public const string MissingCommentsArray = "Missing or invalid comments array.";
    }
}
