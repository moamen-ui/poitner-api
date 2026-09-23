using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspacesDemoColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "demo_comment_cap_override",
                table: "workspaces",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "demo_converted_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "demo_expires_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "demo_expiry_warned_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "demo_extended_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "demo_ttl_hours_override",
                table: "workspaces",
                type: "integer",
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_demo_expires_at",
                table: "workspaces",
                column: "demo_expires_at",
                filter: "demo_expires_at IS NOT NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_workspaces_demo_expires_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "demo_comment_cap_override", table: "workspaces");

            migrationBuilder.DropColumn(name: "demo_converted_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "demo_expires_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "demo_expiry_warned_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "demo_extended_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "demo_ttl_hours_override", table: "workspaces");
        }
    }
}
