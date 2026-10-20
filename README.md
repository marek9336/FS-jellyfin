# Fastshare pro Jellyfin

Plugin pro **Jellyfin 12**, který hledá soubory na [Fastshare.cloud](https://fastshare.cloud) a stahuje je
rovnou do knihovny filmů a seriálů. Běží celý uvnitř Jellyfinu — žádný další kontejner ani pomocná služba.

## Co umí

- **Hledání** na Fastshare (bez diakritiky, Fastshare s ní skoro nic nenajde — plugin ji odstraní sám).
  U každého souboru ukáže, co z názvu vyčetl: film/díl, rok, řadu a díl, kvalitu, dabing a titulky, kodek.
- **Zařazení do fronty** s možností opravit název, rok, řadu a díl, jak mají být v knihovně.
- **Souběžné stahování** (1–8 souborů najednou), navazování po výpadku (HTTP Range), kontrola, že přišel
  opravdu soubor (Fastshare bez přihlášení vrací prázdnou odpověď).
- **Časová okna** — od–do, další okna („22-3,12-13") nebo rozvrh po dnech; volitelně přerušit rozjeté
  soubory na konci okna. „⚡ Stáhnout teď" okna obchází.
- **Volitelné limity**: společný limit rychlosti všech stahování, denní strop, minimální volné místo,
  pauza mezi soubory.
- **Hlídač**: seriál podle názvu → najde všechny díly a pro každý zařadí nejlepší soubor; film podle názvu
  a roku → zařadí, jakmile se objeví. Kontroluje periodicky a dohledává nové díly.
- **Automatický výběr** podle priorit: jazyk (CZ dabing > SK > EN, titulky), max. kvalita, **zvlášť max.
  velikost filmu a dílu seriálu**, max. datový tok, kodek, HDR/DV.
- Pojmenování podle Jellyfinu: `Filmy/Název (rok)/Název (rok) - [1080p CZ].mkv`,
  `Seriály/Název/Season 01/Název - S01E02 - [1080p CZ].mkv`; duplicity ve stejné kvalitě se nestahují.

## Instalace

1. Jellyfin → Nástěnka → Pluginy → Repozitáře → přidat:
   `https://raw.githubusercontent.com/marek9336/FS-jellyfin/main/manifest.json`
2. Katalog → **Fastshare** → nainstalovat, restartovat Jellyfin.
3. V boční liště nástěnky **Fastshare** → Nastavení: jméno a heslo k Fastshare, cesty ke knihovnám
   (cesty uvnitř kontejneru Jellyfinu).

Heslo Jellyfin ukládá do konfigurace pluginu (`/config/plugins/configurations/`) jako čitelný text.

## Vývoj

```bash
dotnet test                       # čtečka názvů, výběr, stahování proti lokálnímu serveru
dotnet publish Jellyfin.Plugin.Fastshare -c Release -o publish
```

Vydání: tag `vX.Y.Z` → GitHub Actions spustí testy, sestaví zip, vytvoří release a doplní `manifest.json`.

`samples/` jsou skutečné odpovědi hledání Fastshare (20. 10. 2026), na kterých se ladí čtečka názvů.

## Plán

- **Kontrola knihovny**: projít stávající seriály a navrhnout chybějící díly (Jellyfin je zná z TMDb),
  s možností je rovnou zařadit.
- **Duplicity v knihovně**: najít víc verzí téhož filmu/dílu, nechat lepší kvalitu, horší navrhnout ke smazání
  (nic se nemaže bez potvrzení).
