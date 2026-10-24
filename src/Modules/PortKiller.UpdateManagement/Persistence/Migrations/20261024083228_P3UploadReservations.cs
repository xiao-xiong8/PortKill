using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortKiller.UpdateManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P3UploadReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ManifestExpectedBytes",
                table: "UploadSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ManifestReceivedBytes",
                table: "UploadSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ManifestSha256",
                table: "UploadSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PackageExpectedBytes",
                table: "UploadSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "PackageReceivedBytes",
                table: "UploadSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "PackageSha256",
                table: "UploadSessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManifestExpectedBytes",
                table: "UploadSessions");

            migrationBuilder.DropColumn(
                name: "ManifestReceivedBytes",
                table: "UploadSessions");

            migrationBuilder.DropColumn(
                name: "ManifestSha256",
                table: "UploadSessions");

            migrationBuilder.DropColumn(
                name: "PackageExpectedBytes",
                table: "UploadSessions");

            migrationBuilder.DropColumn(
                name: "PackageReceivedBytes",
                table: "UploadSessions");

            migrationBuilder.DropColumn(
                name: "PackageSha256",
                table: "UploadSessions");
        }
    }
}
