using Alp.Api.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Serilog;
using Serilog.Parsing;

namespace Alp.Api.Tests;

// İstek korelasyonu (docs/brifler/14-loglama-altyapi.md §2, brif 11 E6).
// `ResolveId` saf karar — HTTP yığını kurmadan doğrudan test edilir
// (LogBufferTests'teki desenin aynısı). `InvokeAsync` ise `DefaultHttpContext`
// ile gerçek istek/yanıt döngüsünü ve `LogContext` akışını doğrular.
public class RequestIdMiddlewareTests
{
    [Fact]
    public void gecerli_baslik_aynen_kullanilir()
    {
        // nginx `$request_id` biçimi: 32 hex, tiresiz.
        var incoming = "0123456789abcdef0123456789abcdef";
        Assert.Equal(incoming, RequestIdMiddleware.ResolveId(incoming));
    }

    [Fact]
    public void tireli_uuid_de_kabul_edilir()
    {
        var incoming = "550e8400-e29b-41d4-a716-446655440000";
        Assert.Equal(incoming, RequestIdMiddleware.ResolveId(incoming));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void baslik_yoksa_uretilir(string? incoming)
    {
        var id = RequestIdMiddleware.ResolveId(incoming);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    // Gelen başlık saldırgan kontrolündedir — desene uymayan değer sessizce
    // YOK SAYILIR (hata döndürülmez), yeni kimlik üretilir. Enjeksiyon
    // denemesi (`<script>`), boşluk, tavan-ALTI/tavan-ÜSTÜ uzunluk (ikisi de
    // GEÇERLİ karakterlerle — yalnız uzunluk sınırını izole eder, "kisa"/
    // "gggggggg" gibi karışık örnekler değil) hepsi aynı yoldan reddedilir.
    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("has space")]
    [InlineData("1234567")] // 7 hex — yalnız 1 eksik, tavan-altı sınır
    public void gecersiz_baslik_yok_sayilir_uretilir(string incoming)
    {
        var id = RequestIdMiddleware.ResolveId(incoming);
        Assert.NotEqual(incoming, id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public void tavan_uzunluk_asilinca_uretilir()
    {
        var tooLong = new string('a', 65);
        var id = RequestIdMiddleware.ResolveId(tooLong);
        Assert.NotEqual(tooLong, id);
    }

    // Review bulgusu: `$` (Multiline KAPALIYKEN bile) sondaki bir `\n`'den
    // ÖNCE de eşleşir — `\A`/`\z`e geçmeden önce bu sızardı. Yanıt başlığı
    // yoluna ulaşmazdı (Kestrel `\r\n`i reddeder) ama panele/LogContext'e
    // ulaşırdı.
    [Fact]
    public void sondaki_satir_sonu_gecersiz_sayilir()
    {
        var incoming = "0123456789abcdef0123456789abcdef\n";
        var id = RequestIdMiddleware.ResolveId(incoming);
        Assert.NotEqual(incoming, id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Fact]
    public async Task invoke_traceidentifier_ayarlar_next_cagirir()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[RequestIdMiddleware.HeaderName] = "0123456789abcdef0123456789abcdef";
        var nextCalled = false;

        await RequestIdMiddleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Equal("0123456789abcdef0123456789abcdef", context.TraceIdentifier);
    }

    // `HttpResponseFeature.OnStarting` yalnız kaydeder — callback'i gerçekten
    // ATEŞLEMEK barındırma katmanının (Kestrel/TestServer) işi, bare
    // `DefaultHttpContext().Response.StartAsync()` bunu YAPMAZ (ilk denemede
    // görüldü: test sessizce "başlık hiç yazılmadı" ile başarısız oldu).
    // Kaydı gerçekten doğrulamak için callback'i YAKALAYAN minik bir feature.
    private sealed class CapturingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> callbacks = [];
        public override void OnStarting(Func<object, Task> callback, object state) => callbacks.Add((callback, state));
        public async Task FireOnStartingAsync()
        {
            foreach (var (callback, state) in callbacks) await callback(state);
        }
    }

    // Yanıt başlığı `OnStarting`e taşındı (review bulgusu — erken/senkron
    // atama `UseExceptionHandler`in 5xx'te çağırdığı `Response.Clear()`e
    // giderdi). Burada doğrulanan: (a) `InvokeAsync` dönüşte HENÜZ başlık
    // yazmamıştır — kayıt ertelenmiştir; (b) barındırma katmanı callback'i
    // gerçekten ateşlediğinde başlık doğru değerle yazılır.
    [Fact]
    public async Task yanit_baslamadan_once_baslik_yazilmaz_baslarken_yazilir()
    {
        var responseFeature = new CapturingResponseFeature();
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        features.Set<IHttpResponseFeature>(responseFeature);
        var context = new DefaultHttpContext(features);
        context.Request.Headers[RequestIdMiddleware.HeaderName] = "0123456789abcdef0123456789abcdef";

        await RequestIdMiddleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.True(string.IsNullOrEmpty(context.Response.Headers[RequestIdMiddleware.HeaderName]));

        await responseFeature.FireOnStartingAsync();

        Assert.Equal("0123456789abcdef0123456789abcdef", context.Response.Headers[RequestIdMiddleware.HeaderName]);
    }

    // Bu testin kendi Serilog logger'ı kurması `LogContext` akışının
    // GENEL doğrulaması değil (o zaten .NET'in `AsyncLocal` garantisi) —
    // asıl kanıtladığı, `InvokeAsync`in bastığı özellik ADININ
    // (`PropertyName` sabiti) `LogBufferSink.PropertyString(..., "RequestId")`
    // okuduğu adla AYNI olduğu; ikisi ayrışırsa panel hiçbir zaman kimlik
    // göstermez ama hiçbir tip hatası vermez.
    [Fact]
    public async Task next_icinde_basilan_satir_requestid_tasir()
    {
        var context = new DefaultHttpContext();
        var buffer = new LogBufferSink(capacity: 10);
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(buffer)
            .CreateLogger();

        await RequestIdMiddleware.InvokeAsync(context, _ =>
        {
            logger.Information("istek icinde satir");
            return Task.CompletedTask;
        });

        var entry = Assert.Single(buffer.Snapshot());
        Assert.Equal(context.TraceIdentifier, entry.RequestId);
    }
}
