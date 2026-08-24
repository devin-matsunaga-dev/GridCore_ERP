using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Metering.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP21_Meters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "metering");

            migrationBuilder.CreateTable(
                name: "meters",
                schema: "metering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    serial_number = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    service_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    installation_reading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "meter_history",
                schema: "metering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    service_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meter_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_meter_history_meter",
                        column: x => x.meter_id,
                        principalSchema: "metering",
                        principalTable: "meters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_meter_history_meter_id",
                schema: "metering",
                table: "meter_history",
                column: "meter_id");

            migrationBuilder.CreateIndex(
                name: "ix_meter_history_service_location_id",
                schema: "metering",
                table: "meter_history",
                column: "service_location_id");

            migrationBuilder.CreateIndex(
                name: "ix_meters_status",
                schema: "metering",
                table: "meters",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_meters_type",
                schema: "metering",
                table: "meters",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ux_meters_meter_number",
                schema: "metering",
                table: "meters",
                column: "meter_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_meters_serial_number",
                schema: "metering",
                table: "meters",
                column: "serial_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_meters_service_location",
                schema: "metering",
                table: "meters",
                column: "service_location_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meter_history",
                schema: "metering");

            migrationBuilder.DropTable(
                name: "meters",
                schema: "metering");
        }
    }
}
