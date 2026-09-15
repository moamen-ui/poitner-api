using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixPayloadFlagsJsonDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "payload_flags",
                table: "replies",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'",
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "feature_bullets",
                table: "plans",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'",
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "payload_flags",
                table: "comments",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'",
                oldClrType: typeof(string),
                oldType: "jsonb");

            // Repair rows written while the column default was '{}' (an object). The mapping reads
            // these as List<string>; System.Text.Json threw on the first such row and the whole
            // comments list returned 500 (prod: 104 comments). Normalise anything that is not an
            // array to an empty array, in every table that uses the same converter.
            migrationBuilder.Sql("UPDATE comments SET payload_flags = '[]'::jsonb WHERE jsonb_typeof(payload_flags) IS DISTINCT FROM 'array';");
            migrationBuilder.Sql("UPDATE replies  SET payload_flags = '[]'::jsonb WHERE jsonb_typeof(payload_flags) IS DISTINCT FROM 'array';");
            migrationBuilder.Sql("UPDATE plans    SET feature_bullets = '[]'::jsonb WHERE jsonb_typeof(feature_bullets) IS DISTINCT FROM 'array';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "payload_flags",
                table: "replies",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'");

            migrationBuilder.AlterColumn<string>(
                name: "feature_bullets",
                table: "plans",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'");

            migrationBuilder.AlterColumn<string>(
                name: "payload_flags",
                table: "comments",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'");
        }
    }
}
