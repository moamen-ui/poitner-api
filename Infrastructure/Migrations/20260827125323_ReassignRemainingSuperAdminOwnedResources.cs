using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReassignRemainingSuperAdminOwnedResources : Migration
    {
        // Follow-up to 20260827124245_ReassignPointerLandingOwnership: a production sweep after
        // that migration found the super-admin self-ownership leak (closed in ProjectService
        // .CreateAsync/CommentService.CreateAsync/PredefinedActionService.CreateTenantAsync) had
        // produced SIX MORE self-owned projects (pointer-api, pointer-dashboard, tuwaiq-clubs,
        // bugbounty, new-test, tuwaiq-permit), their comments, a couple of tenant-wide (ProjectId
        // == null) predefined actions, and 3 member-user accounts whose OwnerId also pointed at the
        // super admin's own PublicId. Reassigns everything still owned by the super admin to the
        // same dedicated tenant pointer-landing was migrated to, so nothing is left orphaned.
        // Guarded on the OLD owner id — a no-op anywhere that owner id has nothing left (e.g. after
        // this runs once, or in dev/test/CI where none of this data exists).
        private const string OldOwnerId = "95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42"; // super admin (moamen.ui@gmail.com)
        private const string NewOwnerId = "98699076-e7cb-4392-a271-8db09430fcd6"; // dedicated tenant (moamen.ui2@gmail.com, Workspace Admin)

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"UPDATE projects SET owner_id = '{NewOwnerId}' WHERE owner_id = '{OldOwnerId}';");
            migrationBuilder.Sql($"UPDATE comments SET owner_id = '{NewOwnerId}' WHERE owner_id = '{OldOwnerId}';");
            migrationBuilder.Sql($"UPDATE predefined_actions SET owner_id = '{NewOwnerId}' WHERE owner_id = '{OldOwnerId}';");
            migrationBuilder.Sql($"UPDATE users SET owner_id = '{NewOwnerId}' WHERE owner_id = '{OldOwnerId}';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"UPDATE users SET owner_id = '{OldOwnerId}' WHERE owner_id = '{NewOwnerId}';");
            migrationBuilder.Sql($"UPDATE predefined_actions SET owner_id = '{OldOwnerId}' WHERE owner_id = '{NewOwnerId}';");
            migrationBuilder.Sql($"UPDATE comments SET owner_id = '{OldOwnerId}' WHERE owner_id = '{NewOwnerId}';");
            migrationBuilder.Sql($"UPDATE projects SET owner_id = '{OldOwnerId}' WHERE owner_id = '{NewOwnerId}';");
        }
    }
}
