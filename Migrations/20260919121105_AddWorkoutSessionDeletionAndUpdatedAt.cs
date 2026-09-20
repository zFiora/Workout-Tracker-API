using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkoutTrackerAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkoutSessionDeletionAndUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "WorkoutSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "WorkoutSessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // EF Core's auto-generated default above (0001-01-01) is only there to
            // satisfy the NOT NULL constraint while the column is being added — it must
            // never be the value existing rows end up with. Backfill from CreatedAtServer,
            // the only sensible "last touched" timestamp that already exists for old rows.
            migrationBuilder.Sql(
                "UPDATE \"WorkoutSessions\" SET \"UpdatedAt\" = \"CreatedAtServer\";");

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessions_UserId_UpdatedAt_Id",
                table: "WorkoutSessions",
                columns: new[] { "UserId", "UpdatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkoutSessions_UserId_UpdatedAt_Id",
                table: "WorkoutSessions");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "WorkoutSessions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "WorkoutSessions");
        }
    }
}
