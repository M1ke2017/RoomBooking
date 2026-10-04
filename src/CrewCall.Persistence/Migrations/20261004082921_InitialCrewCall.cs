using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <summary>
    /// Baseline: creates the module-owned schemas (ADR-0004). No tables yet, and none of the legacy RoomBooking tables.
    /// Schema names are literals on purpose: a migration is a historical record and must not change if constants do.
    /// </summary>
    public partial class InitialCrewCall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "workorders");
            migrationBuilder.EnsureSchema(name: "workforce");
            migrationBuilder.EnsureSchema(name: "resources");
            migrationBuilder.EnsureSchema(name: "scheduling");
            migrationBuilder.EnsureSchema(name: "ops");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropSchema(name: "scheduling");
            migrationBuilder.DropSchema(name: "resources");
            migrationBuilder.DropSchema(name: "workforce");
            migrationBuilder.DropSchema(name: "workorders");

            // "ops" is kept: it holds the migrations history table.
        }
    }
}
