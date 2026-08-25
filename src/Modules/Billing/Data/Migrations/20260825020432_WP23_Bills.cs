using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Billing.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP23_Bills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_rate_plans",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate_plan_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_rate_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bills",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    service_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate_plan_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rate_plan_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    rate_plan_effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    unit_of_measure = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    period_end = table.Column<DateOnly>(type: "date", nullable: false),
                    cycle_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    meter_reading_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    previous_reading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    current_reading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    consumption = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    amount_paid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bills", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bill_lines",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tier_sequence = table.Column<int>(type: "integer", nullable: true),
                    units = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    rate_per_unit = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bill_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_bill_lines_bill",
                        column: x => x.bill_id,
                        principalSchema: "billing",
                        principalTable: "bills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_account_rate_plans_account",
                schema: "billing",
                table: "account_rate_plans",
                column: "service_account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_bill_lines_sequence",
                schema: "billing",
                table: "bill_lines",
                columns: new[] { "bill_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bills_customer_id",
                schema: "billing",
                table: "bills",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_bills_status",
                schema: "billing",
                table: "bills",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_bills_account_cycle",
                schema: "billing",
                table: "bills",
                columns: new[] { "service_account_id", "cycle_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_bills_bill_number",
                schema: "billing",
                table: "bills",
                column: "bill_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_rate_plans",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "bill_lines",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "bills",
                schema: "billing");
        }
    }
}
