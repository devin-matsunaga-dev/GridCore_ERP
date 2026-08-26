using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP215_AccountTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "class_changed_at",
                schema: "customers",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "class_effective_on",
                schema: "customers",
                table: "customers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "status_effective_on",
                schema: "customers",
                table: "customers",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_transitions",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    effective_on = table.Column<DateOnly>(type: "date", nullable: false),
                    from_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    to_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    from_service_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_service_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deposit_carried = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    deposit_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_transitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_account_transitions_customer",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_transitions_customer_id",
                schema: "customers",
                table: "account_transitions",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_transitions_from_account",
                schema: "customers",
                table: "account_transitions",
                column: "from_service_account_id",
                filter: "\"from_service_account_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_account_transitions_to_account",
                schema: "customers",
                table: "account_transitions",
                column: "to_service_account_id",
                filter: "\"to_service_account_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_transitions",
                schema: "customers");

            migrationBuilder.DropColumn(
                name: "class_changed_at",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "class_effective_on",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "status_effective_on",
                schema: "customers",
                table: "customers");
        }
    }
}
