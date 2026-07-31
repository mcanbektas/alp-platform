using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alp.Data.Migrations
{
    /// <inheritdoc />
    public partial class CalculationPreview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviewJson",
                table: "Calculations",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviewJson",
                table: "Calculations");
        }
    }
}
