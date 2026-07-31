namespace Alp.Domain;

public enum ReportFormat
{
    Pdf,
    Xlsx,
}

public class Report
{
    // Bkz. Project'teki not: şema ve uç doğrulaması tek kaynaktan okur.
    public const int TitleMaxLength = 200;
    public const int PreparedByMaxLength = 200;

    public Guid Id { get; set; }

    // Rapor bir projeye kaydedilmeden de üretilebilir — "rapor al" ve
    // "projeye kaydet" bilinçli olarak ayrı yeteneklerdir (bkz.
    // docs/uyelik-ve-rapor-plani.md §1). Proje seçilmişse ilişki kurulur.
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public string UserId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string PreparedBy { get; set; } = string.Empty;
    public int Revision { get; set; } = 1;
    public ReportFormat Format { get; set; }

    // Üretilen belge diske yazılmaz, bu yüzden dosya YOLU da yoktur: "tekrar
    // indir" kayıttan yeniden üretmek demektir (bkz. ReportEndpoints saklama
    // notu). `FileSize` kütük değeridir — kullanıcı ne kadar rapor üretti,
    // hangi boyutta.
    public long FileSize { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
