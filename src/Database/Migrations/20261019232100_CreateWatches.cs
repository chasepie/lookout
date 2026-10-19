using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Database.Migrations
{
    /// <inheritdoc />
    public partial class CreateWatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScriptVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<double>(type: "REAL", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsChange = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScriptVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    FileKind = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Watches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NotificationUrl = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentScriptVersionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Baseline = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Watches_ScriptVersions_CurrentScriptVersionId",
                        column: x => x.CurrentScriptVersionId,
                        principalTable: "ScriptVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ScriptVersionId",
                table: "Runs",
                column: "ScriptVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_WatchId_Id",
                table: "Runs",
                columns: new[] { "WatchId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ScriptVersions_WatchId",
                table: "ScriptVersions",
                column: "WatchId");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_CurrentScriptVersionId",
                table: "Watches",
                column: "CurrentScriptVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Runs_ScriptVersions_ScriptVersionId",
                table: "Runs",
                column: "ScriptVersionId",
                principalTable: "ScriptVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Runs_Watches_WatchId",
                table: "Runs",
                column: "WatchId",
                principalTable: "Watches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ScriptVersions_Watches_WatchId",
                table: "ScriptVersions",
                column: "WatchId",
                principalTable: "Watches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Watches_ScriptVersions_CurrentScriptVersionId",
                table: "Watches");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "ScriptVersions");

            migrationBuilder.DropTable(
                name: "Watches");
        }
    }
}
