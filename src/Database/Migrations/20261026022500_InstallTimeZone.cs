using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Database.Migrations
{
    /// <inheritdoc />
    public partial class InstallTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "Settings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "Settings");
        }
    }
}
