using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOwnerIdNotNull : Migration
    {
        // Defense-in-depth follow-up to the super-admin self-ownership fix: a super admin can no
        // longer create/own a project, a comment always inherits its project's (now-never-null)
        // owner, and a tenant-wide predefined action was already required non-null — so none of
        // these three columns can legitimately be null anymore. Enforced at the DB level so a
        // future bug can't silently reintroduce the recurring "owner_id" bug class.
        //
        // Hand-written (not the raw EF-scaffolded AlterColumn) to avoid ALTER COLUMN ... SET DEFAULT
        // '00000000-...': a permanent all-zero-GUID default would make an accidental missing OwnerId
        // silently write a bogus sentinel tenant instead of failing loudly — exactly the opposite of
        // the point. The UPDATE backfills are defensive no-ops (verified zero null rows in production
        // before writing this) in case a straggler somehow exists elsewhere.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE projects SET owner_id = gen_random_uuid() WHERE owner_id IS NULL;");
            migrationBuilder.Sql("ALTER TABLE projects ALTER COLUMN owner_id SET NOT NULL;");

            migrationBuilder.Sql("UPDATE predefined_actions SET owner_id = gen_random_uuid() WHERE owner_id IS NULL;");
            migrationBuilder.Sql("ALTER TABLE predefined_actions ALTER COLUMN owner_id SET NOT NULL;");

            migrationBuilder.Sql("UPDATE comments SET owner_id = gen_random_uuid() WHERE owner_id IS NULL;");
            migrationBuilder.Sql("ALTER TABLE comments ALTER COLUMN owner_id SET NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE comments ALTER COLUMN owner_id DROP NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE predefined_actions ALTER COLUMN owner_id DROP NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE projects ALTER COLUMN owner_id DROP NOT NULL;");
        }
    }
}
