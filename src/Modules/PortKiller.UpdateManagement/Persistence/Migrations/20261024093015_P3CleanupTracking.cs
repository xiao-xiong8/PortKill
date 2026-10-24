using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortKiller.UpdateManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P3CleanupTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StorageCleanedUtcMs",
                table: "UploadSessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ArchiveCleanedUtcMs",
                table: "ReleaseDrafts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageCleanedUtcMs",
                table: "UploadSessions");

            migrationBuilder.DropColumn(
                name: "ArchiveCleanedUtcMs",
                table: "ReleaseDrafts");
        }
    }
}
