using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspacesPauseAndDeletionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_confirmed_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_reminder_sent_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_requested_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "deletion_requested_by",
                table: "workspaces",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "deletion_scheduled_for",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "paused_at",
                table: "workspaces",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "paused_by",
                table: "workspaces",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "paused_by_operator",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateIndex(
                name: "ix_workspaces_deletion_scheduled_for",
                table: "workspaces",
                column: "deletion_scheduled_for",
                filter: "deletion_scheduled_for IS NOT NULL"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_workspaces_deletion_request_consistent",
                table: "workspaces",
                sql: "(deletion_requested_at IS NULL) = (deletion_requested_by IS NULL)"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_workspaces_deletion_schedule_consistent",
                table: "workspaces",
                sql: "(deletion_confirmed_at IS NULL) = (deletion_scheduled_for IS NULL) AND (deletion_confirmed_at IS NULL OR deletion_requested_at IS NOT NULL) AND (deletion_reminder_sent_at IS NULL OR deletion_scheduled_for IS NOT NULL)"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_workspaces_pause_consistent",
                table: "workspaces",
                sql: "(paused_at IS NULL) = (paused_by IS NULL) AND (paused_at IS NOT NULL OR NOT paused_by_operator)"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workspaces_deletion_scheduled_for",
                table: "workspaces"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_workspaces_deletion_request_consistent",
                table: "workspaces"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_workspaces_deletion_schedule_consistent",
                table: "workspaces"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_workspaces_pause_consistent",
                table: "workspaces"
            );

            migrationBuilder.DropColumn(name: "deletion_confirmed_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "deletion_reminder_sent_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "deletion_requested_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "deletion_requested_by", table: "workspaces");

            migrationBuilder.DropColumn(name: "deletion_scheduled_for", table: "workspaces");

            migrationBuilder.DropColumn(name: "paused_at", table: "workspaces");

            migrationBuilder.DropColumn(name: "paused_by", table: "workspaces");

            migrationBuilder.DropColumn(name: "paused_by_operator", table: "workspaces");
        }
    }
}
