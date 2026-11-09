using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Backend.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "WatchRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreenshotMediaType",
                table: "WatchRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScreenshotOnCrash",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ScreenshotOnKnownFailure",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ScreenshotOnSuccess",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "WatchRuns");

            migrationBuilder.DropColumn(
                name: "ScreenshotMediaType",
                table: "WatchRuns");

            migrationBuilder.DropColumn(
                name: "ScreenshotOnCrash",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "ScreenshotOnKnownFailure",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "ScreenshotOnSuccess",
                table: "Watches");
        }
    }
}
