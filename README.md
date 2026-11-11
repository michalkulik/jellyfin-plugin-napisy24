# Jellyfin Plugin — Napisy24

Wtyczka do Jellyfina pobierająca polskie napisy z [napisy24.pl](https://napisy24.pl).
Łączy dwa tryby:

1. **Hash (jak NapiSub)** — `POST /run/CheckSubAgent.php` z hashem OSH pliku;
   jedno zapytanie zwraca metadane i ZIP z napisami dopasowanymi do konkretnego
   wydania (`IsHashMatch = true`).
2. **Metadane (jak OpenSubtitles)** — `GET /libs/webapi.php` po IMDb lub tytule
   (także polskim) i `GET /run/pages/download.php?napisId=…&typ=sru` po pobranie
   czystego UTF-8 SRT.

## Wymagania

- Jellyfin **10.11.x** (`net9.0`) lub **12.x** (`net10.0`).
- .NET SDK 10 do zbudowania (np. `~/.local/share/tools/dotnet`).

## Instalacja

Repo jest publiczne, więc Jellyfin może korzystać z niego jak ze zwykłego
repozytorium wtyczek:

```
https://raw.githubusercontent.com/michalkulik/jellyfin-plugin-napisy24/main/manifest.json
```

*Dashboard → Plugins → Repositories → +*, wklej URL, odśwież katalog i zainstaluj
**Napisy24**.

Ręcznie (np. przed pierwszym release):

```bash
./build.sh net10.0        # lub net9.0 dla 10.11
unzip -o dist/napisy24_1.0.0.0-net10.0.zip -d /tmp/napisy24
```

Na hoście Jellyfina (u mnie `ubuntu`):

```bash
sudo mkdir -p "/srv/share/docker/jellyfin/plugins/Napisy24_1.0.0.0"
sudo cp Jellyfin.Plugin.Napisy24.dll meta.json \
  "/srv/share/docker/jellyfin/plugins/Napisy24_1.0.0.0/"
cd /srv/share/docker/portainer/compose/58
docker compose -p jellyfin restart jellyfin
```

Po restarcie: *Dashboard → Plugins* powinno pokazać **Napisy24 1.0.0.0**
(Status: Active). Konfiguracja: *Dashboard → Plugins → Napisy24 → Settings*.

## Aktywacja

**Bez ważnego kodu aktywacyjnego wtyczka nie zwraca żadnych wyników.**
Kod jest podpisany kluczem prywatnym ECDSA P-256, którego **nie ma w repo**;
publiczny klucz wystarcza tylko do weryfikacji, więc nikt nie wygeneruje sobie
kodu z publicznego kodu źródłowego.

1. Otwórz *Dashboard → Plugins → Napisy24 → Settings*.
2. Skopiuj **Server id** (albo `GET /Napisy24/Status`).
3. Wygeneruj kod:

   ```bash
   ./tools/make-activation-code.py --server-id <server-id> --name "jellykulik"
   # kod współdzielony dla wielu serwerów (jak hasło):
   ./tools/make-activation-code.py --any --name "friends" --days 365
   ```

   Skrypt czyta klucz prywatny z
   `~/.local/share/signing/jellyfin-plugin-napisy24/activation-private.pem`
   (poza repo). `--keygen` tworzy nową parę kluczy.
4. Wklej kod w polu **Activation code** i kliknij **Activate**.

Kod per-serwer działa tylko na tym Jellyfinie; kod `--any` działa wszędzie,
więc traktuj go jak hasło. Dezaktywacja: **Deactivate** (albo wyczyszczenie
pola przez API).

> **Uczciwie:** to jest bramka „cywilna”, nie DRM. Kod jest sprawdzany
> w pluginie, więc ktoś zdeterminowany może patchować DLL i ją pominąć.
> Publiczny kod źródłowy nie pozwala jednak **wygenerować** ważnego kodu bez
> klucza prywatnego. Nie ma też zdalnej rewokacji — przy wycieku kodu
> współdzielonego trzeba wydać nową wersję/klucz.

## Konfiguracja

| Pole | Domyślnie | Znaczenie |
|---|---|---|
| `ActivationCode` | puste | podpisany kod aktywacyjny (wymagany) |
| `BaseUrl` | `https://napisy24.pl` | adres serwisu |
| `EnableHashLookup` | `true` | dopasowanie po hashu OSH |
| `AgentUsername` / `AgentPassword` | `subliminal` / `lanimilbus` | dane aplikacji do `CheckSubAgent` |
| `EnableMetadataSearch` | `true` | wyszukiwanie po IMDb/tytule |
| `EnableAutomaticMetadataSearch` | `false` | metadane także w auto-download |
| `UseMd5Fallback` | `false` | dodatkowo wysyła MD5 pierwszych 10 MiB |
| `MaxResults` | `10` | maks. liczba wyników |
| `RequestDelayMs` | `750` | odstęp między zapytaniami |
| `MergeMultiCd` | `true` | scala napisy multi-CD w jeden SRT |
| `AutoScoreThreshold` | `60` | próg pewności dla auto-download |

Domyślne `subliminal` / `lanimilbus` to publiczna para projektu
Subliminal/Bazarr, nie nasza. Do stałej instalacji warto poprosić administrację
napisy24 o własną parę `ua`/`ap`.

## Jak to działa

### Hash

- `fh` = hash OpenSubtitles/MPC: rozmiar + suma pierwszego i ostatniego 64 KiB
  jako `uint64` little-endian. Wystarczy 128 KiB I/O.
- `CheckSubAgent` zwraca `OK-2` + metadane + ZIP; ZIP może zawierać `.srt`
  albo MicroDVD `.txt` (CP1250). MicroDVD jest konwertowane do SRT z fps
  z odpowiedzi.
- `napisId:0` oznacza napisy powiązane tylko hashem — wtedy używany jest ZIP
  z odpowiedzi.

### Metadane

- Filmy: IMDb, a gdy go brak — tytuł (działa też „8 Mila”, „Skazani na
  Shawshank”).
- Seriale: `SeriesName SxxExx`, np. `Przyjaciele 1x01`, `Gra o tron 1x01`;
  wynik filtrowany po `season`/`episode`.
- Wyniki są punktowane (rozmiar, czas, fps, release, rok, ocena), a w trybie
  automatycznym odfiltrowywane progiem `AutoScoreThreshold`.

### Multi-CD

`download.php?napisId=…&typ=sru` może zwrócić kilka plików SRT (CD1, CD2, …).
Czasy CD są brane z `<time>` z `webapi.php` i kolejne części są przesuwane
o sumę wcześniejszych; na końcu numeracja jest odtwarzana.

## Build i testy

```bash
export DOTNET_ROOT="$HOME/.local/share/tools/dotnet"
export PATH="$DOTNET_ROOT:$PATH"

dotnet build Jellyfin.Plugin.Napisy24.slnx -c Release
dotnet test  Jellyfin.Plugin.Napisy24.slnx -c Release
./build.sh net10.0
```

Testy jednostkowe obejmują hash OSH/MD5, kodowanie id, parser `webapi`,
parser `CheckSubAgent`, konwersję MicroDVD/CP1250, scalanie CD i walidację ZIP.

## Ograniczenia i ryzyka

- **API jest nieoficjalne** — `CheckSubAgent`, `webapi.php` i `download.php`
  mogą się zmienić. Parser jest tolerancyjny, ale bez gwarancji.
- **Regulamin napisy24** zabrania masowego pobierania programami bez zgody
  administracji. Wtyczka jest przeznaczona do prywatnego użytku; domyślnie
  auto-download metadanych jest **wyłączony**. Przed publicznym wydaniem
  skontaktuj się z administracją.
- **Hash pokrywa tylko powiązane wydania** — dla wielu plików hash zwróci
  `OK-0`; wtedy działa ścieżka metadanych.
- **Multi-CD** jest scalane heurystycznie po czasach z serwisu; przy braku
  `<time>` wybierany jest największy plik.
- **Języki**: `pl` (podstawowy) i `en` (nieliczne wpisy). Hash zwraca tylko PL.
- **FPS/cut**: napisy mogą nie pasować do innego wydania; użytkownik wybiera
  wynik, a score pomaga.

## Dokumentacja

- [ANALIZA.md](ANALIZA.md) — pełna analiza protokołu napisy24, integracji
  z Jellyfinem, wariantów i ryzyk.
- [areqq/aqnapi](https://github.com/areqq/aqnapi) — dokumentacja protokołu
  (m.in. `docs/napisy24.md`).
- [CaTzil/service.subtitles.napisy24pl](https://github.com/CaTzil/service.subtitles.napisy24pl)
  — addon Kodi.
- [LavX/bazarr-provider-catalog](https://github.com/LavX/bazarr-provider-catalog)
  — provider Bazarr.

## Licencja

MIT — patrz [LICENSE](LICENSE).
