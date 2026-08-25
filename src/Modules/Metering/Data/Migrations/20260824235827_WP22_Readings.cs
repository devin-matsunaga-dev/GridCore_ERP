using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Metering.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP22_Readings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfilled to the ordinary domestic five, then the default is dropped so the column
            // matches the model exactly. A meter registered before this migration has no recorded
            // register width, and zero — which is what EF's own backfill would leave — is not a
            // width any rollover arithmetic can run against: the first reading taken off such a
            // meter would be refused rather than measured. Five is the width of the meter on the
            // side of an ordinary house, and every seeded meter that needs a wider one names it.
            migrationBuilder.AddColumn<int>(
                name: "register_digits",
                schema: "metering",
                table: "meters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE "metering"."meters" SET "register_digits" = 5 WHERE "register_digits" = 0;
                """);

            // The model declares no database default — a register width guessed by the schema is one
            // nobody transcribed off a nameplate — so the backfill default goes with it, and the
            // next `migrations add` has nothing to reconcile.
            migrationBuilder.Sql(
                """
                ALTER TABLE "metering"."meters" ALTER COLUMN "register_digits" DROP DEFAULT;
                """);

            migrationBuilder.CreateTable(
                name: "meter_readings",
                schema: "metering",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    meter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reading_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    previous_reading = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    previous_reading_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumption = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    rolled_over = table.Column<bool>(type: "boolean", nullable: false),
                    exception_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cycle_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meter_readings", x => x.id);
                    table.ForeignKey(
                        name: "fk_meter_readings_meter",
                        column: x => x.meter_id,
                        principalSchema: "metering",
                        principalTable: "meters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_meter_readings_exception_code",
                schema: "metering",
                table: "meter_readings",
                column: "exception_code");

            migrationBuilder.CreateIndex(
                name: "ix_meter_readings_meter_id",
                schema: "metering",
                table: "meter_readings",
                column: "meter_id");

            migrationBuilder.CreateIndex(
                name: "ix_meter_readings_service_location_id",
                schema: "metering",
                table: "meter_readings",
                column: "service_location_id");

            migrationBuilder.CreateIndex(
                name: "ux_meter_readings_meter_cycle",
                schema: "metering",
                table: "meter_readings",
                columns: new[] { "meter_id", "cycle_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meter_readings",
                schema: "metering");

            migrationBuilder.DropColumn(
                name: "register_digits",
                schema: "metering",
                table: "meters");
        }
    }
}
