# @mcanbektas/design

ALP Suite'in ortak tasarım katmanı: semantik CSS token'ları + shell bileşenleri
(`Header`, `AccountMenu`, `ProductSwitcher`). Ürün SPA'ları (PCB, Comm, ...) bunu
bağımlılık olarak alır; kendi ürün içeriği kendi deposunda kalır.

## Scope kararı

Paket `@alp/design` değil `@mcanbektas/design` — GitHub Packages'ta npm scope,
repo sahibinin GitHub kullanıcı/org adıyla birebir eşleşmek zorunda ve `alp`
kullanıcı adı GitHub'da başkasına ait. Süit büyüyüp ayrı bir org'a taşınırsa
(`@alp-suite` gibi) scope değişir, kod değişmez.

## Token'lar

`src/tokens/light.css` ve `dark.css` — PCB'nin 4 temasından (solder-light,
instrument, graphite, solder) damıtılmış semantik isimler. PCB'ye özgü sabit
paletler (`--pro-*` rozet renkleri, `--band-*` direnç bantları) buraya girmedi;
onlar PCB'nin kendi tema dosyasında domain-specific extension olarak kalacak
(Faz P retrofit).

Seçim `data-theme="light"` / `data-theme="dark"` attribute'u ile; attribute yoksa
`prefers-color-scheme` fallback'i devreye girer.

```html
<link rel="stylesheet" href="@mcanbektas/design/tokens/index.css" />
<html data-theme="dark">
```

## Bileşenler

Hepsi prop-driven, sunum katmanı — auth durumu veya router'a bağımlı değil:

- `ProductSwitcher` — ürünler arası geçiş **tam sayfa navigasyonu** (`<a href>`),
  react-router `Link` değil; her ürün ayrı SPA build'i olduğu için client-side
  routing ürünler arası çalışmaz.
- `AccountMenu` — `user: AccountUser | null` prop'u ürünün kendi `AuthProvider`'ından
  akar; bu paket `/api/auth`'a dokunmaz, yalnız görünüm.
- `Header` — ikisini birleştirir, `brand` slot'uyla ürüne özgü logo/başlık alır.

## Geliştirme

```bash
npm install
npm run build      # tsc + token CSS'lerini dist/'e kopyalar
npm run typecheck
```

## Yayınlama

GitHub Packages'a `npm publish` — kimlik doğrulama için `.npmrc`'de
`//npm.pkg.github.com/:_authToken` gerekir (CI'da `GITHUB_TOKEN`, yerelde PAT).
