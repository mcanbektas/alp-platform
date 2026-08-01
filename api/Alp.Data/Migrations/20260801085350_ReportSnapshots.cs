using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alp.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Company",
                table: "Reports",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchemaVersion",
                table: "Reports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SectionBlobs",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Length = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SectionBlobs", x => new { x.UserId, x.Hash });
                    table.ForeignKey(
                        name: "FK_SectionBlobs_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportSnapshotSections",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSnapshotSections", x => new { x.ReportId, x.SortOrder });
                    table.ForeignKey(
                        name: "FK_ReportSnapshotSections_Reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReportSnapshotSections_SectionBlobs_UserId_Hash",
                        columns: x => new { x.UserId, x.Hash },
                        principalTable: "SectionBlobs",
                        principalColumns: new[] { "UserId", "Hash" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportSnapshotSections_UserId_Hash",
                table: "ReportSnapshotSections",
                columns: new[] { "UserId", "Hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportSnapshotSections");

            migrationBuilder.DropTable(
                name: "SectionBlobs");

            migrationBuilder.DropColumn(
                name: "Company",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                table: "Reports");
        }
    }
}
