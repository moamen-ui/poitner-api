namespace Pointer.Application.Resources;

public static class MessageKeys
{
    public static class Auth
    {
        public const string InvalidCredentials = "Invalid email or password.";
        public const string Inactive = "Account is disabled.";
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
    }

    public static class Comment
    {
        public const string NotFound = "Comment not found.";
        public const string BodyRequired = "Comment body is required.";
        public const string Created = "Comment created.";
        public const string Applied = "Comment marked applied.";
    }
}
