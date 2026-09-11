using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MigrateDefaultProjectAppUrlsToLocal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                -- 1. Ensure the global 'local' row exists
                -- created_by is NOT NULL (BaseEntity); a fresh database has no row to copy it from,
                -- so the all-zero Guid stands for created-by-the-system rather than a user. Omitting it
                -- aborted first boot on an empty database while passing on an existing one.
                INSERT INTO app_environments (name, owner_id, created_at, created_by)
                SELECT 'local', NULL, CURRENT_TIMESTAMP, '00000000-0000-0000-0000-000000000000'::uuid
                WHERE NOT EXISTS (SELECT 1 FROM app_environments WHERE name = 'local' AND owner_id IS NULL);

                -- 2. Soft-delete 'default' rows for projects that already have a 'local' row
                UPDATE project_app_urls
                SET deleted_at = CURRENT_TIMESTAMP
                WHERE app_environment_id = (SELECT id FROM app_environments WHERE name = 'default' AND owner_id IS NULL)
                  AND deleted_at IS NULL
                  AND project_id IN (
                      SELECT project_id 
                      FROM project_app_urls 
                      WHERE app_environment_id = (SELECT id FROM app_environments WHERE name = 'local' AND owner_id IS NULL)
                        AND deleted_at IS NULL
                  );

                -- 3. Repoint the remainder
                UPDATE project_app_urls
                SET app_environment_id = (SELECT id FROM app_environments WHERE name = 'local' AND owner_id IS NULL)
                WHERE app_environment_id = (SELECT id FROM app_environments WHERE name = 'default' AND owner_id IS NULL)
                  AND deleted_at IS NULL;

                -- 4. Set is_enabled=false, is_retired=true on the global 'default'
                UPDATE app_environments
                SET is_enabled = false, is_retired = true
                WHERE name = 'default' AND owner_id IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
