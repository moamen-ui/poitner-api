namespace Pointer.Application.Resources;

public static class MessageKeys
{
    public static class Auth
    {
        public const string InvalidCredentials = "Invalid email or password.";
        public const string InvalidApiKey = "Invalid or revoked API key.";
        public const string Inactive = "Account is disabled.";
        public const string PendingApproval = "Your request is awaiting admin approval.";
        public const string Rejected = "Your request was rejected.";
        public const string Disabled = "Your account is disabled.";
        public const string RegistrationSubmitted = "Request submitted for approval.";
        public const string AccountExists = "An account with this email already exists.";
        public const string TokenRequired = "Reset token is required.";
        public const string TooManyAttempts =
            "Too many failed login attempts. Please try again later.";

        /// <summary>DB-11b: no live workspace membership at all.</summary>
        public const string NoWorkspace = "Your account is not a member of any workspace.";

        /// <summary>DB-11b: several approved, active memberships and no (or ambiguous) projectKey routing — the client must show the picker.</summary>
        public const string ChooseWorkspace = "Choose which workspace to open.";

        /// <summary>DB-11b: switch-workspace target is not a live, approved, active membership of the caller.</summary>
        public const string NotAMember = "You are not an active member of that workspace.";

        /// <summary>
        /// GLM A8: a wrong password against an identity that absorbed another (merged, DB-11a) row —
        /// points the person at "Forgot password" rather than the generic InvalidCredentials.
        /// </summary>
        public const string InvalidCredentialsAfterMerge =
            "Invalid email or password. Accounts that shared this e-mail were combined into one — if your previous password no longer works, use \"Forgot password\".";

        /// <summary>DB-14: an unverified identity hit an admin-only (non-GET, /api/admin/*) action.</summary>
        public const string EmailNotVerified = "Verify your e-mail address to do this — check your inbox or resend the link from your profile.";

        /// <summary>DB-14: POST /api/me/verification/resend succeeded.</summary>
        public const string VerificationSent = "We've e-mailed you a verification link. It expires in 30 minutes.";

        /// <summary>DB-14: a resend was requested within 5 minutes of the last one.</summary>
        public const string VerificationRecentlySent = "A verification link was sent a moment ago — check your inbox (and spam) before requesting another.";

        /// <summary>DB-14: resend requested for an already-verified identity.</summary>
        public const string AlreadyVerified = "Your e-mail address is already verified.";

        /// <summary>DB-14: resend requested for a demo/passwordless/super-admin identity.</summary>
        public const string VerificationNotApplicable = "This account does not need e-mail verification.";

        /// <summary>DB-14: POST /api/auth/verify-email failed — one message for every failure (enumeration resistance).</summary>
        public const string VerificationLinkInvalid = "This verification link is invalid or has expired — request a new one from your profile.";

        /// <summary>DB-14: POST /api/auth/verify-email succeeded.</summary>
        public const string EmailVerified = "Your e-mail address is verified.";
    }

    public static class DeviceLogin
    {
        public const string NotFound = "This code is invalid or has expired.";
        public const string AlreadyDecided = "This code has already been approved or denied.";
        public const string SuperAdminNotAllowed =
            "Super admins cannot sign in the CLI. Sign in with a tenant account instead.";
    }

    public static class User
    {
        public const string NotFound = "User not found.";
        public const string EmailTaken = "Email already in use.";
        public const string EmailRequired = "Email is required.";
        public const string PasswordRequired = "Password is required.";
        public const string PasswordWeak = "Password must be at least 10 characters.";

        /// <summary>DB-14 D14.4: the password policy's upper bound (128 chars).</summary>
        public const string PasswordTooLong = "Password must be 128 characters or fewer.";

        /// <summary>DB-14 D14.4: an exact (case-insensitive) match against the embedded top-1000 list.</summary>
        public const string PasswordCommon = "That password is too common — choose something less guessable.";

        /// <summary>DB-14 D14.4: the password equals the e-mail address or its local part (case-insensitive).</summary>
        public const string PasswordIsEmail = "Your password must not be your e-mail address.";
        public const string DisplayNameRequired = "Display name is required.";
        public const string TargetWorkspaceRequired =
            "Select which workspace to add this deputy to.";
        public const string WorkspaceNotFound = "The selected workspace does not exist.";
        /// <summary>DB-11c: renamed from CannotDeleteSelf — removal is now membership-scoped, not a delete.</summary>
        public const string CannotRemoveSelf = "You cannot remove yourself — use Leave workspace instead.";

