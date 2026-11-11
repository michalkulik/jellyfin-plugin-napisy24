# Analiza wykonalności: wtyczka Jellyfin „Napisy24” (napisy24.pl)

- **Data:** 2026-11-11
- **Status:** analiza, **bez implementacji**
- **Cel:** odpowiedzieć, czy da się zrobić wtyczkę do Jellyfina pobierającą napisy
  z napisy24.pl, analogiczną do NapiSub (napiprojekt) i OpenSubtitles.
- **Środowisko docelowe:** Jellyfin **12.4.0** „jellykulik” (`192.168.69.11:8097`),
  kontener `jellyfin`, config `/srv/share/docker/jellyfin` → `/config`.
  Aktywne wtyczki napisowe: NapiSub 1.0.2.0, Open Subtitles 25.1.1.1.

---

## 0. Werdykt (TL;DR)

**Tak, to jest wykonalne** i pod względem integracji z Jellyfinem bardzo podobne
do NapiSub. Protokół napisy24.pl został sprawdzony na żywo (2026-11-11) — są dwa
niezależne, działające mechanizmy:

1. **Hash (jak NapiSub):** `POST /run/CheckSubAgent.php` z hashem OSH pliku.
   Jedno zapytanie zwraca metadane **i od razu ZIP z napisami**. Wymaga pary
   `ua`/`ap` (dane aplikacji); publiczne pary istnieją, ale należą do innych
   projektów (Subliminal/Bazarr, Kodi, DMnapi).
2. **Wyszukiwanie po metadanych (jak OpenSubtitles):**
   `GET /libs/webapi.php` po IMDb lub tytule (działa też z polskimi tytułami
   filmów i seriali, oraz z `SxxExx`), a pobranie przez
   `GET /run/pages/download.php?napisId=…&typ=sru` (czysty UTF-8 SRT).

**Rekomendacja:** wariant **B** — hash jako pierwszy (perfect match, zero
konwersji w większości przypadków), a gdy hasha nie ma — wyszukiwanie po
metadanych. Automatyczne pobieranie domyślnie tylko po hashu (mniejsze ryzyko
regulaminowe i mniej zapytań), pełne wyszukiwanie przy ręcznym wyborze.

**Najważniejsze zastrzeżenia:**

- API jest **nieoficjalne** (odtworzone przez klientów Kodi/Bazarr/aqnapi), może
  się zmienić; strona ma banner „Prace modernizacyjne”.
- **Regulamin** zabrania masowego pobierania programami bez zgody administracji.
  Wtyczka publiczna bez zgody = ryzyko blokady IP i naruszenia ToS.
- Hash pokrywa tylko te wydania, które ktoś powiązał z plikiem (na testach:
  F1 2025 i For All Mankind — trafienie; Beforeigners/Kicia Kocia — brak).
- Hash zwraca czasem **MicroDVD w CP1250**, więc konwersja do SRT jest konieczna.
- Multi-CD (kilka plików w ZIP) Jellyfin nie obsługuje natywnie — trzeba scalać
  albo wybrać jeden plik.
- Wspólne dane agenta (`subliminal` / `lanimilbus`) są publiczne, ale należą do
  projektu Subliminal, nie do nas; do publicznej wtyczki lepiej poprosić
  administrację napisy24 o własną parę.

---

## 1. Kontekst i cel

Cel to odpowiednik NapiSub/OpenSubtitles dla bazy **napisy24.pl**:

- wyszukiwanie napisów z poziomu Jellyfina („Search for subtitles”),
- pobieranie i zapis obok filmu / w metadanych,
- automatyczne pobieranie przy dodawaniu mediów (opcjonalnie),
- język polski (baza jest polska; są też pojedyncze wpisy `en`).

Stan zastany w repo:

- `/root/sources/jellyfin-plugin-napi` — NapiSub (hash napiprojekt, net9/net10,
  manifest + `build.sh` + GitHub Actions) — najlepszy wzorzec.
- `/root/sources/jellyfin-plugin-opensubtitles` — wzorzec na konfigurację,
  kontroler API i wielojęzyczność.
- `/root/sources/jellyfin-plugin-napisy24` — **pusty katalog** (brak kodu).
- W GitHubie istniał kiedyś `rafal-dunaj-sobera/jellyfin-plugin-napisy24`, ale
  obecnie zwraca 404 — brak dostępnego kodu do reużycia.

### 1.1 Porównanie z NapiSub i OpenSubtitles

| Cecha | NapiSub (napiprojekt) | OpenSubtitles | Napisy24 (ten projekt) |
|---|---|---|---|
| Identyfikacja pliku | MD5 pierwszych 10 MiB | IMDb, hash OSH, tytuł | **OSH (128 KiB)** + IMDb/tytuł/`SxxExx` |
| Uwierzytelnianie | brak | konto + API key | **para `ua`/`ap`** (dane aplikacji) |
| Wyszukiwanie | tylko hash | IMDb/hash/tytuł | hash + IMDb/tytuł/`SxxExx` |
| Format odpowiedzi | MicroDVD/SRT | SRT | **SRT** (`typ=sru`) lub MicroDVD (hash) |
| Synchronizacja | hash = pewna | hash / score | hash = pewna; metadane = score (size/time/fps/release) |
| Pokrycie PL | duże | duże | **polska baza**, ale hash tylko dla powiązanych |
| Legalność | publiczne API | oficjalne API | **nieoficjalne, ToS** |

Czyli integracja jest bliźniaczo podobna do NapiSub (hash + konwersja
MicroDVD), a wyszukiwanie po metadanych jest bliższe OpenSubtitles. Główna
różnica: napisy24 nie ma oficjalnego, udokumentowanego API i wymaga danych
aplikacji.

---

## 2. Jak Jellyfin obsługuje providery napisów

### 2.1 Interfejsy i przepływ

Provider napisów to implementacja
`MediaBrowser.Controller.Subtitles.ISubtitleProvider`:

```csharp
string Name { get; }
IEnumerable<VideoContentType> SupportedMediaTypes { get; }
Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken ct);
Task<SubtitleResponse> GetSubtitles(string id, CancellationToken ct);
```

