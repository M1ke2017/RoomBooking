using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkforceAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            // Existing technicians get a time context through temporary column defaults (Poland, the system's home market;
            // review and correct them through the data, not the schema). The defaults are dropped again below, so new
            // rows must always provide both values.
            migrationBuilder.AddColumn<string>(
                name: "country_code",
                schema: "workforce",
                table: "technicians",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: false,
                defaultValue: "PL");

            migrationBuilder.AddColumn<string>(
                name: "timezone_id",
                schema: "workforce",
                table: "technicians",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Europe/Warsaw");

            migrationBuilder.Sql("""
                ALTER TABLE workforce.technicians ALTER COLUMN country_code DROP DEFAULT;
                ALTER TABLE workforce.technicians ALTER COLUMN timezone_id DROP DEFAULT;
                """);

            migrationBuilder.CreateTable(
                name: "holiday_calendar",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: false),
                    region_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holiday_calendar", x => x.id);
                    table.CheckConstraint("ck_holiday_calendar_country_code", "country_code ~ '^[A-Z]{2}$'");
                    table.CheckConstraint("ck_holiday_calendar_region_code", "region_code IS NULL OR region_code ~ '^[A-Z0-9]{1,3}$'");
                });

            migrationBuilder.CreateTable(
                name: "technician_absences",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_absences", x => x.id);
                    table.CheckConstraint("ck_technician_absences_start_before_end", "start_at < end_at");
                    table.CheckConstraint("ck_technician_absences_type", "type IN ('Vacation', 'SickLeave', 'Training', 'Other')");
                    table.ForeignKey(
                        name: "FK_technician_absences_technicians_technician_id",
                        column: x => x.technician_id,
                        principalSchema: "workforce",
                        principalTable: "technicians",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "technician_working_hours",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    start_local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_working_hours", x => x.id);
                    table.CheckConstraint("ck_technician_working_hours_day_of_week", "day_of_week IN ('Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday')");
                    table.CheckConstraint("ck_technician_working_hours_start_before_end", "end_local_time = '00:00' OR start_local_time < end_local_time");
                    table.ForeignKey(
                        name: "FK_technician_working_hours_technicians_technician_id",
                        column: x => x.technician_id,
                        principalSchema: "workforce",
                        principalTable: "technicians",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_technicians_country_code",
                schema: "workforce",
                table: "technicians",
                sql: "country_code ~ '^[A-Z]{2}$'");

            migrationBuilder.CreateIndex(
                name: "IX_holiday_calendar_country_code_date_region_code",
                schema: "workforce",
                table: "holiday_calendar",
                columns: new[] { "country_code", "date", "region_code" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_technician_absences_technician_id_start_at_end_at",
                schema: "workforce",
                table: "technician_absences",
                columns: new[] { "technician_id", "start_at", "end_at" });

            migrationBuilder.CreateIndex(
                name: "IX_technician_working_hours_technician_id_day_of_week",
                schema: "workforce",
                table: "technician_working_hours",
                columns: new[] { "technician_id", "day_of_week" });

            // No overlap, enforced by the database too (EF Core cannot model exclusion constraints). Half-open ranges
            // ('[)'), so touching ranges are allowed. Working-hours times are placed on a fixed date to form a range;
            // an end of 00:00 means 24:00, i.e. midnight of the following day.
            migrationBuilder.Sql("""
                ALTER TABLE workforce.technician_working_hours
                    ADD CONSTRAINT ex_technician_working_hours_no_overlap
                    EXCLUDE USING gist (
                        technician_id WITH =,
                        day_of_week WITH =,
                        tsrange(
                            DATE '2000-01-01' + start_local_time,
                            CASE WHEN end_local_time = TIME '00:00' THEN TIMESTAMP '2000-01-02 00:00'
                                 ELSE DATE '2000-01-01' + end_local_time END,
                            '[)') WITH &&);

                ALTER TABLE workforce.technician_absences
                    ADD CONSTRAINT ex_technician_absences_no_overlap
                    EXCLUDE USING gist (technician_id WITH =, tstzrange(start_at, end_at, '[)') WITH &&);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holiday_calendar",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "technician_absences",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "technician_working_hours",
                schema: "workforce");

            migrationBuilder.DropCheckConstraint(
                name: "ck_technicians_country_code",
                schema: "workforce",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "country_code",
                schema: "workforce",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "timezone_id",
                schema: "workforce",
                table: "technicians");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:btree_gist", ",,");
        }
    }
}
