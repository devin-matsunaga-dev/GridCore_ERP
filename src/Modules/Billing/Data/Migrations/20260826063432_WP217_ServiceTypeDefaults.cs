using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Billing.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP217_ServiceTypeDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_rate_plans_default_effective",
                schema: "billing",
                table: "rate_plans");

            migrationBuilder.CreateIndex(
                name: "ux_rate_plans_default_effective",
                schema: "billing",
                table: "rate_plans",
                columns: new[] { "service_type", "is_default", "effective_from" },
                unique: true,
                filter: "is_default");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_rate_plans_default_effective",
                schema: "billing",
                table: "rate_plans");

            migrationBuilder.CreateIndex(
                name: "ux_rate_plans_default_effective",
                schema: "billing",
                table: "rate_plans",
                columns: new[] { "is_default", "effective_from" },
                unique: true,
                filter: "is_default");
        }
    }
}
