using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Reporting.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitRescheduleTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "planned_changed_at_utc",
                schema: "reporting",
                table: "visit_activity",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reschedule_count",
                schema: "reporting",
                table: "visit_activity",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "total_delay_minutes",
                schema: "reporting",
                table: "visit_activity",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "planned_changed_at_utc",
                schema: "reporting",
                table: "visit_activity");

            migrationBuilder.DropColumn(
                name: "reschedule_count",
                schema: "reporting",
                table: "visit_activity");

            migrationBuilder.DropColumn(
                name: "total_delay_minutes",
                schema: "reporting",
                table: "visit_activity");
        }
    }
}
