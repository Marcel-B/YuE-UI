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

## Seiten, Player und Playlist

Die Menüleiste oben wechselt zwischen **Erstellen** (Formular, erweiterte Parameter, Warteschlange), **Transkription** (SheetSage2), **Titel** (die Bibliothek) und **Playlist**; auf dem Handy steckt sie hinter dem Menüknopf. Die Seite steht in der Adresse (`/ui/#/songs`), die Zurück-Taste und ein Lesezeichen funktionieren also.

In der Warteschlange zeigt jeder Song seine Schritte als waagerechte Zeitleiste: Warten, Partitur, Tokens, Synthese und Audio, mit der Dauer jedes erledigten Schritts. Der Kreis des laufenden Schritts füllt sich mit dessen Fortschritt, darunter steht, was der Worker gerade meldet. Ein neu gerenderter Entwurf beginnt gleich bei der Synthese. Fertige Songs klappen auf eine Zeile mit der Gesamtdauer zusammen; **Schritte** öffnet die Zeitleiste wieder.

Abgespielt wird in einem Player am unteren Rand, der beim Seitenwechsel weiterläuft. Ein Song aus der Bibliothek spielt danach die folgenden Songs der Bibliothek, einer aus der Playlist die folgenden der Playlist. Titel und Vor/Zurück erscheinen auch auf dem Sperrbildschirm.

Das Plus neben einem Song setzt ihn ans Ende der Playlist, der Haken nimmt ihn wieder heraus; denselben Knopf hat der Player für den Song, der gerade läuft. Auf der Playlist-Seite lässt sich die Reihenfolge ändern. Die Playlist liegt auf dem Server in `~/Library/Application Support/YuE UI/yueui.db` (SQLite), Handy und Mac sehen also dieselbe. Gelöschte Songs fallen von selbst heraus.

Der Stift neben einem Lauf gibt ihm einen neuen Titel. Er gilt für Bibliothek, Player, Playlist, Warteschlange und die Dateinamen beim Herunterladen; der Ordner behält seinen Namen, damit Playlist, Links und YuE Studio den Song weiter finden. Der neue Titel liegt ebenfalls in `yueui.db`, ein leeres Feld stellt den ursprünglichen wieder her.

Jeder Song lässt sich mit 1 bis 5 Sternen bewerten, in der Bibliothek und im Player für den Song, der gerade läuft. Ein Tipp auf denselben Stern nimmt die Bewertung wieder weg. Die Bewertung gehört zum einzelnen Song, nicht zum Lauf, und liegt in `yueui.db`. Die Auswahl neben der Suche zeigt nur Songs ab einer Bewertung (oder nur die mit fünf Sternen); was dann in der Liste steht, spielt der Player auch nacheinander ab.

Der Knopf **Teilen** beim Song (auch im Menü von Player und Playlist) öffnet das Teilen-Menü des Handys, etwa um einen Song per Messenger zu verschicken. Geteilt wird nicht die FLAC, sondern eine kleine AAC-Datei (`.m4a`, 128 kbit/s, etwa ein Zehntel der Größe), die der Mac dafür jedes Mal neu mit `afconvert` erzeugt, das zu macOS gehört (anderswo mit `ffmpeg`, falls installiert). Dauert das länger, als Safari einen Tipp gelten lässt, bleibt ein Fenster mit einem zweiten Knopf **Teilen** stehen. Browser ohne Teilen-Menü für Dateien laden die kleine Datei herunter.

Das Suchfeld über der Bibliothek sucht wahlweise in **Titel & Stil** oder im **Text**. Getrennt, weil fast jeder Songtext Allerweltswörter wie „Nacht“ enthält und die wenigen Treffer im Titel sonst untergingen. Mehrere Wörter müssen alle vorkommen, in beliebiger Reihenfolge; Groß- und Kleinschreibung und Akzente zählen nicht („traume“ findet „Träume“). Was in Anführungszeichen steht, muss genau so vorkommen, etwa eine erinnerte Zeile. Bei der Textsuche stehen die passenden Zeilen mit markierten Treffern unter dem Stil. Der Player spielt nach einem Song die weiteren Treffer.

## Benachrichtigungen

Die Glocke oben rechts meldet per Web Push, wenn ein Song fertig ist oder fehlschlägt, eine Transkription endet oder ein Songtext-Entwurf steht, auch bei gesperrtem Handy. Abgebrochene Songs bleiben still. Auf iPhone und iPad geht das nur in der App auf dem Home-Bildschirm (ab iOS 16.4) und nur über HTTPS, also über `tailscale serve`; im normalen Safari-Tab erklärt die Glocke das. Beim Einschalten kommt eine Test-Nachricht. Jedes Gerät schaltet für sich ein, die Texte kommen in der Sprache, die dort eingestellt ist.