- `SubtitleSearchRequest` (12.4) zawiera m.in.: `Language`,
  `TwoLetterISOLanguageName`, `ContentType` (`Movie`/`Episode`), `MediaPath`,
  `SeriesName`, `Name`, `IndexNumber`, `ParentIndexNumber`, `ProductionYear`,
  `RuntimeTicks`, `ProviderIds` (Imdb, Tmdb, Tvdb…), `IsPerfectMatch`,
  `IsAutomated`, `SearchAllProviders`, `DisabledSubtitleFetchers`,
  `SubtitleFetcherOrder`.
- `RemoteSubtitleInfo` zawiera: `Id`, `ProviderName`, `Name`, `Format`,
  `Author`, `Comment`, `CommunityRating`, `FrameRate`, `DownloadCount`,
  `IsHashMatch`, `Forced`, `HearingImpaired`, `ThreeLetterISOLanguageName`.
- `SubtitleResponse` to **jeden** strumień: `Stream`, `Format`, `Language`,
  `IsForced`, `IsHearingImpaired`.

Pliki w źródłach Jellyfina:

- `MediaBrowser.Controller/Subtitles/ISubtitleProvider.cs`
- `MediaBrowser.Controller/Subtitles/SubtitleSearchRequest.cs`
- `MediaBrowser.Controller/Subtitles/SubtitleResponse.cs`
- `MediaBrowser.Model/Providers/RemoteSubtitleInfo.cs`
- `MediaBrowser.Providers/Subtitles/SubtitleManager.cs`
- `MediaBrowser.Providers/MediaInfo/SubtitleDownloader.cs`

### 2.2 Konsekwencje projektowe

1. **Rejestracja jest obowiązkowa.** Jellyfin **nie skanuje** assembly wtyczki
   w poszukiwaniu `ISubtitleProvider`. Trzeba jawnie zarejestrować provider
   przez `IPluginServiceRegistrator` (dokładnie tak, jak zrobił NapiSub po
   naprawie błędu „provider silently ignored”):

   ```csharp
   serviceCollection.AddSingleton<ISubtitleProvider, Napisy24Provider>();
   ```

2. **Id wyniku jest prefiksowane przez Jellyfin.**
   `SubtitleManager.Normalize()` robi `Id = md5(providerName) + "_" + Id`,
   a `GetRemoteSubtitles()` dzieli po **pierwszym** `_` i przekazuje resztę do
   `provider.GetSubtitles(id)`. Nasze id może więc zawierać `:` (np.
   `h:ABC…:2785089006:…` albo `w:127240:0`), ale nie powinno zaczynać się od
   `_`.

3. **Tylko jeden strumień napisów.** Jellyfin zapisze jeden plik
   (`<film>.<lang>.srt`). Multi-CD trzeba scalać w pamięci albo świadomie
   ograniczyć do jednego pliku.

4. **`IsPerfectMatch` i `IsAutomated` trzeba respektować.**
   - `IsPerfectMatch == true` → zwracać **tylko** trafienia hash (inaczej
     biblioteka z opcją „perfect match” dostanie napisy niezsynchronizowane).
   - `IsAutomated == true` (auto-download) → domyślnie tylko hash, ewentualnie
     metadane z wysokim progiem pewności. Uwaga: `SubtitleDownloader` bierze
     **pierwszy** wynik z pierwszego providera, który coś zwróci
     (`SearchAllProviders=false`).

5. **Kolejność providerów.** `IHasOrder.Order`; NapiSub ma `1`. Dla Napisy24
   sensowne jest `2`, żeby w trybie automatycznym najpierw spróbować
   napiprojekt (mniej zapytań do napisy24).

6. **Wersje Jellyfina.** Serwer to 12.4 (`net10.0`), ale wzorcowo budować
   multi-target `net9.0;net10.0`:
   - `net9.0` + `targetAbi 10.11.0.0` — działa na 10.11 i 12.x,
   - `net10.0` + `targetAbi 12.0.0.0` — tylko 12.x.
   Nigdy odwrotnie (assembly nowsze niż runtime serwera się nie załaduje).

7. **Kodowanie CP1250.** Jellyfin rejestruje `CodePagesEncodingProvider`
   (`Jellyfin.Server/Helpers/StartupHelpers.cs:298`), więc w pluginie
   `Encoding.GetEncoding(1250)` działa. To ważne dla MicroDVD z hash-path.

---

## 3. napisy24.pl — protokół (zweryfikowany na żywo)

Wszystkie testy wykonano 2026-11-11 z hosta `ubuntu` (ten sam, na którym stoi
Jellyfin) oraz z `hp`. HTTPS działa; HTTP też, ale używamy HTTPS.

### 3.1 Endpointy — przegląd

| Endpoint | Metoda | Auth | Rola | Status |
|---|---|---|---|---|
| `/run/CheckSubAgent.php` | POST form | app `ua`/`ap` | hash → metadane + ZIP | działa |
| `/libs/webapi.php` | GET | brak | szukanie po IMDb/tytule (XML) | działa |
| `/run/pages/download.php?napisId=&typ=sru` | GET | brak | pobranie ZIP po id | działa (z `Referer`) |
| `/download?napisId=` | GET | sesja/login | ścieżka WWW | **403** dla klienta |
| `/run/pages/serial_napis.php` | POST | brak | AJAX serialu (JSON) | działa, ale niepotrzebne |
| `/run/pages/szukaj_podpowiedz.php` | POST | brak | autocomplete (JSON) | działa, fallback |
| `/run/CheckSub2.php` | POST multipart | konto + obfuskacja | hash (klient Windows) | nieużywane |
| `/run/AddSub.php` | POST | — | upload | zablokowany serwerowo |

`robots.txt` zabrania m.in. `/run/`, `/download`, `/pliki/`; `/libs/webapi.php`
nie jest zabronione (jest `/libraries/`, nie `/libs/`).

### 3.2 Hasze pliku

Dwa niezależne:

| Pole | Algorytm | Format |
|---|---|---|
| `fh` | **OSH/OpenSubtitles**: `rozmiar + Σ(pierwsze 64 KiB jako uint64 LE) + Σ(ostatnie 64 KiB)` mod 2^64 | 16 hex, może być uppercase |
| `md` | MD5 z pierwszych 10 MiB (hash napiprojekt) | 32 hex lowercase |

