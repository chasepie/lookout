using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Backend.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAvoidedRegions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AskedForFailover",
                table: "WatchRuns",
                newName: "AskedToAvoidRegion");

            migrationBuilder.CreateTable(
                name: "AvoidedRegions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvoidedRegions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AvoidedRegions_WatchRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "WatchRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AvoidedRegions_Watches_WatchId",
                        column: x => x.WatchId,
                        principalTable: "Watches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvoidedRegions_RunId",
                table: "AvoidedRegions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_AvoidedRegions_WatchId_Region",
                table: "AvoidedRegions",
                columns: new[] { "WatchId", "Region" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AvoidedRegions");

            migrationBuilder.RenameColumn(
                name: "AskedToAvoidRegion",
                table: "WatchRuns",
                newName: "AskedForFailover");
        }
    }
}
