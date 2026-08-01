namespace Alp.Domain;

// Rapor anlık görüntüsünün içerik-adresli deposu: bir raporun üretildiği andaki
// bölüm kaydının (`Calculation.ReportJson`) ham kopyası.
//
// İÇERİK ADRESLİ olmasının nedeni israftır: proje raporları küçük
// düzenlemelerle evrilir ve on hesaplı bir projede tek hesap değiştiğinde on
// bölümün dokuzu öncekiyle bayt bayt aynıdır. PDF ve XLSX'i art arda indirmek
// de aynı içeriği iki kez yazardı. Aynı içerik aynı özeti ürettiği için ikinci
// yazma hiç olmaz; kalıcı boyut rapor sayısı × boyut değil, FARKLI bölüm
// sürümlerinin toplamıdır.
//
// Anahtar (UserId, Hash) BİLEŞİKTİR: dedup kullanıcı sınırında kalır, iki
// kullanıcının içeriği asla aynı satırı paylaşmaz. Hesap silinince blob'ları da
// gider (Cascade) — "unutulma hakkı" beklentisi şemanın geri kalanında nasılsa
// öyle.
//
// Sıkıştırma elle yapılmaz: Postgres TOAST metni kendiliğinden sıkıştırır ve
// SVG metni iyi sıkışır. Karar: docs/rapor-snapshot-karari.md §1.
public class SectionBlob
{
    // SHA-256 hex — RefreshToken.TokenHash ile aynı desen ve aynı uzunluk.
    public const int HashLength = 64;

    public string UserId { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;

    // Bölümün ham JSON'u. Dil HARİTASI olarak durur (`{"tr": …, "en": …}`) —
    // yani indirme anında dil hâlâ seçilebilir. Bayt saklamak bu yeteneği
    // öldürürdü (karar §1).
    public string Content { get; set; } = string.Empty;

    // Kota ölçümü bu kolondan yapılır: `Content.Length` her turda yeniden
    // hesaplanacak bir şey değil, yazma anında bilinen bir sayı.
    public int Length { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ApplicationUser? User { get; set; }
}
