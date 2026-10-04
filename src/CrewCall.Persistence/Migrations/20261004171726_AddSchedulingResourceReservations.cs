using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulingResourceReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scheduling");

            migrationBuilder.CreateTable(
                name: "resource_reservations",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_reservations", x => x.id);
                    table.CheckConstraint("ck_resource_reservations_resource_type", "resource_type IN ('Technician', 'Vehicle', 'Equipment')");
                    table.CheckConstraint("ck_resource_reservations_start_before_end", "start_at < end_at");
                });

            migrationBuilder.CreateIndex(
                name: "ix_resource_reservations_resource_period",
                schema: "scheduling",
                table: "resource_reservations",
                columns: new[] { "resource_type", "resource_id", "start_at", "end_at" });

            migrationBuilder.CreateIndex(
                name: "IX_resource_reservations_visit_id",
                schema: "scheduling",
                table: "resource_reservations",
                column: "visit_id");

            // The final guard against double-booking a resource (EF Core cannot model exclusion constraints): no two
            // reservations of the same resource may overlap. Half-open ranges ('[)'), so touching reservations are
            // allowed. btree_gist (added in AddWorkforceAvailability) provides "=" on text and uuid inside a gist index.
            migrationBuilder.Sql("""
                ALTER TABLE scheduling.resource_reservations
                    ADD CONSTRAINT ex_resource_reservations_no_overlap
                    EXCLUDE USING gist (resource_type WITH =, resource_id WITH =, tstzrange(start_at, end_at, '[)') WITH &&);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_reservations",
                schema: "scheduling");
        }
    }
}