**Ważne:** na testowanym pliku (F1 2025) wystarczało **`fh`** (`fh` alone →
`OK-2`; samo `md` → `OK-0`). Czyli do trafienia w bazę wystarczy 128 KiB,
a nie 10 MiB. Nie zakładać jednak, że `md` nigdy się nie przyda — oficjalny
klient wysyła oba; można wysyłać `md` tylko awaryjnie, gdy `fh` da `OK-0`
(oszczędność I/O w typowym przypadku).

### 3.3 `CheckSubAgent.php` — hash

Request (`application/x-www-form-urlencoded`):

```
postAction=CheckSub
ua=<app user>          # np. subliminal
ap=<app password>      # np. lanimilbus
fh=<OSH hex>           # wymagane
fs=<rozmiar w bajtach>
fn=<nazwa pliku>
nl=PL                  # opcjonalne; w testach EN/XX nie zmieniło języka (zwróciło PL)
n24pref=1              # opcjonalne
```

Odpowiedź:

```
OK-2|res:1920x800|time:02:35:49|fps:23.976|imdb:16311594|ilea:7|
ftitle:|fyear:|fgenres:|fwstawil:Kielbi|ftlumacz:Marzena Falkowska|
fkorekta:|fcover:|fimdb:16311594|napisId:132185|lp:425716990|
tlId:|tlPrc:|tlInfo:||<ZIP BYTES>
```

- Status `OK-2` = są napisy i po separatorze `||` jest **ZIP**.
- `OK-0`, `OK-1`, `OK-3` = brak napisów (różne warianty; traktować jako brak).
- `login error` = złe `ua`/`ap`.
- `napisId:0` = napisy powiązane tylko hashem, **bez** publicznego id (wtedy
  trzeba użyć ZIP z odpowiedzi, bo `download.php` po id nie zadziała).
- ZIP zawiera plik napisów (`.srt` **albo** `.txt` MicroDVD); czasem dołączony
  jest też `Napisy24.pl.url` (w testach: F1 miał `.url`, For All Mankind nie).
- `imdb` jest bez `tt` (np. `16311594`), `fps` bywa `23.976`, `res` `WxH`,
  `time` `HH:MM:SS`.

**App credentials (najprawdopodobniej dane aplikacji, nie konta użytkownika):**

| Zestaw | ua | ap | Źródło |
|---|---|---|---|
| Subliminal/Bazarr | `subliminal` | `lanimilbus` | publiczny provider Bazarr |
| Kodi | `service.subtitles.napisy24` | `eqpXLJLp` | addon Kodi |
| DMnapi | `dmnapi` | `4lumen28` | enigma2 |

Sprawdzone: `subliminal`/`lanimilbus` działa; złe dane → `login error`.
Wygląda to na dane **aplikacji** (`ua` = nazwa programu, `ap` = hasło
aplikacji), a nie konto użytkownika — ale nie udało się tego jednoznacznie
potwierdzić bez konta testowego. Do publicznej wtyczki wypada poprosić
administrację o własną parę.

### 3.4 `libs/webapi.php` — szukanie po metadanych

```
GET https://napisy24.pl/libs/webapi.php?imdb=tt0499549&lang=pl
GET https://napisy24.pl/libs/webapi.php?title=The%20Last%20of%20Us%201x01&lang=pl
```

- `lang=pl` / `lang=en` filtruje język; `lang=0` lub brak = mieszane;
  `lang=pol`/`PL` jest ignorowane.
- Odpowiedź to **fragment XML z wieloma rootami** (`<subtitle>…</subtitle>` x N,
  bez wspólnego roota) albo tekst `brak wynikow`. To trzeba parsować jako
  `ConformanceLevel.Fragment` albo opakować w sztuczny root.
- Limit **~25 wyników**, **bez paginacji** (`page`, `offset`, `limit` są
  ignorowane). Dla seriali trzeba pytać konkretnym `SxxExx`, a nie całą serią.
- Pola: `id` (napisId), `title`, `altTitle`, `season`, `episode`, `epTitle`,
  `imdb`, `year`, `release` (średnikami), `language`, `cd`, `time`
  (per-CD, `|`), `size` (per-CD, `|`), `fps`, `resolution`, `author`, `rating`.

Przykłady (zweryfikowane):

| Zapytanie | Wynik |
|---|---|
| `imdb=tt0499549&lang=pl` | 19 wpisów `pl` (Avatar) |
| `title=The Last of Us 1x01&lang=pl` | 1 wpis, id `127240` |
| `title=The Last of Us 1x1` / `01x01` | to samo (tolerancyjne zero-padding) |
| `title=Przyjaciele 1x01&lang=pl` | `Friends 1x01`, 2 wpisy |
| `title=Teoria wielkiego podrywu 1x01&lang=pl` | `The Big Bang Theory 1x01` (+ `11x01` — trzeba filtrować po season/episode) |
| `title=Gra o tron 1x01&lang=pl` | `Game of Thrones 1x01` |
| `title=8 Mila&lang=pl` | `8 Mile` (2002), 3 wpisy |
| `title=Skazani na Shawshank&lang=pl` | `The Shawshank Redemption` |
| `title=The Simpsons&lang=pl` | 25 wyników, głównie sezon 10 (cap) |
| `title=nieistniejący` | `brak wynikow` |

Wnioski:

- Tytuły **polskie i oryginalne** działają (przynajmniej dla seriali i wielu
  filmów), ale wyszukiwanie jest „podciągowe” i potrafi zwrócić śmieci
  (`Ojciec chrzestny` → `Godfather of Harlem`; `Kler` → `Herz ist ein dunkler
  Wald`). Dlatego przy filmach **najpierw IMDb**, a tytuł tylko jako fallback
  z filtrem roku i podobieństwa.
- Filmy polskie często nie mają polskich napisów (Seksmisja, Pitbull,
  Kogel-mogel → 0), co nie jest błędem API.
- Dla odcinków filtr `season`/`episode` jest obowiązkowy, bo `1x01` łapie
  też `11x01`.

### 3.5 `download.php` — pobranie po id

