using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChasePie.Lookout.Database.Migrations
{
    /// <inheritdoc />
    public partial class DebugOutputAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunDebugOutputs",
                columns: table => new
                {
                    RunId = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsoleOutput = table.Column<string>(type: "TEXT", nullable: true),
                    Screenshot = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunDebugOutputs", x => x.RunId);
                    table.ForeignKey(
                        name: "FK_RunDebugOutputs_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                    table.CheckConstraint("CK_Settings_Singleton", "Id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_FinishedAt",
                table: "Runs",
                column: "FinishedAt");

            // The only row of settings, with the defaults as they were when it was created. It isn't the model's seed
            // data, which later migrations would reset to whatever the defaults are then, overwriting the user's.
            migrationBuilder.InsertData(
                table: "Settings",
                columns: ["Id", "RetentionDays"],
                values: [1, 30]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunDebugOutputs");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropIndex(
                name: "IX_Runs_FinishedAt",
                table: "Runs");
        }
    }
}
