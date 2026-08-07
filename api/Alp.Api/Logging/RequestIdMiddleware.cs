using System.Text.RegularExpressions;
using Serilog.Context;

namespace Alp.Api.Logging;

// İstek korelasyonu (docs/brifler/14-loglama-altyapi.md §2, brif 11 E6'yı
// kapatır). Gelen X-Request-Id GEÇERLİYSE alınır, değilse üretilir; kimlik
// `LogContext`e basılır (istek içindeki HER Serilog satırına — buffer'a
// düşenler DAHİL, F1'in LogBufferEntry.RequestId alanı buradan besleniyor)
// ve yanıt başlığına yazılır. `Program.cs`te `UseExceptionHandler`DAN ÖNCE
// kaydedilir (pipeline'ı SARAR) — aksi hâlde 5xx durumunda en değerli satır
// (istisna) kimliği KAYBEDERDİ, bkz. aşağıdaki iki not.
public static partial class RequestIdMiddleware
{
    public const string HeaderName = "X-Request-Id";
    private const string PropertyName = "RequestId";

    // Gelen başlık SALDIRGAN KONTROLÜNDEDİR — nginx `$request_id` biçimiyle
    // aynı aralık (hex + tire), ama kaynak istemci de olabilir. CLEF'te
    // kaçış sorun değil (Serilog JSON'a düzgün yazar), risk panelin HAM
    // gösterdiği ve nginx log satırına yazılan bir dizeye serbest metin
    // sızması. Uymayan değer YOK SAYILIR, yeni kimlik üretilir — hata
    // döndürülmez (kimliğin kendisi işlevsel değil, teşhis amaçlı).
    // `\A`/`\z` (ÇİPA `^`/`$` DEĞİL): `$`, `Multiline` kapalıyken bile bir
    // sondaki `\n`'den ÖNCE de eşleşir — review'da yakalandı, "geçerli"
    // sayılan aralığa bir satır sonu karakteri sızabilirdi.
    [GeneratedRegex(@"\A[0-9a-fA-F-]{8,64}\z")]
    private static partial Regex ValidIdPattern();

    // Saf karar — ayrı fonksiyon, testler tam bir ApplicationBuilder/TestServer
    // kurmadan doğrudan çağırır (bu depodaki "işleyiciyi doğrudan çağır" deseni,
    // bkz. AdminEndpoints.ListLogs).
    internal static string ResolveId(string? incomingHeader) =>
        !string.IsNullOrEmpty(incomingHeader) && ValidIdPattern().IsMatch(incomingHeader)
            ? incomingHeader
            : Guid.NewGuid().ToString("n");

    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var id = ResolveId(context.Request.Headers[HeaderName].ToString());
        context.TraceIdentifier = id;

        // `Headers[...] =` BURADA değil: `UseExceptionHandler` 5xx'te
        // `Response.Clear()` çağırır ve erken atanan başlığı da silerdi
        // (review bulgusu — bir 500 yanıtı kimliksiz giderdi). `OnStarting`
        // yanıtın İLK baytından hemen önce, yani her türlü `Clear()`dan
        // SONRA çalışır — ne olursa olsun kalıcı.
        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            ctx.Response.Headers[HeaderName] = ctx.TraceIdentifier;
            return Task.CompletedTask;
        }, context);

        using (LogContext.PushProperty(PropertyName, id))
        {
            await next(context);
        }
    }

    public static IApplicationBuilder UseRequestId(this IApplicationBuilder app) =>
        app.Use(InvokeAsync);
}
