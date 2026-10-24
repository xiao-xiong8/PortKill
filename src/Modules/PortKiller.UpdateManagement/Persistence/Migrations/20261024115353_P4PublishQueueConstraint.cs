using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortKiller.UpdateManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P4PublishQueueConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReleaseJobs_Application_Channel",
                table: "ReleaseJobs");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseJobs_Application_Channel",
                table: "ReleaseJobs",
                columns: new[] { "Application", "Channel" },
                unique: true,
                filter: "\"Kind\" = 'publish' AND \"State\" IN ('Queued','Publishing','ManifestActivated','PublicVerifying')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReleaseJobs_Application_Channel",
                table: "ReleaseJobs");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseJobs_Application_Channel",
                table: "ReleaseJobs",
                columns: new[] { "Application", "Channel" },
                unique: true,
                filter: "\"Kind\" = 'publish' AND \"State\" IN ('Publishing','ManifestActivated','PublicVerifying')");
        }
    }
}
