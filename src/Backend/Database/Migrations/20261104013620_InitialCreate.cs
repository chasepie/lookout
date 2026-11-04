using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Backend.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VpnSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CurrentRegion = table.Column<string>(type: "TEXT", nullable: true),
                    LastSwitchTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRotationTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    RotationEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RotationIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    RotationJitterMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ExcludedRegions = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VpnSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Watches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScriptFileName = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptFileId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduleEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduleKind = table.Column<int>(type: "INTEGER", nullable: true),
                    ScheduleInterval = table.Column<string>(type: "TEXT", nullable: true),
                    UseVpn = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Screenshot = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchRuns_Watches_WatchId",
                        column: x => x.WatchId,
                        principalTable: "Watches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "VpnSettings",
                columns: new[] { "Id", "CurrentRegion", "ExcludedRegions", "LastError", "LastSwitchTime", "NextRotationTime", "RotationEnabled", "RotationIntervalMinutes", "RotationJitterMinutes" },
                values: new object[] { 1, null, "[]", null, null, null, false, 15, 3 });

            migrationBuilder.CreateIndex(
                name: "IX_WatchRuns_WatchId",
                table: "WatchRuns",
                column: "WatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VpnSettings");

            migrationBuilder.DropTable(
                name: "WatchRuns");

            migrationBuilder.DropTable(
                name: "Watches");
        }
    }
}
