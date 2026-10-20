using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Database.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleWatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JitterMinutes",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "NextRunAt",
                table: "Watches",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextRunTimeZone",
                table: "Watches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Schedule_Cron",
                table: "Watches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Schedule_Every",
                table: "Watches",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Schedule_Kind",
                table: "Watches",
                type: "TEXT",
                nullable: false,
                defaultValue: "Interval");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "Schedule_TimeOfDay",
                table: "Watches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Schedule_Unit",
                table: "Watches",
                type: "TEXT",
                nullable: true);

            // Watches created before scheduling run hourly; the scheduler sets their NextRunAt when it starts.
            migrationBuilder.Sql("UPDATE Watches SET Schedule_Every = 1, Schedule_Unit = 'Hours'");

            migrationBuilder.CreateIndex(
                name: "IX_Watches_NextRunAt",
                table: "Watches",
                column: "NextRunAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Watches_NextRunAt",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "JitterMinutes",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "NextRunAt",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "NextRunTimeZone",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "Schedule_Cron",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "Schedule_Every",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "Schedule_Kind",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "Schedule_TimeOfDay",
                table: "Watches");

            migrationBuilder.DropColumn(
                name: "Schedule_Unit",
                table: "Watches");
        }
    }
}
