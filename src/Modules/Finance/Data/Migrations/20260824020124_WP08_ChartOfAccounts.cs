using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP08_ChartOfAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "finance");

            migrationBuilder.CreateTable(
                name: "accounts",
                schema: "finance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                });

            migrationBuilder.InsertData(
                schema: "finance",
                table: "accounts",
                columns: new[] { "id", "code", "name", "type" },
                values: new object[,]
                {
                    { new Guid("01a03111-1c00-7037-a7df-af26abe1265b"), "1300", "Inventory", "Asset" },
                    { new Guid("01a03111-1c00-7051-8175-845988f13f61"), "1000", "Cash at bank", "Asset" },
                    { new Guid("01a03111-1c00-7090-9470-20e033534635"), "4000", "Utility revenue", "Revenue" },
                    { new Guid("01a03111-1c00-70a1-8afd-46c582f82b4c"), "5900", "Bad debt expense", "Expense" },
                    { new Guid("01a03111-1c00-7176-aeb3-1e601c3877c8"), "3000", "Retained earnings", "Equity" },
                    { new Guid("01a03111-1c00-71a8-b544-cf93c245178c"), "2000", "Accounts payable", "Liability" },
                    { new Guid("01a03111-1c00-7299-806d-517825c0529d"), "5100", "Maintenance and repairs", "Expense" },
                    { new Guid("01a03111-1c00-75fd-ac96-3805de594b61"), "1400", "Utility plant in service", "Asset" },
                    { new Guid("01a03111-1c00-7989-8c7b-b333c6ad353c"), "4100", "Connection and service fees", "Revenue" },
                    { new Guid("01a03111-1c00-7b98-a8ca-390581b72629"), "1100", "Accounts receivable", "Asset" },
                    { new Guid("01a03111-1c00-7cf6-9ce9-e7aca4e0f11b"), "5200", "Materials and supplies", "Expense" },
                    { new Guid("01a03111-1c00-7e52-a0c4-102d65865bde"), "4200", "Late payment fees", "Revenue" },
                    { new Guid("01a03111-1c00-7f51-b1ea-0c5a3e7f4c5f"), "2100", "Customer deposits", "Liability" },
                    { new Guid("01a03111-1c00-7f83-b5d2-a98f83f0aed5"), "2200", "Accrued liabilities", "Liability" },
                    { new Guid("01a03111-1c00-7f8e-b4b7-2b6e0c9e88b3"), "5000", "Purchased power", "Expense" }
                });

            migrationBuilder.CreateIndex(
                name: "ux_accounts_code",
                schema: "finance",
                table: "accounts",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "accounts",
                schema: "finance");
        }
    }
}