```
GET https://napisy24.pl/run/pages/download.php?napisId=127240&typ=sru
Referer: https://napisy24.pl/
```

- `typ=sru` → ZIP z **UTF-8 SRT** (nawet dla starych wpisów MicroDVD — serwer
  konwertuje).
- Bez `typ` → MicroDVD `.txt` (CP1250).
- **Bez `Referer`** serwer zwraca `302` z ciałem ZIP; `HttpClient` z domyślnym
  `AllowAutoRedirect=true` pójdzie na `/komentarze` i straci ZIP. Rozwiązanie:
  wysyłać `Referer: https://napisy24.pl/` (wtedy `200`) i/lub wyłączyć
  auto-redirect i akceptować `200`/`302` z `application/zip` + `PK`.
- Zły `napisId` → **HTTP 500** (puste ciało). Obsłużyć jako błąd.
- `/download?napisId=…` (ścieżka z WWW) → **403** nawet z ciasteczkami;
  nie używać.
- ZIP może zawierać wiele plików (multi-CD) + `Napisy24.pl.url`.

Przykłady:

| id | Wynik |
|---|---|
| `127240` (TLOU S01E01) | 1 SRT UTF-8 + `.url` |
| `4434` (Simpsons S10E01, stary) | 1 SRT UTF-8 (serwer skonwertował) |
| `34102` (Avatar, cd=3) | `dmd-avatar1.srt`, `dmd-avatar2.srt`, `dmd-avatar3.srt` |
| `30326` (Avatar, en) | 1 SRT `en` |
| `999999999` | HTTP 500 |

### 3.6 Formaty, kodowanie, multi-CD

- **SRT z `typ=sru`:** UTF-8, CRLF, bez BOM; nic nie trzeba robić.
- **Hash-path ZIP:** może być `.srt` (F1) **albo** MicroDVD `.txt` CP1250
  (For All Mankind S01E02). MicroDVD: `{51}{90}tekst|druga linia`, bez nagłówka
  fps; fps bierzemy z pola `fps` odpowiedzi (`23.976`). Konwersja:
  `ms = frame * 1000 / fps`, `|` → nowa linia.
- **Multi-CD:** `<cd>`, `<time>` i `<size>` w webapi są per-CD (np.
  `00:59:24|00:53:58|00:48:11|`). Scalać można przez przesunięcie CD2/CD3
  o sumę wcześniejszych czasów; numeracja od nowa. Dla hash-path zwykle
  dostajemy jeden plik; jeśli wiele — brak per-CD czasów, więc wybrać
  największy i zalogować ograniczenie.
- **Walidacja ZIP:** serwer potrafi zwrócić śmieci (patrz 3.7), więc sprawdzać
  rozszerzenie **i** treść (timecode SRT/MicroDVD), limit rozmiaru (np. 10 MB),
  nie rozpakowywać na dysk.

### 3.7 Dziwne zachowania i pułapki (ważne dla implementacji)

| Zachowanie | Wniosek |
|---|---|
| `CheckSubAgent` z `napisId` (bez `fh`) → `OK-2` + ZIP `nativelog.txt` (Minecraft!) | **Nigdy** nie wysyłać `napisId` do CheckSubAgent; walidować treść ZIP |
| `OK-1` + `res:Note to develope…`, `ftitle:INVALID`, `imdb:32323060` | traktować jak brak napisów; logować |
| `OK-0` bez metadanych vs `OK-0|res:…|imdb:0…` | oba = brak; parser ma być tolerancyjny |
| `webapi` bez paginacji, cap 25 | dla seriali pytać `SxxExx`, nie całą serię |
| `webapi` „podciągowe” dopasowania | filtrować po `season`/`episode`, roku, IMDb |
| `title=The Simpsons&lang=pl` → sezon 10 | nie da się wylistować całej serii |
| `lang=pol`/`PL` ignorowane | używać `pl`/`en` |
| `download.php` 302 bez `Referer` | wysyłać `Referer` lub wyłączyć redirect |
| `/download` 403 | używać `/run/pages/download.php` |
| zły `napisId` → 500 | obsłużyć, nie wywalać search |
| banner „Prace modernizacyjne” | API działa, ale może się zmienić; parser tolerancyjny |

### 3.8 Czego nie używamy

- `CheckSub2.php` (obfuskacja pól, konto użytkownika) — możliwe jako opcja
  „własne konto”, ale to więcej kodu i przechowywanie hasła w XML.
- `AddSub.php` / upload — zablokowane po stronie serwera; poza zakresem.
- `serial_napis.php`, `szukaj_podpowiedz.php`, scraping `/szukaj` — działają,
  ale `webapi.php` + `SxxExx` wystarcza czyściej. Zostawić jako awaryjny
  fallback dla nietypowych tytułów.

---

## 4. Projekt wtyczki

### 4.1 Architektura i pliki

```
Jellyfin.Plugin.Napisy24/
  Jellyfin.Plugin.Napisy24.csproj      # net9.0;net10.0, Jellyfin.Controller/Model
  Plugin.cs                            # BasePlugin<PluginConfiguration>, GUID, Name
  PluginServiceRegistrator.cs          # AddSingleton<ISubtitleProvider, Napisy24Provider>
  Configuration/PluginConfiguration.cs
  Providers/Napisy24Provider.cs        # Search/GetSubtitles, scoring, id
  Napisy24/Napisy24Client.cs           # HTTP: CheckSubAgent, webapi, download
  Napisy24/Models.cs                   # XML DTO, meta CheckSubAgent, statusy
  Napisy24/OshHash.cs                  # OSH (128 KiB), opcjonalnie MD5 10 MiB
  Napisy24/MicroDvdConverter.cs        # MicroDVD/CP1250 → SRT
  Napisy24/SrtTools.cs                 # scalanie CD, walidacja, normalizacja
  Napisy24/SubtitleScoring.cs          # score: hash/size/time/fps/release
  Web/napisy24.html, Web/napisy24.js   # opcjonalna strona konfiguracji
  thumb.png                            # opcjonalnie
tests/Jellyfin.Plugin.Napisy24.Tests/  # xUnit: parser, hash, konwersja, scoring
build.sh
manifest.json
tools/update-manifest.py
.github/workflows/release.yml
```

