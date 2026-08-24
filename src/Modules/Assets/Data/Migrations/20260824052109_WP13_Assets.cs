using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Assets.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP13_Assets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "assets");

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_tag = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    @class = table.Column<string>(name: "class", type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    serial_number = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    installed_on = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    condition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    location_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    condition_assessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asset_history",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    from_condition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_condition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_asset_history_asset",
                        column: x => x.asset_id,
                        principalSchema: "assets",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_history_asset_id",
                schema: "assets",
                table: "asset_history",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_asset_history_entry_type",
                schema: "assets",
                table: "asset_history",
                column: "entry_type");

            migrationBuilder.CreateIndex(
                name: "ix_assets_class",
                schema: "assets",
                table: "assets",
                column: "class");

            migrationBuilder.CreateIndex(
                name: "ix_assets_condition",
                schema: "assets",
                table: "assets",
                column: "condition");

            migrationBuilder.CreateIndex(
                name: "ix_assets_status",
                schema: "assets",
                table: "assets",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_assets_asset_tag",
                schema: "assets",
                table: "assets",
                column: "asset_tag",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_assets_serial_number",
                schema: "assets",
                table: "assets",
                column: "serial_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_history",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "assets",
                schema: "assets");
        }
    }
}
