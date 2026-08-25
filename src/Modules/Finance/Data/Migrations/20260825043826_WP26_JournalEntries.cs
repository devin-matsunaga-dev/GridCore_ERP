using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP26_JournalEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "journal_entries",
                schema: "finance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    posted_on = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    total_debits = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_credits = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "journal_lines",
                schema: "finance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    journal_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    credit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_journal_lines_account",
                        column: x => x.account_id,
                        principalSchema: "finance",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_journal_lines_entry",
                        column: x => x.journal_entry_id,
                        principalSchema: "finance",
                        principalTable: "journal_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_customer",
                schema: "finance",
                table: "journal_entries",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_posted_on",
                schema: "finance",
                table: "journal_entries",
                column: "posted_on");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_service_account",
                schema: "finance",
                table: "journal_entries",
                column: "service_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_source",
                schema: "finance",
                table: "journal_entries",
                column: "source");

            migrationBuilder.CreateIndex(
                name: "ux_journal_entries_event",
                schema: "finance",
                table: "journal_entries",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_journal_entries_number",
                schema: "finance",
                table: "journal_entries",
                column: "entry_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_journal_lines_account",
                schema: "finance",
                table: "journal_lines",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_journal_lines_sequence",
                schema: "finance",
                table: "journal_lines",
                columns: new[] { "journal_entry_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "journal_lines",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "journal_entries",
                schema: "finance");
        }
    }
}
