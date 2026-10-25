using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortKiller.UpdateManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P4PublicVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ActivatedUtcMs",
                table: "ReleaseJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublicCheckAttempts",
                table: "ReleaseJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "PublicCheckCompletedUtcMs",
                table: "ReleaseJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicCheckErrorCode",
                table: "ReleaseJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PublicCheckStartedUtcMs",
                table: "ReleaseJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublicCheckSucceeded",
                table: "ReleaseJobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivatedUtcMs",
                table: "ReleaseJobs");

            migrationBuilder.DropColumn(
                name: "PublicCheckAttempts",
                table: "ReleaseJobs");

            migrationBuilder.DropColumn(
                name: "PublicCheckCompletedUtcMs",
                table: "ReleaseJobs");

            migrationBuilder.DropColumn(
                name: "PublicCheckErrorCode",
                table: "ReleaseJobs");

            migrationBuilder.DropColumn(
                name: "PublicCheckStartedUtcMs",
                table: "ReleaseJobs");

            migrationBuilder.DropColumn(
                name: "PublicCheckSucceeded",
                table: "ReleaseJobs");
        }
    }
}
