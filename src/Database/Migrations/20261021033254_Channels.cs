using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Database.Migrations
{
    /// <inheritdoc />
    public partial class Channels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FailureStreakNotified",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // A Watch already failing has had its streak for a while, which is too late to notify of now. Counting the
            // streak as notified also keeps a streak of Errored Runs from passing for Known failures in a row.
            migrationBuilder.Sql("UPDATE Watches SET FailureStreakNotified = 1 WHERE FailureStreak > 0");

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    WebhookUrl = table.Column<string>(type: "TEXT", nullable: true),
                    UserKey = table.Column<string>(type: "TEXT", nullable: true),
                    AppToken = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelWatch",
                columns: table => new
                {
                    ChannelsId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WatchesId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelWatch", x => new { x.ChannelsId, x.WatchesId });
                    table.ForeignKey(
                        name: "FK_ChannelWatch_Channels_ChannelsId",
                        column: x => x.ChannelsId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelWatch_Watches_WatchesId",
                        column: x => x.WatchesId,
                        principalTable: "Watches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelWatch_WatchesId",
                table: "ChannelWatch",
                column: "WatchesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelWatch");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropColumn(
                name: "FailureStreakNotified",
                table: "Watches");
        }
    }
}
