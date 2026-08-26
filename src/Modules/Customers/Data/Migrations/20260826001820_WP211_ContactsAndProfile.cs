using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP211_ContactsAndProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_contacts",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    relationship = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_authorised_to_discuss = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_contacts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_profiles",
                schema: "customers",
                columns: table => new
                {
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailing_line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    mailing_line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    mailing_city = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    mailing_region = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    mailing_postal_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    mailing_country = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bill_delivery_channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outage_notices = table.Column<bool>(type: "boolean", nullable: false),
                    dunning_notices = table.Column<bool>(type: "boolean", nullable: false),
                    preferred_language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_profiles", x => x.customer_id);
                });

            migrationBuilder.CreateTable(
                name: "contact_methods",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_contact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contact_methods", x => x.id);
                    table.ForeignKey(
                        name: "fk_contact_methods_contact",
                        column: x => x.customer_contact_id,
                        principalSchema: "customers",
                        principalTable: "customer_contacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contact_methods_contact_kind",
                schema: "customers",
                table: "contact_methods",
                columns: new[] { "customer_contact_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_contacts_customer",
                schema: "customers",
                table: "customer_contacts",
                column: "customer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_methods",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "customer_profiles",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "customer_contacts",
                schema: "customers");
        }
    }
}
