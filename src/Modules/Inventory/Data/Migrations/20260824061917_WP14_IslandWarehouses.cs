using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP14_IslandWarehouses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // fk_stock_levels_warehouse and fk_stock_movements_warehouse are both ON DELETE
            // RESTRICT, so whatever sat on the retired shelves has to go before the warehouses
            // themselves can. Two cases, and this is correct for each: a database seeing WP-1.4
            // for the first time had these tables created empty moments ago by WP14_Items, so
            // this is a no-op; a developer database that already ran WP-1.4 and its demo seeder
            // loses the stock those three stores held, which can only be demo stock, because the
            // stock tables ship in this same work package and nothing else has written to them.
            // Re-seed by deleting the 'inventory.stock' row from platform.demo_seed_records.
            migrationBuilder.Sql(
                """
                delete from inventory.stock_movements
                where warehouse_id in (
                    '01a03111-1c00-7583-ad03-8fbf7e6529e6', -- MAIN
                    '01a03111-1c00-74a4-89fb-589a0a731112', -- NORTH
                    '01a03111-1c00-7d5d-9d3e-2a1ea67ec09e'  -- YARD
                );

                delete from inventory.stock_levels
                where warehouse_id in (
                    '01a03111-1c00-7583-ad03-8fbf7e6529e6',
                    '01a03111-1c00-74a4-89fb-589a0a731112',
                    '01a03111-1c00-7d5d-9d3e-2a1ea67ec09e'
                );
                """);

            migrationBuilder.DeleteData(
                schema: "inventory",
                table: "warehouses",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-74a4-89fb-589a0a731112"));

            migrationBuilder.DeleteData(
                schema: "inventory",
                table: "warehouses",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7583-ad03-8fbf7e6529e6"));

            migrationBuilder.DeleteData(
                schema: "inventory",
                table: "warehouses",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7d5d-9d3e-2a1ea67ec09e"));

            migrationBuilder.InsertData(
                schema: "inventory",
                table: "warehouses",
                columns: new[] { "id", "code", "is_active", "location", "name" },
                values: new object[,]
                {
                    { new Guid("01a03111-1c00-7448-b032-95181b438bb0"), "TINIAN", true, "San Jose, Tinian", "Tinian Warehouse" },
                    { new Guid("01a03111-1c00-7c0d-9c1e-e98375f2c81d"), "LB", true, "Lower Base, Saipan", "Lower Base Warehouse" },
                    { new Guid("01a03111-1c00-7d37-a6f3-535f3ba97c35"), "ROTA", true, "Songsong, Rota", "Rota Warehouse" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetrically: the island stores cannot be removed while stock sits on them.
            migrationBuilder.Sql(
                """
                delete from inventory.stock_movements
                where warehouse_id in (
                    '01a03111-1c00-7c0d-9c1e-e98375f2c81d', -- LB
                    '01a03111-1c00-7d37-a6f3-535f3ba97c35', -- ROTA
                    '01a03111-1c00-7448-b032-95181b438bb0'  -- TINIAN
                );

                delete from inventory.stock_levels
                where warehouse_id in (
                    '01a03111-1c00-7c0d-9c1e-e98375f2c81d',
                    '01a03111-1c00-7d37-a6f3-535f3ba97c35',
                    '01a03111-1c00-7448-b032-95181b438bb0'
                );
                """);

            migrationBuilder.DeleteData(
                schema: "inventory",
                table: "warehouses",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7448-b032-95181b438bb0"));

            migrationBuilder.DeleteData(
                schema: "inventory",
                table: "warehouses",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7c0d-9c1e-e98375f2c81d"));

            migrationBuilder.DeleteData(
                schema: "inventory",
                table: "warehouses",
                keyColumn: "id",
                keyValue: new Guid("01a03111-1c00-7d37-a6f3-535f3ba97c35"));

            migrationBuilder.InsertData(
                schema: "inventory",
                table: "warehouses",
                columns: new[] { "id", "code", "is_active", "location", "name" },
                values: new object[,]
                {
                    { new Guid("01a03111-1c00-74a4-89fb-589a0a731112"), "NORTH", true, "45 Kestrel Road, North district", "North depot" },
                    { new Guid("01a03111-1c00-7583-ad03-8fbf7e6529e6"), "MAIN", true, "1 Utility Way, Central depot", "Main store" },
                    { new Guid("01a03111-1c00-7d5d-9d3e-2a1ea67ec09e"), "YARD", true, "Substation 7, East industrial park", "Substation yard" }
                });
        }
    }
}
