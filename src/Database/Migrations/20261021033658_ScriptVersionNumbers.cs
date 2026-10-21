using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Database.Migrations
{
    /// <inheritdoc />
    public partial class ScriptVersionNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScriptVersions_WatchId",
                table: "ScriptVersions");

            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "ScriptVersions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UploadedAt",
                table: "ScriptVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Edited by hand: existing Script versions are numbered in the order they were added. When each was
            // uploaded wasn't recorded, so it's taken as its first Run, or as now for one that never ran.
            migrationBuilder.Sql("""
                UPDATE ScriptVersions SET
                    Number = (
                        SELECT COUNT(*) FROM ScriptVersions AS Earlier
                        WHERE Earlier.WatchId = ScriptVersions.WatchId AND Earlier.rowid <= ScriptVersions.rowid),
                    UploadedAt = COALESCE(
                        (SELECT MIN(Runs.StartedAt) FROM Runs WHERE Runs.ScriptVersionId = ScriptVersions.Id),
                        strftime('%Y-%m-%d %H:%M:%S+00:00', 'now'));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ScriptVersions_WatchId_Number",
                table: "ScriptVersions",
                columns: new[] { "WatchId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScriptVersions_WatchId_Number",
                table: "ScriptVersions");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "ScriptVersions");

            migrationBuilder.DropColumn(
                name: "UploadedAt",
                table: "ScriptVersions");

            migrationBuilder.CreateIndex(
                name: "IX_ScriptVersions_WatchId",
                table: "ScriptVersions",
                column: "WatchId");
        }
    }
}
