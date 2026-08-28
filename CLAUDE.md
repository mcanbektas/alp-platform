# CLAUDE.md

Bu dosya, bu depoda çalışan Claude Code'a (claude.ai/code) yol gösterir.

## Depo ne, ne değil

**ALP Platform** — ALP ürün süitinin ortak sırtı. Hesap (kimlik/oturum), veritabanı,
dağıtım yığını ve ortak tasarım katmanı burada yaşar. **Ürün arayüzleri burada DEĞİL**;
her ürün kendi deposunda kendi SPA'sını ve kendi imajını taşır:

| Depo | İçerik | Durum |
|---|---|---|
| **alp-platform** (burası) | `api/` (ASP.NET Core 9), `deploy/`, `assets/`, `design/`, `landing/` | aktif |
| **alp-pcb-toolkit** | PCB SPA'sı (`web/`, Vite + React 18, JS) | aktif |
| **alp-comm-toolkit** | Comm SPA'sı (Vite + React 18, TS + Tailwind) | aktif |
| **alp-aerospace** | SIM-IT Aerospace SPA'sı (İHA/roket) | **süite bağlanmadı** |
| **alp-systemlab** | SIM-IT SystemLab SPA'sı (blok simülasyonu) | **süite bağlanmadı** |

Autodesk modeli: tek hesap, tek alan adı, tek veritabanı, tek deploy — ürünler bağımsız
depolarda, bağımsız sürümlerle. **Mikroservis değil, modüler monolit**: tek api servisi,
ürün başına feature klasörü.

Bu ayrıştırma 2026-08-09'da yapıldı; `api/`, `deploy/` ve `assets/` alp-pcb-toolkit'ten
geçmişiyle birlikte taşındı (`git filter-repo`). **Ayrıştırma öncesi api/deploy kararlarının
tarihçesi alp-pcb-toolkit deposunun `docs/` dizinindedir** — `uyelik-ve-rapor-plani.md`,
`loglama-karari.md`, `rapor-snapshot-karari.md`, `eposta-dili-karari.md`,
`brifler/06-sunucu-gunu.md`, `brifler/11|12|14-*.md`. Buraya kopyalanmadı: kopya ayrışır.
Yeni platform kararları bu depoda yeni bir `docs/` altında yazılır.

## Komutlar

```bash
dotnet build api/Alp.Api.sln -c Release
dotnet test  api/Alp.Api.sln            # xunit — bellek içi SQLite, DB servisi gerekmez
dotnet run --project api/Alp.Api        # http://localhost:5289
```

**API portu 5289'dur ve tek yerde değişmez**: `api/Alp.Api/Properties/launchSettings.json`
ile ürün SPA'larının dev proxy hedefi (`vite.config.js`) aynı olmak zorunda. Ayrıştıklarında
uygulama açılır ama `/api` istekleri sessizce 404 döner — giriş de rapor da çalışmaz, hata
mesajı çıkmaz.

Docker yığını günlük iş için gerekmez; ürün SPA'sı kendi deposunda `npm run stack` ile koşup
`/api`'yi buradaki `dotnet run`a vekiller. Yığın yalnızca DERLENMİŞ çıktıyı doğrulamak için
kaldırılır — `deploy/README.md`.

## Mimari

```
Alp.Domain  (entity'ler)
   ↑
Alp.Data    (AppDbContext + EF migration)      Alp.Reports (QuestPDF / ClosedXML)
   ↑                                              ↑
Alp.Api     (Minimal API — feature klasörleri)  ←┘
   ↑
Alp.Api.Tests (xunit)
```

`Alp.Api` **Minimal API**'dir, MVC controller yoktur. Uçlar özellik klasörlerinde toplanır ve
`Program.cs`'te tek satırla bağlanır: `app.MapAuthEndpoints()`, `MapAdminEndpoints()`,
`MapReportEndpoints()`, `MapProjectEndpoints()`. Tüm uçlar `/api` öneklidir; sağlık uçları da
oradadır (`/api/health`, `/api/health/ready`).

Middleware sırası (`Program.cs`): CORS → ForwardedHeaders → RateLimiting → RequestId → Serilog
→ JWT/Identity. `App__KnownProxyNetworks` verilmezse `X-Forwarded-For` yok sayılır ve bütün
istekler ters vekilin tek IP'sine düşer — hız sınırı tek kova olur.

### Ürün modülü kuralları

Süit büyüdükçe monolitin dağılmaması bu üç kurala bağlı:

1. **Modül = feature klasörü.** `Comm/`, `Pcb/`… kendi uçları, servisleri ve DTO'larıyla.
2. **Her modül kendi DB şemasına yazar** (`comm`, `pcb`); şemalar arası foreign key **yasak**.
   Tek istisna Identity ve Audit tabloları — onlar `platform` şemasında ortak.