GUID (propozycja, nowy — nie koliduje z NapiSub/OpenSubtitles):
`1e3057d0-c2e0-4bbc-a67b-5fa31cdbebd9`
Nazwa providera: **`Napisy24`** (stabilna; od niej Jellyfin liczy prefiks id).
Kategoria w manifeście: `Subtitles`.

### 4.2 `Search` — strategia i fallbacki

```
Search(request):
  lang = Normalize(request.Language)        // pl / en, inaczej []
  results = []

  # 1. HASH (tylko pl, tylko lokalny plik)
  if EnableHash && lang == "pl" && File.Exists(request.MediaPath):
      fh = OshHash(MediaPath)               // 128 KiB, cache po path+size+mtime
      resp = CheckSubAgent(fh, size, filename)
      if resp.Status == OK-2 && ValidZip(resp.Archive):
          results.Add(HashResult(resp, lang))   // IsHashMatch=true, score 100

  if request.IsPerfectMatch:
      return results                          # tylko hash

  # 2. METADANE
  if EnableMetadata:
      if Movie:
          imdb = request.ProviderIds["Imdb"]
          xml = imdb ? webapi(imdb, lang)
                     : webapi(request.Name, lang) + filtr roku/podobieństwa
      else: # Episode
          if brak season/episode/series -> []
          imdb = request.ProviderIds["Imdb"]  # bywa (TVDB)
          xml = imdb ? webapi(imdb, lang)
                     : webapi($"{SeriesName} {season}x{episode}", lang)
          filtr: season == request.ParentIndexNumber && episode == request.IndexNumber
      results.AddRange(BuildResults(xml))
      # fallbacki dla seriali: SeriesName bez "(rok)", OriginalTitle z biblioteki,
      # autocomplete jako ostatnia deska ratunku

  sort: hash first, score desc, rating desc
  if request.IsAutomated: odfiltruj score < threshold (np. 60)
  return results.Take(MaxResults)
```

Uwagi:

- `request.Name` w bibliotece bywa **polski** (np. „8 Mila”), a `webapi` radzi
  sobie z polskimi tytułami; mimo to IMDb jest pewniejsze.
- Dla seriali `SeriesName` też bywa polski („Przyjaciele”, „Gra o tron”) i
  `webapi` to obsługuje. Jeśli nie — spróbować `OriginalTitle` serialu
  (przez `ILibraryManager`/`FindByPath`) albo autocomplete.
- `webapi` cap 25 → dla seriali zawsze `SxxExx`, nie nazwa serii.
- Filtrować `season`/`episode` **liczbowo** (nie stringiem), bo `1x01` łapie
  `11x01`.

### 4.3 `GetSubtitles` i schemat id

Dwa rodzaje id:

| Prefix | Przykład | Co zawiera | Pobranie |
|---|---|---|---|
| `h:` | `h:A5E905CBBC002765:2785089006:<base64url(name)>` | fh, rozmiar, nazwa | ponowny `CheckSubAgent`, wyciągnięcie napisów z ZIP |
| `w:` | `w:127240:0` | napisId, indeks CD (0 = całość/scal) | `download.php?napisId=…&typ=sru` |

- `h:` — nie trzeba znać ścieżki pliku w `GetSubtitles`; wszystkie dane są w id.
  Walidować: fh 16 hex, rozmiar liczba, nazwa bez separatorów ścieżek.
- `w:` — tylko liczby; brak SSRF (stały URL).
- Cache w pamięci (np. `ConcurrentDictionary` z TTL 10 min) na ZIP/SRT, żeby
  `Search` → `GetSubtitles` nie robiło dwóch pełnych zapytań; opcjonalne.
- Dla `w:` z `cd=0` i wieloma plikami: scalać (jeśli `MergeMultiCd`) albo wybrać
  największy. Dla `cd=N`: wybrać N-ty plik.
- Zwracać `Format = "srt"`, `Language = "pl"`/`"eng"`, `IsForced=false`,
  `IsHearingImpaired=false`.

### 4.4 Scoring i „perfect match”

Score (0–100) dla wyników metadanych:

| Składnik | Punkty | Jak liczyć |
|---|---|---|
| hash | 100 | tylko `CheckSubAgent OK-2`, `IsHashMatch=true` |
| rozmiar | +40 | suma `<size>` vs `FileInfo.Length`, tolerancja 1% lub zaokrąglenie MB |
| czas | +30 | suma `<time>` vs `RuntimeTicks`, tolerancja ~2 s |
| fps | +10 | `<fps>` vs fps pliku (jeśli znany) |
| release | +20 | podobieństwo tokenów nazwy pliku/folderu do `<release>` |
| rok | +10 | `ProductionYear` == `<year>` (±1) |
| ocena | +5 | `<rating>` przeskalowane 0–6 → 0–10 |

- `IsHashMatch = true` **tylko** dla hash-path.
- `Comment`: release, resolution, size, time, fps, author/translator, „hash”.
- `CommunityRating`: `<rating> * 10/6` (Jellyfin oczekuje 0–10).
- `FrameRate`: `<fps>`.
- `Name`: np. `F1.The.Movie.2025.1080p.AMZN.WEB-DL… (hash)` lub
  `8 Mile (2002) — DVDRip.XviD-KiKO`.
- W trybie `IsAutomated` próg np. 60; w ręcznym pokazać wszystko (posortowane).

