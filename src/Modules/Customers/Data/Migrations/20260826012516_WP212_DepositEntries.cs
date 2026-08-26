using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP212_DepositEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "customers",
                table: "deposit_rules",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "deposit_entries",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    is_interest_bearing = table.Column<bool>(type: "boolean", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bill_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposit_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_deposit_entries_customer",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7a7d-8b76-34355f44b9d8"),
                column: "currency",
                value: "USD");

            migrationBuilder.UpdateData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7b13-b0b4-4a028a08b4e8"),
                column: "currency",
                value: "USD");

            migrationBuilder.CreateIndex(
                name: "ix_deposit_entries_bill_id",
                schema: "customers",
                table: "deposit_entries",
                column: "bill_id",
                filter: "\"bill_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deposit_entries_customer_id",
                schema: "customers",
                table: "deposit_entries",
                column: "customer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deposit_entries",
                schema: "customers");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "customers",
                table: "deposit_rules");
        }
    }
}
