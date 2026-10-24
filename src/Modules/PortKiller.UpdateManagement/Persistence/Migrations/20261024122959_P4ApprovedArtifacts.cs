using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortKiller.UpdateManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P4ApprovedArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublishApprovalId",
                table: "ReleaseJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Application",
                table: "PublishApprovals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ArchiveId",
                table: "PublishApprovals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "PublishApprovals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Runtime",
                table: "PublishApprovals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseJobs_PublishApprovalId",
                table: "ReleaseJobs",
                column: "PublishApprovalId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ReleaseJobs_PublishApprovals_PublishApprovalId",
                table: "ReleaseJobs",
                column: "PublishApprovalId",
                principalTable: "PublishApprovals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReleaseJobs_PublishApprovals_PublishApprovalId",
                table: "ReleaseJobs");

            migrationBuilder.DropIndex(
                name: "IX_ReleaseJobs_PublishApprovalId",
                table: "ReleaseJobs");

            migrationBuilder.DropColumn(
                name: "PublishApprovalId",
                table: "ReleaseJobs");

            migrationBuilder.DropColumn(
                name: "Application",
                table: "PublishApprovals");

            migrationBuilder.DropColumn(
                name: "ArchiveId",
                table: "PublishApprovals");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "PublishApprovals");

            migrationBuilder.DropColumn(
                name: "Runtime",
                table: "PublishApprovals");
        }
    }
}
