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
  - *seriály*: po seriálech — přehled kvalit dílů, cíl = převažující kvalita (nebo ručně „sjednotit na…"),
    díly pod cílem s nalezenou lepší verzí (hromadně „Stáhnout všech", nová verze jde do složky seriálu),
    díly ve víc verzích,
  - *chybějící díly*: eviduje je TMDb plugin Jellyfinu („Importovat chybějící epizody", v Navržených jde
    zapnout jedním tlačítkem); u seriálu se zapne „Dohledat chybějící" a díly se hledají v cílové kvalitě
    (když není, nejlepší nižší) a ukládají do složky řady,
  - u víc verzí se zaškrtne, které smazat (Harry Potter: smazat SD, nechat 4K i Full HD); délka u každé verze,
    jiná délka (prodloužená verze) se předem nezaškrtne,
  - špatně zařazené soubory (Jellyfin dal „Titanic (1997)" k Thorovi, „05x01" jako S01E02, „Saw 3" jako Saw 3D,
    různá čísla dílů série v jedné skupině, díl bez čísla „Černobyl E01") se označí ⚠️; „Zařadit jako S05E01"
    přesune díl do správné řady,
  - ruční oprava u každé verze, i když ji kontrola nepozná: ✏️ *Jiný film* (najít v TMDb, nebo oddělit do vlastní
    složky „Název (rok)/", když ho Jellyfin sloučil s jinými) a ✏️ *Řada/díl* (přesun a přejmenování na „Seriál - SxxEyy"),
  - díly, které Jellyfin neočísloval („comeback-22-koprovka", „Po stopách duchů XIII (9)", „Highschool_DxD_NeW_03",
    „S Homerem přijde zákon.avi"): odhad z čísla v názvu, římské řady, složky („2. série"), pořadí od začátku seriálu
    („Comeback-45" → S02E19) a podle názvu dílu z TMDb; jistý odhad (TMDb díl zná a název neodporuje) jde hromadně
    „🔧 Zařadit odhadnuté", nejistý jednotlivě,
  - přesuny běží na pozadí (mezi Filmy a Seriály se 4 GB kopírují minuty), kopie jde do skrytého souboru a každý
    soubor se přesouvá jen jednou — opakované kliknutí nic nezdvojí,
  - 🚚 díly seriálů ve Filmech → přesun do složky řady seriálu; stejný seriál ve dvou složkách → sloučit;
    smazání celého seriálu,
  - 🔎 *Hledat teď* u seriálu (a zapnutí „Dohledat chybějící") ho pošle na začátek fronty hledání; chybějící díl,
    který v cílové kvalitě není, se nabídne v nejlepší nižší — po stažení se dál hledá lepší verze,
  - 🎬 filmy ve složkách seriálů (neočíslovaný „díl" 70+ min, název nesedí k seriálu) → „Přesunout do Filmů"
    (`Filmy/Název (rok)/`); u každého dílu ruční „🎬 Je to film",
  - automatická hledání jen v okně hodin (Nastavení, výchozí 1–7 h) s náhodným rozestupem ±25 %,
  - po výpadku Fastshare (HTTP 5xx, 429) kontrola po hodinové pauze sama pokračuje,
  - řazení: nalezená lepší verze první, nejvíc ušetří místa (u lepší verze: dnešní soubory − nový soubor, takže
    větší nový soubor jde dolů), nejvíc kopií, od nejhorší / nejlepší kvality,
    velikost, název.
  Kontrola běží na pozadí (jednou týdně, nebo tlačítkem), automatická hledání mají strop (výchozí 20/h);
  díly seriálů se hledají po seriálech (jeden dotaz „Mentalista" = až 200 souborů, nejvýš 5 stránek) — 150 chybějících
  dílů jsou 2 dotazy, ne 150;
  lepší verze se u položky hledá znovu po 30 dnech, po restartu Jellyfinu kontrola pokračuje.
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

