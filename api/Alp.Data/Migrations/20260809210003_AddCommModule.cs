using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alp.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "comm");

            migrationBuilder.CreateTable(
                name: "comm_projects",
                schema: "comm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comm_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_comm_projects_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "comm_protocol_schemas",
                schema: "comm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CommProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comm_protocol_schemas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_comm_protocol_schemas_comm_projects_CommProjectId",
                        column: x => x.CommProjectId,
                        principalSchema: "comm",
                        principalTable: "comm_projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_comm_projects_UserId",
                schema: "comm",
                table: "comm_projects",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_comm_protocol_schemas_CommProjectId_Name_Version",
                schema: "comm",
                table: "comm_protocol_schemas",
                columns: new[] { "CommProjectId", "Name", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comm_protocol_schemas",
                schema: "comm");

            migrationBuilder.DropTable(
                name: "comm_projects",
                schema: "comm");
        }
    }
}
