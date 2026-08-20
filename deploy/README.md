# deploy/

> **BİLİNEN ENGEL — go-live kapısı.** `/pcb` ve `/comm` path routing bu depoda
> (Faz 4) kuruldu ve smoke-test edildi (edge → api, landing, sağlıklı 502
> geri düşüşü), ama PCB ve Comm'un KENDİ depolarında bir önkoşul TAMAMLANMADI:
> **Comm bunu kapattı** (`base: '/comm/'` + `basename={import.meta.env.BASE_URL}`).
> **PCB kapatmadı:** düzeltme `fix/pcb-suit-base-path` dalında hazır (`75626c1`)
> ama `main`e alınmadı; `main` hâlâ `base: '/'` ile derliyor. O dal birleşmeden
> `/pcb/` gerçek trafikte VARLIK 404'leriyle boş açılır. Ayrıntı
> ve hangi depoda ne değişmesi gerekiyor: aşağıdaki "Yönlendirme" bölümü.
> Sunucu henüz yok (bkz. altındaki not) — bu yüzden bugün canlı bir
> regresyon riski YOK, ama ilk gerçek dağıtımdan önce bu kapı kapanmalı.

Barındırma ve dağıtım yapılandırması. Servisler: `edge` (nginx, süit kenarı) +
`pcb` + `comm` (isteğe bağlı) + `api` (platform) + `postgres` + `seq`. PCB'nin
ayrıştırma-öncesi kararlarının tarihçesi: `docs/uyelik-ve-rapor-plani.md` §7
(alp-pcb-toolkit deposunda).

