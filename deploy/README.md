# deploy/

Barındırma ve dağıtım yapılandırması. Üç servis: `nginx` (web) + `api` + `postgres`.
Plan: `docs/uyelik-ve-rapor-plani.md` §7.

| Dosya | Ne yapar |
|---|---|
| `docker-compose.yml` | Temel yığın. İmajları **yerelde derler**. |
| `docker-compose.prod.yml` | Üretim örtüsü — `ghcr.io`'daki hazır imajları kullanır, TLS ve certbot ekler. |
| `nginx.conf` | TLS'siz sunum: SPA geri düşüşü + `/api` ters vekili. Web imajına gömülüdür. |
| `nginx.prod.conf.template` | Üretim: 80 → 443 yönlendirme, TLS, HSTS. `APP_DOMAIN` ile doldurulur. |
| `.env.example` | Ortam değişkeni şablonu. Kopyası `.env` **depoya girmez**. |
| `backup.sh` | Günlük `pg_dump` + saklama + sunucu dışına kopya. |

**Yazı tipleri.** api imajı rapor yazı tiplerini `assets/report-fonts/` dizininden
`/app/fonts` altına alır ve `Reports__FontsPath` oraya bakar (`docker-compose.yml`).
Dizin boş kalırsa PDF konteyner tabanındaki `fonts-dejavu-core`'a düşer; api açılışta
bunu uyarı olarak basar, doğrulama listesindeki günlük taraması onu yakalar.

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

Uygulama: <http://localhost:8080>

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
sudo mkdir -p /opt/alp-pcb-toolkit
sudo chown "$USER" /opt/alp-pcb-toolkit
git clone https://github.com/mcanbektas/alp-pcb-toolkit.git /opt/alp-pcb-toolkit
```

DNS: `APP_DOMAIN` için A kaydı sunucunun IP'sine bakmalı. Sertifika alınmadan
**önce** yayılmış olması gerekir.

Güvenlik duvarı: yalnızca 22, 80, 443 açık. **5432 dışarı açılmaz** — Postgres
yalnızca compose ağından erişilir, host'a port yayınlanmaz.

### 2. Ortam değişkenleri

```bash
cd /opt/alp-pcb-toolkit/deploy
cp .env.example .env
```

`.env` içinde doldurulacaklar:

| Değişken | Not |
|---|---|
| `POSTGRES_PASSWORD` | `openssl rand -base64 32` |
| `JWT_KEY` | `openssl rand -base64 48` — **en az 32 bayt**, kısa anahtarla uygulama açılışta durur |
| `APP_DOMAIN` | Alan adı, `https://` olmadan |
| `FRONTEND_BASE_URL` | `https://<alan-adi>` — doğrulama/parola bağlantıları bundan üretilir |
| `IMAGE_PREFIX` | `ghcr.io/mcanbektas/alp-pcb-toolkit` |
| `IMAGE_TAG` | `latest` ya da `sha-<commit>` |
| `WEB_PORT` | Üretimde **80**. Örtü yalnızca 443'ü ekler; 80 temel dosyadan gelir. |
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
> `web/scripts/build-sitemap.mjs` `dist/sitemap.xml`'i bu değişkenden üretir;
> tanımsızken placeholder alan adıyla üretir ve uyarı basar. Alan adı
> alınınca `.env`'e eklenir ve web derlemesine geçilir (`web/Dockerfile`'daki
> `VITE_API_BASE_URL` notuyla aynı desen). `robots.txt`'teki `Sitemap:` satırı
> göreli değil TAM url ister ve statik dosya olduğu için build zamanı
> değişkeninden gelemez — o satır da aynı günde elle eklenir.

> **SMTP olmadan canlıya çıkılmaz.** E-posta doğrulaması zorunludur
> (`SignIn.RequireConfirmedEmail`); doğrulama postası gitmezse **hiçbir kullanıcı
> giriş yapamaz**. SMTP yapılandırılmamışsa uygulama açılışta uyarı basar ama
> durmaz — günlükte `SMTP yapılandırılmadı` satırı varsa yapılandırma eksiktir.

### 3. İlk sertifika

TLS bloğu sertifika olmadan açılamaz, sertifika da 80 portundan doğrulama ister —
bu yüzden sıra şudur: önce TLS'siz yığın, sonra sertifika, sonra üretim örtüsü.

```bash
cd /opt/alp-pcb-toolkit/deploy
set -a; source .env; set +a

# a) TLS'siz yığını kaldır (imajın gömülü nginx.conf'u 80'de servis eder)
docker compose up -d

# b) http-01 doğrulaması ile ilk sertifika
docker run --rm \
  -v alp-pcb-toolkit_certbot-conf:/etc/letsencrypt \
  -v alp-pcb-toolkit_certbot-webroot:/var/www/certbot \
  -p 80:80 certbot/certbot certonly --standalone \
  -d "$APP_DOMAIN" --email "$CERTBOT_EMAIL" --agree-tos --no-eff-email
```

