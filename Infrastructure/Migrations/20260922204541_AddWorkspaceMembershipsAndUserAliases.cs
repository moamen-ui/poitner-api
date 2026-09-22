using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-11a")]
    public partial class AddWorkspaceMembershipsAndUserAliases : Migration
    {
        /// <inheritdoc />
        // DB-RULES: index change approved 2026-09-22 by Moamen (owner; requirement "one identity, memberships per workspace, keys per membership", relayed by the orchestrator; docs/db/execution/DB-11a-identity-and-workspace-memberships.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ux_api_keys_active_per_user", table: "api_keys");

            migrationBuilder.AddColumn<int>(
                name: "merged_into_user_id",
                table: "users",
                type: "integer",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "user_aliases",
                columns: table => new
                {
                    alias_public_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    source_workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    merged_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_aliases", x => x.alias_public_id);
                    table.ForeignKey(
                        name: "FK_user_aliases_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "workspace_memberships",
                columns: table => new
                {
                    id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    approval_status = table.Column<int>(
                        type: "integer",
                        nullable: false,
                        defaultValue: 1
                    ),
                    security_stamp = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    left_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    left_reason = table.Column<int>(type: "integer", nullable: true),
                    invite_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspace_memberships", x => x.id);
                    table.ForeignKey(
                        name: "FK_workspace_memberships_invites_invite_id",
                        column: x => x.invite_id,
                        principalTable: "invites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                    table.ForeignKey(
                        name: "FK_workspace_memberships_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_workspace_memberships_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_workspace_memberships_workspaces_owner_id",
                        column: x => x.owner_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_users_merged_into_user_id",
                table: "users",
                column: "merged_into_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_user_id",
                table: "api_keys",
                column: "user_id"
            );

            migrationBuilder
                .CreateIndex(
                    name: "ux_api_keys_active_per_membership",
                    table: "api_keys",
                    columns: new[] { "user_id", "owner_id" },
                    unique: true,
                    filter: "revoked_at IS NULL AND deleted_at IS NULL"
                )
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_user_aliases_user_id",
                table: "user_aliases",
                column: "user_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_workspace_memberships_invite_id",
                table: "workspace_memberships",
                column: "invite_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_workspace_memberships_owner_role",
                table: "workspace_memberships",
                columns: new[] { "owner_id", "role_id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_workspace_memberships_role_id",
                table: "workspace_memberships",
                column: "role_id"
            );

            migrationBuilder.CreateIndex(
                name: "ux_workspace_memberships_user_workspace_live",
                table: "workspace_memberships",
                columns: new[] { "user_id", "owner_id" },
                unique: true,
                filter: "left_at IS NULL AND deleted_at IS NULL"
            );

            migrationBuilder.AddForeignKey(
                name: "fk_users_merged_into_user",
                table: "users",
                column: "merged_into_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "fk_users_merged_into_user", table: "users");

            migrationBuilder.DropTable(name: "user_aliases");

            migrationBuilder.DropTable(name: "workspace_memberships");

            migrationBuilder.DropIndex(name: "IX_users_merged_into_user_id", table: "users");

            migrationBuilder.DropIndex(name: "ix_api_keys_user_id", table: "api_keys");

            migrationBuilder.DropIndex(
                name: "ux_api_keys_active_per_membership",
                table: "api_keys"
            );

            migrationBuilder.DropColumn(name: "merged_into_user_id", table: "users");

            migrationBuilder.CreateIndex(
                name: "ux_api_keys_active_per_user",
                table: "api_keys",
                column: "user_id",
                unique: true,
                filter: "revoked_at IS NULL AND deleted_at IS NULL"
            );
        }
    }
}
