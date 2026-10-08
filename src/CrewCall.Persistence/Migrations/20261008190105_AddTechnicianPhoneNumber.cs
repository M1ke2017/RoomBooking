using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTechnicianPhoneNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "phone_number",
                schema: "workforce",
                table: "technicians",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_technicians_phone_number_e164",
                schema: "workforce",
                table: "technicians",
                sql: "phone_number ~ '^\\+[1-9][0-9]{6,14}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_technicians_phone_number_e164",
                schema: "workforce",
                table: "technicians");

            migrationBuilder.DropColumn(
                name: "phone_number",
                schema: "workforce",
                table: "technicians");
        }
    }
}