| Dosya | Ne yapar |
|---|---|
| `docker-compose.yml` | Temel yığın. api'yi **yerelde derler**, ürün SPA'larını ghcr'dan çeker, `edge`i resmi `nginx:alpine` imajıyla mount'lu config'le ayağa kaldırır. |
| `docker-compose.prod.yml` | Üretim örtüsü — `ghcr.io`'daki hazır imajları kullanır, TLS ve certbot ekler (`edge`e taşındı). |
| `docker-compose.pcb-local.yml` | PCB SPA'sını yerelde derlemek için örtü (kardeş dizin varsayar). |
| `docker-compose.comm-local.yml` | Aynı desen, Comm için — BUGÜN çalışmaz, alp-comm-toolkit henüz bir Dockerfile yayınlamıyor (yukarıdaki engel notu). |
| `nginx.conf` | Yerel/TLS'siz edge yapılandırması — landing + `/pcb` + `/comm` + `/api` yönlendirmesi. |
| `nginx.prod.conf.template` | Üretim: 80 → 443 yönlendirme, TLS, HSTS + aynı yönlendirme. `APP_DOMAIN` ile doldurulur. |
| `.env.example` | Ortam değişkeni şablonu. Kopyası `.env` **depoya girmez**. |
| `backup.sh` | Günlük `pg_dump` + saklama + sunucu dışına kopya. |
| `../landing/` | Süitin karşılama sayfası (statik, build'siz) — `edge` doğrudan mount'lar. |

**Yazı tipleri.** api imajı rapor yazı tiplerini `assets/report-fonts/` dizininden
`/app/fonts` altına alır ve `Reports__FontsPath` oraya bakar (`docker-compose.yml`).
Dizin boş kalırsa PDF konteyner tabanındaki `fonts-dejavu-core`'a düşer; api açılışta
bunu uyarı olarak basar, doğrulama listesindeki günlük taraması onu yakalar.

---

## Yönlendirme (Faz 4)

Path tabanlı: `/` landing, `/pcb` PCB Toolkit, `/comm` Comm Toolkit, `/api`
platform API'si — hepsi TEK alan adından, tek `edge` konteynerinin arkasında.

```
İstemci ──▶ edge (nginx, 80/443)
              ├─ /               → landing (statik dosya, edge'in kendi root'u)
              ├─ /api/           → api:8080         (merkezi, tek yer)
              ├─ /pcb/           → pcb:80           (PCB'nin kendi konteyneri)
              └─ /comm/          → comm:80          (comm profili açıkken)
```

`edge` vekillemeyi ŞEFFAF yapar: `/pcb/arac/x` isteği `pcb:80`'e AYNEN
`/pcb/arac/x` olarak gider, önek SİLİNMEZ (`deploy/nginx.conf`'taki
"BİLİNEN ENGEL" notu). Bunun karşılığı olarak PCB/Comm konteynerlerinin KENDİ
ürettiği HTML'in de `/pcb/...`/`/comm/...` köklü varlık yolları taşıması
gerekir — aksi hâlde tarayıcı `/assets/...`i edge'in KÖKÜNDE arar, orada
landing'in dosyaları durur, 404.

**Bu depoda BİTTİ:** `edge` servisi, `nginx.conf`/`nginx.prod.conf.template`,
`comm` servisi (profil, imaj yayınlanınca varsayılana alınır), landing
sayfası. Hepsi smoke-test edildi (`docker compose up -d postgres seq api` +
`edge` → `/api/health`, `/`, `/healthz`, `/pcb` yönlendirmesi ve upstream
yokken temiz 502 — kırılan bir açılış değil).

**Bu depoda BİTMEDİ (başka repo, ayrı iş):**

| Depo | Değişmesi gereken | Bugünkü durum |
|---|---|---|
| alp-pcb-toolkit | `web/vite.config.js`: `base: '/'` → `/pcb/'` | Yok |
| alp-pcb-toolkit | `web/src/App.jsx`: `<BrowserRouter>`e `basename="/pcb"` | Yok |
| alp-pcb-toolkit | PWA manifest (`start_url`, `scope`, ikon yolları) `/pcb/` önekli | Yok |
| alp-pcb-toolkit | `web/nginx.conf`: kendi `try_files`/`location` blokları `/pcb` önekini tanımalı (edge önek SİLMİYOR) | Yok |
| alp-comm-toolkit | `vite.config.ts`: `base: '/'` → `/comm/'` | Yok (router zaten `BASE_URL`den okuyor — `AppRouter.tsx`, tek eksik bu) |
| alp-comm-toolkit | Bir `web/Dockerfile` + yayınlanan imaj | Yok — Faz 2 olgunluğu |

Bu satırlar tamamlanmadan `/pcb` ve `/comm`'u gerçek bir sunucuda açmayın —
`App__FrontendBaseUrl` de (aşağıya bkz.) PCB `/pcb`'ye taşınana kadar KÖKÜ
göstermeye devam etmeli, yoksa doğrulama postaları kırık bağlantı üretir.

---

## Yerelde çalıştırma

```bash
cd deploy
cp .env.example .env
# .env içinde en az POSTGRES_PASSWORD ve JWT_KEY doldurulur:
#   openssl rand -base64 32   → POSTGRES_PASSWORD
#   openssl rand -base64 48   → JWT_KEY   (en az 32 bayt, yoksa uygulama açılmaz)

docker compose up -d --build
```

Uygulama: <http://localhost:8080> (landing). `/pcb` bugün yukarıdaki engel
yüzünden boş/bozuk açılır — PCB'yi denemek için kendi deposunda
`npm run stack` kullanın (kökten, `/pcb` altında değil).

`docker compose up` **Comm'u başlatmaz** (bilerek — `comm` servisi `profiles:
[comm]` altında, imajı henüz yok). Comm için Comm kendi deposunda
`npm run stack` ile koşar, `App__Products__comm__FrontendBaseUrl` üzerinden
CORS'a tanıtılır (aşağıya bkz., ve `api/Alp.Api/Auth/ProductMail.cs`).

```bash
docker compose logs -f api      # açılış, migration, e-posta günlükleri
docker compose ps               # sağlık durumu
docker compose down             # durdur (veri kalır)
docker compose down -v          # veriyi de sil
```

**Yerel yığın `ASPNETCORE_ENVIRONMENT=Development` ile koşar.** Tek gerekçe:
yenileme çerezi üretimde `Secure=true` işaretlenir ve tarayıcı böyle bir çerezi düz
`http://localhost` üzerinde saklamaz — yenileme akışı hiç sınanamazdı. Üretim örtüsü
`Production`'a çevirir. Bunun dışındaki bütün üretim davranışları (migration, ters
vekil başlıkları, HTTPS yönlendirmesinin kapalı olması) yerelde de aynı yoldan geçer.

**SMTP verilmezse postalar gönderilmez, konsola yazılır.** Doğrulama bağlantısını
`docker compose logs api` çıktısından alıp elle açabilirsiniz.

---

## Sunucuya kurulum

Sunucu henüz yok; bu bölüm hazır olduğunda izlenecek sıradır.

### 1. Sunucu hazırlığı

