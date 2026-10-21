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
- **Archivy** (celé řady seriálů v `.rar` / `.zip` / `.7z`, i vícedílné `.partN.rar`, `.r00…`, `.7z.001…`):
  hledání přes API Fastshare vrací jen videa, archivy proto najdeš na webu Fastshare v záložce *Archivy*
  a odkaz(y) vložíš do pluginu (Hledání → 📎). Plugin stáhne všechny části, rozbalí je a díly roztřídí
  podle S01E02 / 1x02 v názvech; řadu bez označení vezme z názvu archivu („9. série"). Co nepozná,
  nechá ve skryté složce `.nerozpoznane` u seriálu. Zaheslované archivy neumí. Rozbaluje knihovnou
  SharpCompress, kterou má Jellyfin 12 sám — nic dalšího se neinstaluje.
- **Navržené** (kontrola knihovny filmů — nic se nemaže ani nestahuje samo):
  - *lepší verze*: film pod Full HD → najde 1080p na Fastshare, „Stáhnout" (volitelně **nahradit** —
    stará verze se smaže až po úspěšném stažení), „Stačí" (už nenavrhovat), „Smazat film",
  - *duplicity*: film ve víc verzích → nejlepší nechat, horší smazat, případně rovnou stáhnout Full HD,
  - *bez metadat*: z názvu souboru vyčte skutečný název a rok, najde ho v TMDb (vlastní „Identifikovat"
    Jellyfinu) a po potvrzení doplní plakát, herce a popis.
  Kontrola běží na pozadí (jednou týdně, nebo tlačítkem); lepší verze se u filmu hledá znovu po 30 dnech.
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

## Co nejde

- **Řazení podle lajků**: Fastshare palce nahoru eviduje a web podle nich řadí, ale API (to samé, co
  používá oficiální Kodi doplněk) je nevrací a webové hledání je chráněné tokenem z JavaScriptu.
  Plugin ho obcházet nebude — kdyby Fastshare hodnocení přidal do API, doplní se.

## Plán

- **Seriály v Navržených**: chybějící díly (Jellyfin je zná z TMDb) a lepší verze dílů pod Full HD.
