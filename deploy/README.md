# deploy/

Barındırma ve dağıtım yapılandırması. İçerik Faz 8'de eklenir:

- `docker-compose.yml` — `nginx` + `api` + `postgres` üç servis
- `nginx.conf` — statik `web/dist` sunumu, `/api` ters vekili, TLS
- Gizli değerler (`.env`) depoya girmez, `.gitignore`'da hariç tutulmuştur

Ayrıntı: `docs/uyelik-ve-rapor-plani.md` §7.
