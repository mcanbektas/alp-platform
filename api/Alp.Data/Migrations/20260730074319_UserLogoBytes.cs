using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alp.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserLogoBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LogoPath",
                table: "AspNetUsers",
                newName: "LogoContentType");

            migrationBuilder.AddColumn<byte[]>(
                name: "LogoBytes",
                table: "AspNetUsers",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoBytes",
                table: "AspNetUsers");

            migrationBuilder.RenameColumn(
                name: "LogoContentType",
                table: "AspNetUsers",
                newName: "LogoPath");
        }
    }
}