Der Server legt beim ersten Mal ein VAPID-Schlüsselpaar an und speichert es mit den Abonnements in `~/Library/Application Support/YuE UI/push.json` (nur für den eigenen Benutzer lesbar). Diese Datei nicht löschen: Mit einem neuen Schlüssel kommt nichts mehr an, bis jedes Gerät die Glocke einmal neu einschaltet. Die Nachrichten gehen über die Push-Dienste von Apple, Google oder Mozilla, der Mac braucht dafür Internet.

## Speicherplatz

Jeder Song belegt mit FLAC, Tokens und Zwischendateien einiges an Platz. Die Bibliothek zeigt deshalb, was jeder Lauf belegt und wie viel auf dem Datenträger noch frei ist. Über den Papierkorb lassen sich einzelne Songs, ganze Läufe und Transkriptionen löschen; mit dem letzten Song eines Laufs verschwindet auch sein Ordner. Gelöscht wird nach einer Rückfrage endgültig und nicht in den Papierkorb von macOS, denn dort würde der Platz erst beim Leeren frei. Songs, an denen der Worker von YuE UI noch arbeitet, lassen sich erst nach dem Abbrechen löschen; was YuE Studio gerade erzeugt, erkennt YuE UI nicht.

## YuE Studio und YuE UI gleichzeitig

Beide haben einen eigenen Worker und damit ein eigenes Modell im Speicher. Rechnen beide gleichzeitig, kann der Speicher knapp werden. Die Oberfläche zeigt deshalb einen Hinweis, solange die App geöffnet ist. **Worker beenden** in der Warteschlange gibt den Speicher von YuE UI sofort frei. Ansonsten entlädt der Worker das Modell nach zehn Minuten Leerlauf von selbst.

## Stil aus Bausteinen

Unter dem Stil-Feld öffnet **Bausteine** eine Auswahl nach Reitern: Sprache, Genre, Stimme (männlich, weiblich, Duett, Chor, Kinderstimme), Klangfarbe, Instrumente, Stimmung und Tempo. Antippen setzt einen Baustein in den Stil, nochmal Antippen nimmt ihn heraus; selbst geschriebene Begriffe bleiben stehen. Sprache, Stimme und Tempo gibt es nur einmal pro Song, eine neue Wahl ersetzt die alte (beim Tempo auch ein von Hand geschriebenes wie `95 BPM`). Die Sprache kommt nach vorn und das Tempo ans Ende, wie in den Beispielen von YuE2.

