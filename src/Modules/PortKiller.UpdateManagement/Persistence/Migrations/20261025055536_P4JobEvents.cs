using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortKiller.UpdateManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P4JobEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastEventSequence",
                table: "ReleaseJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("""
                UPDATE "ReleaseJobs" SET "LastEventSequence" = COALESCE(
                    (SELECT MAX("Sequence") FROM "JobEvents" WHERE "JobId" = "ReleaseJobs"."Id"), 0);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_JobEvents_CreatedUtcMs_JobId_Sequence",
                table: "JobEvents",
                columns: new[] { "CreatedUtcMs", "JobId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobEvents_CreatedUtcMs_JobId_Sequence",
                table: "JobEvents");

            migrationBuilder.DropColumn(
                name: "LastEventSequence",
                table: "ReleaseJobs");
        }
    }
}