        /// <summary>Pre-DB-11c name, kept for source compatibility with callers that still reference it.</summary>
        public const string CannotDeleteSelf = "You cannot delete your own account.";
        public const string CannotDeleteAdmin =
            "The workspace admin cannot be deleted directly — promote a deputy to replace them first, or remove the whole workspace instead.";
        public const string CannotDeleteDeputy =
            "Deputies cannot remove other deputies — only the workspace admin or a super admin can.";
        public const string CannotChangeSelfFromAdmin =
            "You cannot change your own role away from Workspace Admin — promote a deputy to replace you first.";
        public const string DeleteNotAuthorized = "You are not authorized to delete this user.";
        public const string NotADeputy =
            "Only an existing deputy can be promoted to workspace admin.";
        public const string TransferNotAuthorized =
            "Only the current workspace admin or a super admin can transfer ownership.";
        public const string CurrentPasswordIncorrect = "Current password is incorrect.";
        public const string PasswordChanged = "Password changed.";

        /// <summary>DB-11a D6: an admin cannot set another member's password once that identity has more than one live membership.</summary>
        public const string PasswordManagedElsewhere =
            "This user also belongs to other workspaces — they must change their password themselves.";

        /// <summary>DB-11a: the target already has a live membership in this workspace.</summary>
        public const string AlreadyMember = "This person is already a member of this workspace.";

        /// <summary>
        /// DB-11c (S-13). Applies to every actor, super admins included — the way out is
        /// TransferOwnershipAsync. {0} = comma-joined workspace names.
        /// </summary>
        public const string SoleAdminBlocked = "Blocked: this person is the only Workspace Admin of {0}. Promote a deputy there first.";

        public const string LeftWorkspace = "You have left the workspace.";
        public const string Erased = "Your account has been deleted.";
        public const string CannotEraseSuperAdmin = "Super-admin accounts cannot be erased here.";
        public const string EraseNeedsEmailConfirmation = "Magic-link accounts confirm deletion by e-mail — request a deletion link first.";
        public const string EraseUsePassword = "Your account has a password — confirm deletion with it instead.";
        public const string EraseLinkSent = "We've e-mailed you a link to confirm deleting your account. It expires in 30 minutes.";
        public const string EraseLinkInvalid = "This deletion link is invalid or has expired — request a new one.";

        /// <summary>DB-11c review finding #1: a pending member cannot reject their own request.</summary>
        public const string CannotRejectSelf = "You cannot reject yourself.";

        /// <summary>DB-11c review finding #1: reject is only meaningful for a still-pending request.</summary>
        public const string NotPending = "This member is not pending approval.";

        /// <summary>
        /// DB-11c review finding #7: a Deputy (non-super-admin) may not remove a Workspace Admin —
        /// only the workspace's own Workspace Admin or a super admin can.
        /// </summary>
        public const string CannotRemoveAdmin = "Only the workspace admin or a super admin can remove a Workspace Admin.";

        /// <summary>DB-11d: magic-link (passwordless) identities have no password to confirm a change with.</summary>
        public const string ChangeEmailNeedsPassword = "Magic-link accounts cannot change their e-mail — ask your workspace admin for a new invite.";

        /// <summary>DB-11d: the operator account's address is configured server-side (ADMIN__EMAIL).</summary>
        public const string ChangeEmailSuperAdmin = "The operator account's e-mail is configured on the server (ADMIN__EMAIL).";

        /// <summary>DB-11d: the requested new address normalises to the caller's current one.</summary>
        public const string EmailUnchanged = "That is already your e-mail address.";

        /// <summary>DB-11d: POST /api/me/change-email succeeded — nothing changes until the link is confirmed.</summary>
        public const string EmailChangeLinkSent = "We've sent a confirmation link to the new address. It expires in 30 minutes; until you confirm, nothing changes.";

        /// <summary>DB-11d: the confirm-email-change token was missing/expired/reused/cancelled — one message for every failure (enumeration resistance).</summary>
        public const string EmailChangeLinkInvalid = "This confirmation link is invalid or has expired — request the change again.";

