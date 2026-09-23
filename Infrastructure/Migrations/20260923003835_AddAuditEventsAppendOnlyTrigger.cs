using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-12")]
    public partial class AddAuditEventsAppendOnlyTrigger : Migration
    {
        /// <inheritdoc />
        // DB-RULES: R4 constraint approved 2026-09-22 by Moamen (owner; foundations report §2 row 2 "append-only audit log", relayed by the orchestrator; docs/db/execution/DB-12-audit-log.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"
CREATE OR REPLACE FUNCTION audit_events_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN
    -- The ONLY permitted change: fk_audit_events_workspaces_owner_id ON DELETE SET NULL detaching the row
    -- from a hard-deleted workspace. Every other column must be byte-identical.
    IF OLD.owner_id IS NOT NULL AND NEW.owner_id IS NULL
       AND (to_jsonb(NEW) - 'owner_id') = (to_jsonb(OLD) - 'owner_id') THEN
      RETURN NEW;
    END IF;
  END IF;
  RAISE EXCEPTION 'audit_events is append-only (DB-12): % is not allowed', TG_OP USING ERRCODE = 'restrict_violation';
END $$;
CREATE TRIGGER trg_audit_events_append_only BEFORE UPDATE OR DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION audit_events_append_only();
CREATE TRIGGER trg_audit_events_no_truncate BEFORE TRUNCATE ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION audit_events_append_only();
"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"
DROP TRIGGER IF EXISTS trg_audit_events_no_truncate ON audit_events;
DROP TRIGGER IF EXISTS trg_audit_events_append_only ON audit_events;
DROP FUNCTION IF EXISTS audit_events_append_only();
"
            );
        }
    }
}
