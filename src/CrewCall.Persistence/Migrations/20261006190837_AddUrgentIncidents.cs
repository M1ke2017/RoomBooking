using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUrgentIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incidents",
                schema: "workorders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.id);
                    table.CheckConstraint("ck_incidents_priority", "priority IN ('High', 'Urgent', 'Critical')");
                    table.CheckConstraint("ck_incidents_requested_start_before_end", "requested_start < requested_end");
                    table.CheckConstraint("ck_incidents_resolved_at_when_resolved", "(status = 'Resolved') = (resolved_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_incidents_status", "status IN ('New', 'Analyzing', 'ReadyForDispatch', 'Dispatched', 'Resolved', 'Cancelled')");
                    table.CheckConstraint("ck_incidents_work_order_when_dispatched", "(status IN ('Dispatched', 'Resolved')) = (work_order_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_incidents_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "workorders",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incidents_sites_site_id_customer_id",
                        columns: x => new { x.site_id, x.customer_id },
                        principalSchema: "workorders",
                        principalTable: "sites",
                        principalColumns: new[] { "id", "customer_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incidents_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalSchema: "workorders",
                        principalTable: "work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_required_skills",
                schema: "workorders",
                columns: table => new
                {
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_required_skills", x => new { x.incident_id, x.skill_code });
                    table.CheckConstraint("ck_incident_required_skills_skill_code_normalized", "skill_code <> '' AND skill_code = upper(btrim(skill_code))");
                    table.ForeignKey(
                        name: "FK_incident_required_skills_incidents_incident_id",
                        column: x => x.incident_id,
                        principalSchema: "workorders",
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_created_at_utc_id",
                schema: "workorders",
                table: "incidents",
                columns: new[] { "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_customer_id",
                schema: "workorders",
                table: "incidents",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_site_id_customer_id",
                schema: "workorders",
                table: "incidents",
                columns: new[] { "site_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_incidents_status",
                schema: "workorders",
                table: "incidents",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_incidents_work_order_id",
                schema: "workorders",
                table: "incidents",
                column: "work_order_id",
                unique: true,
                filter: "work_order_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident_required_skills",
                schema: "workorders");

            migrationBuilder.DropTable(
                name: "incidents",
                schema: "workorders");
        }
    }
}
