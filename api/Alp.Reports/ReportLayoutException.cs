namespace Alp.Reports;

// PDF dizgisi içeriği sayfaya sığdıramadığında fırlatılır. Uygulama katmanı
// QuestPDF'in kendi istisna türünü tanımasın diye burada sarılır: rapor
// kütüphanesi değişirse (ya da ikinci bir dizgici eklenirse) uç noktadaki
// `catch` bloğu aynı kalır.
//
// Neden ayrı bir tür: bu durum bir sunucu arızası DEĞİL, kullanıcının verdiği
// yükün sınırı. İşlenmeden bırakıldığında çıplak 500 dönüyordu — çok hesaplı
// bir proje raporunda kullanıcı kendi projesiyle 500 alıyordu
// (docs/kod-incelemesi-2026-07-29.md "PDF üretimi korumasız").
public class ReportLayoutException(Exception inner)
    : Exception("Rapor içeriği sayfa düzenine sığdırılamadı.", inner);
