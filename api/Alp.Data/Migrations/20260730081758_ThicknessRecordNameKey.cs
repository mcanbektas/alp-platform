using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alp.Data.Migrations
{
    /// <inheritdoc />
    public partial class ThicknessRecordNameKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NameKey",
                table: "ThicknessRecords",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Var olan satırların anahtarı doldurulur. Uygulama bu değeri Türkçe
            // kurallarıyla üretir; SQL tarafında yaklaşık karşılığı kullanılır
            // (I/İ dışında aynı sonucu verir) — kayıt bir kez daha kaydedildiğinde
            // anahtar zaten uygulamanın ürettiğiyle güncellenir.
            migrationBuilder.Sql(
                "UPDATE \"ThicknessRecords\" " +
                "SET \"NameKey\" = lower(btrim(regexp_replace(\"Name\", '\\s+', ' ', 'g')));");

            // Benzersiz dizin konmadan ÖNCE kopyalar temizlenir: var olan bir
            // çakışmayla dizin kurulamaz. Aynı adı taşıyan satırlardan en yenisi
            // kalır — kullanıcının son kaydettiği hâl odur.
            migrationBuilder.Sql(
                "DELETE FROM \"ThicknessRecords\" t " +
                "USING \"ThicknessRecords\" newer " +
                "WHERE t.\"UserId\" = newer.\"UserId\" " +
                "  AND t.\"NameKey\" = newer.\"NameKey\" " +
                "  AND (t.\"CreatedAt\" < newer.\"CreatedAt\" " +
                "       OR (t.\"CreatedAt\" = newer.\"CreatedAt\" AND t.\"Id\" < newer.\"Id\"));");

            migrationBuilder.CreateIndex(
                name: "IX_ThicknessRecords_UserId_NameKey",
                table: "ThicknessRecords",
                columns: new[] { "UserId", "NameKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ThicknessRecords_UserId_NameKey",
                table: "ThicknessRecords");

            migrationBuilder.DropColumn(
                name: "NameKey",
                table: "ThicknessRecords");
        }
    }
}
