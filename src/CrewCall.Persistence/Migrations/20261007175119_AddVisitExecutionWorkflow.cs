using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVisitExecutionWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "visit_executions",
                schema: "workorders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    travel_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    work_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit_executions", x => x.id);
                    table.CheckConstraint("ck_visit_executions_cancelled_at", "(status = 'Cancelled') = (cancelled_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_visit_executions_completed_at", "(status = 'Completed') = (completed_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_visit_executions_status", "status IN ('NotStarted', 'Traveling', 'Working', 'Paused', 'Completed', 'Cancelled')");
                    table.CheckConstraint("ck_visit_executions_travel_before_work", "travel_started_at_utc IS NULL OR work_started_at_utc IS NULL OR travel_started_at_utc <= work_started_at_utc");
                    table.CheckConstraint("ck_visit_executions_travel_started", "status <> 'Traveling' OR travel_started_at_utc IS NOT NULL");
                    table.CheckConstraint("ck_visit_executions_work_before_completion", "work_started_at_utc IS NULL OR completed_at_utc IS NULL OR work_started_at_utc <= completed_at_utc");
                    table.CheckConstraint("ck_visit_executions_work_started", "status NOT IN ('Working', 'Paused', 'Completed') OR work_started_at_utc IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_visit_executions_visits_visit_id",
                        column: x => x.visit_id,
                        principalSchema: "workorders",
                        principalTable: "visits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "visit_execution_pauses",
                schema: "workorders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    visit_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visit_execution_pauses", x => x.id);
                    table.CheckConstraint("ck_visit_execution_pauses_start_before_end", "ended_at_utc IS NULL OR started_at_utc < ended_at_utc");
                    table.ForeignKey(
                        name: "FK_visit_execution_pauses_visit_executions_visit_execution_id",
                        column: x => x.visit_execution_id,
                        principalSchema: "workorders",
                        principalTable: "visit_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_visit_execution_pauses_execution_started",
                schema: "workorders",
                table: "visit_execution_pauses",
                columns: new[] { "visit_execution_id", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_visit_execution_pauses_one_open",
                schema: "workorders",
                table: "visit_execution_pauses",
                column: "visit_execution_id",
                unique: true,
                filter: "ended_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_visit_executions_visit_id",
                schema: "workorders",
                table: "visit_executions",
                column: "visit_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "visit_execution_pauses",
                schema: "workorders");

            migrationBuilder.DropTable(
                name: "visit_executions",
                schema: "workorders");
        }
    }
}
