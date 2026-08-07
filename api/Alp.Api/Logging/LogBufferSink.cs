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
    string? UserId);

// Sabit kapasiteli halka tampon. Kilit basit `lock` — event başına maliyet
// zaten Serilog formatter'ıyla kıyaslanabilir, lock-free yapıya gerek yok.
public sealed class LogBufferSink(int capacity) : ILogEventSink
{
    private readonly Queue<LogBufferEntry> buffer = new();
    private readonly Lock gate = new();

    public int Capacity { get; } = capacity;

    // Seviye eşiği burada (sink'in kendi değişmezi), Program.cs'teki
    // WriteTo.Sink çağrısında DEĞİL: iki yerde aynı kural tekrarlanmasın ve
    // eşik doğrudan Emit çağrısıyla da test edilebilsin.
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;

        var entry = new LogBufferEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            // Tam yığın izi stdout'ta zaten var; tamponda yalnız teşhis için
            // ilk satır (mesaj) tutulur, bellek satır sayısıyla şişmesin.
            logEvent.Exception?.ToString() is { } ex ? ex.Split('\n', 2)[0] : null,
            PropertyString(logEvent, "SourceContext"),
            PropertyString(logEvent, "RequestPath"),
            PropertyString(logEvent, "UserId"));

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
}
