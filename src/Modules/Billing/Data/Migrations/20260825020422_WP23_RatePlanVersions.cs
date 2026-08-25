using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Billing.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP23_RatePlanVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A TARIFF IS A CODE AND A DATE, and this migration is what makes that true.
            //
            // WP-0.8 shipped one version of each plan and keyed a row's id on its code alone, which
            // made repricing impossible: a second RES-STD would derive the same ReferenceId and so
            // the same primary key, and ux_rate_plans_code would have refused it anyway. A utility
            // that cannot republish a tariff is not one, and "effective-dating picks the right rate"
            // is untestable while every code has exactly one version.
            //
            // So the two indexes are rebuilt on (code, effective_from) and (is_default,
            // effective_from), and every reference row is re-keyed. A code change is an id change
            // (WP-1.4's island warehouses), so the rows are deleted and reinserted rather than
            // updated — which is safe here because nothing references billing.rate_plans yet: the
            // bills that will are created by the migration after this one, and a bill stamps its
            // tariff's code, name and rates onto itself rather than pointing at them.
            //
            // Three rows go in where two came out. The residential tariff gains a revision effective
            // 1 July 2026, and both shipped tariffs move their original effective date back to
            // 1 January 2025 — WP-0.8's 1 January 2026 falls AFTER five of the twelve monthly cycles
            // the demo seeder lays down, so those months priced to nothing at all.
            //
            // DefaultRatePlans.AuthoredAt is unchanged, as it must be. What changed is the natural
            // key hashed with it — see RatePlan.KeyFor.
            migrationBuilder.DropIndex(
                name: "ux_rate_plans_code",
                schema: "billing",
                table: "rate_plans");

            migrationBuilder.DropIndex(
                name: "ux_rate_plans_default",
                schema: "billing",
                table: "rate_plans");

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-723d-bcd7-80ea4ecbf39c"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7545-8b53-76122bf03982"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7b3f-85a9-3f4876dbe728"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7c6f-9b8c-d303bdc8e7c9"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7ddb-9d30-63f09b9c7610"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plans",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7440-82af-b70efb8671c9"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plans",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7eb4-b30b-55d2b561d9bb"));

            migrationBuilder.InsertData(
                schema: "billing",
                table: "rate_plans",
                columns: new[] { "id", "code", "currency", "effective_from", "is_default", "monthly_service_charge", "name", "service_type", "unit_of_measure" },
                values: new object[,]
                {
                    { new Guid("01a03111-1c00-751c-a792-94a84d62db9a"), "RES-STD", "USD", new DateOnly(2025, 1, 1), true, 12.50m, "Residential standard", "Electricity", "kWh" },
                    { new Guid("01a03111-1c00-7b1b-a2bb-cd85284d214f"), "COM-STD", "USD", new DateOnly(2025, 1, 1), false, 45.00m, "Commercial standard", "Electricity", "kWh" },
                    { new Guid("01a03111-1c00-7c54-a54e-2d4921568a7c"), "RES-STD", "USD", new DateOnly(2026, 7, 1), true, 13.75m, "Residential standard", "Electricity", "kWh" }
                });

            migrationBuilder.InsertData(
                schema: "billing",
                table: "rate_plan_tiers",
                columns: new[] { "id", "rate_per_unit", "rate_plan_id", "sequence", "up_to_units" },
                values: new object[,]
                {
                    { new Guid("01a03111-1c00-7002-806d-6835aacf0b0c"), 0.1225m, new Guid("01a03111-1c00-7c54-a54e-2d4921568a7c"), 1, 500m },
                    { new Guid("01a03111-1c00-73e3-9a13-986fb86fc536"), 0.1290m, new Guid("01a03111-1c00-7b1b-a2bb-cd85284d214f"), 1, 2000m },
                    { new Guid("01a03111-1c00-74fb-bae8-d5da5f7394c3"), 0.1105m, new Guid("01a03111-1c00-7b1b-a2bb-cd85284d214f"), 2, null },
                    { new Guid("01a03111-1c00-7bdb-958f-15b5c5a3cc68"), 0.1620m, new Guid("01a03111-1c00-751c-a792-94a84d62db9a"), 3, null },
                    { new Guid("01a03111-1c00-7c16-96c8-173e40ae8222"), 0.1145m, new Guid("01a03111-1c00-751c-a792-94a84d62db9a"), 1, 500m },
                    { new Guid("01a03111-1c00-7c1b-bc31-49b94fcf81a8"), 0.1385m, new Guid("01a03111-1c00-751c-a792-94a84d62db9a"), 2, 1000m },
                    { new Guid("01a03111-1c00-7d31-98e9-32d3927853be"), 0.1480m, new Guid("01a03111-1c00-7c54-a54e-2d4921568a7c"), 2, 1000m },
                    { new Guid("01a03111-1c00-7ffe-a8fb-2c329fdfad51"), 0.1735m, new Guid("01a03111-1c00-7c54-a54e-2d4921568a7c"), 3, null }
                });

            migrationBuilder.CreateIndex(
                name: "ux_rate_plans_code_effective",
                schema: "billing",
                table: "rate_plans",
                columns: new[] { "code", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rate_plans_default_effective",
                schema: "billing",
                table: "rate_plans",
                columns: new[] { "is_default", "effective_from" },
                unique: true,
                filter: "is_default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_rate_plans_code_effective",
                schema: "billing",
                table: "rate_plans");

            migrationBuilder.DropIndex(
                name: "ux_rate_plans_default_effective",
                schema: "billing",
                table: "rate_plans");

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7002-806d-6835aacf0b0c"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-73e3-9a13-986fb86fc536"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-74fb-bae8-d5da5f7394c3"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7bdb-958f-15b5c5a3cc68"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7c16-96c8-173e40ae8222"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7c1b-bc31-49b94fcf81a8"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7d31-98e9-32d3927853be"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plan_tiers",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7ffe-a8fb-2c329fdfad51"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plans",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-751c-a792-94a84d62db9a"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plans",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7b1b-a2bb-cd85284d214f"));

            migrationBuilder.DeleteData(
                schema: "billing",
                table: "rate_plans",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7c54-a54e-2d4921568a7c"));

            migrationBuilder.InsertData(
                schema: "billing",
                table: "rate_plans",
                columns: new[] { "id", "code", "currency", "effective_from", "is_default", "monthly_service_charge", "name", "service_type", "unit_of_measure" },
                values: new object[,]
                {
                    { new Guid("01a03111-1c00-7440-82af-b70efb8671c9"), "COM-STD", "USD", new DateOnly(2026, 1, 1), false, 45.00m, "Commercial standard", "Electricity", "kWh" },
                    { new Guid("01a03111-1c00-7eb4-b30b-55d2b561d9bb"), "RES-STD", "USD", new DateOnly(2026, 1, 1), true, 12.50m, "Residential standard", "Electricity", "kWh" }
                });

            migrationBuilder.InsertData(
                schema: "billing",
                table: "rate_plan_tiers",
                columns: new[] { "id", "rate_per_unit", "rate_plan_id", "sequence", "up_to_units" },
                values: new object[,]
                {
                    { new Guid("01a03111-1c00-723d-bcd7-80ea4ecbf39c"), 0.1385m, new Guid("01a03111-1c00-7eb4-b30b-55d2b561d9bb"), 2, 1000m },
                    { new Guid("01a03111-1c00-7545-8b53-76122bf03982"), 0.1145m, new Guid("01a03111-1c00-7eb4-b30b-55d2b561d9bb"), 1, 500m },
                    { new Guid("01a03111-1c00-7b3f-85a9-3f4876dbe728"), 0.1620m, new Guid("01a03111-1c00-7eb4-b30b-55d2b561d9bb"), 3, null },
                    { new Guid("01a03111-1c00-7c6f-9b8c-d303bdc8e7c9"), 0.1105m, new Guid("01a03111-1c00-7440-82af-b70efb8671c9"), 2, null },
                    { new Guid("01a03111-1c00-7ddb-9d30-63f09b9c7610"), 0.1290m, new Guid("01a03111-1c00-7440-82af-b70efb8671c9"), 1, 2000m }
                });

            migrationBuilder.CreateIndex(
                name: "ux_rate_plans_code",
                schema: "billing",
                table: "rate_plans",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_rate_plans_default",
                schema: "billing",
                table: "rate_plans",
                column: "is_default",
                unique: true,
                filter: "is_default");
        }
    }
}
