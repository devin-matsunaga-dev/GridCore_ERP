using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Billing.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP219_LateCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "amount",
                schema: "billing",
                table: "fee_schedule",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            // "Flat", not the empty string EF generates. Every fee published before this package is a
            // flat one, and an empty string is not a FeeBasis member — a backfilled row carrying it
            // would fail to materialise the first time anybody read the schedule. The same trap
            // WP-2.16's bill kind and WP-2.17's service type each had to step around.
            migrationBuilder.AddColumn<string>(
                name: "basis",
                schema: "billing",
                table: "fee_schedule",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Flat");

            migrationBuilder.AddColumn<decimal>(
                name: "rate",
                schema: "billing",
                table: "fee_schedule",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            // "Flat" again, and here it is the only backfill there is: the seeded schedule rows above
            // are corrected by UpdateData, but every charge already raised is real data nothing
            // re-seeds. All of them were priced off a flat fee, because until this package there was
            // no other kind.
            migrationBuilder.AddColumn<string>(
                name: "basis",
                schema: "billing",
                table: "account_charges",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Flat");

            migrationBuilder.AddColumn<decimal>(
                name: "basis_amount",
                schema: "billing",
                table: "account_charges",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "rate",
                schema: "billing",
                table: "account_charges",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "late_charge_assessments",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    assessed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    days_past_due = table.Column<int>(type: "integer", nullable: false),
                    basis_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    fee_schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_late_charge_assessments", x => x.id);
                });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "fee_schedule",
                keyColumn: "id",
                keyValue: new Guid("01a03b5d-d400-7726-bbcd-93fad896309f"),
                columns: new[] { "basis", "rate" },
                values: new object[] { "Flat", null });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "fee_schedule",
                keyColumn: "id",
                keyValue: new Guid("01a03b5d-d400-7903-9ba4-0e2b130bef15"),
                columns: new[] { "basis", "rate" },
                values: new object[] { "Flat", null });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "fee_schedule",
                keyColumn: "id",
                keyValue: new Guid("01a03b5d-d400-7935-ae46-cb50fc10e937"),
                columns: new[] { "basis", "rate" },
                values: new object[] { "Flat", null });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "fee_schedule",
                keyColumn: "id",
                keyValue: new Guid("01a03b5d-d400-7e42-80c3-b9f9b4e18513"),
                columns: new[] { "basis", "rate" },
                values: new object[] { "Flat", null });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "fee_schedule",
                keyColumn: "id",
                keyValue: new Guid("01a03b5d-d400-7e49-8b5b-7db00eb0ae92"),
                columns: new[] { "basis", "rate" },
                values: new object[] { "Flat", null });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "fee_schedule",
                keyColumn: "id",
                keyValue: new Guid("01a03b5d-d400-7e9d-876d-604b24264e33"),
                columns: new[] { "basis", "rate" },
                values: new object[] { "Flat", null });

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "fee_schedule",
                keyColumn: "id",
                keyValue: new Guid("01a03b5d-d400-7f1a-99ee-1a8a6b1ee323"),
                columns: new[] { "basis", "rate" },
                values: new object[] { "Flat", null });

            migrationBuilder.InsertData(
                schema: "billing",
                table: "fee_schedule",
                columns: new[] { "id", "amount", "basis", "code", "currency", "description", "effective_from", "name", "rate", "service_type" },
                values: new object[] { new Guid("01a03b5d-d400-7f47-8dfa-fb2ea961cf60"), null, "Rate", "LateCharge", "USD", "One per cent per month of the past-due balance, assessed once per bill per month while it remains unpaid. Demo figure following CUC's published customer-service information and the delinquency regime of CNMI Public Law 16-17; that schedule changes without notice, so this is a demo rate and not an authoritative charge.", new DateOnly(2025, 1, 1), "Late payment charge", 0.0100m, "Electricity" });

            migrationBuilder.CreateIndex(
                name: "ix_late_charge_assessments_account_period",
                schema: "billing",
                table: "late_charge_assessments",
                columns: new[] { "service_account_id", "period_start" });

            migrationBuilder.CreateIndex(
                name: "ux_late_charge_assessments_bill_period",
                schema: "billing",
                table: "late_charge_assessments",
                columns: new[] { "bill_id", "period_start" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "late_charge_assessments",
                schema: "billing");

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "fee_schedule",
                keyColumn: "id",
                keyValue: new Guid("01a03b5d-d400-7f47-8dfa-fb2ea961cf60"));

            migrationBuilder.DropColumn(
                name: "basis",
                schema: "billing",
                table: "fee_schedule");

            migrationBuilder.DropColumn(
                name: "rate",
                schema: "billing",
                table: "fee_schedule");

            migrationBuilder.DropColumn(
                name: "basis",
                schema: "billing",
                table: "account_charges");

            migrationBuilder.DropColumn(
                name: "basis_amount",
                schema: "billing",
                table: "account_charges");

            migrationBuilder.DropColumn(
                name: "rate",
                schema: "billing",
                table: "account_charges");

            migrationBuilder.AlterColumn<decimal>(
                name: "amount",
                schema: "billing",
                table: "fee_schedule",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2,
                oldNullable: true);
        }
    }
}
