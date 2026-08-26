using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Billing.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP216_FeeSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "unit_of_measure",
                schema: "billing",
                table: "bills",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<string>(
                name: "rate_plan_name",
                schema: "billing",
                table: "bills",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AlterColumn<Guid>(
                name: "rate_plan_id",
                schema: "billing",
                table: "bills",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<DateOnly>(
                name: "rate_plan_effective_from",
                schema: "billing",
                table: "bills",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.AlterColumn<string>(
                name: "rate_plan_code",
                schema: "billing",
                table: "bills",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<Guid>(
                name: "meter_reading_id",
                schema: "billing",
                table: "bills",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "meter_number",
                schema: "billing",
                table: "bills",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24);

            migrationBuilder.AlterColumn<Guid>(
                name: "meter_id",
                schema: "billing",
                table: "bills",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<decimal>(
                name: "fee_amount",
                schema: "billing",
                table: "bills",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // BACKFILLED AS Consumption, not as the empty string EF generates. Every bill that
            // existed before this migration was raised from a meter reading against a tariff, which
            // is exactly what BillKind.Consumption means — and an empty kind is not a member of the
            // enum, so a bill carrying one would fail to materialise the first time anybody read it.
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "billing",
                table: "bills",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Consumption");

            migrationBuilder.CreateTable(
                name: "account_charges",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    fee_code = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    fee_schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    raised_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bill_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_charges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fee_schedule",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    service_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fee_schedule", x => x.id);
                });

            migrationBuilder.InsertData(
                schema: "billing",
                table: "fee_schedule",
                columns: new[] { "id", "amount", "code", "currency", "description", "effective_from", "name", "service_type" },
                values: new object[,]
                {
                    { new Guid("01a03b5d-d400-7726-bbcd-93fad896309f"), 50.00m, "Reconnection", "USD", "Levied when supply is restored after it was cut for non-payment. Demo figure following CUC's published customer-service information; not an authoritative charge.", new DateOnly(2025, 1, 1), "Reconnection fee", "Electricity" },
                    { new Guid("01a03b5d-d400-7903-9ba4-0e2b130bef15"), 550.00m, "UnauthorizedConnection", "USD", "The penalty for taking supply without an account or interfering with a meter, levied on top of an estimate of the unbilled usage. Demo figure following CUC's published customer-service information; not an authoritative charge.", new DateOnly(2025, 1, 1), "Unauthorized connection penalty", "Electricity" },
                    { new Guid("01a03b5d-d400-7935-ae46-cb50fc10e937"), 50.00m, "Inspection", "USD", "Levied for inspecting a customer's installation before supply is established. Demo figure following CUC's published customer-service information; not an authoritative charge.", new DateOnly(2025, 1, 1), "Installation inspection fee", "Electricity" },
                    { new Guid("01a03b5d-d400-7e42-80c3-b9f9b4e18513"), 60.00m, "Reconnection", "USD", "Republished figure, effective 1 July 2026. Demo figure following CUC's published customer-service information; not an authoritative charge.", new DateOnly(2026, 7, 1), "Reconnection fee", "Electricity" },
                    { new Guid("01a03b5d-d400-7e49-8b5b-7db00eb0ae92"), 75.00m, "MeterTest", "USD", "Levied when a customer asks for their meter to be tested. Refundable where the meter is found to be faulty, which is WP-3.8's business rather than the schedule's. Demo figure following CUC's published customer-service information; not an authoritative charge.", new DateOnly(2025, 1, 1), "Meter test fee", "Electricity" },
                    { new Guid("01a03b5d-d400-7e9d-876d-604b24264e33"), 135.00m, "ServiceConnection", "USD", "Levied once when supply is established at a premise, covering the meter and the service drop. Demo figure following CUC's published customer-service information; that schedule changes without notice, so this is a demo schedule and not an authoritative charge.", new DateOnly(2025, 1, 1), "Service connection fee", "Electricity" },
                    { new Guid("01a03b5d-d400-7f1a-99ee-1a8a6b1ee323"), 25.00m, "ReturnedPayment", "USD", "Levied when a payment that settled is returned unpaid by the bank. Demo figure following CUC's published customer-service information; not an authoritative charge.", new DateOnly(2025, 1, 1), "Returned payment fee", "Electricity" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_charges_account_status",
                schema: "billing",
                table: "account_charges",
                columns: new[] { "service_account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_account_charges_bill_id",
                schema: "billing",
                table: "account_charges",
                column: "bill_id");

            migrationBuilder.CreateIndex(
                name: "ux_fee_schedule_code_effective",
                schema: "billing",
                table: "fee_schedule",
                columns: new[] { "code", "effective_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_charges",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "fee_schedule",
                schema: "billing");

            migrationBuilder.DropColumn(
                name: "fee_amount",
                schema: "billing",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "billing",
                table: "bills");

            migrationBuilder.AlterColumn<string>(
                name: "unit_of_measure",
                schema: "billing",
                table: "bills",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "rate_plan_name",
                schema: "billing",
                table: "bills",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "rate_plan_id",
                schema: "billing",
                table: "bills",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "rate_plan_effective_from",
                schema: "billing",
                table: "bills",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1),
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "rate_plan_code",
                schema: "billing",
                table: "bills",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "meter_reading_id",
                schema: "billing",
                table: "bills",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "meter_number",
                schema: "billing",
                table: "bills",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(24)",
                oldMaxLength: 24,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "meter_id",
                schema: "billing",
                table: "bills",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
