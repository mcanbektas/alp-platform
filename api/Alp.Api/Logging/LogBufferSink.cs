using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace Alp.Api.Logging;

// Operasyonel log ekranının kaynağı (docs/brifler/12-loglama-ekrani.md §2-3).
// Süreç belleğinde yaşar, yeniden başlatmada uçar — bilerek: kalıcı iz zaten
// AuditEvents'te (denetim izi), bu tampon yalnız "şu an ne oluyor" sorusuna
// bakar. Stdout-tek-hedef kararını (docs/loglama-karari.md §3) değiştirmez;
// stdout'a giden event'lerin AYNISININ yanına bir kopya eklenir.
public sealed record LogBufferEntry(
    DateTimeOffset OccurredAt,
    string Level,
    string Message,
    string? Exception,
    string? SourceContext,
    string? RequestPath,
    string? UserId,
    string? RequestId,
    // Brif 12'nin "LogEvent'in kendisi tutulmaz, property sözlüğü bellekte
    // şişer" kararının BİLİNÇLİ revizyonu (docs/brifler/14-loglama-altyapi.md
    // §3) — o karar SINIRSIZ saklamaya karşıydı, bu tavanlı bir kopya.
    // Yukarıdaki dört özel alan burada TEKRAR ETMEZ (çift gösterim olmaz).
    IReadOnlyDictionary<string, string> Properties,
    int TruncatedPropertyCount);

// Sabit kapasiteli halka tampon. Kilit basit `lock` — event başına maliyet
// zaten Serilog formatter'ıyla kıyaslanabilir, lock-free yapıya gerek yok.
public sealed class LogBufferSink(int capacity) : ILogEventSink
{
    private readonly Queue<LogBufferEntry> buffer = new();
    private readonly Lock gate = new();

    public int Capacity { get; } = capacity;

    // Kayıt başına en kötü boyut ≈ 24×(64+256) + 4000 ≈ 12 KB (docs/brifler/
    // 14-loglama-altyapi.md §3) — sınırsız property sözlüğü/istisna metni
    // tamponu (ve uç yanıtını) öngörülemez şişirmesin diye.
    private const int MaxProperties = 24;
    private const int MaxPropertyNameLength = 64;
    private const int MaxPropertyValueLength = 256;
    private const int MaxExceptionLength = 4000;

    // Bu dört alanın ZATEN kendi özel kolonu var (yukarıda) — sözlüğe
    // GİRMEZLER, yoksa aynı bilgi ayrıntı kartında iki kez görünürdü.
    private static readonly HashSet<string> DedicatedPropertyKeys =
        new(StringComparer.Ordinal) { "SourceContext", "RequestPath", "UserId", "RequestId" };

    // Seviye eşiği burada (sink'in kendi değişmezi), Program.cs'teki
    // WriteTo.Sink çağrısında DEĞİL: iki yerde aynı kural tekrarlanmasın ve
    // eşik doğrudan Emit çağrısıyla da test edilebilsin.
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;

        var (properties, truncatedCount) = BuildProperties(logEvent);

        var entry = new LogBufferEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            TruncateException(logEvent.Exception),
            PropertyString(logEvent, "SourceContext"),
            PropertyString(logEvent, "RequestPath"),
            PropertyString(logEvent, "UserId"),
            PropertyString(logEvent, "RequestId"),
            properties,
            truncatedCount);

        lock (gate)
        {
            buffer.Enqueue(entry);
            while (buffer.Count > Capacity) buffer.Dequeue();
        }
    }

    public IReadOnlyList<LogBufferEntry> Snapshot()
    {
        lock (gate) return buffer.ToArray();
    }

    private static string? PropertyString(LogEvent logEvent, string key) =>
        logEvent.Properties.TryGetValue(key, out var value) && value is ScalarValue { Value: string s }
            ? s
            : null;

    // Tavanlı kopya — ADET (en çok 24), ad (64) ve değer (256) uzunluğunda.
    // Sıra deterministik olsun diye anahtara göre alfabetik: `LogEvent.
    // Properties`in kendi sırası garanti değil, testler ve ekran aynı
    // sırayı görsün ister.
    private static (IReadOnlyDictionary<string, string> Properties, int TruncatedCount) BuildProperties(
        LogEvent logEvent)
    {
        var candidates = logEvent.Properties
            .Where(kv => !DedicatedPropertyKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        // `SortedDictionary` — düz `Dictionary`nin enumerate sırası .NET
        // SÖZLEŞMESİ DEĞİL (bugün insertion-order gibi davranıyor olması
        // garanti anlamına gelmez); alfabetik sıra burada GERÇEK bir
        // sözleşme olsun diye (ekran/testler buna güveniyor, üstteki yorum).
        var dict = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in candidates.Take(MaxProperties))
        {
            var name = key.Length > MaxPropertyNameLength ? key[..MaxPropertyNameLength] : key;
            dict[name] = RenderValue(value);
        }

        var truncatedCount = Math.Max(0, candidates.Count - MaxProperties);
        return (dict, truncatedCount);
    }

    // ScalarValue.ToString() Serilog'un metin biçimlendiricisinden geçer ve
    // dizeleri TIRNAKLAR ("GET" gibi) — ekranda çift tırnak istenmiyor, ham
    // CLR değeri doğrudan string'e çevrilir. Yapısal (dizi/nesne) değerler
    // için derin serileştirme YAZILMAZ, Serilog'un kendi ToString()'i (iç
    // içe metin gösterimi) kullanılır — brif §3'ün "derin serileştirme YOK"
    // kuralı.
    //
    // `InvariantCulture` ŞART: sayısal bir özellik (örn. `Elapsed`) sunucu
    // kültürü Türkçe iken virgüllü ondalık basardı (12,5) — mühendislik/log
    // çıktısı gibi burası da noktayı bekler, testte yakalanan gerçek bir bug.
    private static string RenderValue(LogEventPropertyValue value)
    {
        var rendered = value is ScalarValue { Value: var raw }
            ? raw switch
            {
                null => "null",
                // `IFormattable` invariant biçimi DateTime/DateTimeOffset için
                // varsayılan "G" — ay/gün SIRASI belirsiz (08/07 mi 7 Ağustos mu
                // 8 Temmuz mu). Round-trip "O" (ISO 8601) hem tektip hem
                // sıralanabilir; review'da yakalandı, bugün üreten bir property
                // yok ama gelecekte biri eklendiğinde sessizce yanlış okunmasın.
                DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                // Ondalık türler tam ("G"/shortest-round-trip) yazılınca
                // gürültülü çıkıyordu — canlı turda `Elapsed` "1.167333" gibi
                // 6 basamak bastı. Süre/oran gibi bir değerin 3 ondalıktan
                // fazlasının teşhis değeri yok; `"0.###"` fazla basamağı
                // KIRPAR, gereksiz sıfırları da basmaz (`2` → "2", `2.5` →
                // "2.5"). Yalnız bu üç tip — int/long zaten tam sayı, Guid/
                // enum/TimeSpan aşağıdaki genel `IFormattable` dalına düşer.
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => raw.ToString() ?? "null",
            }
            : value.ToString();
        return rendered.Length > MaxPropertyValueLength
            ? rendered[..MaxPropertyValueLength] + "…"
            : rendered;
    }

    private static string? TruncateException(Exception? exception)
    {
        if (exception is null) return null;
        var text = exception.ToString();
        return text.Length <= MaxExceptionLength
            ? text
            : text[..MaxExceptionLength] + "… (kısaltıldı)";
    }
}
