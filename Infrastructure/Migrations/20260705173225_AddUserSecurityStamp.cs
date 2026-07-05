using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Back-fill existing users with a distinct random stamp (not a shared zero-guid), so
            // enabling Auth:ValidateSecurityStamp later can't collapse everyone onto one stamp.
            migrationBuilder.AddColumn<Guid>(
                name: "security_stamp",
                table: "users",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "security_stamp",
                table: "users");
        }
    }
}
