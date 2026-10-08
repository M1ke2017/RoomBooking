using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Reporting.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialReportingProjections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reporting");

            migrationBuilder.CreateTable(
                name: "assignment_activity",
                schema: "reporting",
                columns: table => new
                {
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: true),
                    vehicle_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_assignment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignment_activity", x => x.assignment_id);
                    table.CheckConstraint("ck_assignment_activity_status", "status IN ('Active', 'Replaced', 'Cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "reporting",
                columns: table => new
                {
                    consumer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.consumer_name, x.message_id });
                    table.CheckConstraint("ck_inbox_messages_consumer_name_not_blank", "btrim(consumer_name) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "incident_activity",
                schema: "reporting",
                columns: table => new
                {
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_activity", x => x.incident_id);
                });

            migrationBuilder.CreateTable(
                name: "projection_checkpoints",
                schema: "reporting",
                columns: table => new
                {
                    projection_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_event_occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_messages = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projection_checkpoints", x => x.projection_name);
                });

            migrationBuilder.CreateTable(
                name: "technician_activity",
                schema: "reporting",
                columns: table => new
                {
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date_utc = table.Column<DateOnly>(type: "date", nullable: false),
                    completed_visits = table.Column<int>(type: "integer", nullable: false),
                    assignment_count = table.Column<int>(type: "integer", nullable: false),
                    incident_dispatch_count = table.Column<int>(type: "integer", nullable: false),
                    travel_minutes = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    gross_work_minutes = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    pause_minutes = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    net_work_minutes = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_activity", x => new { x.technician_id, x.date_utc });
                    table.CheckConstraint("ck_technician_activity_counts", "completed_visits >= 0 AND assignment_count >= 0 AND incident_dispatch_count >= 0");
                    table.CheckConstraint("ck_technician_activity_minutes", "travel_minutes >= 0 AND gross_work_minutes >= 0 AND pause_minutes >= 0 AND net_work_minutes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "visit_activity",
                schema: "reporting",
                columns: table => new
                {
                    visit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_order_priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: true),
                    planned_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    planned_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actual_travel_minutes = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    actual_gross_work_minutes = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    actual_pause_minutes = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    actual_net_work_minutes = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    visit_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit_activity", x => x.visit_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_activity_technician_created_at",
                schema: "reporting",
                table: "assignment_activity",
                columns: new[] { "technician_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_activity_visit",
                schema: "reporting",
                table: "assignment_activity",
                column: "visit_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_activity_technician_dispatched_at",
                schema: "reporting",
                table: "incident_activity",
                columns: new[] { "technician_id", "dispatched_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_visit_activity_site_planned_start",
                schema: "reporting",
                table: "visit_activity",
                columns: new[] { "site_id", "planned_start_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_visit_activity_technician_completed_at",
                schema: "reporting",
                table: "visit_activity",
                columns: new[] { "technician_id", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_visit_activity_technician_planned_start",
                schema: "reporting",
                table: "visit_activity",
                columns: new[] { "technician_id", "planned_start_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assignment_activity",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "incident_activity",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "projection_checkpoints",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "technician_activity",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "visit_activity",
                schema: "reporting");
        }
    }
}