### 4.5 Konfiguracja

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableHashLookup { get; set; } = true;
    public string AgentUsername { get; set; } = "subliminal";
    public string AgentPassword { get; set; } = "lanimilbus";
    public bool EnableMetadataSearch { get; set; } = true;
    public bool EnableAutomaticSearch { get; set; } = false; // auto-download
    public int MaxResults { get; set; } = 10;
    public int RequestDelayMs { get; set; } = 750;
    public bool MergeMultiCd { get; set; } = true;
    public int AutoScoreThreshold { get; set; } = 60;
}
```

- Strona konfiguracji przez `IHasWebPages` (wzór: OpenSubtitles
  `Web/opensubtitles.html` + `.js`, `ApiClient.getPluginConfiguration`).
- Przycisk „Testuj dane agenta”: `CheckSubAgent` z zerowym `fh` → `OK-0` =
  OK, `login error` = złe dane.
- Hasło w XML jest jawne — ostrzec w UI; ewentualnie override z ENV.
- Domyślne `subliminal`/`lanimilbus` to publiczne dane Subliminal/Bazarr;
  w publicznym wydaniu lepiej zostawić puste i poprosić o własne.

### 4.6 Cache, limity, bezpieczeństwo

- `HttpClient` z `IHttpClientFactory`, timeout 20 s, UA typu
  `Jellyfin.Napisy24/<ver>` (+ opcjonalnie kontakt).
- `SemaphoreSlim(1)` + minimalny odstęp `RequestDelayMs` między zapytaniami do
  napisy24 (jeden użytkownik nie zabije serwera).
- Cache:
  - hash pliku: `path|size|mtime` → fh (30 min),
  - wyniki `webapi`: `imdb|title|season|episode|lang` → XML/list (5–10 min),
  - ZIP/SRT: `id` → bajty (10 min, limit rozmiaru).
- Walidacja wejścia z id (regex), limity rozmiaru odpowiedzi (np. 5–10 MB),
  brak zapisu tymczasowych plików, brak logowania haseł.
- Obsługa błędów: HTTP 4xx/5xx, timeout, `brak wynikow`, niepoprawny XML,
  ZIP-śmieci → log + puste wyniki (nie wywalać całego search).
- `GetSubtitles` może rzucić `InvalidOperationException` z czytelnym komunikatem
  (Jellyfin pokaże błąd pobierania).

### 4.7 Logowanie

- Debug: fh, zapytanie webapi, liczba wyników, wybrany score.
- Info: trafienie hash, pobranie id, konwersja MicroDVD, scalanie CD.
- Warning: 403/429/500, `login error`, niepoprawny ZIP, brak plików.
- Nigdy: `AgentPassword`, pełne ciała odpowiedzi.

---

## 5. Zakres, ograniczenia, macierz wariantów

### 5.1 Co będzie działać

- Filmy: hash (jeśli powiązane) → dokładna synchronizacja; inaczej IMDb/tytuł →
  lista wersji, pobranie SRT UTF-8.
- Seriale: `SeriesName SxxExx` (polski lub oryginalny tytuł) → odcinek;
  filtr season/episode; pobranie SRT.
- Języki: `pl` (podstawowy) i `en` (nieliczne wpisy).
- Auto-download: hash-only (opcjonalnie, domyślnie wyłączone).
- Konfiguracja agenta, limitów, progu scoringu.
- Kompatybilność 10.11/12.x przez multi-target.

### 5.2 Ograniczenia

| Ograniczenie | Wpływ | Mitygacja |
|---|---|---|
| Hash pokrywa tylko powiązane wydania | brak wyniku dla wielu plików | metadata fallback |
| Multi-CD | Jellyfin chce 1 strumień | scalać po `<time>`; inaczej największy plik |
| MicroDVD/CP1250 z hash | trzeba konwertować | własny konwerter + fps z odpowiedzi |
| cap 25 w webapi | długie serie | pytać `SxxExx` |
| „podciągowe” dopasowania | śmieciowe wyniki | filtr season/episode/rok/IMDb |
| polskie tytuły | część filmów 0 wyników | IMDb, oryginalny tytuł, autocomplete |
| różne fps/cut | rozjazd napisów | score/fps; użytkownik wybiera |
| brak oficjalnego API | zmiany mogą zepsuć | parser tolerancyjny, testy, fallbacki |
| hash-path bez `napisId` | brak pobrania po id | ZIP z CheckSubAgent + walidacja |

### 5.3 Macierz wariantów

| Wariant | Zakres | Zalety | Wady | Ryzyko ToS |
|---|---|---|---|---|
| **A. Hash-only** | CheckSubAgent | proste, 1 request, jak NapiSub/official | małe pokrycie, app creds | niskie |
| **B. Hash + metadane** (rekomendacja) | + webapi + download | najlepsze pokrycie, PL/EN | więcej zapytań, /run download | średnie |
| **C. Full scrape** | + /szukaj/autocomplete | radzi sobie z każdym tytułem | kruche, najwięcej zapytań | wysokie |

Rekomendacja: **B**, z C tylko jako awaryjny fallback (np. gdy `SxxExx` nie
znajdzie odcinka).

---

## 6. Zgodność z regulaminem, robots, prawa autorskie

### 6.1 Regulamin napisy24.pl

Punkt IV.6 (cytat): *„Zabronione jest masowe pobieranie plików za pośrednictwem
programów stworzonych do tego celu, chyba że taki program zostanie udostępniony
przez administrację lub też za jej zgodą.”*

To jest **największe ryzyko** publicznej wtyczki. Wtyczka do Jellyfina jest
programem do pobierania; tryb automatyczny dla całej biblioteki może być
potraktowany jako „masowe pobieranie”.

Rekomendacje:

1. **Napisać do administracji napisy24** (formularz kontaktowy / e-mail ze strony
   „Program”) z prośbą o zgodę i **własną parę `ua`/`ap`** dla wtyczki.
2. Do czasu zgody traktować wtyczkę jako **prywatną** (self-hosted), a nie
   publiczne wydanie.
3. Domyślnie **wyłączyć auto-download**; ręczne wyszukiwanie to pojedyncze
   pobrania.
4. Nie cache'ować i nie redystrybuować plików napisów; pobierać na żądanie.
5. Nie obchodzić limitów, nie równoleglić zapytań, zachować odstępy.

### 6.2 robots.txt

`robots.txt` zabrania `/run/`, `/download`, `/pliki/`. `CheckSubAgent` i
`download.php` są pod `/run/`, więc formalnie poza robots dla crawlerów.
Wtyczka nie jest crawlerem (działa na żądanie użytkownika), ale warto to
odnotować w dokumentacji i nie nadużywać.

### 6.3 Prawa autorskie

Napisy są utworami; pobieranie wyłącznie do użytku prywatnego z legalną kopią
filmu. Wtyczka nie powinna umożliwiać uploadu/redystrybucji.

---

## 7. Plan wdrożenia

### 7.1 Fazowanie i estymaty

| Faza | Zakres | Czas |
|---|---|---|
| 0 | Szkielet projektu, rejestracja providera, build/release, manifest | 0,5–1 d |
| 1 | Hash: OSH, CheckSubAgent, parser statusów, walidacja ZIP, SRT/MicroDVD | 0,5–1 d |
| 2 | webapi: model XML, film (IMDb/tytuł), serial (`SxxExx`), filtr/scoring | 1 d |
| 3 | download.php, ZIP, SRT, CP1250, multi-CD merge | 1 d |
| 4 | Konfiguracja + test agenta, cache, rate limit, logi | 0,5–1 d |
| 5 | Testy jednostkowe + E2E na serwerze + docs | 1 d |
| **Razem** | pełny wariant B | **4,5–6 d** |
| MVP | hash-only bez UI | **1,5–2 d** |

### 7.2 Build, release, instalacja

- SDK .NET 10 na hoście brak — doinstalować pod
  `~/.local/share/tools/dotnet` (SATA, ~0,8 GB); build:
  `dotnet build -c Release -f net9.0` / `-f net10.0`.
- `build.sh` jak w NapiSub: DLL do ZIP, MD5, wpis do `manifest.json`.
- GitHub Actions: tag `v*` → build → release → aktualizacja manifestu
  (wzór: `.github/workflows/release.yml` w NapiSub).
- Repozytorium w Jellyfin:
  `https://raw.githubusercontent.com/michalkulik/jellyfin-plugin-napisy24/master/manifest.json`
