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
    private static readonly string[] Langs = ["tr", "en"];

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

    // Hesap YAZILIRKEN çağrılır: `Calculation.PreviewJson`'a konacak, dil
    // başına önizleme taşıyan küçük JSON'u üretir (StoredSection'daki iki
    // dilli kayıt deseninin küçüğü). `reportJson` boşsa PreviewJson de boş
    // kalır — HasReport zaten ayrıca ReportJson'a bakıyor.
    public static string? Write(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return null;

        var map = new Dictionary<string, PreviewPayload>();
        foreach (var lang in Langs)
        {
            var (rows, mode) = From(reportJson, lang);
            map[lang] = new PreviewPayload(rows, mode);
        }
        return JsonSerializer.Serialize(map, Options);
    }

    // Okuma yolu: `PreviewJson`'dan istenen dili seçer, yoksa eldekine düşer —
    // StoredSection'daki dil düşüş kuralıyla aynı gerekçe (yanlış dilde
    // önizleme, hiç önizleme olmamasından iyidir). Bozuk kayıt satırı düşürmez.
    public static (IReadOnlyList<PreviewField> Rows, string? Mode) ReadStored(string? previewJson, string lang)
    {
        if (string.IsNullOrWhiteSpace(previewJson)) return ([], null);

        try
        {
            using var doc = JsonDocument.Parse(previewJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], null);

            if (root.TryGetProperty(lang, out var wanted))
            {
                return FromPayload(wanted.Deserialize<PreviewPayload>(Options));
            }

            foreach (var property in root.EnumerateObject())
            {
                return FromPayload(property.Value.Deserialize<PreviewPayload>(Options));
            }

            return ([], null);
        }
        catch (JsonException)
        {
            return ([], null);
        }
    }

    private static (IReadOnlyList<PreviewField> Rows, string? Mode) FromPayload(PreviewPayload? payload) =>
        (payload?.Rows ?? [], payload?.Mode);
}

// `PreviewJson` dil haritasındaki tek dilin yükü.
public record PreviewPayload(IReadOnlyList<PreviewField> Rows, string? Mode);
