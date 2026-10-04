using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrdersVisitsOperationalEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ops");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_sites_id_customer_id",
                schema: "workorders",
                table: "sites",
                columns: new[] { "id", "customer_id" });

            migrationBuilder.CreateTable(
                name: "operational_events",
                schema: "ops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_events", x => x.id);
                });

            // Append-only history: reject any UPDATE, DELETE or TRUNCATE of operational events at the database level.
            migrationBuilder.Sql("""
                CREATE FUNCTION ops.reject_operational_event_change() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'ops.operational_events is append-only: % is not allowed', TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER operational_events_append_only
                    BEFORE UPDATE OR DELETE ON ops.operational_events
                    FOR EACH ROW EXECUTE FUNCTION ops.reject_operational_event_change();

                CREATE TRIGGER operational_events_no_truncate
                    BEFORE TRUNCATE ON ops.operational_events
                    FOR EACH STATEMENT EXECUTE FUNCTION ops.reject_operational_event_change();
                """);

            migrationBuilder.CreateTable(
                name: "work_orders",
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
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_orders", x => x.id);
                    table.CheckConstraint("ck_work_orders_priority", "priority IN ('Low', 'Normal', 'High', 'Urgent')");
                    table.CheckConstraint("ck_work_orders_status", "status IN ('Open', 'Planned', 'InProgress', 'Completed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_work_orders_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "workorders",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_orders_sites_site_id_customer_id",
                        columns: x => new { x.site_id, x.customer_id },
                        principalSchema: "workorders",
                        principalTable: "sites",
                        principalColumns: new[] { "id", "customer_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "visits",
                schema: "workorders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visits", x => x.id);
                    table.CheckConstraint("ck_visits_start_before_end", "start_at < end_at");
                    table.CheckConstraint("ck_visits_status", "status IN ('Planned', 'InProgress', 'Completed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_visits_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalSchema: "workorders",
                        principalTable: "work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_operational_events_aggregate",
                schema: "ops",
                table: "operational_events",
                columns: new[] { "aggregate_type", "aggregate_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_operational_events_sequence",
                schema: "ops",
                table: "operational_events",
                column: "sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_visits_work_order_id",
                schema: "workorders",
                table: "visits",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_customer_id",
                schema: "workorders",
                table: "work_orders",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_site_id_customer_id",
                schema: "workorders",
                table: "work_orders",
                columns: new[] { "site_id", "customer_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER operational_events_no_truncate ON ops.operational_events;
                DROP TRIGGER operational_events_append_only ON ops.operational_events;
                DROP FUNCTION ops.reject_operational_event_change();
                """);

            migrationBuilder.DropTable(
                name: "operational_events",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "visits",
                schema: "workorders");

            migrationBuilder.DropTable(
                name: "work_orders",
                schema: "workorders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_sites_id_customer_id",
                schema: "workorders",
                table: "sites");
        }
    }
}
