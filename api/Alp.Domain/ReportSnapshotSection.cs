namespace Alp.Domain;

// Bir raporun anlık görüntüsünün MANİFESTİ: hangi bölüm blob'u, hangi sırada.
//
// Ayrı bir tablo olmasının nedeni, blob'ların paylaşılmasıdır — aynı içerik
// birden çok rapora aittir. Manifest gerçek bir join tablosu olduğu için
// sahipsiz blob toplama tek anti-join `DELETE`tir; referans SAYACI tutulmaz:
// sayaç ikinci bir doğruluk kaynağı ve sessizce kayabilecek bir sayı olurdu.
//
// Rapor silinirse manifest de gider (Cascade); blob kalır ve gerçekten
// sahipsizse temizlik turunda toplanır (ReportSnapshotCleanupService).
//
// Manifest satırı olmayan rapor = snapshot'sız rapor: indirme bugünkü
// "kayıttan güncel hâliyle yeniden üret" davranışına düşer. Göç öncesi
// kayıtlar ve kotayla geriletilenler böyledir (docs/rapor-snapshot-karari.md
// §2, §4).
public class ReportSnapshotSection
{
    public Guid ReportId { get; set; }
    public Report? Report { get; set; }

    // Blob (UserId, Hash) ile adreslenir; UserId raporun sahibiyle aynıdır ve
    // ayrıca taşınır, çünkü FK bileşiktir.
    public string UserId { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public SectionBlob? Blob { get; set; }

    // Belgedeki bölüm sırası. Kaynak, üretim anındaki `Calculation.SortOrder`
    // sıralamasıdır; sonradan proje yeniden sıralansa bile RAPOR o günkü
    // sırayı korur — donmuş olan içerik kadar düzendir de.
    public int SortOrder { get; set; }
}