```bash
# Docker Engine + Compose eklentisi
curl -fsSL https://get.docker.com | sh

# Uygulama dizini
sudo mkdir -p /opt/alp-platform
sudo chown "$USER" /opt/alp-platform
git clone https://github.com/mcanbektas/alp-platform.git /opt/alp-platform
```

DNS: `APP_DOMAIN` için A kaydı sunucunun IP'sine bakmalı. Sertifika alınmadan
**önce** yayılmış olması gerekir.

Güvenlik duvarı: yalnızca 22, 80, 443 açık. **5432 dışarı açılmaz** — Postgres
yalnızca compose ağından erişilir, host'a port yayınlanmaz.

### 2. Ortam değişkenleri

```bash
cd /opt/alp-platform/deploy
cp .env.example .env
```

`.env` içinde doldurulacaklar:

| Değişken | Not |
|---|---|
| `POSTGRES_PASSWORD` | `openssl rand -base64 32` |
| `JWT_KEY` | `openssl rand -base64 48` — **en az 32 bayt**, kısa anahtarla uygulama açılışta durur |
| `APP_DOMAIN` | Alan adı, `https://` olmadan |
| `FRONTEND_BASE_URL` | `https://<alan-adi>` — PCB `/pcb`'ye taşınana kadar KÖKÜ gösterir, yukarıdaki "Yönlendirme" bölümüne bkz. |
| `COMM_FRONTEND_BASE_URL` | Comm'un origin'i — Comm'un kendi imajı/dev sunucusu ayakta olduğunda doldurulur, aksi hâlde boş bırakılır |
| `PLATFORM_IMAGE_PREFIX` | `ghcr.io/mcanbektas/alp-platform` — api imajı |
| `PCB_IMAGE_PREFIX` | `ghcr.io/mcanbektas/alp-pcb-toolkit` — PCB SPA imajı |
| `COMM_IMAGE_PREFIX` | `ghcr.io/mcanbektas/alp-comm-toolkit` — Comm SPA imajı (henüz yayınlanmıyor) |
| `PLATFORM_IMAGE_TAG` / `PCB_IMAGE_TAG` / `COMM_IMAGE_TAG` | `latest` ya da `sha-<commit>`. **Ayrı ayrı** — ürünler bağımsız sürümlenir, birini geri almak ötekini etkilemez. |
| `WEB_PORT` | Üretimde **80**. Örtü yalnızca 443'ü ekler; 80 temel dosyadan gelir. Artık `edge`e bağlıdır. |
| `SMTP_*` | **Zorunlu** — aşağıya bakın |
| `CERTBOT_EMAIL` | Sertifika bildirimleri |
| `ADMIN_EMAILS` | Yönetim panelini görecek hesapların e-postaları, virgülle ayrılır |

> **Yönetim yetkisinin tek kaynağı `ADMIN_EMAILS`'tir.** Panelden, kayıttan ya
> da başka bir uçtan admin olunamaz; yetki vermek bu satırı değiştirip `api`yi
> yeniden başlatmayı gerektirir. Açılışta listedeki hesaplara rol verilir,
> listeden çıkarılanlardan **alınır** — yani yetkiyi geri almanın yolu da aynı
> satırdır, elle SQL gerekmez. Adres henüz kayıtlı değilse günlüğe uyarı düşer
> ve o hesap kayıt olduğu anda yetkiyi alır.
>
> Panelde yapılabilen tek yıkıcı işlem hesap silmedir ve yönetici kendi
> hesabını da başka bir yöneticiyi de silemez. Bir yöneticiyi gerçekten silmek
> gerekiyorsa önce `ADMIN_EMAILS`ten çıkarılır ve `api` yeniden başlatılır.

