# YuE UI

Eine Web-Oberfläche für [YuE Studio](https://github.com/multimodal-art-projection/YuE), die sich vom Handy oder Laptop aus bedienen lässt, zum Beispiel über Tailscale. Songs erzeugen, den Fortschritt live verfolgen, Entwürfe in voller Qualität rendern, anhören und herunterladen.

YuE UI bringt kein eigenes Modell mit. Es startet den Worker von YuE Studio (`src/tools/yue2_worker.py`) mit der Python-Umgebung, die die App installiert hat, und spricht ihn über sein JSON-Zeilen-Protokoll auf stdin/stdout an. Die Songs landen deshalb im selben Ordner wie die der App (`~/Music/YuE Studio`), und beide sehen dieselbe Bibliothek.

Davor sitzt eine kleine Erweiterung aus diesem Repository (`src/YueUI.Api/Worker/yueui_worker.py`). Sie lädt den Worker als Python-Modul und ergänzt das `generate`-Kommando um Parameter, die YuE Studio selbst nicht anbietet: Sampling für Partitur und Song (temperature, top_p, top_k, repetition_penalty, penalty_window), die Schrittzahl bei voller Qualität und Songs bis 10 Minuten statt 6. Sie greift dafür an einigen Stellen in den Worker ein und prüft beim Start, ob die noch so aussehen wie erwartet. Hat ein Update von YuE Studio den Worker verändert, läuft er unverändert weiter. Die Oberfläche weist dann darauf hin, dass diese Parameter keine Wirkung haben, der Grund steht im Protokoll. `cfg_scale` fehlt bewusst: Der Worker komponiert Songs in Batches, und die Batch-Decoder von YuE2 können keine CFG.

```
Browser ──(Tailscale, HTTPS)──▶ tailscale serve ──▶ YueUI.Api 127.0.0.1:5090 ──stdin/stdout──▶ yue2_worker.py
          ◀── Server-Sent Events: Warteschlange, Fortschritt, Protokoll
```

## Voraussetzungen

- macOS mit installiertem **YuE Studio** (die Modelle müssen einmal über die App geladen worden sein)
- .NET 10 SDK, Node.js 22.12+
- für den Fernzugriff: Tailscale

## Entwicklung

```sh
dotnet run --project src/YueUI.Api     # API auf 127.0.0.1:5091, SpaProxy startet Vite auf 127.0.0.1:5174/ui/
dotnet test                            # Tests mit einem simulierten Worker
```

Nur das Frontend, in `src/YueUI.Api/ClientApp`: `npm run dev`, `npm run build`, `npm run type-check`.

## Dauerbetrieb und Fernzugriff

```sh
deploy/install.sh                      # veröffentlicht nach ~/Library/Application Support/YueUI und startet einen LaunchAgent
tailscale serve --bg --https=8443 5090 # https://<mac-name>.<tailnet>.ts.net:8443/ → 127.0.0.1:5090
```

Der Server lauscht nur auf `127.0.0.1`. Erreichbar wird er erst über `tailscale serve`, und das nur für Geräte im eigenen Tailnet, mit HTTPS-Zertifikat. Die Oberfläche hat kein eigenes Login; der Schutz ist das Tailnet. Nicht mit `tailscale funnel` ins Internet stellen.

Ein eigener HTTPS-Port (8443), weil `tailscale serve` auf 443 schon einen anderen Dienst weiterleiten kann und `--bg 5090` diesen ersetzen würde. Ein Unterpfad (`--set-path`) funktioniert nicht, da die App mit den festen Pfaden `/ui` und `/api` arbeitet. Abschalten: `tailscale serve --https=8443 off`.

`deploy/install.sh` nach Änderungen einfach erneut ausführen. `deploy/uninstall.sh` entfernt den LaunchAgent wieder, die Songs bleiben erhalten.

Auf dem iPhone lässt sich die Seite über „Teilen → Zum Home-Bildschirm“ wie eine App ablegen.

## Speicherplatz

Jeder Song belegt mit FLAC, Tokens und Zwischendateien einiges an Platz. Die Bibliothek zeigt deshalb, was jeder Lauf belegt und wie viel auf dem Datenträger noch frei ist. Über den Papierkorb lassen sich einzelne Songs, ganze Läufe und Transkriptionen löschen; mit dem letzten Song eines Laufs verschwindet auch sein Ordner. Gelöscht wird nach einer Rückfrage endgültig und nicht in den Papierkorb von macOS, denn dort würde der Platz erst beim Leeren frei. Songs, an denen der Worker von YuE UI noch arbeitet, lassen sich erst nach dem Abbrechen löschen; was YuE Studio gerade erzeugt, erkennt YuE UI nicht.

## YuE Studio und YuE UI gleichzeitig

Beide haben einen eigenen Worker und damit ein eigenes Modell im Speicher. Rechnen beide gleichzeitig, kann der Speicher knapp werden. Die Oberfläche zeigt deshalb einen Hinweis, solange die App geöffnet ist. **Worker beenden** in der Warteschlange gibt den Speicher von YuE UI sofort frei. Ansonsten entlädt der Worker das Modell nach zehn Minuten Leerlauf von selbst.

## Transkription mit SheetSage2

Unter **Transkription** lässt sich eine Aufnahme hochladen (jedes Format, das macOS lesen kann, bis 300 MB). SheetSage2 macht daraus eine Melodie-Partitur im ABC-Format ohne Akkorde. **Als Partitur übernehmen** setzt sie als eigene Partitur ins Formular und stellt die Planung auf „Nur Melodie“, die Grundlage für ein Cover mit neuem Stil und Text. Jede Transkription landet als Ordner in `~/Music/YuE Studio/transcriptions` (Partitur, MIDI-Spuren, Analyse); die Liste zeigt auch die, die in YuE Studio entstanden sind.

SheetSage2 braucht eine eigene Python-Umgebung und etwa 2 GB Modelle. YuE UI installiert beides nicht selbst, sondern nutzt die Installation von YuE Studio: dort einmal **Transcribe recording** öffnen und **Install transcription support** wählen. Transkribiert wird auf der CPU, das dauert einige Minuten, immer eine Aufnahme zur Zeit.

## Songtext entwerfen mit LM Studio

YuE2 singt Texte, schreibt aber selbst keine. Über dem Songtext-Feld steht deshalb **Worum geht es?**: Stichwörter oder ein Satz genügen, **Text entwerfen** lässt ein Sprachmodell in [LM Studio](https://lmstudio.ai) auf dem Mac daraus einen englischen Songtext im Format von YuE2 schreiben (Abschnitte wie `[Verse]` und `[Chorus]`, vier Zeilen je Abschnitt, gleichmäßige Silben). Der Stil aus dem Formular geht mit, damit Stimmung und Tempo passen. Ein vorhandener Text wird erst nach einer Rückfrage ersetzt.

LM Studio muss dafür nicht geöffnet sein: Antwortet sein Server nicht, startet YuE UI ihn mit `~/.lmstudio/bin/lms daemon up` und `lms server start`. YuE UI lädt das Modell (Standard: das kleine `google/gemma-4-e4b`) erst für die Anfrage, mit einem Kontext von 8192 Tokens, und entlädt es gleich danach wieder; ein Modell, das in LM Studio schon geladen ist, nutzt es mit und lässt es geladen. Die großen Modelle (etwa `google/gemma-4-26b-a4b-qat`, rund 15 GB) haben einen Mac mit 24 GB zusammen mit den üblichen offenen Apps bis zum Einfrieren in den Swap getrieben; wer sie nutzen will, schließt vorher möglichst viel. Weigert sich LM Studio wegen zu wenig Speicher, zeigt die Oberfläche seine Meldung. Außerdem gilt: Solange YuE2 rechnet, gibt es keinen Entwurf; ein ruhender Worker wird vorher beendet; und solange ein Entwurf entsteht, startet kein Song. Der erste Entwurf dauert durch das Laden des Modells etwas länger.

## Konfiguration

`appsettings.json` bzw. Umgebungsvariablen:

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Urls` | `http://127.0.0.1:5090` | Adresse des Servers (in der Entwicklung `5091`, siehe `appsettings.Development.json`, damit sie neben der installierten App läuft) |
| `Yue:InstallRoot` (`Yue__InstallRoot`) | `~/Library/Application Support/YuE Studio` | Installation von YuE Studio (`env/`, `src/`, `models/`) |
| `Yue:OutputDir` (`Yue__OutputDir`) | `~/Music/YuE Studio` | Song-Bibliothek |
| `Yue:SheetSagePython` (`Yue__SheetSagePython`) | `<InstallRoot>/sheetsage-env/bin/python` | Python der SheetSage2-Umgebung |
| `Lyrics:BaseUrl` (`Lyrics__BaseUrl`) | `http://127.0.0.1:1234` | Server für Textentwürfe (LM Studio oder ein anderer OpenAI-kompatibler) |
| `Lyrics:Model` (`Lyrics__Model`) | `google/gemma-4-e4b` | Modell-ID, wie `GET /v1/models` sie listet |
| `Lyrics:ContextLength` (`Lyrics__ContextLength`) | `8192` | Kontext, mit dem das Modell geladen wird; die Hälfte davon darf die Antwort samt Denkphase lang sein |
| `Lyrics:ApiToken` (`Lyrics__ApiToken`) | – | nur nötig, wenn in LM Studio „Require Authentication“ an ist |
| `Lyrics:Lms` (`Lyrics__Lms`) | `~/.lmstudio/bin/lms` | Kommandozeilenwerkzeug, mit dem YuE UI den Server von LM Studio startet |

## API

| Methode | Pfad | |
|---|---|---|
| `GET` | `/api/status` | Worker-Zustand, Songs in Arbeit, Protokoll |
| `GET` | `/api/events` | dasselbe live als Server-Sent Events (`snapshot`, `song`, `worker`, `log`, `library`, `transcription`, `ping`) |
| `POST` | `/api/generate` | neuer Lauf: `{ style, lyrics, title?, batch?, quality?: "draft"\|"full", cot?, seed?, instrumental?, engines?, draftSteps?, maxTokens?: 200–15000, abc?, fullSteps?: 1–64, abcSampling?, semanticSampling? }`; `abc` braucht `cot` "full" oder "melody", Sampling ist `{ temperature?, topP?, topK?, repetitionPenalty?, penaltyWindow? }`, über 9000 Tokens, `fullSteps` und Sampling nur mit der Worker-Erweiterung |
| `POST` | `/api/songs/{run}/{song}/render` | Song aus seinen Tokens neu synthetisieren, z. B. einen Entwurf in voller Qualität |
| `POST` | `/api/songs/{run}/{song}/cancel` | Song abbrechen |
| `POST` | `/api/stop` | alle Songs abbrechen |
| `POST` | `/api/worker/shutdown` | Worker-Prozess beenden (Speicher freigeben) |
| `GET` | `/api/transcriptions` | `{ installed, items }`: ob SheetSage2 installiert ist, und die fertigen Transkriptionen |
| `POST` | `/api/transcriptions` | Aufnahme transkribieren: Formular mit `file` und `task` (`melody-full` oder `melody-vocal`); der Fortschritt kommt als `transcription`-Event |
| `POST` | `/api/transcriptions/{id}/cancel` | laufende Transkription abbrechen |
| `GET` | `/api/transcriptions/{id}/score` | `score.abc` der Transkription (`?download=true` als Download) |
| `GET` | `/api/transcriptions/{id}/zip` | alle Dateien der Transkription als ZIP |
| `DELETE` | `/api/transcriptions/{id}` | fertige Transkription löschen |
| `GET` | `/api/library` | alle Läufe mit ihren Songs |
| `GET` | `/api/songs/{run}/{song}/audio` | FLAC (Range-fähig; `?download=true` als Download) |
| `GET` | `/api/songs/{run}/{song}/score` | `score.abc` |
| `GET` | `/api/songs/{run}/{song}/zip` | FLAC und ABC des Songs als ZIP |
| `GET` | `/api/runs/{run}/zip` | FLAC und ABC aller Songs des Laufs als ZIP |
| `DELETE` | `/api/songs/{run}/{song}` | Song löschen, mit dem letzten auch den Lauf; `409`, solange der Worker daran arbeitet |
| `DELETE` | `/api/runs/{run}` | Lauf mit allen Songs löschen; `409`, solange der Worker an einem davon arbeitet |
| `GET` | `/api/storage` | `{ freeBytes, totalBytes }` des Datenträgers der Bibliothek |
| `POST` | `/api/lyrics` | Songtext entwerfen: `{ keywords, style? }` → `{ lyrics }`; `409`, solange YuE2 rechnet oder schon ein Entwurf entsteht, `503`, wenn LM Studio nicht erreichbar ist |

OpenAPI unter `/api/openapi`.

## Roadmap

Ideen und geplante Änderungen, ohne feste Reihenfolge. Erledigtes abhaken oder löschen.

- [ ] Menü im Header
- [ ] Playlists, auf einer eigenen Seite
- [ ] PrimeVue `Timeline` für die Schritte eines Songs nutzen
- [ ] PrimeVue-Importe optimieren (nur benötigte Komponenten, kleineres Bundle)
- [ ] Restliche Oberfläche auf PrimeVue umstellen (`.button`, `.card`, `.link` und Eingabefelder aus `style.css` ablösen)
