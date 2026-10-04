using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillsTeamsVehiclesEquipment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "resources");

            migrationBuilder.AddColumn<Guid>(
                name: "team_id",
                schema: "workforce",
                table: "technicians",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "workforce",
                table: "technicians",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "equipment",
                schema: "resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    asset_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "skills",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skills", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "workforce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vehicles",
                schema: "resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    registration_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    vehicle_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vehicles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "technician_skills",
                schema: "workforce",
                columns: table => new
                {
                    technician_id = table.Column<Guid>(type: "uuid", nullable: false),
                    skill_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technician_skills", x => new { x.technician_id, x.skill_id });
                    table.ForeignKey(
                        name: "FK_technician_skills_skills_skill_id",
                        column: x => x.skill_id,
                        principalSchema: "workforce",
                        principalTable: "skills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_technician_skills_technicians_technician_id",
                        column: x => x.technician_id,
                        principalSchema: "workforce",
                        principalTable: "technicians",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_technicians_team_id",
                schema: "workforce",
                table: "technicians",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_asset_code",
                schema: "resources",
                table: "equipment",
                column: "asset_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skills_code",
                schema: "workforce",
                table: "skills",
                column: "code",
                unique: true,
                filter: "code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_technician_skills_skill_id",
                schema: "workforce",
                table: "technician_skills",
                column: "skill_id");

            migrationBuilder.CreateIndex(
                name: "IX_vehicles_registration_number",
                schema: "resources",
                table: "vehicles",
                column: "registration_number",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_technicians_teams_team_id",
                schema: "workforce",
                table: "technicians",
                column: "team_id",
                principalSchema: "workforce",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_technicians_teams_team_id",
                schema: "workforce",
                table: "technicians");

            migrationBuilder.DropTable(
                name: "equipment",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "technician_skills",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "vehicles",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "skills",
                schema: "workforce");

            migrationBuilder.DropIndex(
                name: "IX_technicians_team_id",
                schema: "workforce",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "team_id",
                schema: "workforce",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "workforce",
                table: "technicians");
        }
    }
}
