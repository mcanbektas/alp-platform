#!/usr/bin/env bash
# Günlük veritabanı yedeği — pg_dump'ı postgres konteynerinin İÇİNDE koşturur,
# sunucuya psql kurulmasını gerektirmez ve konteynerdeki sürümle eşleşmesi
# garanti olur (istemci sürümü sunucudan eskiyse pg_dump reddeder).
#
# Kurulum (sunucuda, root cron):
#   0 3 * * *  /opt/alp-pcb-toolkit/deploy/backup.sh >> /var/log/alp-yedek.log 2>&1
#
# Sunucu dışına kopya ZORUNLUdur: aynı diskteki yedek, disk gittiğinde yedek
# değildir. Kopyalama komutu aşağıda REMOTE_TARGET ile açılır.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# .env'den POSTGRES_* okunur; script sırrı kendi içinde taşımaz.
if [[ ! -f .env ]]; then
  echo "HATA: $SCRIPT_DIR/.env yok. .env.example'dan kopyalayın." >&2
  exit 1
fi
# shellcheck disable=SC1091
set -a; source .env; set +a

BACKUP_DIR="${BACKUP_DIR:-/var/backups/alp-pcb-toolkit}"
KEEP_DAYS="${BACKUP_KEEP_DAYS:-14}"
# Boşsa yalnızca yerel yedek alınır ve script uyarı basar.
REMOTE_TARGET="${BACKUP_REMOTE_TARGET:-}"

STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$BACKUP_DIR/alp-$STAMP.sql.gz"

mkdir -p "$BACKUP_DIR"

# --clean --if-exists: geri yükleme boş olmayan bir veritabanına da uygulanabilsin.
# Çıktı önce .part olarak yazılır; yarım kalan dosya geçerli yedek sanılmasın.
docker compose exec -T postgres \
  pg_dump --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --clean --if-exists \
  | gzip -9 > "$OUT.part"

# DOĞRULA, SONRA ADLANDIR. Eskiden mv önce geliyordu: kesik bir dump geçerli
# görünen adı önce alıyor, ancak ONDAN SONRA hata veriyordu — .part
# aşamalandırmasının bütün amacı boşa gidiyordu. Kontrolden geçmeyen dosya
# hiçbir zaman alp-*.sql.gz adını almaz.
if [[ ! -s "$OUT.part" ]]; then
  rm -f "$OUT.part"
  echo "HATA: yedek boş — $OUT.part silindi" >&2
  exit 1
fi
gzip -t "$OUT.part"

mv "$OUT.part" "$OUT"
echo "Yedek alındı: $OUT ($(du -h "$OUT" | cut -f1))"

# ---- Sunucu dışına kopya ----
if [[ -n "$REMOTE_TARGET" ]]; then
  # Örn. BACKUP_REMOTE_TARGET="yedek@baska-sunucu:/yedekler/alp/"
  scp -q "$OUT" "$REMOTE_TARGET"
  echo "Uzağa kopyalandı: $REMOTE_TARGET"

  # Uzak taraf da budanır — yalnızca yerel budamak uzak diski sonsuz
  # büyütüyordu. Hedef "kullanıcı@sunucu:/yol/" biçiminde; iki nokta yoksa
  # yerel bir dizindir ve aynı find yeter.
  REMOTE_HOST="${REMOTE_TARGET%%:*}"
  REMOTE_PATH="${REMOTE_TARGET#*:}"
  if [[ "$REMOTE_HOST" != "$REMOTE_TARGET" ]]; then
    ssh "$REMOTE_HOST" "find '$REMOTE_PATH' -name 'alp-*.sql.gz' -type f -mtime +$KEEP_DAYS -delete"
  else
    find "$REMOTE_TARGET" -name 'alp-*.sql.gz' -type f -mtime "+$KEEP_DAYS" -delete
  fi
  echo "Uzak yedeklerde $KEEP_DAYS günden eskiler silindi."
else
  echo "UYARI: BACKUP_REMOTE_TARGET boş — yedek yalnızca bu sunucuda duruyor." >&2
fi

# ---- Eskiyenleri sil ----
# Silme, uzağa kopyalama BAŞARILI olduktan sonra yapılır (set -e yukarıda
# başarısız scp'de scripti zaten durdurur).
find "$BACKUP_DIR" -name 'alp-*.sql.gz' -type f -mtime "+$KEEP_DAYS" -delete
echo "$KEEP_DAYS günden eski yedekler silindi."
