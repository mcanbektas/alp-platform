using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alp.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropReportFilePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FilePath",
                table: "Reports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilePath",
                table: "Reports",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
