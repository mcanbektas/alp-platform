using System.Text.Json;
using Alp.Reports;

namespace Alp.Api.Projects;

// `Calculation.ReportJson` içindeki kayıtlı rapor bölümünü okur.
//
// Kayıt İKİ ŞEKİLDEN biri olabilir ve ikisi de desteklenmek zorunda:
//
//   yeni  {"tr": { "toolName": … }, "en": { "toolName": … }}
//   eski  {"toolName": … }
//
// Eski şekil, bölümün yalnızca kaydedildiği dilde saklandığı dönemden kalır;
// o kayıtlar hâlâ açılabilmeli.
//
// Ayrım BÖLÜM ALANLARINA bakılarak yapılır, dil kodlarına değil: kökte bölüm
// şemasından tanıdık bir alan varsa kayıt eski şekildedir. Yalnız "toolName"e
// bakmak yetmiyordu — bölüm alanlarının hepsi isteğe bağlı ve toolName'siz
// kayıtlar (ör. yalnız `results` taşıyan) dil haritası sanılıp içindeki ilk
// nesne bölüm diye okunuyordu. Dil kodlarını sabit listeden aramak da
// istenmedi: sunucuda dil listesi tutmak, yeni dil eklendiğinde iki yerde
// bakım demek.
//
// İstenen dil kayıtta yoksa (tek dille kaydedilmiş ya da sonradan dil
// eklenmiş) ELDEKİNE düşülür: yanlış dilde bir bölüm, hiç bölüm olmamasından
// iyidir — hesap raporun dışında kalırsa kullanıcı sebebini göremez.
//
// Sunucu yine hiçbir aracın ne hesapladığını bilmez ve hiçbir kullanıcı metni
// üretmez; burada tanınan tek şey bölümün kendi şeması ve dil ANAHTARIdır.
public static class StoredSection
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    // `ReportSection`ın alan adları (bkz. Alp.Reports/ReportPayload.cs). Hepsi
    // isteğe bağlı olduğu için TEK BİRİNİN varlığı yeter; dil haritasının kökü
    // ise yalnızca dil kodları taşır ve hiçbiriyle çakışmaz.
    private static readonly string[] SectionFields =
    [
        "toolName", "mode", "inputs", "formula", "results", "notes",
        "schematicSvg", "schematicCaption", "chart",
    ];

    private static bool LooksLikeSection(JsonElement root)
    {
        foreach (var field in SectionFields)
        {
            if (root.TryGetProperty(field, out _)) return true;
        }
        return false;
    }

    public static ReportSection? Read(string? reportJson, string lang)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(reportJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Eski şekil: bölümün kendisi kökte.
            if (LooksLikeSection(root))
            {
                return root.Deserialize<ReportSection>(Options);
            }

            if (root.TryGetProperty(lang, out var wanted) && wanted.ValueKind == JsonValueKind.Object)
            {
                return wanted.Deserialize<ReportSection>(Options);
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    return property.Value.Deserialize<ReportSection>(Options);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            // Bozuk kayıt istisna fırlatmaz: tek bozuk satır ne listeyi ne de
            // bütün projenin raporunu düşürmeli.
            return null;
        }
    }
}
