using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Backend.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Interrupted",
                table: "WatchRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Runs the worker cut off before this column existed, which it marked only with this error
            migrationBuilder.Sql(
                "UPDATE WatchRuns SET Interrupted = 1 WHERE Error = 'Interrupted because the Backend restarted.';");

            // The watches there are already get the default a new one does; 0 would make every success a recovery
            migrationBuilder.AddColumn<int>(
                name: "KnownFailureThreshold",
                table: "Watches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateTable(
                name: "NotificationChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Service = table.Column<int>(type: "INTEGER", nullable: false),
                    Pushover_ApplicationToken = table.Column<string>(type: "TEXT", nullable: true),
                    Pushover_UserKey = table.Column<string>(type: "TEXT", nullable: true),
                    Pushover_RetrySeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    Pushover_ExpireSeconds = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    NotificationChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Events = table.Column<string>(type: "TEXT", nullable: false),
                    Pushover_Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    Pushover_Sound = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscriptions_NotificationChannels_NotificationChannelId",
                        column: x => x.NotificationChannelId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Watches_WatchId",
                        column: x => x.WatchId,
                        principalTable: "Watches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_NotificationChannelId",
                table: "Subscriptions",
                column: "NotificationChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_WatchId",
                table: "Subscriptions",
                column: "WatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "NotificationChannels");

            migrationBuilder.DropColumn(
                name: "Interrupted",
                table: "WatchRuns");

            migrationBuilder.DropColumn(
                name: "KnownFailureThreshold",
                table: "Watches");
        }
    }
}
