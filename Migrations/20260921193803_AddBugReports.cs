using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkoutTrackerAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddBugReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BugReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Platform = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    DeviceInfo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AppScreen = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ErrorDetails = table.Column<string>(type: "text", nullable: true),
                    ScreenshotBase64 = table.Column<string>(type: "text", nullable: true),
                    ScreenshotContentType = table.Column<string>(type: "text", nullable: true),
                    AdminNotes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BugReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BugReports_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BugReports_Status",
                table: "BugReports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BugReports_UserId",
                table: "BugReports",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BugReports");
        }
    }
}
