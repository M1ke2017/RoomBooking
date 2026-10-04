using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "assignment_id",
                schema: "scheduling",
                table: "resource_reservations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assignments",
                schema: "scheduling",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    travel_buffer_before_minutes = table.Column<int>(type: "integer", nullable: false),
                    travel_buffer_after_minutes = table.Column<int>(type: "integer", nullable: false),
                    claimed_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    replaced_by_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.id);
                    table.CheckConstraint("ck_assignments_claimed_window", "claimed_start < claimed_end");
                    table.CheckConstraint("ck_assignments_status", "status IN ('Active', 'Replaced', 'Cancelled')");
                    table.CheckConstraint("ck_assignments_travel_buffers", "travel_buffer_before_minutes >= 0 AND travel_buffer_after_minutes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "assignment_equipment",
                schema: "scheduling",
                columns: table => new
                {
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    equipment_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignment_equipment", x => new { x.assignment_id, x.equipment_id });
                    table.ForeignKey(
                        name: "FK_assignment_equipment_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalSchema: "scheduling",
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_resource_reservations_assignment_id",
                schema: "scheduling",
                table: "resource_reservations",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_equipment_equipment_id",
                schema: "scheduling",
                table: "assignment_equipment",
                column: "equipment_id");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_visit_history",
                schema: "scheduling",
                table: "assignments",
                columns: new[] { "visit_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_assignments_one_active_per_visit",
                schema: "scheduling",
                table: "assignments",
                column: "visit_id",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.AddForeignKey(
                name: "FK_resource_reservations_assignments_assignment_id",
                schema: "scheduling",
                table: "resource_reservations",
                column: "assignment_id",
                principalSchema: "scheduling",
                principalTable: "assignments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_resource_reservations_assignments_assignment_id",
                schema: "scheduling",
                table: "resource_reservations");

            migrationBuilder.DropTable(
                name: "assignment_equipment",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "assignments",
                schema: "scheduling");

            migrationBuilder.DropIndex(
                name: "IX_resource_reservations_assignment_id",
                schema: "scheduling",
                table: "resource_reservations");

            migrationBuilder.DropColumn(
                name: "assignment_id",
                schema: "scheduling",
                table: "resource_reservations");
        }
    }
}
