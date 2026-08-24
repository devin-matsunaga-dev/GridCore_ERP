using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP12_ServiceAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_accounts",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    service_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    service_ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_service_accounts_customer",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_accounts_location",
                        column: x => x.service_location_id,
                        principalSchema: "customers",
                        principalTable: "service_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_account_history",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_account_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_service_account_history_account",
                        column: x => x.service_account_id,
                        principalSchema: "customers",
                        principalTable: "service_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_account_history_account_id",
                schema: "customers",
                table: "service_account_history",
                column: "service_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_accounts_customer_id",
                schema: "customers",
                table: "service_accounts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_accounts_status",
                schema: "customers",
                table: "service_accounts",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_service_accounts_account_number",
                schema: "customers",
                table: "service_accounts",
                column: "account_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_service_accounts_open_location",
                schema: "customers",
                table: "service_accounts",
                column: "service_location_id",
                unique: true,
                filter: "\"status\" <> 'Closed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_account_history",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "service_accounts",
                schema: "customers");
        }
    }
}
