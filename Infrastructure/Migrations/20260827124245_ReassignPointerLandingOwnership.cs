using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReassignPointerLandingOwnership : Migration
    {
        // Data-only migration: the "pointer-landing" marketing-site project (and comments left on
        // it) was self-owned by the super-admin account itself (OwnerId == the super admin's own
        // PublicId) — the concrete production instance of the self-ownership leak closed in
        // ProjectService.CreateAsync/CommentService.CreateAsync. Reassigns it to a real dedicated
        // tenant instead of leaving it as a special case. Guarded on the OLD owner id so this is a
        // no-op anywhere that row doesn't exist in this exact state (dev/test/CI, or if already run).
        private const string OldOwnerId = "95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42"; // super admin (moamen.ui@gmail.com)
        private const string NewOwnerId = "98699076-e7cb-4392-a271-8db09430fcd6"; // dedicated tenant (moamen.ui2@gmail.com, Workspace Admin)

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE projects SET owner_id = '{NewOwnerId}'
                WHERE key = 'pointer-landing' AND owner_id = '{OldOwnerId}';
                """);
            migrationBuilder.Sql($"""
                UPDATE comments SET owner_id = '{NewOwnerId}'
                WHERE project_id = (SELECT id FROM projects WHERE key = 'pointer-landing')
                  AND owner_id = '{OldOwnerId}';
                """);
            migrationBuilder.Sql($"""
                UPDATE predefined_actions SET owner_id = '{NewOwnerId}'
                WHERE project_id = (SELECT id FROM projects WHERE key = 'pointer-landing')
                  AND owner_id = '{OldOwnerId}';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                UPDATE predefined_actions SET owner_id = '{OldOwnerId}'
                WHERE project_id = (SELECT id FROM projects WHERE key = 'pointer-landing')
                  AND owner_id = '{NewOwnerId}';
                """);
            migrationBuilder.Sql($"""
                UPDATE comments SET owner_id = '{OldOwnerId}'
                WHERE project_id = (SELECT id FROM projects WHERE key = 'pointer-landing')
                  AND owner_id = '{NewOwnerId}';
                """);
            migrationBuilder.Sql($"""
                UPDATE projects SET owner_id = '{OldOwnerId}'
                WHERE key = 'pointer-landing' AND owner_id = '{NewOwnerId}';
                """);
        }
    }
}
