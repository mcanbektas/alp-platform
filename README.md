# ALP Platform

ALP ürün süitinin ortak sırtı: kimlik, veritabanı, rapor üretimi ve dağıtım yığını.
Ürün arayüzleri bu depoda değil — her ürün kendi deposunda yaşar.

| Parça | Nerede |
|---|---|
| **api** — ASP.NET Core 9, Identity + JWT, EF Core, PDF/Excel rapor | bu depo, `api/` |
| **deploy** — Docker Compose (postgres + api + ürün SPA'ları + seq), nginx, certbot | bu depo, `deploy/` |
| **PCB Toolkit** — PCB mühendislik hesap araçları (SPA) | [alp-pcb-toolkit](https://github.com/mcanbektas/alp-pcb-toolkit) |
| **Comm Toolkit** — haberleşme protokolü analiz platformu (SPA) | alp-comm-toolkit *(planlandı)* |

Tek hesap, tek alan adı, tek veritabanı, tek deploy; ürünler bağımsız depolarda ve bağımsız
sürümlerle. Tek api servisi, ürün başına feature klasörü — mikroservis değil, modüler monolit.

## Hızlı başlangıç

```bash
dotnet test  api/Alp.Api.sln          # bellek içi SQLite — DB servisi gerekmez
dotnet run --project api/Alp.Api      # http://localhost:5289
```

Ürün SPA'sı kendi deposunda `npm run stack` ile koşar ve `/api`'yi buraya vekiller.

Tam yığın (postgres + api + SPA imajları + seq) için: [`deploy/README.md`](deploy/README.md).

## Ayrıştırma notu

`api/`, `deploy/` ve `assets/` 2026-08-09'da alp-pcb-toolkit deposundan **geçmişiyle
birlikte** taşındı (`git filter-repo`). Ayrıştırma öncesi api/deploy kararlarının tarihçesi
(üyelik planı, loglama kararı, rapor snapshot kararı, e-posta dili kararı, brifler) o deponun
`docs/` dizinindedir — kopyalanmadı, kopya ayrışır.

Ayrıntılı mimari kurallar ve modül sözleşmesi: [`CLAUDE.md`](CLAUDE.md).
