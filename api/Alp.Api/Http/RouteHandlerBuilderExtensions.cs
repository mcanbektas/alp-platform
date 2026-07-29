using Microsoft.AspNetCore.Http.Features;

namespace Alp.Api.Http;

public static class RouteHandlerBuilderExtensions
{
    // Minimal API'ler için istek gövdesi üst sınırı — Kestrel'in ~28.6 MB
    // varsayılanı küçük; her uç kendi gerçekçi üst sınırını burada bildirir
    // (auth gövdeleri birkaç yüz bayt, rapor yükleri birkaç MB olabilir).
    public static RouteHandlerBuilder LimitBodySize(this RouteHandlerBuilder builder, long bytes) =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is not null && !feature.IsReadOnly) feature.MaxRequestBodySize = bytes;
            return await next(ctx);
        });
}
