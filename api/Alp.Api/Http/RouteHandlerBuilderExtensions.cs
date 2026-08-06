using Microsoft.AspNetCore.Http.Metadata;

namespace Alp.Api.Http;

public static class RouteHandlerBuilderExtensions
{
    // Minimal API'ler için istek gövdesi üst sınırı — Kestrel'in ~28.6 MB
    // varsayılanı küçük; her uç kendi gerçekçi üst sınırını burada bildirir
    // (auth gövdeleri birkaç yüz bayt, rapor yükleri birkaç MB olabilir).
    //
    // Sınır ENDPOINT FİLTRESİYLE kurulmaz, METADATA olarak bildirilir. Fark
    // sessiz ve ölçüldü: minimal API'de parametre bağlama filtre hattından
    // ÖNCE koşar (filtre argümanları bağlanmış hâlde alır), yani filtre
    // çalıştığında gövde çoktan okunmuş ve
    // IHttpMaxRequestBodySizeFeature.IsReadOnly `true` olmuştur. Eski
    // uygulama tam da bunu yapıyor ve `!IsReadOnly` koşuluyla atamayı hiçbir
    // uyarı vermeden atlıyordu: 16 KB sınırlı `/api/auth/register`a 200 KB
    // gövde gönderildiğinde istek sorunsuz işleniyor ve kullanıcı
    // oluşuyordu. Dört sınırın (16 KB / 2 MB / 5 MB / 8 KB) dördü de fiilen
    // yoktu; tek gerçek tavan nginx'in 12 MB'ıydı.
    //
    // Metadata'yı yönlendirme katmanı işleyiciye girmeden UYGULAR, yani sınır
    // gövde okunmadan önce yerine oturur ve aşan istek 413 ile döner.
    public static RouteHandlerBuilder LimitBodySize(this RouteHandlerBuilder builder, long bytes) =>
        builder.WithMetadata(new BodySizeLimit(bytes));

    private sealed class BodySizeLimit(long bytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize { get; } = bytes;
    }
}