- Instalacja ręczna (do testów):
  `/srv/share/docker/jellyfin/plugins/Napisy24_1.0.0.0/` + DLL + `meta.json`,
  potem `docker compose -p jellyfin restart jellyfin` (kontener, nie proces
  OpenCode!).
- Weryfikacja: `GET /Plugins` (jest `Napisy24`), logi `Loaded plugin`,
  wyszukiwanie napisów w UI, pobranie pliku.

### 7.3 Testy i kryteria akceptacji

Jednostkowe:

- OSH hash kontra niezależna implementacja (wektor z pliku syntetycznego),
- parser `webapi` (wiele rootów, `brak wynikow`, encje, puste pola),
- parser `CheckSubAgent` (`OK-0/1/2/3`, `login error`, śmieciowy ZIP),
- MicroDVD/CP1250 → SRT (fps 23.976/25, `[ ]` i `{ }`, `|`),
- scalanie CD po `time`, numeracja, brak nachodzenia,
- scoring (size/time/fps/release/year),
- round-trip id i walidacja.

E2E (na serwerze):

1. Film z hash (np. F1 2025) → wynik „hash”, pobranie, polskie znaki OK.
2. Film bez hash, z IMDb (np. 8 Mile / „8 Mila”) → lista wersji, pobranie SRT.
3. Serial po polskim tytule (`Przyjaciele 1x01`) → poprawny odcinek.
4. Serial po `SxxExx` z zerami (`The Last of Us 01x01`) → 1 wynik.
5. Multi-CD (Avatar 34102) → scalony SRT (lub udokumentowany fallback).
6. Zły `napisId`/timeout → brak wyjątku w Search, czytelny błąd w Get.
7. `IsPerfectMatch` → tylko hash; brak hash → pusto.
8. Rate limit: brak równoległych requestów, odstęp działa.

Kryteria „done”:

- brak nieobsłużonych wyjątków w logach,
- brak haseł w logach,
- SRT UTF-8 z polskimi znakami,
- poprawne `ThreeLetterISOLanguageName` (`pol`/`eng`),
- wtyczka widoczna w katalogu/repo i ładuje się po restarcie,
- dokumentacja ograniczeń (multi-CD, hash coverage, ToS).

---

## 8. Ryzyka i mitygacje

| Ryzyko | Prawd. | Wpływ | Mitygacja |
|---|---|---|---|
| Zmiana nieoficjalnego API | średnie | wysoki | parser tolerancyjny, fallbacki, testy, wersjonowanie endpointów w configu |
| Blokada IP / ToS | średnie | wysoki | zgoda admina, własne `ua`/`ap`, rate limit, auto-download off |
| Brak trafień hash | wysokie | średni | metadata fallback + scoring |
| Multi-CD | średnie | średni | scalanie po `time`; fallback największy plik |
| MicroDVD/CP1250 | średnie | średni | konwersja + `CodePagesEncodingProvider` |
| Zlokalizowane tytuły | średnie | średni | IMDb, `OriginalTitle`, `SxxExx`, autocomplete |
| Śmieciowe odpowiedzi (junk ZIP, `OK-1`) | niskie | średni | walidacja treści, traktowanie jako brak |
| Niezgodność fps/cut | średnie | średni | `FrameRate`, score, wybór użytkownika |
| Hasło w XML | średnie | średni | ostrzeżenie, ENV override, własne dane |
| Konflikt GUID/nazwy | niskie | wysoki | nowy GUID, nazwa `Napisy24` |

---

## 9. Załączniki

### 9.1 Log weryfikacji (2026-11-11)

| # | Test | Wynik |
|---|---|---|
| 1 | `GET /libs/webapi.php?imdb=tt0499549&lang=pl` | 200, 19 wpisów `pl` |
| 2 | `GET …title=The Last of Us 1x01&lang=pl` | 1 wpis, id `127240` |
| 3 | `GET …title=Przyjaciele 1x01&lang=pl` | `Friends 1x01`, 2 wpisy |
| 4 | `GET …title=Teoria wielkiego podrywu 1x01&lang=pl` | `The Big Bang Theory 1x01` + `11x01` |
| 5 | `GET …imdb=tt11957006&lang=pl` | odcinek TLOU S01E01 |
| 6 | `GET …title=The Simpsons&lang=pl` | 25 wyników (cap), sezon 10 |
| 7 | `GET …title=Seksmisja&lang=pl` | `brak wynikow` (film PL bez napisów PL) |
| 8 | `GET /run/pages/download.php?napisId=127240&typ=sru` + Referer | ZIP, SRT UTF-8 |
| 9 | `…napisId=34102&typ=sru` | 3 SRT (multi-CD) |
| 10 | `…napisId=4434&typ=sru` (stary) | SRT UTF-8 (konwersja serwera) |
| 11 | `…napisId=999999999` | HTTP 500 |
| 12 | `GET /download?napisId=…` | HTTP 403 |
| 13 | `POST CheckSubAgent` F1 2025 (`fh` only) | `OK-2` + metadane + SRT |
| 14 | `POST CheckSubAgent` For All Mankind S01E02 | `OK-2` + MicroDVD `.txt` CP1250 |
| 15 | `POST CheckSubAgent` Beforeigners S01E01 | `OK-0` |
| 16 | `POST CheckSubAgent` Kicia Kocia | `OK-0` + puste metadane |
| 17 | `POST CheckSubAgent` z `napisId` (bez `fh`) | `OK-2` + junk ZIP `nativelog.txt` |
| 18 | `POST CheckSubAgent` złe `ua`/`ap` | `login error` |
| 19 | `POST CheckSubAgent` `md` only | `OK-0` (fh wymagane) |

