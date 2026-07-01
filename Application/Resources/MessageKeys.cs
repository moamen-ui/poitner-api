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
