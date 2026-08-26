using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP217_ServiceTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_service_accounts_open_location",
                schema: "customers",
                table: "service_accounts");

            migrationBuilder.DropIndex(
                name: "ux_deposit_rules_class",
                schema: "customers",
                table: "deposit_rules");

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7a7d-8b76-34355f44b9d8"));

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7b13-b0b4-4a028a08b4e8"));

            migrationBuilder.RenameColumn(
                name: "amount",
                schema: "customers",
                table: "deposit_rules",
                newName: "minimum_amount");

            // BACKFILLED AS Electricity, not as the empty string EF generates. Every service account
            // that existed before this migration was an electricity account — it is the one supply
            // the demonstration utility distributes — and an empty service is not a member of the
            // enum, so an account carrying one would fail to materialise the first time anybody
            // listed the registry. WP-2.16's bill kind made the same call for the same reason.
            //
            // This is also what WORK_PACKAGES.md means by "the existing class-keyed rules migrate to
            // electric without changing what any current customer was assessed": every account keeps
            // the deposit it was assessed under, because every account keeps the service it was
            // implicitly on.
            migrationBuilder.AddColumn<string>(
                name: "service_type",
                schema: "customers",
                table: "service_accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Electricity");

            migrationBuilder.AlterColumn<string>(
                name: "description",
                schema: "customers",
                table: "deposit_rules",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            // No backfill needed and none given: the two class-keyed rules were deleted above, because
            // re-keying the schedule on (class x service) changes every rule's ReferenceId — a
            // rule's id is derived from its natural key, and the key gained a half. The eight rows
            // inserted below are the whole schedule. Electricity as the default anyway, so a row
            // arriving from anywhere else lands on the supply the utility actually distributes rather
            // than on a value the enum does not declare.
            migrationBuilder.AddColumn<string>(
                name: "service_type",
                schema: "customers",
                table: "deposit_rules",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Electricity");

            migrationBuilder.AddColumn<int>(
                name: "usage_months",
                schema: "customers",
                table: "deposit_rules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "usage_rate",
                schema: "customers",
                table: "deposit_rules",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.InsertData(
                schema: "customers",
                table: "deposit_rules",
                columns: new[] { "id", "currency", "customer_class", "description", "minimum_amount", "service_type", "usage_months", "usage_rate" },
                values: new object[,]
                {
                    { new Guid("01a03637-7800-7065-af71-6221df4735ea"), "USD", "Residential", "Residential electricity: the greater of $75 and two months of average usage at $0.3200/kWh. Demonstration figures — CUC's published residential deposit and its energy rate both move, and neither is quoted here as authoritative.", 75.00m, "Electricity", 2, 0.3200m },
                    { new Guid("01a03637-7800-7159-991a-fed1e0114a6e"), "USD", "Commercial", "Commercial gas: the greater of $250 and two months of average usage at $1.5000 per therm. Demonstration figures — see the residential gas rule.", 250.00m, "Gas", 2, 1.5000m },
                    { new Guid("01a03637-7800-73ce-aafd-d7d13f799782"), "USD", "Commercial", "Commercial wastewater: a flat $150. Unmetered — there is no wastewater meter, so there is nothing to average and no usage basis to apply. Demonstration figure.", 150.00m, "Wastewater", null, null },
                    { new Guid("01a03637-7800-7896-beab-6e1fb5e65cfa"), "USD", "Residential", "Residential water: the greater of $50 and two months of average usage at $2.5000 per cubic metre. Demonstration figures — the utility in this MVP distributes electricity only, and the water schedule exists so the module can express a service it does not yet supply.", 50.00m, "Water", 2, 2.5000m },
                    { new Guid("01a03637-7800-7990-9709-6e7948af367d"), "USD", "Residential", "Residential wastewater: a flat $30. Unmetered — there is no wastewater meter, so there is nothing to average and no usage basis to apply. Demonstration figure.", 30.00m, "Wastewater", null, null },
                    { new Guid("01a03637-7800-7a18-8779-ab6d1f2d9d9f"), "USD", "Residential", "Residential gas: the greater of $50 and two months of average usage at $1.5000 per therm. Demonstration figures — GridCore declares gas as a service type and the demonstration utility does not distribute it; the rule exists so the schedule is complete.", 50.00m, "Gas", 2, 1.5000m },
                    { new Guid("01a03637-7800-7d84-a36d-d09df560653d"), "USD", "Commercial", "Commercial water: the greater of $250 and two months of average usage at $2.5000 per cubic metre. Demonstration figures — see the residential water rule.", 250.00m, "Water", 2, 2.5000m },
                    { new Guid("01a03637-7800-7ff8-b74d-41c6cc4d344a"), "USD", "Commercial", "Commercial electricity: the greater of $450 and two months of average usage at $0.3200/kWh. Demonstration figures — CUC's published commercial deposit and its energy rate both move, and neither is quoted here as authoritative.", 450.00m, "Electricity", 2, 0.3200m }
                });

            migrationBuilder.CreateIndex(
                name: "ux_service_accounts_open_location",
                schema: "customers",
                table: "service_accounts",
                columns: new[] { "service_location_id", "service_type" },
                unique: true,
                filter: "\"status\" <> 'Closed'");

            migrationBuilder.CreateIndex(
                name: "ux_deposit_rules_class_service",
                schema: "customers",
                table: "deposit_rules",
                columns: new[] { "customer_class", "service_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_service_accounts_open_location",
                schema: "customers",
                table: "service_accounts");

            migrationBuilder.DropIndex(
                name: "ux_deposit_rules_class_service",
                schema: "customers",
                table: "deposit_rules");

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7065-af71-6221df4735ea"));

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7159-991a-fed1e0114a6e"));

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-73ce-aafd-d7d13f799782"));

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7896-beab-6e1fb5e65cfa"));

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7990-9709-6e7948af367d"));

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7a18-8779-ab6d1f2d9d9f"));

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7d84-a36d-d09df560653d"));

            migrationBuilder.DeleteData(
                schema: "customers",
                table: "deposit_rules",
                keyColumn: "id",
                keyValue: new Guid("01a03637-7800-7ff8-b74d-41c6cc4d344a"));

            migrationBuilder.DropColumn(
                name: "service_type",
                schema: "customers",
                table: "service_accounts");

            migrationBuilder.DropColumn(
                name: "service_type",
                schema: "customers",
                table: "deposit_rules");

            migrationBuilder.DropColumn(
                name: "usage_months",
                schema: "customers",
                table: "deposit_rules");

            migrationBuilder.DropColumn(
                name: "usage_rate",
                schema: "customers",
                table: "deposit_rules");

            migrationBuilder.RenameColumn(
                name: "minimum_amount",
                schema: "customers",
                table: "deposit_rules",
                newName: "amount");

            migrationBuilder.AlterColumn<string>(
                name: "description",
                schema: "customers",
                table: "deposit_rules",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.InsertData(
                schema: "customers",
                table: "deposit_rules",
                columns: new[] { "id", "amount", "currency", "customer_class", "description" },
                values: new object[,]
                {
                    { new Guid("01a03637-7800-7a7d-8b76-34355f44b9d8"), 450.00m, "USD", "Commercial", "One commercial connection: two months of a small-premises bill, refundable on close." },
                    { new Guid("01a03637-7800-7b13-b0b4-4a028a08b4e8"), 75.00m, "USD", "Residential", "One residential connection: two months of a typical household bill, refundable on close." }
                });

            migrationBuilder.CreateIndex(
                name: "ux_service_accounts_open_location",
                schema: "customers",
                table: "service_accounts",
                column: "service_location_id",
                unique: true,
                filter: "\"status\" <> 'Closed'");

            migrationBuilder.CreateIndex(
                name: "ux_deposit_rules_class",
                schema: "customers",
                table: "deposit_rules",
                column: "customer_class",
                unique: true);
        }
    }
}