> **Alan adı alınınca eklenecek:** `VITE_SITE_URL` henüz `.env`'de yok.
> PCB'nin `web/scripts/build-sitemap.mjs`'i `dist/sitemap.xml`'i bu
> değişkenden üretir; tanımsızken placeholder alan adıyla üretir ve uyarı
> basar. Alan adı alınınca `.env`'e eklenir ve PCB web derlemesine geçilir
> (`web/Dockerfile`'daki `VITE_API_BASE_URL` notuyla aynı desen).
> `robots.txt`'teki `Sitemap:` satırı göreli değil TAM url ister ve statik
> dosya olduğu için build zamanı değişkeninden gelemez — o satır da aynı
> günde elle eklenir. PCB `/pcb` altına taşındığında bu URL'ler de öneki
> almalı — yukarıdaki "Yönlendirme" bölümündeki iş listesinin bir parçası.

> **SMTP olmadan canlıya çıkılmaz.** E-posta doğrulaması zorunludur
> (`SignIn.RequireConfirmedEmail`); doğrulama postası gitmezse **hiçbir kullanıcı
> giriş yapamaz**. SMTP yapılandırılmamışsa uygulama açılışta uyarı basar ama
> durmaz — günlükte `SMTP yapılandırılmadı` satırı varsa yapılandırma eksiktir.

### 3. İlk sertifika

TLS bloğu sertifika olmadan açılamaz, sertifika da 80 portundan doğrulama ister —
bu yüzden sıra şudur: önce TLS'siz yığın, sonra sertifika, sonra üretim örtüsü.

```bash
cd /opt/alp-platform/deploy
set -a; source .env; set +a

# a) TLS'siz yığını kaldır (edge 80'de landing/pcb/comm/api'yi servis eder)
docker compose up -d

# b) http-01 doğrulaması ile ilk sertifika
docker run --rm \
  -v alp-platform_certbot-conf:/etc/letsencrypt \
  -v alp-platform_certbot-webroot:/var/www/certbot \
  -p 80:80 certbot/certbot certonly --standalone \
  -d "$APP_DOMAIN" --email "$CERTBOT_EMAIL" --agree-tos --no-eff-email
```

`--standalone` 80 portunu kendisi dinler, o yüzden b adımından önce edge
konteynerini durdurun (`docker compose stop edge`).

### 4. Üretim yığını

```bash
echo "$GHCR_TOKEN" | docker login ghcr.io -u <kullanıcı> --password-stdin

docker compose -f docker-compose.yml -f docker-compose.prod.yml pull
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --no-build
```

**`--no-build` şart.** Temel dosyadaki `build:` anahtarı örtüde kaldırılamaz
(Compose birleştirmede anahtar silinemez); bayrak verilmezse sunucu imajı kendi
derlemeye kalkar ve .NET derlemesi küçük bir VPS'i tüketir.

Kolaylık için sunucuda takma ad:

```bash
alias alp='docker compose -f /opt/alp-platform/deploy/docker-compose.yml -f /opt/alp-platform/deploy/docker-compose.prod.yml'
```

### 5. Sertifika yenilemesi

`certbot` servisi 12 saatte bir yenilemeyi dener. **Yenilenen sertifikayı nginx
kendiliğinden okumaz** — yeniden yüklenmesi gerekir. Cron:

```
0 4 * * * docker compose -f /opt/alp-platform/deploy/docker-compose.yml -f /opt/alp-platform/deploy/docker-compose.prod.yml exec -T edge nginx -s reload
```

### 6. Yedekleme

```
0 3 * * * /opt/alp-platform/deploy/backup.sh >> /var/log/alp-yedek.log 2>&1
```

`.env` içine eklenebilir: `BACKUP_DIR`, `BACKUP_KEEP_DAYS` (varsayılan 14),
`BACKUP_REMOTE_TARGET` (örn. `yedek@baska-sunucu:/yedekler/alp/`).

**Sunucu dışına kopya olmadan yedek yedek değildir** — script bu değer boşken
uyarı basar.

Geri yükleme — üç adım, sırası önemli:

```bash
cd /opt/alp-platform/deploy
set -a; source .env; set +a          # POSTGRES_USER / POSTGRES_DB buradan gelir
docker compose stop api              # dump --clean ile DROP atar; canlı bağlantı
                                     # varken çakışır, açılan api migration'ı
                                     # yarım şemanın üstüne koşabilir
gunzip -c /var/backups/alp-platform/alp-20260801-030000.sql.gz \
  | docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"
docker compose start api
```

---

## Güncelleme ve geri alma

`main`'e push → GitHub Actions testleri koşar, `api` imajını derleyip
`ghcr.io`'ya iter (`latest` + `sha-<commit>`). PCB ve Comm imajları KENDİ
depolarındaki CI'dan gelir, burada derlenmez. Sunucuda:

```bash
alp pull && alp up -d --no-build
```

Geri alma — yeniden derleme yok, yalnızca etiket değişir:

```bash
sed -i 's/^PLATFORM_IMAGE_TAG=.*/PLATFORM_IMAGE_TAG=sha-<eski-commit>/' .env
alp pull && alp up -d --no-build
```

`landing/` ve `nginx*.conf*` imaja gömülü DEĞİL — bind mount'la okunur (bkz.
`docker-compose.yml` → `edge`). Bu ikisini değiştirmek imaj derlemesi
istemez, `git pull` + `alp up -d` yeter (nginx conf'u yeniden okur).

**Dağıtımı otomatikleştirmek** (isteğe bağlı, sunucu hazır olduğunda):
`.github/workflows/deploy.yml` içine `images` işinden sonra SSH ile bağlanan bir
`deploy` işi eklenir; gereken sırlar `SSH_HOST`, `SSH_USER`, `SSH_KEY`. Sunucu
yokken bu adım bilerek yazılmadı — kullanılmayan dağıtım sırrı depoda durmamalı.

---

## Günlük okuma (runbook)

İki ayrı kayıt vardır ve karıştırılmamalı: **operasyonel günlük** (ne oluyor —
uçucu, Docker'ın kendisinde durur) ve **denetim izi** (kim ne yaptı — kalıcı,
veritabanında durur, panelden okunur). Aşağıdakiler yalnızca birincisi için.

```bash
# Canlı takip (Ctrl+C ile çık)
docker compose logs -f api
docker compose logs -f edge       # nginx erişim + hata günlüğü de burada —
                                   # access.log/error.log resmi nginx imajında
                                   # stdout/stderr'e symlink'tir, `docker exec`
                                   # gerekmez. PCB/Comm'un KENDİ nginx günlüğü
                                   # burada YOKTUR — onlar kendi servislerinde
                                   # (`docker compose logs pcb` / `... comm`).

# Son N dakika/saat — servis düşünce "ne olmuş" diye baştan taramak yerine
docker compose logs --since 30m api
docker compose logs --since 1h edge

# Birden çok servis birlikte, zaman damgasıyla
docker compose logs -f --timestamps api edge
```

**Üretimde** (`ASPNETCORE_ENVIRONMENT=Production`) api'nin konsol çıktısı
`CompactJsonFormatter` ile tek satır JSON'dur (`Program.cs`) ve `jq` ile
süzülebilir. Alan adları CompactJsonFormatter'a özgüdür: seviye `@l`'dedir
(yalnız Information DIŞINDA basılır — bir satırda `@l` yoksa Information'dır),
zenginleştirilmiş alanlar (`RequestPath`, `ClientIp`, `UserId`, …) `Properties`
altında DEĞİL doğrudan satırın kökündedir. `--no-log-prefix` servis adı
önekini kaldırır, aksi hâlde her satırın başındaki `api-1  |` JSON'u bozar
(gerçek çıktıyla doğrulandı):

```bash
# Yalnız hata seviyesi ve üstü
docker compose logs --no-log-prefix --since 1h api | jq -R 'fromjson? // empty' \
  | jq 'select(.["@l"] == "Error" or .["@l"] == "Fatal")'

# Belirli bir istek yolunu içeren satırlar
docker compose logs --no-log-prefix api | jq -R 'fromjson? // empty' \
  | jq 'select(.RequestPath? == "/api/auth/login")'
```

**İstek korelasyonu** (docs/brifler/14-loglama-altyapi.md §2, alp-pcb-toolkit
deposunda): `edge` her isteğe `$request_id` üretir, `X-Request-Id`
başlığıyla API'ye taşır; API kendi kimliğini üretmez, geleni AYNEN kullanır ve
`RequestId` alanıyla (yukarıdaki `RequestPath` gibi, kökte) hem
`/yonetim/loglar` panelinin ayrıntı kartına hem üretim JSON'una basar. Nginx
`access.log`taki karşılığı `rid=` alanıdır (`log_format sorgusuz`). Bir
isteğin nginx satırını ve API satırlarını yan yana okumak:

```bash
# Önce edge'in access.log'undan kimliği bul (rid=... alanı satır sonunda).
# `docker compose exec edge tail .../access.log` ÇALIŞMAZ: resmi nginx imajı
# access.log'u /dev/stdout'a symlink'ler — dosyadan değil, konteyner
# LOGUNDAN okunur.
docker compose logs --no-log-prefix edge | grep '/api/auth/login'

# Sonra o kimlikle API satırlarını süz
docker compose logs --no-log-prefix api | jq -R 'fromjson? // empty' \
  | jq --arg rid "<yukarıda bulunan kimlik>" 'select(.RequestId? == $rid)'
```

`fromjson? // empty` şart: açılış/health-check gibi bazı satırlar JSON değil
düz metin gelebilir, `jq` bunlarda direkt `fromjson` ile patlar.

**Yerelde** (`npm run stack:docker`, yığın `Development` ortamında koşar)
konsol çıktısı okunabilir düz metindir, JSON değildir — yukarıdaki `jq`
örnekleri üretim örtüsü (`docker-compose.prod.yml`) altında anlamlıdır.

**Denetim izi (kim ne yaptı) bu günlüklerde YOKTUR ve aranmamalı.** Yönetici
eylemleri (hesap silme, rol verme/alma), kimlik olayları (parola sıfırlama,
kilitlenme) `AuditEvents` tablosunda kalıcı olarak durur ve `/yonetim/gunluk`
panelinden (`GET /api/admin/audit`) filtrelenip okunur — konteyner yeniden
başlasa da kaybolmaz. Konteyner logları geçicidir ve yukarıdaki `logging:`
sınırına takılınca en eskisi silinir; audit tablosu ayrıca `AuditRetentionDays`
(`.env` → `AUDIT_RETENTION_DAYS`, varsayılan 365) ile kendi saklama süresini
uygular.

---

## Doğrulama listesi

Yeni bir sunucuda ilk açılışta sırayla. **PCB/Comm satırları yukarıdaki
"Bilinen engel" kapanana kadar 404/boş sayfa verir** — bu beklenen bir
kırılmadır, engel notunun kendisi değil.

```bash
curl -I https://<alan-adi>/                        # 200 — landing
curl -I https://<alan-adi>/pcb                     # 301 → /pcb/
curl -I https://<alan-adi>/pcb/                    # 200 (engel kapanınca)
curl -I https://<alan-adi>/comm                    # 301 → /comm/ (comm profili açıksa)
curl -s  https://<alan-adi>/api/health             # {"status":"ok"}
curl -s  https://<alan-adi>/api/health/ready       # {"status":"ready"} — veritabanı bağlantısı
curl -s  https://<alan-adi>/healthz                # ok — edge'in kendisi
docker compose logs api | grep -i "uyarı\|warn"    # SMTP / yazı tipi uyarıları
```

PCB engel kapandıktan sonra derin bağlantı denetimi ilk sıradadır:
`BrowserRouter` kullanılıyor ve prerender'lanmamış bir rotanın
(`/pcb/giris`, `/pcb/proje/…`) dosya karşılığı yok. PCB'nin KENDİ
`nginx.conf`'undaki `try_files` zinciri düşerse `/pcb/` ilk açılışta çalışır,
**sayfa yenilendiğinde 404 verir** — bu artık `edge`in değil PCB
konteynerinin sorumluluğu.

```bash
curl -I https://<alan-adi>/pcb/arac/trace-width     # 200 — 404 ise PCB'nin kendi geri düşüşü bozuk
curl -s https://<alan-adi>/pcb/arac/trace-width | grep -o 'rel="canonical"[^>]*'
```

`VITE_SITE_URL` ayarlanmadan derlenmiş bir PCB `dist/`i yalnız
`sitemap.xml`'de değil, sayfaların `<head>`indeki `canonical` ve `hreflang`
etiketlerinde de placeholder alan adı taşır — yukarıdaki `canonical`
kontrolü bunu yakalar.

## Bilinen eksikler

- **PCB base-path — go-live kapısının kalan yarısı.** Comm kapattı; PCB'nin
  düzeltmesi `alp-pcb-toolkit` deposunda `fix/pcb-suit-base-path` dalında bekliyor
  (`75626c1`). Dal `main`e alınmadan `/pcb` gerçek trafikte çalışmaz. `edge`in
  kendisi doğru kuruldu ve test edildi.
- **Migration açılışta uygulanır** (`Database__MigrateOnStartup=true`). Tek
  kopyalı dağıtımda doğru; api birden çok kopyaya çıkarsa kapatılıp ayrı bir
  migration adımına taşınır.
- **Rapor anlık görüntüsü disk değil veritabanı yer kaplar.** Belge baytları
  saklanmaz ama üretimdeki bölüm kayıtları `SectionBlobs` tablosunda donar
  (içerik adresli, kullanıcı başına). Sınır `App__SnapshotQuotaBytes`
  (varsayılan 100 MB/kullanıcı) ve aşıldığında rapor reddedilmez, en eski
  snapshot'lar düşürülür. Postgres yedeğinin boyutu bu tablonun toplamı kadar
  büyür — `docs/rapor-snapshot-karari.md` §2 (alp-pcb-toolkit deposunda).
- **Sunucu tarafı otomatik dağıtım yok** — yukarıya bakın.
