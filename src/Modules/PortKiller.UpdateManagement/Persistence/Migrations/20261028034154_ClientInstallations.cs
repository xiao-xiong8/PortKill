using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortKiller.UpdateManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClientInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientInstallations",
                columns: table => new
                {
                    InstallationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ClientVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastReportResult = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    RegisteredUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastReportedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedUtcMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientInstallations", x => x.InstallationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientInstallations_ClientVersion_InstallationId",
                table: "ClientInstallations",
                columns: new[] { "ClientVersion", "InstallationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientInstallations_LastReportedUtcMs_InstallationId",
                table: "ClientInstallations",
                columns: new[] { "LastReportedUtcMs", "InstallationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientInstallations_TokenHash",
                table: "ClientInstallations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientInstallations");
        }
    }
}