        /// <summary>DB-11d: POST /api/auth/confirm-email-change succeeded.</summary>
        public const string EmailChanged = "Your e-mail address has been changed. Please sign in again.";
    }

    public static class Workspace
    {
        public const string NotFound = "Workspace not found.";
        public const string NameRequired = "Workspace name is required.";
        public const string NameTooLong = "Workspace name must be 120 characters or fewer.";
        public const string NameInvalid = "Workspace name contains unsupported characters.";
    }

    public static class Project
    {
        public const string NotFound = "Project not found.";
        public const string KeyTaken = "Project key already exists.";
        public const string KeyRequired = "Project key is required.";
        public const string KeyInvalidFormat =
            "Project key must contain only lowercase letters, numbers and dashes (-).";
        public const string Disabled = "This project has been disabled.";
        public const string KeyAmbiguous =
            "This project key matches more than one workspace. Please contact your workspace administrator.";
        public const string SuperAdminNotAllowed =
            "Super admins cannot create projects. Sign in with a tenant account to use Pointer.";
        public const string QuickAccessNotAllowed = "Client accounts cannot manage projects.";
        public const string NoneForOrigin =
            "No project is set up for this site yet. Ask your workspace admin to set the project's App URL.";
        public const string OriginNotAllowed = "Comments are not allowed from this address.";
    }

    public static class Role
    {
        public const string NotFound = "Role not found.";
        public const string NameTaken = "A role with this name already exists.";
        public const string NameRequired = "Role name is required.";
        public const string SystemImmutable = "System roles cannot be modified or disabled.";
        public const string Invalid = "The selected role does not exist or is inactive.";
        public const string HasUsers =
            "This role has assigned users — choose another role to move them to.";
        public const string ReassignSame =
            "The reassignment role must be different from the role being deleted.";
        public const string EscalationNotAllowed =
            "Only a super admin may assign or approve users with an admin-tier role.";
        public const string GlobalRoleToggleOnly =
            "You can only enable or disable a shared role for your workspace — renaming or reconfiguring it is managed by the platform.";
    }

    public static class AppEnvironment
    {
        public const string NotFound = "Environment not found.";
        public const string NameTaken = "An environment with this name already exists.";
        public const string NameRequired = "Environment name is required.";
        public const string NotManageable =
            "You can only rename or delete your own environments — the global catalog is managed by the platform.";
        public const string InUse =
            "This environment has project URLs assigned to it — remove those first.";
        public const string NotEnabled = "This environment is currently disabled.";
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
        public const string PrimaryColorInvalidFormat =
            "Primary color must be a valid hex color (e.g. #2563eb or #fff).";
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
        public const string InvalidPredefinedAction =
            "The selected action is unavailable. Please refresh and try again.";
        public const string StatusInvalid = "Invalid comment status.";
        public const string SuperAdminNotAllowed =
            "Super admins cannot leave comments. Sign in with a tenant account to use Pointer.";
        public const string QuickAccessCannotChangeStatus =
            "Client accounts can leave feedback but can't change its status.";
        public const string VerifyRequiresApplied = "Only applied comments can be verified.";
        public const string VerifyNoteRequired = "A note is required when reporting an issue.";
        public const string Verified = "Comment marked as verified.";
        public const string Reopened = "Comment reopened.";
        public const string AiAttributionInvalid =
            "AI tool/model must be plain identifiers (letters, digits, ., _, :, /, +, -) with no spaces, up to 64 characters.";
    }

    public static class PredefinedAction
    {
        public const string NotFound = "Action not found.";
        public const string TextRequired = "Action text is required.";
        public const string PromptRequired = "Action prompt is required.";
        public const string SuperAdminNotAllowed =
            "Super admins cannot create predefined actions. Sign in with a tenant account to use Pointer.";
    }

    public static class Suggestion
    {
        public const string NotFound = "Suggestion not found.";
        public const string TextRequired = "Suggestion text is required.";
        public const string PromptRequired = "Suggestion prompt is required.";
        public const string CanEditDirectly =
            "You can edit this project — add the predefined action directly instead of suggesting it.";
        public const string ProjectUnavailable = "The target project is no longer available.";
        public const string NotAvailableForProject =
            "Suggestions are not available for this project.";
        public const string Created = "Suggestion sent for admin review.";
        public const string Approved = "Suggestion approved.";
        public const string Rejected = "Suggestion rejected.";
        public const string FeedbackRequired = "Feedback is required.";
        public const string ChangesRequested = "Changes requested from the suggester.";
        public const string Resubmitted = "Suggestion resubmitted for admin review.";
        public const string NotEditable = "Only suggestions awaiting your changes can be edited.";
    }

