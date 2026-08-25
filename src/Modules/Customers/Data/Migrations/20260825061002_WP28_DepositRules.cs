using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP28_DepositRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deposit_rules",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deposit_rules", x => x.id);
                });

            migrationBuilder.InsertData(
                schema: "customers",
                table: "deposit_rules",
                columns: new[] { "id", "amount", "customer_class", "description" },
                values: new object[,]
                {
                    { new Guid("01a03637-7800-7a7d-8b76-34355f44b9d8"), 450.00m, "Commercial", "One commercial connection: two months of a small-premises bill, refundable on close." },
                    { new Guid("01a03637-7800-7b13-b0b4-4a028a08b4e8"), 75.00m, "Residential", "One residential connection: two months of a typical household bill, refundable on close." }
                });

            migrationBuilder.CreateIndex(
                name: "ux_deposit_rules_class",
                schema: "customers",
                table: "deposit_rules",
                column: "customer_class",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deposit_rules",
                schema: "customers");
        }
    }
}
