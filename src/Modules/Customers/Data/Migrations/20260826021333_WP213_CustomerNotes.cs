using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP213_CustomerNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_notes",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    follow_up_on = table.Column<DateOnly>(type: "date", nullable: true),
                    link_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    linked_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    linked_reference = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    corrects_note_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_customer_notes_corrects",
                        column: x => x.corrects_note_id,
                        principalSchema: "customers",
                        principalTable: "customer_notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_notes_customer",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_notes_corrects_note_id",
                schema: "customers",
                table: "customer_notes",
                column: "corrects_note_id",
                filter: "\"corrects_note_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_customer_notes_customer_id",
                schema: "customers",
                table: "customer_notes",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_notes_link",
                schema: "customers",
                table: "customer_notes",
                columns: new[] { "link_kind", "linked_entity_id" },
                filter: "\"linked_entity_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_customer_notes_pinned",
                schema: "customers",
                table: "customer_notes",
                column: "customer_id",
                filter: "\"is_pinned\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_notes",
                schema: "customers");
        }
    }
}
