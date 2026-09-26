using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-20")]
    public partial class AddBillingPaymentsAppendOnlyTrigger : Migration
    {
        /// <inheritdoc />
        // DB-RULES: R4 constraint approved 2026-09-26 by Moamen (owner; instruction "Billing v1 — immutable payments ledger", relayed by the orchestrator; docs/db/execution/DB-20-billing-v1-manual-payments-comp-codes.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"
CREATE OR REPLACE FUNCTION billing_payments_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN
    -- The ONLY permitted change: fk_billing_payments_workspaces_owner_id ON DELETE SET NULL detaching the row
    -- from a hard-deleted workspace. Every other column must be byte-identical.
    IF OLD.owner_id IS NOT NULL AND NEW.owner_id IS NULL
       AND (to_jsonb(NEW) - 'owner_id') = (to_jsonb(OLD) - 'owner_id') THEN
      RETURN NEW;
    END IF;
  END IF;
  RAISE EXCEPTION 'billing_payments is append-only (DB-20): % is not allowed', TG_OP USING ERRCODE = 'restrict_violation';
END $$;
CREATE TRIGGER trg_billing_payments_append_only BEFORE UPDATE OR DELETE ON billing_payments FOR EACH ROW EXECUTE FUNCTION billing_payments_append_only();
CREATE TRIGGER trg_billing_payments_no_truncate BEFORE TRUNCATE ON billing_payments FOR EACH STATEMENT EXECUTE FUNCTION billing_payments_append_only();
"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"
DROP TRIGGER IF EXISTS trg_billing_payments_no_truncate ON billing_payments;
DROP TRIGGER IF EXISTS trg_billing_payments_append_only ON billing_payments;
DROP FUNCTION IF EXISTS billing_payments_append_only();
"
            );
        }
    }
}
