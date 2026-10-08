using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CrewCall.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerConsumerInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_inbox_messages",
                schema: "ops",
                table: "inbox_messages");

            migrationBuilder.AddColumn<string>(
                name: "consumer_name",
                schema: "ops",
                table: "inbox_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                // Every record so far belongs to the integration-audit consumer (Sprint 12). New records always name their
                // consumer, so the default is removed right away.
                defaultValue: "crewcall-integration-audit");

            migrationBuilder.Sql("ALTER TABLE ops.inbox_messages ALTER COLUMN consumer_name DROP DEFAULT;");

            migrationBuilder.AddPrimaryKey(
                name: "PK_inbox_messages",
                schema: "ops",
                table: "inbox_messages",
                columns: new[] { "consumer_name", "message_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_inbox_messages_consumer_name_not_blank",
                schema: "ops",
                table: "inbox_messages",
                sql: "btrim(consumer_name) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_inbox_messages",
                schema: "ops",
                table: "inbox_messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_inbox_messages_consumer_name_not_blank",
                schema: "ops",
                table: "inbox_messages");

            // Back to one record per message: only the integration-audit consumer's records can be kept.
            migrationBuilder.Sql("DELETE FROM ops.inbox_messages WHERE consumer_name <> 'crewcall-integration-audit';");

            migrationBuilder.DropColumn(
                name: "consumer_name",
                schema: "ops",
                table: "inbox_messages");

            migrationBuilder.AddPrimaryKey(
                name: "PK_inbox_messages",
                schema: "ops",
                table: "inbox_messages",
                column: "message_id");
        }
    }
}
