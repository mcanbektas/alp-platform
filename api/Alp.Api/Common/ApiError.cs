namespace Alp.Api.Common;

// src/lib/ hata sözleşmesiyle aynı biçim: kod tek başına yetmiyorsa yapısal
// `detail` alanı gelir, biçimlenmiş cümle asla. Frontend'deki kural burada
// da geçerli — bkz. CLAUDE.md "Mimari" bölümü.
public record ApiError(string Error, object? Detail = null);
