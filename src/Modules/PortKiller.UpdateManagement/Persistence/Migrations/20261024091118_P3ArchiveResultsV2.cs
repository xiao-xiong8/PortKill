using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortKiller.UpdateManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P3ArchiveResultsV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArchiveId",
                table: "ReleaseDrafts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "ValidatedUtcMs",
                table: "ReleaseDrafts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseDrafts_ArchiveId",
                table: "ReleaseDrafts",
                column: "ArchiveId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReleaseDrafts_ArchiveId",
                table: "ReleaseDrafts");

            migrationBuilder.DropColumn(
                name: "ArchiveId",
                table: "ReleaseDrafts");

            migrationBuilder.DropColumn(
                name: "ValidatedUtcMs",
                table: "ReleaseDrafts");
        }
    }
}
