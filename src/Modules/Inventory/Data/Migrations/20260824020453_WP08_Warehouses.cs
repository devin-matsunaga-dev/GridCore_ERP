using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GridCore.Modules.Inventory.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP08_Warehouses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "warehouses",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    location = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouses", x => x.id);
                });

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

            migrationBuilder.CreateIndex(
                name: "ux_warehouses_code",
                schema: "inventory",
                table: "warehouses",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "warehouses",
                schema: "inventory");
        }
    }
}
