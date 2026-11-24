using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LekhaBook.Data.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "Companies",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "Asia/Kathmandu");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "Companies");
        }
    }
}
