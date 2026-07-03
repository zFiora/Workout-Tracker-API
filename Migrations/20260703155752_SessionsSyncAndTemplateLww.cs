using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkoutTrackerAPI.Migrations
{
    /// <inheritdoc />
    public partial class SessionsSyncAndTemplateLww : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkoutSessions_Templates_TemplateId",
                table: "WorkoutSessions");

            migrationBuilder.DropTable(
                name: "WorkoutEntries");

            migrationBuilder.DropTable(
                name: "WorkoutSessionSets");

            migrationBuilder.DropTable(
                name: "WorkoutSessionExercises");

            migrationBuilder.DropIndex(
                name: "IX_WorkoutSessions_TemplateId",
                table: "WorkoutSessions");

            migrationBuilder.DropIndex(
                name: "IX_WorkoutSessions_UserId_TemplateId_StartedAt",
                table: "WorkoutSessions");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "WorkoutSessions");

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Templates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE \"Templates\" SET \"DeletedAt\" = \"UpdatedAt\" WHERE \"IsDeleted\" = true;");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Templates");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "WorkoutSessions",
                newName: "CreatedAtServer");

            migrationBuilder.AddColumn<JsonDocument>(
                name: "LogsJson",
                table: "WorkoutSessions",
                type: "jsonb",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "TemplateIcon",
                table: "WorkoutSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateName",
                table: "WorkoutSessions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessions_UserId_EndedAt",
                table: "WorkoutSessions",
                columns: new[] { "UserId", "EndedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessions_UserId_Id",
                table: "WorkoutSessions",
                columns: new[] { "UserId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessions_UserId_TemplateId",
                table: "WorkoutSessions",
                columns: new[] { "UserId", "TemplateId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkoutSessions_UserId_EndedAt",
                table: "WorkoutSessions");

            migrationBuilder.DropIndex(
                name: "IX_WorkoutSessions_UserId_Id",
                table: "WorkoutSessions");

            migrationBuilder.DropIndex(
                name: "IX_WorkoutSessions_UserId_TemplateId",
                table: "WorkoutSessions");

            migrationBuilder.DropColumn(
                name: "LogsJson",
                table: "WorkoutSessions");

            migrationBuilder.DropColumn(
                name: "TemplateIcon",
                table: "WorkoutSessions");

            migrationBuilder.DropColumn(
                name: "TemplateName",
                table: "WorkoutSessions");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Templates");

            migrationBuilder.RenameColumn(
                name: "CreatedAtServer",
                table: "WorkoutSessions",
                newName: "CreatedAt");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "WorkoutSessions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WorkoutEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Logs = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TemplateIcon = table.Column<string>(type: "text", nullable: false),
                    TemplateId = table.Column<string>(type: "text", nullable: false),
                    TemplateName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkoutEntries_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutSessionExercises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkoutSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExerciseId = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutSessionExercises", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkoutSessionExercises_WorkoutSessions_WorkoutSessionId",
                        column: x => x.WorkoutSessionId,
                        principalTable: "WorkoutSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutSessionSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkoutSessionExerciseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Reps = table.Column<int>(type: "integer", nullable: false),
                    SetType = table.Column<string>(type: "text", nullable: false),
                    WeightKg = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutSessionSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkoutSessionSets_WorkoutSessionExercises_WorkoutSessionEx~",
                        column: x => x.WorkoutSessionExerciseId,
                        principalTable: "WorkoutSessionExercises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessions_TemplateId",
                table: "WorkoutSessions",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessions_UserId_TemplateId_StartedAt",
                table: "WorkoutSessions",
                columns: new[] { "UserId", "TemplateId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutEntries_UserId_TemplateId_StartedAt",
                table: "WorkoutEntries",
                columns: new[] { "UserId", "TemplateId", "StartedAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessionExercises_WorkoutSessionId",
                table: "WorkoutSessionExercises",
                column: "WorkoutSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessionSets_WorkoutSessionExerciseId",
                table: "WorkoutSessionSets",
                column: "WorkoutSessionExerciseId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkoutSessions_Templates_TemplateId",
                table: "WorkoutSessions",
                column: "TemplateId",
                principalTable: "Templates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
