using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingV1SubscriptionAndInviteColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "comp_ends_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "comp_reason",
                table: "subscriptions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "comped_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "comped_by",
                table: "subscriptions",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "is_complimentary",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<string>(
                name: "quoted_currency",
                table: "subscriptions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true
            );

            migrationBuilder.AddColumn<decimal>(
                name: "quoted_price",
                table: "subscriptions",
                type: "numeric(12,2)",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "renewal_reminder_sent_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "requested_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "requested_by",
                table: "subscriptions",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "requested_plan_id",
                table: "subscriptions",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "comp_ends_at",
                table: "invites",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "comp_reason",
                table: "invites",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "is_complimentary",
                table: "invites",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_comp_ends_at",
                table: "subscriptions",
                column: "comp_ends_at",
                filter: "comp_ends_at IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_current_period_end",
                table: "subscriptions",
                column: "current_period_end",
                filter: "current_period_end IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_requested_plan_id",
                table: "subscriptions",
                column: "requested_plan_id"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_comp_consistent",
                table: "subscriptions",
                sql: "(is_complimentary = (comped_at IS NOT NULL)) AND ((comped_at IS NULL) = (comped_by IS NULL)) AND (is_complimentary OR (comp_reason IS NULL AND comp_ends_at IS NULL))"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_comp_no_request",
                table: "subscriptions",
                sql: "NOT (is_complimentary AND requested_plan_id IS NOT NULL)"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_quote_valid",
                table: "subscriptions",
                sql: "quoted_price IS NULL OR (quoted_price >= 0 AND quoted_currency ~ '^[A-Z]{3}$')"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_request_consistent",
                table: "subscriptions",
                sql: "(requested_plan_id IS NULL) = (requested_at IS NULL) AND (requested_plan_id IS NULL) = (requested_by IS NULL) AND (requested_plan_id IS NULL) = (quoted_price IS NULL) AND (requested_plan_id IS NULL) = (quoted_currency IS NULL)"
            );

            migrationBuilder.AddCheckConstraint(
                name: "ck_invites_comp_consistent",
                table: "invites",
                sql: "(is_complimentary OR (comp_reason IS NULL AND comp_ends_at IS NULL)) AND (NOT is_complimentary OR (owner_id IS NULL AND plan_id IS NOT NULL))"
            );

            migrationBuilder.AddForeignKey(
                name: "fk_subscriptions_plans_requested_plan_id",
                table: "subscriptions",
                column: "requested_plan_id",
                principalTable: "plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_subscriptions_plans_requested_plan_id",
                table: "subscriptions"
            );

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_comp_ends_at",
                table: "subscriptions"
            );

            migrationBuilder.DropIndex(
                name: "ix_subscriptions_current_period_end",
                table: "subscriptions"
            );

            migrationBuilder.DropIndex(
                name: "IX_subscriptions_requested_plan_id",
                table: "subscriptions"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_comp_consistent",
                table: "subscriptions"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_comp_no_request",
                table: "subscriptions"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_quote_valid",
                table: "subscriptions"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_request_consistent",
                table: "subscriptions"
            );

            migrationBuilder.DropCheckConstraint(
                name: "ck_invites_comp_consistent",
                table: "invites"
            );

            migrationBuilder.DropColumn(name: "comp_ends_at", table: "subscriptions");

            migrationBuilder.DropColumn(name: "comp_reason", table: "subscriptions");

            migrationBuilder.DropColumn(name: "comped_at", table: "subscriptions");

            migrationBuilder.DropColumn(name: "comped_by", table: "subscriptions");

            migrationBuilder.DropColumn(name: "is_complimentary", table: "subscriptions");

            migrationBuilder.DropColumn(name: "quoted_currency", table: "subscriptions");

            migrationBuilder.DropColumn(name: "quoted_price", table: "subscriptions");

            migrationBuilder.DropColumn(name: "renewal_reminder_sent_at", table: "subscriptions");

            migrationBuilder.DropColumn(name: "requested_at", table: "subscriptions");

            migrationBuilder.DropColumn(name: "requested_by", table: "subscriptions");

            migrationBuilder.DropColumn(name: "requested_plan_id", table: "subscriptions");

            migrationBuilder.DropColumn(name: "comp_ends_at", table: "invites");

            migrationBuilder.DropColumn(name: "comp_reason", table: "invites");

            migrationBuilder.DropColumn(name: "is_complimentary", table: "invites");
        }
    }
}