3. **Modüller birbirini çağırmaz.** İki modülün aynı şeye ihtiyacı varsa o şey `Platform/`'a
   iner. Modülden modüle çağrı, ayrıştırma günü çözülmesi imkânsız düğüm demektir.

Bir ürün ağır, CRUD-dışı bir backend isterse (gerçek zamanlı işleme, uzun süren hesap) o ürüne
**sidecar servis** eklenir — aynı ters vekilin ve aynı JWT'nin arkasında. Kapı açık, bugün
kapalı: hiçbir üründe böyle bir ihtiyaç yok.

### Edge yönlendirme (Faz 4)

Path tabanlı, tek alan adı: `/` landing (statik, `landing/`), `/pcb`, `/comm`, `/api` — hepsi
`deploy/` içindeki `edge` (nginx) konteynerinin arkasında. `edge` vekillemeyi ŞEFFAF yapar,
öneki SİLMEZ: `/pcb/x` isteği `pcb:80`'e AYNEN `/pcb/x` olarak gider. Bunun karşılığı olarak
PCB/Comm'un KENDİ ürettiği HTML'in de `/pcb/…`/`/comm/…` köklü varlık yolları taşıması şart —
aksi hâlde `/assets/…` isteği edge'in KÖKÜNDE (landing'de) 404 alır.

**Go-live kapısı KAPANDI (2026-08-28).** Comm tarafı zaten tamamdı: `vite.config.ts`
`base: '/comm/'` ve `BrowserRouter basename={import.meta.env.BASE_URL}`. PCB tarafında
`fix/pcb-suit-base-path` (`75626c1`) `main`e birleştirildi ve dalın ATLADIĞI iki katman da
kapatıldı (`22bd8e9`, `8d737ca`): prerender `StaticRouter`ı öneksiz koşuyor ve 112 sayfanın
bağlantılarını `/arac/…` basıyordu; service worker precache'i `/spa-fallback.html` isteyip
404 aldığı için hiç kurulmuyordu. Önek artık PCB'de de tek kaynakta (`vite.config.js` → `BASE`,
yönlendirici `import.meta.env.BASE_URL`'den okur) — Comm ile aynı desen. Doğrulandı: 3133 birim,
32 e2e, 5 PWA e2e yeşil ve derleme çıktısı `/pcb/` önekli.

Kalan tek ayar canlı öncesi: `VITE_SITE_URL` süit önekini TAŞIMALI
(`https://<alan>/pcb`), yoksa canonical/hreflang/sitemap kökü gösterir. Yeri:
bu deponun `deploy/.env` ve PCB deposunun `SITE_URL` Actions değişkeni.
alp-platform tarafı (edge, compose, landing) kurulu ve smoke-test edilmişti; tam liste
`deploy/README.md` → "Yönlendirme" bölümünde.

### Kimlik

ASP.NET Core Identity + JWT (HMAC-SHA256, en az 32 bayt — açılışta doğrulanır) + HttpOnly
yenileme çerezi. Access token yalnızca tarayıcı belleğinde tutulur, `localStorage`'a **hiç
yazılmaz** (XSS önlemi); yenileme sessizdir. E-posta doğrulaması zorunludur.

**Admin yetkisi DB rolünden değil `ADMIN_EMAILS` env listesinden gelir.** Panelden ya da
kayıttan admin olunamaz; yetki vermek o satırı değiştirip api'yi yeniden başlatmaktır.
Eşitleme açılışta koşar, listeden çıkan hesabın yetkisi geri alınır.

**Tüm ürünler aynı `/api/auth`'u kullanır.** Yeni bir ürün eklendiğinde CORS'a origin'i
eklenir; ayrı bir kimlik sistemi kurulmaz — süitin tanımı budur.

### Veritabanı

PostgreSQL 16 (testlerde bellek içi SQLite; `InMemory` sağlayıcısı benzersiz dizin
ZORLAMADIĞI için kullanılmaz — şema modelden kurulur). Şema `AppDbContext` + `Migrations/`.
Açılışta uygulanır (`Database__MigrateOnStartup`); konteynerde `dotnet ef` yoktur, bu ayar tek
kopyalı dağıtım içindir — kopya sayısı artarsa kapatılıp ayrı adıma taşınır.

### Raporlama

**Rapor DOSYASI saklanmaz, rapor BÖLÜMLERİ saklanır.** PDF/XLSX baytları hiçbir yere
yazılmaz; üretim anındaki bölüm kayıtlarının ham kopyası `SectionBlobs` +
`ReportSnapshotSections` tablolarında **içerik adresli** olarak donar. Belge yine dizgi anında
üretilir, dil indirme isteğinden seçilir, dizgi düzeltmeleri geçmişe de uygular. Kota
(`App:SnapshotQuotaBytes`) raporu REDDETMEZ, en eski snapshot'ları düşürür.

