using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Billing.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP08_RatePlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "rate_plans",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    service_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    unit_of_measure = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    monthly_service_charge = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rate_plan_tiers",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    up_to_units = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    rate_per_unit = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_plan_tiers", x => x.id);
                    table.ForeignKey(
                        name: "fk_rate_plan_tiers_rate_plans",
                        column: x => x.rate_plan_id,
                        principalSchema: "billing",
                        principalTable: "rate_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                name: "ux_rate_plan_tiers_sequence",
                schema: "billing",
                table: "rate_plan_tiers",
                columns: new[] { "rate_plan_id", "sequence" },
                unique: true);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_plan_tiers",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "rate_plans",
                schema: "billing");
        }
    }
}
