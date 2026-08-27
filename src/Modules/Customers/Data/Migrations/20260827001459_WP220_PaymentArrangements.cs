using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP220_PaymentArrangements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "arrangement_limits",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    maximum_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    maximum_instalments = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_arrangement_limits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_arrangements",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    arrangement_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    customer_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    arrears_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    down_payment = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    instalment_count = table.Column<int>(type: "integer", nullable: false),
                    interval_days = table.Column<int>(type: "integer", nullable: false),
                    arranged_on = table.Column<DateOnly>(type: "date", nullable: false),
                    limit_maximum_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    limit_maximum_instalments = table.Column<int>(type: "integer", nullable: false),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    approval_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_on = table.Column<DateOnly>(type: "date", nullable: true),
                    closed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_arrangements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "arrangement_instalments",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_arrangement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    paid_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    is_down_payment = table.Column<bool>(type: "boolean", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_arrangement_instalments", x => x.id);
                    table.ForeignKey(
                        name: "fk_arrangement_instalments_arrangement",
                        column: x => x.payment_arrangement_id,
                        principalSchema: "customers",
                        principalTable: "payment_arrangements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "customers",
                table: "arrangement_limits",
                columns: new[] { "id", "currency", "customer_class", "maximum_balance", "maximum_instalments", "notes" },
                values: new object[,]
                {
                    { new Guid("01a04317-5e00-7a7d-8b76-34355f44b9d8"), "USD", "Commercial", 5000.00m, 12, "Demo figures, set higher than the residential ceiling because a commercial arrears of several thousand dollars over a year is ordinary. Not an authoritative delegation." },
                    { new Guid("01a04317-5e00-7b13-b0b4-4a028a08b4e8"), "USD", "Residential", 1500.00m, 6, "Demo figures. CUC publishes that Customer Service will arrange payment rather than disconnect, and does not publish what a representative may agree without a supervisor; these ceilings are GridCore's own and are not an authoritative delegation." }
                });

            migrationBuilder.CreateIndex(
                name: "ix_arrangement_instalments_due_date",
                schema: "customers",
                table: "arrangement_instalments",
                column: "due_date");

            migrationBuilder.CreateIndex(
                name: "ux_arrangement_instalments_arrangement_sequence",
                schema: "customers",
                table: "arrangement_instalments",
                columns: new[] { "payment_arrangement_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_arrangement_limits_customer_class",
                schema: "customers",
                table: "arrangement_limits",
                column: "customer_class",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_arrangements_account_status",
                schema: "customers",
                table: "payment_arrangements",
                columns: new[] { "service_account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_payment_arrangements_number",
                schema: "customers",
                table: "payment_arrangements",
                column: "arrangement_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "arrangement_instalments",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "arrangement_limits",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "payment_arrangements",
                schema: "customers");
        }
    }
}
