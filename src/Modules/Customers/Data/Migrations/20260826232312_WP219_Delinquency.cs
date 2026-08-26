using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP219_Delinquency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dunning_notices",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    notice_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    served_on = table.Column<DateOnly>(type: "date", nullable: false),
                    arrears_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    days_past_due = table.Column<int>(type: "integer", nullable: false),
                    dunning_step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    waiting_period_days = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dunning_notices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dunning_steps",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notice_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    days_past_due = table.Column<int>(type: "integer", nullable: false),
                    minimum_arrears = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    waiting_period_days = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dunning_steps", x => x.id);
                });

            migrationBuilder.InsertData(
                schema: "customers",
                table: "dunning_steps",
                columns: new[] { "id", "currency", "days_past_due", "message", "minimum_arrears", "name", "notice_type", "sequence", "waiting_period_days" },
                values: new object[,]
                {
                    { new Guid("01a04084-3000-7175-bbbf-a17e6e3d1550"), "USD", 10, "Your account is past due. Please pay the outstanding balance to avoid further action. If you have already paid, thank you — please disregard this notice. Demo wording and demo timing following CUC's published customer-service information; not an authoritative notice.", 10.00m, "Payment reminder", "Reminder", 1, 0 },
                    { new Guid("01a04084-3000-7913-a281-a6a386bcf4df"), "USD", 45, "Service at this premise is scheduled for disconnection for non-payment. To avoid disconnection, pay the outstanding balance, or contact Customer Service to arrange payment, within ten days of the date of this notice. Any security deposit held will be applied to qualifying past-due amounts before service is disconnected. Demo wording and demo timing following CUC's published customer-service information and CNMI Public Law 16-17; not an authoritative notice.", 50.00m, "Notice of disconnection", "Disconnection", 3, 10 },
                    { new Guid("01a04084-3000-7be2-b5b2-f1676c455930"), "USD", 30, "Your account is delinquent. Pay the outstanding balance in full, or contact Customer Service to arrange payment, to avoid disconnection of service. Demo wording and demo timing following CUC's published customer-service information; not an authoritative notice.", 25.00m, "Notice of delinquency", "Delinquency", 2, 0 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_dunning_notices_account_type_served",
                schema: "customers",
                table: "dunning_notices",
                columns: new[] { "service_account_id", "notice_type", "served_on" });

            migrationBuilder.CreateIndex(
                name: "ux_dunning_steps_notice_type",
                schema: "customers",
                table: "dunning_steps",
                column: "notice_type",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_dunning_steps_sequence",
                schema: "customers",
                table: "dunning_steps",
                column: "sequence",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dunning_notices",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "dunning_steps",
                schema: "customers");
        }
    }
}
