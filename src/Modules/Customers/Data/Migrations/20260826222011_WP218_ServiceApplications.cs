using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GridCore.Modules.Customers.Data.Migrations
{
    /// <inheritdoc />
    public partial class WP218_ServiceApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_applications",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    application_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_on = table.Column<DateOnly>(type: "date", nullable: false),
                    notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_by_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    submitted_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    review_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewer_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    reviewer_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    decided_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    decision_reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    decision_notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replaces_application_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_applications", x => x.id);
                    table.ForeignKey(
                        name: "fk_service_applications_customer",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_applications_location",
                        column: x => x.service_location_id,
                        principalSchema: "customers",
                        principalTable: "service_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_documents",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    content_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    size_in_bytes = table.Column<long>(type: "bigint", nullable: false),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_application_documents_application",
                        column: x => x.service_application_id,
                        principalSchema: "customers",
                        principalTable: "service_applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_application_documents_application_id",
                schema: "customers",
                table: "application_documents",
                column: "service_application_id");

            migrationBuilder.CreateIndex(
                name: "ux_application_documents_storage_key",
                schema: "customers",
                table: "application_documents",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_applications_customer_id",
                schema: "customers",
                table: "service_applications",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_applications_status",
                schema: "customers",
                table: "service_applications",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_service_applications_number",
                schema: "customers",
                table: "service_applications",
                column: "application_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_service_applications_open_premise",
                schema: "customers",
                table: "service_applications",
                columns: new[] { "service_location_id", "service_type" },
                unique: true,
                filter: "\"status\" IN ('Submitted', 'UnderReview')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_documents",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "service_applications",
                schema: "customers");
        }
    }
}
