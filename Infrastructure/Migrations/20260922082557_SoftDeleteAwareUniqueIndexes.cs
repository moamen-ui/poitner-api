using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-05")]
    public partial class SoftDeleteAwareUniqueIndexes : Migration
    {
        /// <inheritdoc />
        // DB-RULES: index change approved 2026-09-22 by orchestrator (owner instruction "proceed"; docs/db/execution/DB-05-soft-delete-aware-unique-indexes.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_users_email_owner_id", table: "users");

            migrationBuilder.DropIndex(name: "IX_subscriptions_owner_id", table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_status_presentations_status_value_owner_id",
                table: "status_presentations"
            );

            migrationBuilder.DropIndex(name: "IX_roles_name_owner_id", table: "roles");

            migrationBuilder.DropIndex(
                name: "IX_role_tenant_overrides_role_id_owner_id",
                table: "role_tenant_overrides"
            );

            migrationBuilder.DropIndex(name: "IX_plans_name", table: "plans");

            migrationBuilder.DropIndex(name: "IX_plans_slug", table: "plans");

            migrationBuilder.DropIndex(
                name: "IX_extension_sites_owner_id_origin",
                table: "extension_sites"
            );

            migrationBuilder.DropIndex(name: "IX_app_settings_key", table: "app_settings");

            migrationBuilder.DropIndex(
                name: "IX_app_environments_name_owner_id",
                table: "app_environments"
            );

            migrationBuilder
                .CreateIndex(
                    name: "ux_users_email_owner_live",
                    table: "users",
                    columns: new[] { "email", "owner_id" },
                    unique: true,
                    filter: "deleted_at IS NULL"
                )
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_subscriptions_owner_live",
                table: "subscriptions",
                column: "owner_id",
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder
                .CreateIndex(
                    name: "ux_status_presentations_status_owner_live",
                    table: "status_presentations",
                    columns: new[] { "status_value", "owner_id" },
                    unique: true,
                    filter: "deleted_at IS NULL"
                )
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder
                .CreateIndex(
                    name: "ux_roles_name_owner_live",
                    table: "roles",
                    columns: new[] { "name", "owner_id" },
                    unique: true,
                    filter: "deleted_at IS NULL"
                )
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_role_tenant_overrides_role_owner_live",
                table: "role_tenant_overrides",
                columns: new[] { "role_id", "owner_id" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ux_plans_name_live",
                table: "plans",
                column: "name",
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ux_plans_slug_live",
                table: "plans",
                column: "slug",
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ux_extension_sites_owner_origin_live",
                table: "extension_sites",
                columns: new[] { "owner_id", "origin" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ux_app_settings_key_live",
                table: "app_settings",
                column: "key",
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder
                .CreateIndex(
                    name: "ux_app_environments_name_owner_live",
                    table: "app_environments",
                    columns: new[] { "name", "owner_id" },
                    unique: true,
                    filter: "deleted_at IS NULL"
                )
                .Annotation("Npgsql:NullsDistinct", false);

            // Retire the four hand-written raw SQL owner-id-is-null-bucket unique indexes from
            // AddTenancy (20260629130828) and AddTenantRoleNameIndex (20260629131255). Their
            // global-uniqueness guarantee is now expressed in the EF model via AreNullsDistinct(false)
            // above (users, roles, status_presentations) and is moot for projects (owner_id NOT NULL
            // since 20260827130246_EnforceOwnerIdNotNull, so the projects one has been permanently
            // empty).
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_users_email_global;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_roles_name_global;");
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS ix_status_presentations_status_value_global;"
            );
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_projects_key_global;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ux_users_email_owner_live", table: "users");

            migrationBuilder.DropIndex(name: "ux_subscriptions_owner_live", table: "subscriptions");

            migrationBuilder.DropIndex(
                name: "ux_status_presentations_status_owner_live",
                table: "status_presentations"
            );

            migrationBuilder.DropIndex(name: "ux_roles_name_owner_live", table: "roles");

            migrationBuilder.DropIndex(
                name: "ux_role_tenant_overrides_role_owner_live",
                table: "role_tenant_overrides"
            );

            migrationBuilder.DropIndex(name: "ux_plans_name_live", table: "plans");

            migrationBuilder.DropIndex(name: "ux_plans_slug_live", table: "plans");

            migrationBuilder.DropIndex(
                name: "ux_extension_sites_owner_origin_live",
                table: "extension_sites"
            );

            migrationBuilder.DropIndex(name: "ux_app_settings_key_live", table: "app_settings");

            migrationBuilder.DropIndex(
                name: "ux_app_environments_name_owner_live",
                table: "app_environments"
            );

            migrationBuilder.CreateIndex(
                name: "IX_users_email_owner_id",
                table: "users",
                columns: new[] { "email", "owner_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_owner_id",
                table: "subscriptions",
                column: "owner_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_status_presentations_status_value_owner_id",
                table: "status_presentations",
                columns: new[] { "status_value", "owner_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_roles_name_owner_id",
                table: "roles",
                columns: new[] { "name", "owner_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_role_tenant_overrides_role_id_owner_id",
                table: "role_tenant_overrides",
                columns: new[] { "role_id", "owner_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_plans_name",
                table: "plans",
                column: "name",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_plans_slug",
                table: "plans",
                column: "slug",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_extension_sites_owner_id_origin",
                table: "extension_sites",
                columns: new[] { "owner_id", "origin" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_key",
                table: "app_settings",
                column: "key",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_app_environments_name_owner_id",
                table: "app_environments",
                columns: new[] { "name", "owner_id" },
                unique: true
            );

            // Recreate the four raw SQL owner-id-is-null-bucket indexes this migration retired
            // (verbatim from 20260629130828_AddTenancy.cs:147-149 and
            // 20260629131255_AddTenantRoleNameIndex.cs:24).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_projects_key_global ON projects(key) WHERE owner_id IS NULL;"
            );
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_status_presentations_status_value_global ON status_presentations(status_value) WHERE owner_id IS NULL;"
            );
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_users_email_global ON users(email) WHERE owner_id IS NULL;"
            );
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_roles_name_global ON roles(name) WHERE owner_id IS NULL;"
            );
        }
    }
}
