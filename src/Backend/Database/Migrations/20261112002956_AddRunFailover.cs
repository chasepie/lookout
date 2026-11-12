using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Backend.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRunFailover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AskedForFailover",
                table: "WatchRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "WatchRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryOfRunId",
                table: "WatchRuns",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AskedForFailover",
                table: "WatchRuns");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "WatchRuns");

            migrationBuilder.DropColumn(
                name: "RetryOfRunId",
                table: "WatchRuns");
        }
    }
}
