using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_role_tenant_overrides_owner_id",
                table: "role_tenant_overrides",
                column: "owner_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_quick_access_links_owner_id",
                table: "quick_access_links",
                column: "owner_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_project_builds_owner_id",
                table: "project_builds",
                column: "owner_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_device_logins_owner_id",
                table: "device_logins",
                column: "owner_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_owner_id",
                table: "api_keys",
                column: "owner_id"
            );

            migrationBuilder.AddForeignKey(
                name: "fk_ai_rules_workspaces_owner_id",
                table: "ai_rules",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_api_keys_workspaces_owner_id",
                table: "api_keys",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_app_environments_workspaces_owner_id",
                table: "app_environments",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_comments_workspaces_owner_id",
                table: "comments",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_device_logins_workspaces_owner_id",
                table: "device_logins",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_extension_sites_workspaces_owner_id",
                table: "extension_sites",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_invites_workspaces_owner_id",
                table: "invites",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_notifications_workspaces_owner_id",
                table: "notifications",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_page_context_snapshots_workspaces_owner_id",
                table: "page_context_snapshots",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_predefined_action_suggestions_workspaces_owner_id",
                table: "predefined_action_suggestions",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_predefined_actions_workspaces_owner_id",
                table: "predefined_actions",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_project_app_urls_workspaces_owner_id",
                table: "project_app_urls",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_project_builds_workspaces_owner_id",
                table: "project_builds",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_projects_workspaces_owner_id",
                table: "projects",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_quick_access_links_workspaces_owner_id",
                table: "quick_access_links",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_replies_workspaces_owner_id",
                table: "replies",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_role_tenant_overrides_workspaces_owner_id",
                table: "role_tenant_overrides",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_roles_workspaces_owner_id",
                table: "roles",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_status_presentations_workspaces_owner_id",
                table: "status_presentations",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_subscriptions_workspaces_owner_id",
                table: "subscriptions",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_usage_events_workspaces_owner_id",
                table: "usage_events",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull
            );

            migrationBuilder.AddForeignKey(
                name: "fk_users_workspaces_owner_id",
                table: "users",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_workspace_settings_workspaces_owner_id",
                table: "workspace_settings",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ai_rules_workspaces_owner_id",
                table: "ai_rules"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_api_keys_workspaces_owner_id",
                table: "api_keys"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_app_environments_workspaces_owner_id",
                table: "app_environments"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_comments_workspaces_owner_id",
                table: "comments"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_device_logins_workspaces_owner_id",
                table: "device_logins"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_extension_sites_workspaces_owner_id",
                table: "extension_sites"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_invites_workspaces_owner_id",
                table: "invites"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_notifications_workspaces_owner_id",
                table: "notifications"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_page_context_snapshots_workspaces_owner_id",
                table: "page_context_snapshots"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_predefined_action_suggestions_workspaces_owner_id",
                table: "predefined_action_suggestions"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_predefined_actions_workspaces_owner_id",
                table: "predefined_actions"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_project_app_urls_workspaces_owner_id",
                table: "project_app_urls"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_project_builds_workspaces_owner_id",
                table: "project_builds"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_projects_workspaces_owner_id",
                table: "projects"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_quick_access_links_workspaces_owner_id",
                table: "quick_access_links"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_replies_workspaces_owner_id",
                table: "replies"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_role_tenant_overrides_workspaces_owner_id",
                table: "role_tenant_overrides"
            );

            migrationBuilder.DropForeignKey(name: "fk_roles_workspaces_owner_id", table: "roles");

            migrationBuilder.DropForeignKey(
                name: "fk_status_presentations_workspaces_owner_id",
                table: "status_presentations"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_subscriptions_workspaces_owner_id",
                table: "subscriptions"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_usage_events_workspaces_owner_id",
                table: "usage_events"
            );

            migrationBuilder.DropForeignKey(name: "fk_users_workspaces_owner_id", table: "users");

            migrationBuilder.DropForeignKey(
                name: "fk_workspace_settings_workspaces_owner_id",
                table: "workspace_settings"
            );

            migrationBuilder.DropIndex(
                name: "IX_role_tenant_overrides_owner_id",
                table: "role_tenant_overrides"
            );

            migrationBuilder.DropIndex(
                name: "IX_quick_access_links_owner_id",
                table: "quick_access_links"
            );

            migrationBuilder.DropIndex(name: "IX_project_builds_owner_id", table: "project_builds");

            migrationBuilder.DropIndex(name: "IX_device_logins_owner_id", table: "device_logins");

            migrationBuilder.DropIndex(name: "IX_api_keys_owner_id", table: "api_keys");
        }
    }
}