    public static class Project_Delete
    {
        public const string HasComments =
            "This project has comments — only an admin can delete it.";
        public const string NotOwner = "You can only delete your own projects.";
    }

    public static class Invite
    {
        public const string NotFound = "Invite not found.";

        /// <summary>
        /// ONE message for every magic-link failure — expired, revoked, used up, unknown token,
        /// disabled user, wrong role. Distinguishing them would tell an anonymous caller holding a
        /// guessed token which part of their guess was right.
        /// </summary>
        public const string LinkInvalid =
            "This invite link is invalid or expired — ask for a new one.";
        public const string Invalid = "This invite link is invalid or has expired.";
        public const string Expired = "This invite link has expired.";
        public const string Revoked = "This invite link has been revoked.";
        public const string UsedUp = "This invite link has reached its usage limit.";
        public const string EmailMismatch = "This invite is locked to a different email address.";
        public const string Forbidden = "You are not allowed to create invites.";
        public const string Created = "Invite created.";
        public const string Revoked_Ok = "Invite revoked.";
        public const string QuickAccessEmailRequired =
            "An email is required for a client invite so the account can be provisioned.";
        public const string QuickAccessProjectRequired = "Select a project for this client invite.";
        public const string QuickAccessAppUrlRequired =
            "Set this project's App URL before sending a client invite.";
        public const string NotQuickAccess = "This invite has no magic link to rotate.";
        public const string LinkRotated =
            "A new link was issued; the previous one no longer works.";
    }

    public static class Build
    {
        public const string ShaInvalid = "A build sha must be 7-40 hexadecimal characters.";
        public const string TooManyShas = "Too many commit shas in one report; send at most 200.";
        public const string Reported = "Build recorded.";
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
        public const string CannotDeleteFree =
            "The Free plan is the fallback and cannot be deleted.";
        public const string InUse =
            "This plan has active subscriptions — move those tenants to another plan first.";
        public const string LimitReached = "You've reached your plan's limit. Upgrade to add more.";
        public const string ExtensionDisabled =
            "The browser extension is not enabled on your plan.";
        public const string Created = "Plan created.";
        public const string Updated = "Plan updated.";
        public const string Deleted = "Plan deleted.";
        public const string SubscriptionUpdated = "Subscription updated.";
    }

    public static class Demo
    {
        public const string NotDemoUser = "This account is not a demo account.";
        public const string AlreadyUpgraded =
            "This demo has already been upgraded to a permanent account.";
        public const string DemoExpired = "This demo session has expired. Please start a new demo.";
        public const string EmailTaken = "That email is already registered.";
        public const string UpgradeSuccess =
            "Your workspace has been upgraded. Welcome to Pointer!";
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

    public static class Common
    {
        public const string Forbidden = "You are not authorized to perform this action.";
        public const string NotFound = "Resource not found.";
    }

    public static class Audit
    {
        public const string PageSizeTooLarge = "Page size must be 200 or fewer.";
        public const string InvalidActionPrefix =
            "Action must be lower-case letters, underscores, and dots only (^[a-z_.]{1,64}$).";
    }

    public static class AiRule
    {
        public const string NotFound = "AI rule not found.";
        public const string TitleRequired = "Title is required.";
        public const string PromptRequired = "Prompt instruction is required.";
        public const string Forbidden = "You are not authorized to manage this AI rule.";
    }

    /// <summary>DB-13 (F2): metadata-only operator + audited, time-boxed impersonation.</summary>
    public static class Impersonation
    {
        public const string Required =
            "This content is only available inside an impersonation session (View as…).";
        public const string AlreadyActive =
            "You already have a live impersonation session — end it first.";
        public const string NoSession = "No impersonation session to end.";
        public const string Ended = "Impersonation session ended.";
        public const string ReadOnly = "Impersonation sessions are read-only.";
    }
}