### 9.2 Przykładowe payloady

Hash (F1 2025):

```
postAction=CheckSub
ua=subliminal
ap=lanimilbus
fh=A5E905CBBC002765
fs=2785089006
fn=F1 The Movie 2025 1080p WEB-DL HEVC x265 5.1 BONE.mkv
nl=PL
n24pref=1
```

Odpowiedź (meta, bez ZIP):

```
OK-2|res:1920x800|time:02:35:49|fps:23.976|imdb:16311594|ilea:7|
ftitle:|fyear:|fgenres:|fwstawil:Kielbi|ftlumacz:Marzena Falkowska|
fkorekta:|fcover:|fimdb:16311594|napisId:132185|lp:425716990|
tlId:|tlPrc:|tlInfo:
```

`webapi` (fragment):

```xml
<subtitle>
  <id>127240</id>
  <title>The Last of Us 1x01</title>
  <season>1</season>
  <episode>1</episode>
  <epTitle>When You're Lost in the Darkness</epTitle>
  <imdb>tt11957006</imdb>
  <year>2023</year>
  <release>WEBRip.x264-ION10;1080p.WEB.H264-CAKES;…</release>
  <language>pl</language>
  <cd>1</cd>
  <time>01:20:47|||</time>
  <size>819684409|||</size>
  <fps>23.976</fps>
  <resolution>720x400</resolution>
  <author>Wojtek Stybliński</author>
  <rating>0.0000</rating>
</subtitle>
```

### 9.3 Odniesienia

- Protokół napisy24 (najpełniejszy, z inżynierii wstecznej):
  <https://github.com/areqq/aqnapi> → `docs/napisy24.md`
- Addon Kodi (Python, `SxxExx`, `/szukaj`, `/download`):
  <https://github.com/CaTzil/service.subtitles.napisy24pl>
- Provider Bazarr (hash, `CheckSubAgent`, statusy, CP1250):
  <https://github.com/LavX/bazarr-provider-catalog> →
  `providers/napisy24/provider.py`
- Regulamin: <https://napisy24.pl/regulamin>
- robots: <https://napisy24.pl/robots.txt>
- Jellyfin 12.4 (lokalnie):
  `MediaBrowser.Controller/Subtitles/ISubtitleProvider.cs`,
  `MediaBrowser.Providers/Subtitles/SubtitleManager.cs`,
  `MediaBrowser.Providers/MediaInfo/SubtitleDownloader.cs`,
  `Jellyfin.Server/Helpers/StartupHelpers.cs:298`
- Wzorce wtyczek:
  `/root/sources/jellyfin-plugin-napi`,
  `/root/sources/jellyfin-plugin-opensubtitles`

### 9.4 Pytania otwarte (do decyzji przed implementacją)

1. **Publiczna czy prywatna?** Jeśli publiczna — najpierw zgoda administracji
   napisy24 i własne `ua`/`ap`; jeśli prywatna — można zostać przy
   `subliminal`/`lanimilbus` z jasnym opisem.
2. **Auto-download?** Domyślnie off (bezpieczniej) czy on z progiem 60?
3. **Zakres języków:** tylko `pl`, czy także `en` (nieliczne wpisy)?
4. **Multi-CD:** scalać automatycznie (lepsze UX, więcej kodu) czy wybrać
   największy plik (MVP)?
5. **Konfiguracja:** pełna strona w Dashboardzie czy tylko XML + ENV?
6. **Nazwa/GUID:** `Napisy24` + `1e3057d0-c2e0-4bbc-a67b-5fa31cdbebd9` OK?
7. **Repo GitHub:** `michalkulik/jellyfin-plugin-napisy24` (publiczne/prywatne)?
8. **Czy kontaktować się z napisy24 już teraz** (przed kodem), czy dopiero
   przed publikacją?

---

## 10. Aktywacja (wdrożona po analizie)

Repo jest publiczne, ale wtyczka **nie zwraca wyników bez ważnego kodu
aktywacyjnego**. Kod jest podpisany kluczem prywatnym **ECDSA P-256**; w repo
i w DLL jest tylko klucz publiczny, więc nie da się z niego wygenerować nowego
kodu.

- Kod per-serwer: `tools/make-activation-code.py --server-id <id> --name ...`
  (pole `sid` w podpisanym payloadzie),
- kod współdzielony: `--any` (pole `sid = "*"`), działa jak hasło na wielu
  serwerach,
- opcjonalny termin ważności: `--days N`,
- klucz prywatny:
  `~/.local/share/signing/jellyfin-plugin-napisy24/activation-private.pem`
  (poza repo, prawa 600).

Panel w Jellyfinie pokazuje status, Server id, pole na kod oraz przyciski
Activate/Deactivate; endpointy to `GET /Napisy24/Status`,
`POST /Napisy24/Activate`, `POST /Napisy24/Deactivate`.

**Ograniczenie:** to bramka „cywilna”, nie DRM. Kod jest sprawdzany w pluginie,
więc zdeterminowany użytkownik może patchować DLL i pominąć weryfikację.
Publiczny kod nie pozwala jednak **wygenerować** ważnego kodu bez klucza
prywatnego. Nie ma zdalnej rewokacji — przy wycieku kodu współdzielonego
trzeba wydać nową wersję/klucz.