`--standalone` 80 portunu kendisi dinler, o yüzden b adımından önce web
konteynerini durdurun (`docker compose stop web`).

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
alias alp='docker compose -f /opt/alp-pcb-toolkit/deploy/docker-compose.yml -f /opt/alp-pcb-toolkit/deploy/docker-compose.prod.yml'
```

### 5. Sertifika yenilemesi

`certbot` servisi 12 saatte bir yenilemeyi dener. **Yenilenen sertifikayı nginx
kendiliğinden okumaz** — yeniden yüklenmesi gerekir. Cron:

```
0 4 * * * docker compose -f /opt/alp-pcb-toolkit/deploy/docker-compose.yml -f /opt/alp-pcb-toolkit/deploy/docker-compose.prod.yml exec -T web nginx -s reload
```

### 6. Yedekleme

```
0 3 * * * /opt/alp-pcb-toolkit/deploy/backup.sh >> /var/log/alp-yedek.log 2>&1
```

`.env` içine eklenebilir: `BACKUP_DIR`, `BACKUP_KEEP_DAYS` (varsayılan 14),
`BACKUP_REMOTE_TARGET` (örn. `yedek@baska-sunucu:/yedekler/alp/`).

**Sunucu dışına kopya olmadan yedek yedek değildir** — script bu değer boşken
uyarı basar.

Geri yükleme — üç adım, sırası önemli:

```bash
cd /opt/alp-pcb-toolkit/deploy
set -a; source .env; set +a          # POSTGRES_USER / POSTGRES_DB buradan gelir
docker compose stop api              # dump --clean ile DROP atar; canlı bağlantı
                                     # varken çakışır, açılan api migration'ı
                                     # yarım şemanın üstüne koşabilir
gunzip -c /var/backups/alp-pcb-toolkit/alp-20260801-030000.sql.gz \
  | docker compose exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"
docker compose start api
```

---

## Güncelleme ve geri alma

`main`'e push → GitHub Actions testleri koşar, `api` ve `web` imajlarını derleyip
`ghcr.io`'ya iter (`latest` + `sha-<commit>`). Sunucuda:

```bash
alp pull && alp up -d --no-build
```

Geri alma — yeniden derleme yok, yalnızca etiket değişir:

```bash
sed -i 's/^IMAGE_TAG=.*/IMAGE_TAG=sha-<eski-commit>/' .env
alp pull && alp up -d --no-build
```

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
docker compose logs -f web        # nginx erişim + hata günlüğü de burada —
                                   # access.log/error.log imajda stdout/stderr'e
                                   # symlink'tir, ayrıca `docker exec` gerekmez

# Son N dakika/saat — servis düşünce "ne olmuş" diye baştan taramak yerine
docker compose logs --since 30m api
docker compose logs --since 1h web

# Birden çok servis birlikte, zaman damgasıyla
docker compose logs -f --timestamps api web
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

Yeni bir sunucuda ilk açılışta sırayla:

```bash
curl -I https://<alan-adi>/                        # 200
curl -I https://<alan-adi>/arac/trace-width        # 200 — 404 ise SPA geri düşüşü bozuk
curl -I https://<alan-adi>/en/tool/trace-width     # 200 — İngilizce ağaç (prerender'lı)
curl -I https://<alan-adi>/en                      # 200 — dist/en.html
curl -s  https://<alan-adi>/api/health             # {"status":"ok"}
curl -s  https://<alan-adi>/api/health/ready       # {"status":"ready"} — veritabanı bağlantısı
curl -s  https://<alan-adi>/healthz                # ok — nginx'in kendisi
docker compose logs api | grep -i "uyarı\|warn"    # SMTP / yazı tipi uyarıları
```

Derin bağlantı denetimi ilk sıradadır: `BrowserRouter` kullanılıyor ve
prerender'lanmamış bir rotanın (`/giris`, `/proje/…`) dosya karşılığı yok.
`nginx.conf`'taki zincir
`try_files $uri $uri.html $uri/index.html /spa-fallback.html /index.html` düşerse
site ilk açılışta çalışır, **sayfa yenilendiğinde 404 verir**. Zincirin her
parçasının gerekçesi ölçülmüştür — `docs/prerender-karari.md` §6.

İngilizce satırlar aynı zinciri sınar: `/en/tool/trace-width` isteği
`dist/en/tool/trace-width.html` dosyasına, `/en` ise `dist/en.html`e düşer
(`$uri.html` adımı). Yönlendirme görülmemeli — sitemap'teki URL'ler eğik
çizgisizdir.

`VITE_SITE_URL` ayarlanmadan derlenmiş bir `dist/` yalnız `sitemap.xml`de değil,
76 sayfanın `<head>`indeki `canonical` ve `hreflang` etiketlerinde de placeholder
alan adı taşır. Kontrol:

```bash
curl -s https://<alan-adi>/arac/trace-width | grep -o 'rel="canonical"[^>]*'
curl -s https://<alan-adi>/sitemap.xml | head -6
```

## Bilinen eksikler

- **Migration açılışta uygulanır** (`Database__MigrateOnStartup=true`). Tek
  kopyalı dağıtımda doğru; api birden çok kopyaya çıkarsa kapatılıp ayrı bir
  migration adımına taşınır.
- **Rapor anlık görüntüsü disk değil veritabanı yer kaplar.** Belge baytları
  saklanmaz ama üretimdeki bölüm kayıtları `SectionBlobs` tablosunda donar
  (içerik adresli, kullanıcı başına). Sınır `App__SnapshotQuotaBytes`
  (varsayılan 100 MB/kullanıcı) ve aşıldığında rapor reddedilmez, en eski
  snapshot'lar düşürülür. Postgres yedeğinin boyutu bu tablonun toplamı kadar
  büyür — `docs/rapor-snapshot-karari.md` §2.
- **Sunucu tarafı otomatik dağıtım yok** — yukarıya bakın.