Die Auswahl folgt dem [Prompt-Leitfaden von YuE](https://github.com/multimodal-art-projection/YuE/tree/YuE-v1#prompt-engineering-guide): Am stabilsten sind Genre, Instrument, Stimmung, Stimme und Klangfarbe, möglichst alle fünf, mit Begriffen aus seiner Liste der 200 häufigsten Tags (`top_200_tags.json`). YuE2 ergänzt Sprache und Tempo und versteht auch freie Beschreibungen. Duett und Chor stehen nicht in der Liste; wer welche Zeile singt, lässt sich damit nicht festlegen.

## Transkription mit SheetSage2

Unter **Transkription** lässt sich eine Aufnahme hochladen (jedes Format, das macOS lesen kann, bis 300 MB). SheetSage2 macht daraus eine Melodie-Partitur im ABC-Format ohne Akkorde. **Als Partitur übernehmen** setzt sie als eigene Partitur ins Formular und stellt die Planung auf „Nur Melodie“, die Grundlage für ein Cover mit neuem Stil und Text. Jede Transkription landet als Ordner in `~/Music/YuE Studio/transcriptions` (Partitur, MIDI-Spuren, Analyse); die Liste zeigt auch die, die in YuE Studio entstanden sind.

SheetSage2 braucht eine eigene Python-Umgebung und etwa 2 GB Modelle. YuE UI installiert beides nicht selbst, sondern nutzt die Installation von YuE Studio: dort einmal **Transcribe recording** öffnen und **Install transcription support** wählen. Transkribiert wird auf der CPU, das dauert einige Minuten, immer eine Aufnahme zur Zeit.

## Songtext entwerfen mit LM Studio

YuE2 singt Texte, schreibt aber selbst keine. Über dem Songtext-Feld steht deshalb **Worum geht es?**: Stichwörter oder ein Satz genügen, **Text entwerfen** lässt ein Sprachmodell in [LM Studio](https://lmstudio.ai) auf dem Mac daraus einen Songtext im Format von YuE2 schreiben (Abschnitte wie `[Verse]` und `[Chorus]`, vier Zeilen je Abschnitt, gleichmäßige Silben). **EN** oder **DE** daneben wählt, ob er englisch oder deutsch wird; die Abschnittsmarken bleiben englisch, weil YuE2 sie so liest. Damit YuE2 einen deutschen Text auch deutsch ausspricht, gehört `German` an den Anfang des Stils (Bausteine → Sprache). Der Stil aus dem Formular geht mit, damit Stimmung und Tempo passen. Ein vorhandener Text wird erst nach einer Rückfrage ersetzt. Der Entwurf läuft auf dem Mac weiter, auch wenn das Handy zwischendurch sperrt oder die Seite neu lädt; der Text landet danach im Feld.

Statt oder neben den Stichwörtern kann ein **Foto** die Vorlage sein: Die Kamera neben dem Feld nimmt eins auf oder holt es aus der Mediathek. Der Entwurf erzählt dann von dem, was darauf zu sehen ist, Stimmung, Ort, eine mögliche Geschichte; Stichwörter lenken, worauf er achtet. Der Browser verkleinert das Foto vorher auf 1024 Pixel (JPEG, auch aus HEIC), es wird nirgends gespeichert und gilt nur bis zum Neuladen der Seite. Das Modell muss Bilder lesen können, wie Gemma 4; in der Modellauswahl steht bei solchen „sieht Fotos“, und mit einem, das LM Studio als blind meldet, bleibt der Entwurf-Knopf aus.

Darunter lässt sich das Sprachmodell wählen: Die Liste zeigt alle Modelle, die in LM Studio heruntergeladen sind (ohne Embedding-Modelle), mit ihrer Größe, das eingestellte `Lyrics:Model` als „Standard“. Die Größe entspricht etwa dem Speicher, den das Modell braucht. Die Wahl merkt sich der Browser wie den Rest des Formulars; wird das Modell in LM Studio gelöscht, gilt wieder der Standard. Mit nur einem Modell bleibt die Auswahl ausgeblendet.

LM Studio muss dafür nicht geöffnet sein: Antwortet sein Server nicht, startet YuE UI ihn mit `~/.lmstudio/bin/lms daemon up` und `lms server start`. YuE UI lädt das Modell (Standard: `google/gemma-4-26b-a4b-qat`) erst für die Anfrage, mit einem Kontext von 30000 Tokens, und entlädt es gleich danach wieder; ein Modell, das in LM Studio schon geladen ist, nutzt es mit und lässt es geladen. Dieses Modell (rund 15 GB) hat einen Mac mit 24 GB zusammen mit den üblichen offenen Apps schon bis zum Einfrieren in den Swap getrieben; vor einem Entwurf also möglichst viel schließen, oder das kleine `google/gemma-4-e4b` eintragen. Weigert sich LM Studio wegen zu wenig Speicher (seine Schutzgrenze „insufficient system resources“, etwa bei dichten Modellen wie Qwen 27B), entlädt YuE UI zuerst andere noch geladene Modelle und versucht es dann mit halbem Kontext, bis hinunter zu `Lyrics:MinContextLength`; erst dann zeigt die Oberfläche seine Meldung. Die Schutzgrenze selbst bleibt an. Außerdem gilt: Solange YuE2 rechnet, gibt es keinen Entwurf; ein ruhender Worker wird vorher beendet; und solange ein Entwurf entsteht, startet kein Song. Der erste Entwurf dauert durch das Laden des Modells etwas länger.

## Als Logic-Projekt laden

Steht unter `Logic:BaseUrl` ein Server von [yue-to-logic-pro](https://github.com/Marcel-B/yue-to-logic-pro), zeigt die Bibliothek bei jedem Song mit Audio und Partitur einen Knopf **Als Logic-Projekt laden**. YuE UI schickt `audio.flac` und `score.abc` direkt von Server zu Server dorthin; der Browser muss die FLAC also nicht erst herunter- und wieder hochladen. Zurück kommt ein ZIP mit dem `.logicx`-Projekt: Audio auf der ersten Spur, Gesang, Instrument und Akkorde als MIDI, Tempo, Takt und Abschnitte aus der Partitur. Hinweise von yue-to-logic-pro zeigt die Oberfläche nach dem Download an; lehnt es einen Song ab (etwa eine Partitur, die es nicht lesen kann), steht der Grund in der Fehlermeldung.

**Zurück aus Logic.** Wer den Song in Logic weiterbearbeitet hat (Melodie, Akkorde, Tempo), wählt dort alle MIDI-Regionen aus und exportiert sie als MIDI-Datei (*Ablage → Exportieren → Auswahl als MIDI-Datei*); die Spurnamen `Vocal`, `Ins` und `Chords` müssen bleiben, an ihnen erkennt yue-to-logic-pro die Stimmen. Der Knopf **MIDI aus Logic als Partitur** beim Song schickt sie an yue-to-logic-pro, das daraus wieder eine `score.abc` macht; die steht dann mit Stil, Text und Seed des Songs im Formular, bereit für einen neuen Song. Dasselbe geht ohne Song über den Knopf neben dem Partiturfeld unter „Erweiterte Parameter“, etwa für eine Melodie, die in Logic entstanden ist. Spuren, die yue-to-logic-pro nicht zuordnen kann, nennt die Oberfläche als Hinweis.

yue-to-logic-pro verlangt keinen Schlüssel. Läuft es hinter einem Proxy, muss der Uploads in FLAC-Größe durchlassen (bei Nginx Proxy Manager `client_max_body_size 300m;`). Ohne `Logic:BaseUrl` gibt es den Knopf nicht.

## Konfiguration

`appsettings.json` bzw. Umgebungsvariablen:

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Urls` | `http://127.0.0.1:5090` | Adresse des Servers (in der Entwicklung `5091`, siehe `appsettings.Development.json`, damit sie neben der installierten App läuft) |
| `Yue:InstallRoot` (`Yue__InstallRoot`) | `~/Library/Application Support/YuE Studio` | Installation von YuE Studio (`env/`, `src/`, `models/`) |
| `Yue:OutputDir` (`Yue__OutputDir`) | `~/Music/YuE Studio` | Song-Bibliothek |
| `Yue:SheetSagePython` (`Yue__SheetSagePython`) | `<InstallRoot>/sheetsage-env/bin/python` | Python der SheetSage2-Umgebung |
| `Lyrics:BaseUrl` (`Lyrics__BaseUrl`) | `http://127.0.0.1:1234` | Server für Textentwürfe (LM Studio oder ein anderer OpenAI-kompatibler) |
| `Lyrics:Model` (`Lyrics__Model`) | `google/gemma-4-26b-a4b-qat` | Modell-ID, wie `GET /v1/models` sie listet |
| `Lyrics:ContextLength` (`Lyrics__ContextLength`) | `30000` | Kontext, mit dem das Modell geladen wird; bis auf 1024 Tokens für den Prompt darf die Antwort samt Denkphase ihn ganz nutzen |
| `Lyrics:MinContextLength` (`Lyrics__MinContextLength`) | `8192` | Kleinster Kontext, auf den YuE UI zurückgeht, wenn LM Studio das Modell wegen zu wenig Speicher ablehnt |
| `Lyrics:ApiToken` (`Lyrics__ApiToken`) | – | nur nötig, wenn in LM Studio „Require Authentication“ an ist |
| `Lyrics:Lms` (`Lyrics__Lms`) | `~/.lmstudio/bin/lms` | Kommandozeilenwerkzeug, mit dem YuE UI den Server von LM Studio startet |
| `Logic:BaseUrl` (`Logic__BaseUrl`) | – | Server von yue-to-logic-pro ohne `/api`, z. B. `https://music.idsrv.info`; leer schaltet den Logic-Export ab |
| `Logic:SplitSections` (`Logic__SplitSections`) | `false` | eine Region je Songabschnitt statt einer je Spur |
| `Push:DataPath` (`Push__DataPath`) | `~/Library/Application Support/YuE UI/push.json` | VAPID-Schlüssel und Abonnements für Benachrichtigungen |
| `Data:Path` (`Data__Path`) | `~/Library/Application Support/YuE UI/yueui.db` | SQLite-Datenbank von YuE UI (Playlist, geänderte Titel, Bewertungen) |
| `Push:Subject` (`Push__Subject`) | `https://github.com/Marcel-B/YuE-UI` | Kontaktadresse (`mailto:` oder `https:`) für die Push-Dienste; Apple lehnt Adressen wie `mailto:ich@localhost` ab |

## API

| Methode | Pfad | |
|---|---|---|
| `GET` | `/api/status` | Worker-Zustand, Songs in Arbeit, Protokoll |
| `GET` | `/api/events` | dasselbe live als Server-Sent Events (`snapshot`, `song`, `worker`, `log`, `library`, `transcription`, `lyrics`, `ping`) |
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
| `GET` | `/api/songs/{run}/{song}/share` | Song als kleine AAC (`.m4a`, 128 kbit/s) zum Teilen; `501`, wenn weder `afconvert` noch `ffmpeg` da ist |
| `GET` | `/api/songs/{run}/{song}/zip` | FLAC und ABC des Songs als ZIP |
| `GET` | `/api/runs/{run}/zip` | FLAC und ABC aller Songs des Laufs als ZIP |
| `GET` | `/api/logic` | `{ configured }`: ob ein Server von yue-to-logic-pro eingetragen ist |
| `GET` | `/api/songs/{run}/{song}/logic` | Song als Logic-Projekt (ZIP mit `.logicx`), gebaut von yue-to-logic-pro; Hinweise im Header `X-YueToLogic-Diagnostics`; `422`, wenn yue-to-logic-pro den Song ablehnt, `501` ohne Server, `502`/`504`, wenn er nicht oder zu spät antwortet |
| `POST` | `/api/midi/abc` | Multipart-Feld `file`: MIDI-Datei (bis 4 MB), von yue-to-logic-pro zurück in eine Partitur gewandelt: `{ abc, warnings }`; `422`, wenn keine Partitur daraus wird, `413` für zu große Dateien, `501` ohne Server, `502`, wenn er nicht antwortet |
| `DELETE` | `/api/songs/{run}/{song}` | Song löschen, mit dem letzten auch den Lauf; `409`, solange der Worker daran arbeitet |
| `PUT` | `/api/songs/{run}/{song}/rating` | Song bewerten (`{"rating": 1…5}`, `null` oder `0` nimmt die Bewertung weg); `400` außerhalb von 1 bis 5 |
| `PUT` | `/api/runs/{run}/title` | Lauf umbenennen (`{"title": "…"}`, leer = ursprünglicher Titel); Ordner und Song-IDs bleiben |
| `DELETE` | `/api/runs/{run}` | Lauf mit allen Songs löschen; `409`, solange der Worker an einem davon arbeitet |
| `GET` | `/api/storage` | `{ freeBytes, totalBytes }` des Datenträgers der Bibliothek |
| `GET` | `/api/lyrics/models` | Modelle für Textentwürfe: `{ default, models: [{ id, name, sizeBytes, loaded, vision }] }` (`vision`: kann Fotos lesen, `null`, wenn der Server es nicht sagt); startet den Server von LM Studio bei Bedarf, `503`, wenn er nicht erreichbar ist |
| `POST` | `/api/lyrics` | Songtext entwerfen: `{ keywords, style?, language?, model?, image? }` (ohne `model` das eingestellte; `image` ein Foto als `data:image/jpeg;base64,…`-URL, dann ist `keywords` optional); antwortet `202`, der Entwurf kommt als `lyrics`-Event (`writing`, dann `done` mit `lyrics` oder `failed` mit `message`); `409`, solange YuE2 rechnet oder schon ein Entwurf entsteht |
| `GET` | `/api/playlist` | `{ songIds }`: Songs der Playlist in Reihenfolge (`run/songN`), gelöschte weggelassen |
| `PUT` | `/api/playlist` | `{ songIds }`: Playlist ersetzen; `400` für einen Song, den es nicht gibt |
| `GET` | `/api/push` | `{ publicKey }`: VAPID-Schlüssel für `pushManager.subscribe` |
| `POST` | `/api/push/subscriptions` | Browser benachrichtigen: `PushSubscription.toJSON()` plus `language` (`de`/`en`) |
| `DELETE` | `/api/push/subscriptions` | `{ endpoint }`: Abonnement entfernen |
| `POST` | `/api/push/test` | `{ endpoint }`: Test-Nachricht an genau diesen Browser; `404`, wenn er nicht abonniert ist |

OpenAPI unter `/api/openapi`.

## Roadmap

Ideen und geplante Änderungen, ohne feste Reihenfolge. Erledigtes abhaken oder löschen.

- [x] Suche in der Bibliothek: nach Titel und im Volltext (Style und Lyrics)
- [x] Songs bewerten (1 bis 5 Sterne)
- [x] Songs vom Handy teilen (kleine AAC statt FLAC)
- [ ] Mehrere Playlists (die Tabelle `playlists` ist schon da)
- [x] PrimeVue-Importe optimieren (nur benötigte Komponenten, kleineres Bundle)
- [ ] Restliche Oberfläche auf PrimeVue umstellen (`.button`, `.card`, `.link` und Eingabefelder aus `style.css` ablösen)