QuestPDF'in yerel bağımlılığı fontconfig'tir — kurulmazsa PDF üretimi çalışma anında patlar,
derlemede görünmez (`api/Dockerfile`). Rapor yazı tipleri `assets/report-fonts/` altındaki
**ttf**'lerdir; SPA'nın woff2 alt kümeleri çizim katmanınca okunamaz. Lisans metni fontun
baytıyla birlikte taşınır (SIL OFL 1.1 şartı).

### Kimlik postaları — metin SUNUCUDA durur

Doğrulama, parola sıfırlama ve kayıt denemesi postalarının konusu ve gövdesi iki dilli olarak
`api/Alp.Api/Auth/AuthEmailText.cs`tedir; istemciden gelen tek şey **dil kodudur**. Gövdeyi
istemci belirlerse uç, bizim alan adımızdan çıkan ve markamızı taşıyan serbest metni istenen
adrese postalayan bir araca döner — kimlik avı yüzeyi.

**Ürün başına posta yapılandırması (Faz 3'te kapandı).** Bağlantı yolları, marka adı ve ön
yüz adresi artık `Auth/ProductMail.cs`te ürün anahtarına göre çözülür ve appsettings'ten
(`App:Products:<ürün>:...`) override edilebilir — sabit, tek ürüne bağlı bir tablo değil.
İstemci isteğe `product` alanı ekler (`"pcb"` | `"comm"`, varsayılan `"pcb"`); eski PCB
istemcisi bu alanı hiç göndermez ve `App:FrontendBaseUrl` üzerinden ESKİ davranışla birebir
aynı sonuca düşer — geriye dönük kırılma yok. Comm için karşılığı ayarlanmamışsa
`localhost:3001` ve PCB'yle aynı yol adlarına düşülür; gerçek rotalar netleşince yalnızca
appsettings değişir, kod değişmez. Eski bekçi testinin (`authMailPaths.guard.test.js`,
alp-pcb-toolkit) çözdüğü riski — postadaki yolun SPA'nın gerçek rotasından sapması — artık
kod değil yapılandırma taşıdığı için o test devralınmadı; yol değiştiğinde `App:Products`
altındaki ilgili anahtarın SPA'nın rota sözlüğüyle elle karşılaştırılması gerekir.

## Kurallar

- **Yeni bir uç yazarken kuralını da test et** — sahiplik, sınır ve tür doğrulaması için desen
  `Alp.Api.Tests` içinde hazırdır. Uçlar HTTP üzerinden değil, işleyicileri doğrudan çağırarak
  sınanır (test edilen üyeler `internal` + `InternalsVisibleTo`).
- **Sırlar depoya girmez.** `deploy/.env` gitignore'dadır; şablon `.env.example`.
- **Sunucuya bağlanan CI adımı bilerek yoktur** — sunucu henüz yok, kullanılmayan SSH sırrı
  depoda durmaz. `ci.yml` test+build, `images.yml` yalnız `main`'de imaj derleyip ghcr'a iter.
- **Kod yorumları Türkçedir ve çevrilmez.** Değişken/sınıf/dosya adları İngilizce.
- SMTP verilmezse `ConsoleEmailSender`'a düşülür. **Üretimde bu düşüş bir arızadır:** e-posta
  doğrulaması zorunlu olduğu için hiçbir kullanıcı giriş yapamaz. Uygulama açılışta uyarı basar.

## Yol haritası

Faz planı ve faz başına model/effort önerisi:
`~/dev/alp-comm-toolkit/docs/plan-fazlar.md`.

**Bu depodaki fazların hepsi bitti** (Faz 0–4, son commit 2026-08-10). Aşağıdaki tablo
tarihçedir, yapılacak iş listesi değil.

| Faz | İş | Depo |
|---|---|---|
| 0 ✅ | Platform ayrıştırma | burası + pcb |
| 1 ✅ | `design/` — tasarım token'ları + `@mcanbektas/design` (ortak header, hesap menüsü, ürün değiştirici). **Scope `@alp/design` DEĞİL**: GitHub'da `alp` kullanıcı adı başkasına ait, Packages scope'u repo sahibiyle eşleşmek zorunda | burası |
| 2 ✅ | Comm SPA iskeleti | alp-comm-toolkit |
| 3 ✅ | `Comm/` feature modülü + `comm` şeması + CORS + auth mail yollarının ürün başına taşınması | burası |
| 4 ✅ | `landing/` + edge nginx path routing (`/pcb`, `/comm`) + compose süit düzeni + deploy runbook'unun yeniden yazımı | burası |
