using System.Text.Json;
using Alp.Reports;

namespace Alp.Api.Projects;

// Kaydedilmiş rapor bölümünden proje listesi için kısa önizleme türetir.
//
// Bu iş eskiden istemcideydi (`lib/savedCalculation.js` → `previewRows`), ama
// oraya taşınabilmesi için ham `ReportJson`'ın tamamının ağdan geçmesi
// gerekiyordu — satır içi SVG dahil, yani yanıtın ~%92'si boşuna. Kural
// sunucuya taşındı; süzme aynı kaldı:
//
//   - yalnızca `results` dizisi okunur (şematik/grafik SVG'sine dokunulmaz),
//   - etiket ya da değer okunamayan satır atlanır,
//   - vurgulanan satır (`emphasis`) başa alınır — bir hesabın baş sonucu odur,
//   - en fazla iki satır döner ve uzun metin kırpılır.
//
// Sunucu yine hiçbir aracın ne hesapladığını bilmez: burada tanınan tek şey
// rapor bölümünün kendi şeması, araç değil.
public static class ReportPreview
{
    private const int RowLimit = 2;
    private const int MaxTextLength = 80;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // `lang`: kayıt artık bölümü her dilde taşıyor (bkz. StoredSection); liste
    // önizlemesi de arayüz dilinde okunmalı, yoksa İngilizce arayüzde Türkçe
    // etiketler görünür. Bozuk/eski kayıt satırı düşürmez, yalnızca
    // önizlemesiz gösterilir — kaydın kendisi hâlâ açılabilir olmalı.
    public static (IReadOnlyList<PreviewField> Rows, string? Mode) From(string? reportJson, string lang)
    {
        var section = StoredSection.Read(reportJson, lang);
        if (section is null) return ([], null);

        if (section?.Results is null) return ([], Text(section?.Mode));

        var emphasised = new List<PreviewField>();
        var rest = new List<PreviewField>();

        foreach (var field in section.Results)
        {
            var label = Text(field?.Label);
            var value = Text(field?.Value);
            if (label is null || value is null) continue;

            var row = new PreviewField(label, value, Text(field!.Unit), field.Emphasis);
            (field.Emphasis ? emphasised : rest).Add(row);

            // İki satırdan fazlası hiçbir zaman gösterilmiyor; vurgulanan
            // satırlar önce geldiği için o kadarı dolunca aramaya devam etmenin
            // anlamı yok.
            if (emphasised.Count >= RowLimit) break;
        }

        return ([.. emphasised.Concat(rest).Take(RowLimit)], Text(section.Mode));
    }

    private static string? Text(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > MaxTextLength ? string.Concat(trimmed[..MaxTextLength], "…") : trimmed;
    }
}
