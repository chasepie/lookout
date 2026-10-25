using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Database.Migrations
{
    /// <inheritdoc />
    public partial class NotificationChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Edited by hand: Channels are renamed Notification channels. EF would drop the old tables; instead, each
            // Notification channel and Watch's choice of it is copied into the new tables, whose keys and index get the
            // names EF expects, as renamed tables' wouldn't.
            migrationBuilder.CreateTable(
                name: "NotificationChannels",
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
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchNotificationChannels",
                columns: table => new
                {
                    WatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NotificationChannelId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchNotificationChannels", x => new { x.WatchId, x.NotificationChannelId });
                    table.ForeignKey(
                        name: "FK_WatchNotificationChannels_NotificationChannels_NotificationChannelId",
                        column: x => x.NotificationChannelId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchNotificationChannels_Watches_WatchId",
                        column: x => x.WatchId,
                        principalTable: "Watches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchNotificationChannels_NotificationChannelId",
                table: "WatchNotificationChannels",
                column: "NotificationChannelId");

            migrationBuilder.Sql("""
                INSERT INTO NotificationChannels (Id, Name, Kind, WebhookUrl, UserKey, AppToken)
                SELECT Id, Name, Kind, WebhookUrl, UserKey, AppToken FROM Channels
                """);
            migrationBuilder.Sql("""
                INSERT INTO WatchNotificationChannels (WatchId, NotificationChannelId)
                SELECT WatchId, ChannelId FROM WatchChannels
                """);

            migrationBuilder.DropTable(
                name: "WatchChannels");

            migrationBuilder.DropTable(
                name: "Channels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Edited by hand: copied back, as in Up.
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppToken = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    UserKey = table.Column<string>(type: "TEXT", nullable: true),
                    WebhookUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchChannels",
                columns: table => new
                {
                    WatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchChannels", x => new { x.WatchId, x.ChannelId });
                    table.ForeignKey(
                        name: "FK_WatchChannels_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchChannels_Watches_WatchId",
                        column: x => x.WatchId,
                        principalTable: "Watches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WatchChannels_ChannelId",
                table: "WatchChannels",
                column: "ChannelId");

            migrationBuilder.Sql("""
                INSERT INTO Channels (Id, Name, Kind, WebhookUrl, UserKey, AppToken)
                SELECT Id, Name, Kind, WebhookUrl, UserKey, AppToken FROM NotificationChannels
                """);
            migrationBuilder.Sql("""
                INSERT INTO WatchChannels (WatchId, ChannelId)
                SELECT WatchId, NotificationChannelId FROM WatchNotificationChannels
                """);

            migrationBuilder.DropTable(
                name: "WatchNotificationChannels");

            migrationBuilder.DropTable(
                name: "NotificationChannels");
        }
    }
}
