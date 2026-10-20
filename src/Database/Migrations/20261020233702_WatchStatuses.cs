using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Database.Migrations
{
    /// <inheritdoc />
    public partial class WatchStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailureStreak",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsPaused",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                // Edited by hand: existing Watches get the default timeout, not 0, which is invalid.
                defaultValue: 120);

            // Edited by hand: a Watch whose latest Runs were Known failures or Errored is already in a streak.
            migrationBuilder.Sql("""
                UPDATE Watches SET FailureStreak = (
                    SELECT COUNT(*) FROM Runs
                    WHERE Runs.WatchId = Watches.Id AND Runs.Id > COALESCE((
                        SELECT MAX(Succeeded.Id) FROM Runs AS Succeeded
                        WHERE Succeeded.WatchId = Watches.Id AND Succeeded.Outcome = 'Succeeded'), 0));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureStreak",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "IsPaused",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "Watches");
        }
    }
}
