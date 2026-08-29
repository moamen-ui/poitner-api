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
        public const string TokenRequired = "Reset token is required.";
    }

    public static class User
    {
        public const string NotFound = "User not found.";
        public const string EmailTaken = "Email already in use.";
        public const string EmailRequired = "Email is required.";
        public const string PasswordRequired = "Password is required.";
        public const string PasswordWeak = "Password must be at least 8 characters.";
        public const string DisplayNameRequired = "Display name is required.";
        public const string TargetWorkspaceRequired = "Select which workspace to add this deputy to.";
        public const string WorkspaceNotFound = "The selected workspace does not exist.";
        public const string CannotDeleteSelf = "You cannot delete your own account.";
        public const string CannotDeleteAdmin = "The workspace admin cannot be deleted directly — promote a deputy to replace them first, or remove the whole workspace instead.";
        public const string CannotDeleteDeputy = "Deputies cannot remove other deputies — only the workspace admin or a super admin can.";
        public const string DeleteNotAuthorized = "You are not authorized to delete this user.";
        public const string NotADeputy = "Only an existing deputy can be promoted to workspace admin.";
        public const string TransferNotAuthorized = "Only the current workspace admin or a super admin can transfer ownership.";
        public const string CurrentPasswordIncorrect = "Current password is incorrect.";
        public const string PasswordChanged = "Password changed.";
    }

    public static class Project
    {
        public const string NotFound = "Project not found.";
        public const string KeyTaken = "Project key already exists.";
        public const string KeyRequired = "Project key is required.";
        public const string KeyInvalidFormat = "Project key must contain only lowercase letters, numbers and dashes (-).";
        public const string Disabled = "This project has been disabled.";
        public const string KeyAmbiguous = "This project key matches more than one workspace. Please contact your workspace administrator.";
        public const string SuperAdminNotAllowed = "Super admins cannot create projects. Sign in with a tenant account to use Pointer.";
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

    public static class Status
    {
        public const string LabelRequired = "Label must not be empty.";
        public const string ColorInvalidFormat = "Color must be a valid hex color (e.g. #0ea5e9).";
        public const string OrderInvalid = "Order must be 0 or greater.";
    }

    public static class Branding
    {
        public const string PrimaryColorInvalidFormat = "Primary color must be a valid hex color (e.g. #2563eb or #fff).";
        public const string UrlAppInvalidFormat = "App URL must be an http(s) URL.";
        public const string UrlDemoInvalidFormat = "Demo URL must be an http(s) URL.";
        public const string UrlDocsInvalidFormat = "Docs URL must be an http(s) URL.";
        public const string UrlLandingInvalidFormat = "Landing URL must be an http(s) URL.";
    }

    public static class Comment
    {
        public const string NotFound = "Comment not found.";
        public const string BodyRequired = "Comment body is required.";
        public const string Created = "Comment created.";
        public const string Applied = "Comment marked applied.";
        public const string InvalidPredefinedAction = "The selected action is unavailable. Please refresh and try again.";
        public const string StatusInvalid = "Invalid comment status.";
        public const string SuperAdminNotAllowed = "Super admins cannot leave comments. Sign in with a tenant account to use Pointer.";
    }

    public static class PredefinedAction
    {
        public const string NotFound = "Action not found.";
        public const string TextRequired = "Action text is required.";
        public const string PromptRequired = "Action prompt is required.";
        public const string SuperAdminNotAllowed = "Super admins cannot create predefined actions. Sign in with a tenant account to use Pointer.";
    }

    public static class Suggestion
    {
        public const string NotFound = "Suggestion not found.";
        public const string TextRequired = "Suggestion text is required.";
        public const string PromptRequired = "Suggestion prompt is required.";
        public const string CanEditDirectly = "You can edit this project — add the predefined action directly instead of suggesting it.";
        public const string ProjectUnavailable = "The target project is no longer available.";
        public const string NotAvailableForProject = "Suggestions are not available for this project.";
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

    public static class Plan
    {
        public const string NotFound = "Plan not found.";
        public const string SlugTaken = "A plan with this slug already exists.";
        public const string NameTaken = "A plan with this name already exists.";
        public const string NameRequired = "Plan name is required.";
        public const string SlugRequired = "Plan slug is required.";
        public const string UnknownEntitlement = "Unknown entitlement key.";
        public const string InvalidEntitlementValue = "Invalid value for an entitlement key.";
        public const string CannotDeleteFree = "The Free plan is the fallback and cannot be deleted.";
        public const string InUse = "This plan has active subscriptions — move those tenants to another plan first.";
        public const string LimitReached = "You've reached your plan's limit. Upgrade to add more.";
        public const string ExtensionDisabled = "The browser extension is not enabled on your plan.";
        public const string Created = "Plan created.";
        public const string Updated = "Plan updated.";
        public const string Deleted = "Plan deleted.";
        public const string SubscriptionUpdated = "Subscription updated.";
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
