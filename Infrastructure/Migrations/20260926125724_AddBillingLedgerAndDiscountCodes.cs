using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingLedgerAndDiscountCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discount_codes",
                columns: table => new
                {
                    id = table
                        .Column<int>(type: "integer", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    code = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    label = table.Column<string>(
                        type: "character varying(120)",
                        maxLength: 120,
                        nullable: true
                    ),
                    note = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    currency = table.Column<string>(
                        type: "character varying(3)",
                        maxLength: 3,
                        nullable: true
                    ),
                    duration = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    valid_from = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    valid_until = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    max_redemptions = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: true
                    ),
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
                    table.PrimaryKey("PK_discount_codes", x => x.id);
                    table.CheckConstraint(
                        "ck_discount_codes_code_format",
                        "code ~ '^[A-Z0-9][A-Z0-9_-]{2,31}$'"
                    );
                    table.CheckConstraint("ck_discount_codes_duration", "duration IN (1, 2)");
                    table.CheckConstraint(
                        "ck_discount_codes_max_redemptions",
                        "max_redemptions IS NULL OR max_redemptions > 0"
                    );
                    table.CheckConstraint(
                        "ck_discount_codes_value",
                        "(kind = 1 AND value > 0 AND value <= 100 AND currency IS NULL) OR (kind = 2 AND value > 0 AND currency IS NOT NULL AND currency ~ '^[A-Z]{3}$')"
                    );
                    table.CheckConstraint(
                        "ck_discount_codes_window",
                        "valid_from IS NULL OR valid_until IS NULL OR valid_until > valid_from"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "discount_code_plans",
                columns: table => new
                {
                    discount_code_id = table.Column<int>(type: "integer", nullable: false),
                    plan_id = table.Column<int>(type: "integer", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_discount_code_plans",
                        x => new { x.discount_code_id, x.plan_id }
                    );
                    table.ForeignKey(
                        name: "fk_discount_code_plans_discount_codes_discount_code_id",
                        column: x => x.discount_code_id,
                        principalTable: "discount_codes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "fk_discount_code_plans_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "discount_redemptions",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    discount_code_id = table.Column<int>(type: "integer", nullable: false),
                    plan_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    code_snapshot = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    kind_snapshot = table.Column<int>(type: "integer", nullable: false),
                    duration_snapshot = table.Column<int>(type: "integer", nullable: false),
                    value_snapshot = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    currency_snapshot = table.Column<string>(
                        type: "character varying(3)",
                        maxLength: 3,
                        nullable: true
                    ),
                    original_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    final_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    price_currency = table.Column<string>(
                        type: "character varying(3)",
                        maxLength: 3,
                        nullable: false
                    ),
                    created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    applied_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    released_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    release_reason = table.Column<int>(type: "integer", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discount_redemptions", x => x.id);
                    table.CheckConstraint(
                        "ck_discount_redemptions_amounts",
                        "original_price >= 0 AND discount_amount >= 0 AND discount_amount <= original_price AND final_price = original_price - discount_amount AND price_currency ~ '^[A-Z]{3}$'"
                    );
                    table.CheckConstraint(
                        "ck_discount_redemptions_status_shape",
                        "(status = 1 AND applied_at IS NULL AND released_at IS NULL AND release_reason IS NULL) OR (status = 2 AND applied_at IS NOT NULL AND released_at IS NULL AND release_reason IS NULL) OR (status = 3 AND released_at IS NOT NULL AND release_reason IS NOT NULL)"
                    );
                    table.ForeignKey(
                        name: "fk_discount_redemptions_discount_codes_discount_code_id",
                        column: x => x.discount_code_id,
                        principalTable: "discount_codes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_discount_redemptions_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_discount_redemptions_workspaces_owner_id",
                        column: x => x.owner_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "billing_payments",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plan_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    currency = table.Column<string>(
                        type: "character varying(3)",
                        maxLength: 3,
                        nullable: false
                    ),
                    quoted_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    method = table.Column<int>(type: "integer", nullable: true),
                    reference = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    note = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    paid_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    period_start = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    period_end = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    previous_plan_id = table.Column<int>(type: "integer", nullable: true),
                    previous_status = table.Column<int>(type: "integer", nullable: true),
                    previous_period_end = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    discount_redemption_id = table.Column<long>(type: "bigint", nullable: true),
                    discount_first_applied = table.Column<bool>(
                        type: "boolean",
                        nullable: false,
                        defaultValue: false
                    ),
                    voids_payment_id = table.Column<long>(type: "bigint", nullable: true),
                    recorded_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_payments", x => x.id);
                    table.CheckConstraint(
                        "ck_billing_payments_amounts",
                        "amount >= 0 AND (quoted_amount IS NULL OR quoted_amount >= 0)"
                    );
                    table.CheckConstraint(
                        "ck_billing_payments_currency",
                        "currency ~ '^[A-Z]{3}$'"
                    );
                    table.CheckConstraint(
                        "ck_billing_payments_first_applied",
                        "NOT discount_first_applied OR discount_redemption_id IS NOT NULL"
                    );
                    table.CheckConstraint(
                        "ck_billing_payments_kind_shape",
                        "(kind = 1 AND method IN (1, 2, 3) AND paid_at IS NOT NULL AND period_start IS NOT NULL AND period_end IS NOT NULL AND period_end > period_start AND previous_plan_id IS NOT NULL AND previous_status IS NOT NULL AND voids_payment_id IS NULL) OR (kind = 2 AND voids_payment_id IS NOT NULL AND method IS NULL AND paid_at IS NULL AND period_start IS NULL AND period_end IS NULL AND discount_redemption_id IS NULL AND NOT discount_first_applied)"
                    );
                    table.ForeignKey(
                        name: "fk_billing_payments_billing_payments_voids_payment_id",
                        column: x => x.voids_payment_id,
                        principalTable: "billing_payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_billing_payments_discount_redemptions_discount_redemption_id",
                        column: x => x.discount_redemption_id,
                        principalTable: "discount_redemptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_billing_payments_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_billing_payments_plans_previous_plan_id",
                        column: x => x.previous_plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_billing_payments_workspaces_owner_id",
                        column: x => x.owner_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_billing_payments_discount_redemption_id",
                table: "billing_payments",
                column: "discount_redemption_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_billing_payments_owner_recorded",
                table: "billing_payments",
                columns: new[] { "owner_id", "recorded_at" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "IX_billing_payments_plan_id",
                table: "billing_payments",
                column: "plan_id"
            );

            migrationBuilder.CreateIndex(
                name: "IX_billing_payments_previous_plan_id",
                table: "billing_payments",
                column: "previous_plan_id"
            );

            migrationBuilder.CreateIndex(
                name: "ux_billing_payments_voids_payment_id",
                table: "billing_payments",
                column: "voids_payment_id",
                unique: true,
                filter: "voids_payment_id IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "IX_discount_code_plans_plan_id",
                table: "discount_code_plans",
                column: "plan_id"
            );

            migrationBuilder.CreateIndex(
                name: "ux_discount_codes_code_live",
                table: "discount_codes",
                column: "code",
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_discount_redemptions_code_status",
                table: "discount_redemptions",
                columns: new[] { "discount_code_id", "status" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_discount_redemptions_plan_id",
                table: "discount_redemptions",
                column: "plan_id"
            );

            migrationBuilder.CreateIndex(
                name: "ux_discount_redemptions_code_owner_open",
                table: "discount_redemptions",
                columns: new[] { "discount_code_id", "owner_id" },
                unique: true,
                filter: "status IN (1, 2)"
            );

            migrationBuilder.CreateIndex(
                name: "ux_discount_redemptions_owner_pending",
                table: "discount_redemptions",
                column: "owner_id",
                unique: true,
                filter: "status = 1"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "billing_payments");

            migrationBuilder.DropTable(name: "discount_code_plans");

            migrationBuilder.DropTable(name: "discount_redemptions");

            migrationBuilder.DropTable(name: "discount_codes");
        }
    }
}
