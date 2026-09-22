using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-06")]
    public partial class AddForeignKeysForLogicalReferences : Migration
    {
        /// <inheritdoc />
        // DB-RULES: R4 constraint approved 2026-09-22 by Moamen (owner; instruction "choose the best for clean db", relayed by the orchestrator; docs/db/execution/DB-06-foreign-keys-for-logical-references.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notifications_projects_project_id",
                table: "notifications"
            );

            migrationBuilder.DropIndex(
                name: "IX_predefined_actions_owner_id",
                table: "predefined_actions"
            );

            migrationBuilder.DropIndex(name: "IX_ai_rules_owner_id", table: "ai_rules");

            migrationBuilder.CreateIndex(
                name: "IX_quick_access_links_invite_id",
                table: "quick_access_links",
                column: "invite_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_quick_access_links_project_id",
                table: "quick_access_links",
                column: "project_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_predefined_actions_project_id",
                table: "predefined_actions",
                column: "project_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_predefined_action_suggestions_project_id",
                table: "predefined_action_suggestions",
                column: "project_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_invites_plan_id",
                table: "invites",
                column: "plan_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_invites_project_id",
                table: "invites",
                column: "project_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_invites_role_id",
                table: "invites",
                column: "role_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ai_rules_project_id",
                table: "ai_rules",
                column: "project_id"
            );

            migrationBuilder.AddForeignKey(
                name: "FK_ai_rules_projects_project_id",
                table: "ai_rules",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "FK_invites_plans_plan_id",
                table: "invites",
                column: "plan_id",
                principalTable: "plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_invites_projects_project_id",
                table: "invites",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull
            );

            migrationBuilder.AddForeignKey(
                name: "FK_invites_roles_role_id",
                table: "invites",
                column: "role_id",
                principalTable: "roles",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull
            );

            migrationBuilder.AddForeignKey(
                name: "FK_notifications_projects_project_id",
                table: "notifications",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "FK_predefined_action_suggestions_projects_project_id",
                table: "predefined_action_suggestions",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "FK_predefined_actions_projects_project_id",
                table: "predefined_actions",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "FK_quick_access_links_invites_invite_id",
                table: "quick_access_links",
                column: "invite_id",
                principalTable: "invites",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_quick_access_links_projects_project_id",
                table: "quick_access_links",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "FK_role_tenant_overrides_roles_role_id",
                table: "role_tenant_overrides",
                column: "role_id",
                principalTable: "roles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "FK_usage_events_projects_project_id",
                table: "usage_events",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ai_rules_projects_project_id",
                table: "ai_rules"
            );

            migrationBuilder.DropForeignKey(name: "FK_invites_plans_plan_id", table: "invites");

            migrationBuilder.DropForeignKey(
                name: "FK_invites_projects_project_id",
                table: "invites"
            );

            migrationBuilder.DropForeignKey(name: "FK_invites_roles_role_id", table: "invites");

            migrationBuilder.DropForeignKey(
                name: "FK_notifications_projects_project_id",
                table: "notifications"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_predefined_action_suggestions_projects_project_id",
                table: "predefined_action_suggestions"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_predefined_actions_projects_project_id",
                table: "predefined_actions"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_quick_access_links_invites_invite_id",
                table: "quick_access_links"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_quick_access_links_projects_project_id",
                table: "quick_access_links"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_role_tenant_overrides_roles_role_id",
                table: "role_tenant_overrides"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_usage_events_projects_project_id",
                table: "usage_events"
            );

            migrationBuilder.DropIndex(
                name: "IX_quick_access_links_invite_id",
                table: "quick_access_links"
            );

            migrationBuilder.DropIndex(
                name: "IX_quick_access_links_project_id",
                table: "quick_access_links"
            );

            migrationBuilder.DropIndex(
                name: "IX_predefined_actions_project_id",
                table: "predefined_actions"
            );

            migrationBuilder.DropIndex(
                name: "IX_predefined_action_suggestions_project_id",
                table: "predefined_action_suggestions"
            );

            migrationBuilder.DropIndex(name: "IX_invites_plan_id", table: "invites");

            migrationBuilder.DropIndex(name: "IX_invites_project_id", table: "invites");

            migrationBuilder.DropIndex(name: "IX_invites_role_id", table: "invites");

            migrationBuilder.DropIndex(name: "IX_ai_rules_project_id", table: "ai_rules");

            migrationBuilder.CreateIndex(
                name: "IX_predefined_actions_owner_id",
                table: "predefined_actions",
                column: "owner_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_ai_rules_owner_id",
                table: "ai_rules",
                column: "owner_id"
            );

            migrationBuilder.AddForeignKey(
                name: "FK_notifications_projects_project_id",
                table: "notifications",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );
        }
    }
}
