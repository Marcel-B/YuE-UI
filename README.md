# Tonwerk

Eine Web-Oberfläche für [YuE Studio](https://github.com/multimodal-art-projection/YuE), die sich vom Handy oder Laptop aus bedienen lässt, zum Beispiel über Tailscale. Songs erzeugen, den Fortschritt live verfolgen, Entwürfe in voller Qualität rendern, anhören und herunterladen.

Tonwerk bringt kein eigenes Modell mit. Es startet den Worker von YuE Studio (`src/tools/yue2_worker.py`) mit der Python-Umgebung, die die App installiert hat, und spricht ihn über sein JSON-Zeilen-Protokoll auf stdin/stdout an. Die Songs landen deshalb im selben Ordner wie die der App (`~/Music/YuE Studio`), und beide sehen dieselbe Bibliothek.

Tonwerk hieß früher YuE UI. Repository, Projektnamen (`YueUI.Api`), der LaunchAgent `de.bvelop.yueui`, der Datenordner `~/Library/Application Support/YuE UI` und `yueui.db` tragen weiter den alten Namen, damit Deploy, Auto-Deploy und die vorhandenen Daten weiterlaufen.

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

## Seiten, Player und Playlists

Die Menüleiste oben wechselt zwischen **Erstellen** (Formular, erweiterte Parameter), **Titel** (die Bibliothek) und **Playlist**; unter **Werkzeuge** stehen **Transkription** (SheetSage2), **Stimmen** (nur mit ChangeMyVoice oder StemMyWav, siehe unten), **Logic**, **Begleitstimmen** und **Sprachlabor** (siehe unten). Auf dem Handy steckt das hinter dem Menüknopf. Die Seite steht in der Adresse (`/ui/#/songs`), die Zurück-Taste und ein Lesezeichen funktionieren also. Rechts führt die Sanduhr zur **Warteschlange** (was gerade rechnet und was wartet, mit Protokoll); solange etwas rechnet oder wartet, steht die Anzahl daneben, auch auf dem Handy. Das Zahnrad daneben schaltet Benachrichtigungen und die Sprache um, in der App auf dem Home-Bildschirm lädt es auch neu. Auf **Erstellen** zeigt dann eine Zeile, welches Modell gerade den Speicher hat.

In der Warteschlange zeigt jeder Song seine Schritte als waagerechte Zeitleiste: Warten, Partitur, Tokens, Synthese und Audio, mit der Dauer jedes erledigten Schritts. Der Kreis des laufenden Schritts füllt sich mit dessen Fortschritt, darunter steht, was der Worker gerade meldet. Ein neu gerenderter Entwurf beginnt gleich bei der Synthese. Fertige Songs klappen auf eine Zeile mit der Gesamtdauer zusammen; **Schritte** öffnet die Zeitleiste wieder.

Songs, Neuberechnungen und Textentwürfe werden immer angenommen. Der Mac hat nur Speicher für ein großes Modell zur Zeit (YuE2, das Textmodell oder Stem-Trennung und Seed-VC); was gerade keinen Platz hat, steht oben in der Warteschlange unter **Wartet** und startet der Reihe nach, sobald der Speicher frei ist. Eine Ausnahme bündelt nach Modell: Rechnet YuE2 gerade, gehen neue Songs und Neuberechnungen an einem wartenden Textentwurf vorbei direkt an den Worker, der sie mit den laufenden bündelt; so wird YuE2 nicht für den Entwurf entladen und danach neu geladen. Das gilt nur, bis der Entwurf vorn 20 Minuten gewartet hat (`Queue:BundleWindow`); dann warten neue Songs hinter ihm. Ebenso hält eine Fassung, die schon so lange auf den Speicher wartet, neue Songs zurück, bis sie dran war. Mit den Pfeilen ändert sich die Reihenfolge, mit dem Kreuz fliegt ein Auftrag wieder heraus. Wartende Aufträge liegen in `yueui.db` und überstehen einen Neustart oder ein Deploy. Ein Textentwurf, der warten muss, landet trotzdem im Formular, sobald er fertig ist (auch nach dem Neuladen, mit Benachrichtigung).

Abgespielt wird in einem Player am unteren Rand, der beim Seitenwechsel weiterläuft. Ein Song aus der Bibliothek spielt danach die folgenden Songs der Bibliothek, einer aus einer Playlist die folgenden dieser Playlist. Titel und Vor/Zurück erscheinen auch auf dem Sperrbildschirm.

Neben den Sternen im Player zeigt ein kleiner Analyzer das Spektrum des laufenden Songs. Ein Tipp darauf öffnet zwei Schalter: den Analyzer selbst und **Hintergrund zur Musik**, bei dem auf jeder Seite hinter den Karten drei weiche Lichter mit Bass, Mitten und Höhen pulsieren (standardmäßig aus; bei der Systemeinstellung „Bewegung reduzieren“ bewegt sich nichts). Beides merkt sich der Browser. Für den Analyzer läuft der Ton über Web Audio; wer das auf dem iPhone nicht will (etwa wenn die Wiedergabe im Hintergrund stockt), schaltet ihn aus und lädt die Seite neu.

Auf der Playlist-Seite wählt das Feld oben, welche Playlist sie zeigt; daneben legen Plus, Stift und Papierkorb eine neue an, benennen die gezeigte um oder löschen sie (die Songs selbst bleiben, die letzte Playlist lässt sich nicht löschen). Welche Playlist gezeigt wird, merkt sich jeder Browser selbst. Die Reihenfolge lässt sich dort ändern.

Das Plus neben einem Song setzt ihn ans Ende der Playlist, der Haken nimmt ihn wieder heraus; denselben Knopf hat der Player für den Song, der gerade läuft. Gibt es mehrere Playlists, öffnet der Knopf eine Liste, in der jeder Tipp den Song in eine Playlist legt oder herausnimmt; der Haken am Knopf heißt dann, dass der Song in mindestens einer steckt. Die Playlists liegen auf dem Server in `~/Library/Application Support/YuE UI/yueui.db` (SQLite), Handy und Mac sehen also dieselben. Gelöschte Songs fallen von selbst heraus.

Der Pfeil neben einem Lauf übernimmt Titel, Stil und Text als **Vorlage** ins Formular. **Alles übernehmen** beim Song (auf dem Handy im Menü „…“, im Player und in der Playlist ebenso) trägt alles ein, womit genau dieser Song erzeugt wurde: Titel, Stil, Text, Seed, Partitur samt Planung, Qualität, Länge und die erweiterten Parameter. „Erzeugen“ macht ihn dann noch einmal, oder man ändert vorher den Seed oder einzelne Werte. **Als Partitur übernehmen** nimmt dagegen nur die Partitur, mit Stil, Text und Seed des Laufs.

Der Stift neben einem Lauf gibt ihm einen neuen Titel. Er gilt für Bibliothek, Player, Playlist, Warteschlange und die Dateinamen beim Herunterladen; der Ordner behält seinen Namen, damit Playlist, Links und YuE Studio den Song weiter finden. Der neue Titel liegt ebenfalls in `yueui.db`, ein leeres Feld stellt den ursprünglichen wieder her.

Jeder Song lässt sich mit 1 bis 5 Sternen bewerten, in der Bibliothek und im Player für den Song, der gerade läuft. Ein Tipp auf denselben Stern nimmt die Bewertung wieder weg. Die Bewertung gehört zum einzelnen Song, nicht zum Lauf, und liegt in `yueui.db`. Die Auswahl neben der Suche zeigt nur Songs ab einer Bewertung (oder nur die mit fünf Sternen); was dann in der Liste steht, spielt der Player auch nacheinander ab. Die zweite Auswahl sortiert die Bibliothek: nach Datum (neueste oder älteste zuerst), nach Bewertung oder nach Dauer (längste oder kürzeste zuerst). Ein Lauf bleibt dabei zusammen und rückt nach seinem besten Song ein, der dann innerhalb des Laufs vorne steht. Die Reihenfolge merkt sich jeder Browser selbst.

**Abspielen unterwegs.** Der Player spielt nicht die FLAC (rund 1 Mbit/s), sondern eine AAC-Kopie mit 192 kbit/s, etwa ein Sechstel davon: Der Mac hängt an einem Anschluss mit langsamem Upload, und über Mobilfunk hat die FLAC oft lange geladen. Die Kopie entsteht mit `afconvert` (sonst `ffmpeg`), sobald ein Song oder eine Fassung fertig ist, bei älteren Songs beim ersten Abspielen. Sie liegt in `stream/` neben der Datenbank (die einer Fassung neben deren FLAC in `versions/`), wird nach einem Neu-Rendern neu gemacht und geht mit dem Song. Wie ein Export trägt sie Titel, Album, Nummer, Songtext, Stil samt Seed und das eigene Cover des Songs (Fassungen mit der Stimme im Titel); ändern sich Titel, Cover oder Text, bekommt sie beim nächsten Abspielen neue Tags, ohne neu kodiert zu werden. Songs ohne eigenes Cover haben keins in der Datei, das gezeichnete gibt es nur im Browser. Herunterladen, Exportieren und die Logic-Seite nehmen weiter die FLAC (Downloads und Exporte mit denselben Tags, siehe unten); ohne Encoder spielt auch der Player sie.

Der Knopf **Teilen** beim Song (auch im Menü von Player und Playlist) öffnet das Teilen-Menü des Handys, etwa um einen Song per Messenger zu verschicken. Geteilt wird nicht die FLAC, sondern eine kleine AAC-Datei (`.m4a`, 128 kbit/s, etwa ein Zehntel der Größe), die der Mac dafür jedes Mal neu mit `afconvert` erzeugt, das zu macOS gehört (anderswo mit `ffmpeg`, falls installiert). Dauert das länger, als Safari einen Tipp gelten lässt, bleibt ein Fenster mit einem zweiten Knopf **Teilen** stehen. Browser ohne Teilen-Menü für Dateien laden die kleine Datei herunter. Auch sie trägt Titel, Songtext und das Cover des Songs.

**Exportieren** beim Song (auf dem Handy im Menü „…“) macht eine Datei für die Musiksammlung: MP3 (320 kbit/s, braucht `ffmpeg`), M4A (AAC, 256 kbit/s) oder FLAC. Titel, Album (der Songtitel), Jahr, Nummer innerhalb des Laufs, Songtext und Stil samt Seed (als Kommentar) stehen in den Tags des Formats (ID3v2.3, iTunes-Atome, Vorbis-Kommentare). Interpret und Genre fragt der Dialog; der Interpret bleibt pro Browser gespeichert, das Genre schlägt er aus dem Stil vor. **Cover** gibt es an genau einer Stelle: **Cover wählen** beim Song nimmt ein Foto, schneidet es quadratisch zu und speichert es beim Song. Die Bibliothek zeigt es neben dem Song, der Player und der Sperrbildschirm beim Abspielen, und jede Datei, die Tonwerk verlässt, trägt es mitsamt Songtext: Export, Teilen, FLAC-Download (auch einer Fassung), die ZIPs von Song und Lauf und die Kopie des Players. **Cover entfernen** nimmt es wieder weg. Ein Song ohne eigenes Cover bekommt nur beim Export eins, das der Browser aus Songtitel und einem Farbverlauf zeichnet (Farben aus der Song-ID); der Export-Dialog zeigt, welches hineinkommt. Der Songordner von YuE Studio bleibt unberührt: Downloads sind getaggte Kopien. Die Bilder liegen in `covers/` neben der Datenbank, der Songordner bleibt unberührt. Die fertige Datei lässt sich speichern oder, wo der Browser das kann, teilen.

Das Suchfeld über der Bibliothek sucht wahlweise in **Titel & Stil** oder im **Text**. Getrennt, weil fast jeder Songtext Allerweltswörter wie „Nacht“ enthält und die wenigen Treffer im Titel sonst untergingen. Mehrere Wörter müssen alle vorkommen, in beliebiger Reihenfolge; Groß- und Kleinschreibung und Akzente zählen nicht („traume“ findet „Träume“). Was in Anführungszeichen steht, muss genau so vorkommen, etwa eine erinnerte Zeile. Bei der Textsuche stehen die passenden Zeilen mit markierten Treffern unter dem Stil. Der Player spielt nach einem Song die weiteren Treffer.

## Benachrichtigungen

**Benachrichtigungen einschalten** im Zahnrad-Menü oben rechts meldet per Web Push, wenn ein Song fertig ist oder fehlschlägt, eine Transkription endet oder ein Songtext-Entwurf steht, auch bei gesperrtem Handy. Abgebrochene Songs bleiben still. Auf iPhone und iPad geht das nur in der App auf dem Home-Bildschirm (ab iOS 16.4) und nur über HTTPS, also über `tailscale serve`; im normalen Safari-Tab erklärt der Eintrag das. Beim Einschalten kommt eine Test-Nachricht. Jedes Gerät schaltet für sich ein, die Texte kommen in der Sprache, die dort eingestellt ist.

Der Server legt beim ersten Mal ein VAPID-Schlüsselpaar an und speichert es mit den Abonnements in `~/Library/Application Support/YuE UI/push.json` (nur für den eigenen Benutzer lesbar). Diese Datei nicht löschen: Mit einem neuen Schlüssel kommt nichts mehr an, bis jedes Gerät die Benachrichtigungen einmal neu einschaltet. Die Nachrichten gehen über die Push-Dienste von Apple, Google oder Mozilla, der Mac braucht dafür Internet.

## Speicherplatz

Jeder Song belegt mit FLAC, Tokens und Zwischendateien einiges an Platz. Die Bibliothek zeigt deshalb, was jeder Lauf belegt und wie viel auf dem Datenträger noch frei ist. Über den Papierkorb lassen sich einzelne Songs, ganze Läufe und Transkriptionen löschen; mit dem letzten Song eines Laufs verschwindet auch sein Ordner. Gelöscht wird nach einer Rückfrage endgültig und nicht in den Papierkorb von macOS, denn dort würde der Platz erst beim Leeren frei. Songs, an denen der Worker von Tonwerk noch arbeitet, lassen sich erst nach dem Abbrechen löschen; was YuE Studio gerade erzeugt, erkennt Tonwerk nicht.

## YuE Studio und Tonwerk gleichzeitig

Beide haben einen eigenen Worker und damit ein eigenes Modell im Speicher. Rechnen beide gleichzeitig, kann der Speicher knapp werden. Die Oberfläche zeigt deshalb einen Hinweis, solange die App geöffnet ist. **Worker beenden** in der Warteschlange gibt den Speicher von Tonwerk sofort frei. Ansonsten entlädt der Worker das Modell nach zehn Minuten Leerlauf von selbst.

## Stil aus Bausteinen

Unter dem Stil-Feld öffnet **Bausteine** eine Auswahl nach Reitern: Sprache, Genre, Stimme (männlich, weiblich, Duett, Chor, Kinderstimme), Klangfarbe, Instrumente, Stimmung und Tempo. Antippen setzt einen Baustein in den Stil, nochmal Antippen nimmt ihn heraus; selbst geschriebene Begriffe bleiben stehen. Sprache, Stimme und Tempo gibt es nur einmal pro Song, eine neue Wahl ersetzt die alte (beim Tempo auch ein von Hand geschriebenes wie `95 BPM`). Die Sprache kommt nach vorn und das Tempo ans Ende, wie in den Beispielen von YuE2.

Die Auswahl folgt dem [Prompt-Leitfaden von YuE](https://github.com/multimodal-art-projection/YuE/tree/YuE-v1#prompt-engineering-guide): Am stabilsten sind Genre, Instrument, Stimmung, Stimme und Klangfarbe, möglichst alle fünf, mit Begriffen aus seiner Liste der 200 häufigsten Tags (`top_200_tags.json`). YuE2 ergänzt Sprache und Tempo und versteht auch freie Beschreibungen. Duett und Chor stehen nicht in der Liste; wer welche Zeile singt, lässt sich damit nicht festlegen.

## Transkription mit SheetSage2

Unter **Transkription** lässt sich eine Aufnahme hochladen (jedes Format, das macOS lesen kann, bis 300 MB). SheetSage2 macht daraus eine Melodie-Partitur im ABC-Format ohne Akkorde. **Als Partitur übernehmen** setzt sie als eigene Partitur ins Formular und stellt die Planung auf „Nur Melodie“, die Grundlage für ein Cover mit neuem Stil und Text. Jede Transkription landet als Ordner in `~/Music/YuE Studio/transcriptions` (Partitur, MIDI-Spuren, Analyse); die Liste zeigt auch die, die in YuE Studio entstanden sind.

SheetSage2 braucht eine eigene Python-Umgebung und etwa 2 GB Modelle. Tonwerk installiert beides nicht selbst, sondern nutzt die Installation von YuE Studio: dort einmal **Transcribe recording** öffnen und **Install transcription support** wählen. Transkribiert wird auf der CPU, das dauert einige Minuten, immer eine Aufnahme zur Zeit. Weitere Aufnahmen warten in der Warteschlange, ebenso eine, während ein Textentwurf, eine Stimme oder das Sprachlabor den Speicher belegt; Songs laufen neben einer Transkription weiter. Umgekehrt warten Textentwurf, Stimme und Sprachlabor, bis die Transkription fertig ist, weil sie sonst den Worker samt Transkription beenden würden.

## Songtext entwerfen mit LM Studio

YuE2 singt Texte, schreibt aber selbst keine. Über dem Songtext-Feld steht deshalb **Worum geht es?**: Stichwörter oder ein Satz genügen, **Text entwerfen** lässt ein Sprachmodell in [LM Studio](https://lmstudio.ai) auf dem Mac daraus einen Songtext im Format von YuE2 schreiben (Abschnitte wie `[Verse]` und `[Chorus]`, vier Zeilen je Abschnitt, gleichmäßige Silben). **EN** oder **DE** daneben wählt, ob er englisch oder deutsch wird; die Abschnittsmarken bleiben englisch, weil YuE2 sie so liest. Damit YuE2 einen deutschen Text auch deutsch ausspricht, gehört `German` an den Anfang des Stils (Bausteine → Sprache). Der Stil aus dem Formular geht mit, damit Stimmung und Tempo passen. Ein vorhandener Text wird erst nach einer Rückfrage ersetzt. Der Entwurf läuft auf dem Mac weiter, auch wenn das Handy zwischendurch sperrt oder die Seite neu lädt; der Text landet danach im Feld.

Passt ein Entwurf fast, muss er nicht neu gewürfelt werden: Unter dem Songtext steht ein Feld für eine kurze Anweisung wie „Refrain eingängiger“ oder „zweite Strophe trauriger“. Der Knopf daneben schickt den Text, wie er gerade im Feld steht (auch von Hand geändert), mit dieser Anweisung an dasselbe Sprachmodell; es soll nur ändern, worum es gebeten wurde, und den Rest Wort für Wort lassen. Das Ergebnis ersetzt den Text ohne Rückfrage, **Rückgängig** holt den vorherigen zurück, solange niemand am neuen etwas geändert hat.

Statt oder neben den Stichwörtern kann ein **Foto** die Vorlage sein: Die Kamera neben dem Feld nimmt eins auf oder holt es aus der Mediathek. Der Entwurf erzählt dann von dem, was darauf zu sehen ist, Stimmung, Ort, eine mögliche Geschichte; Stichwörter lenken, worauf er achtet. Der Browser verkleinert das Foto vorher auf 1024 Pixel (JPEG, auch aus HEIC), es wird nirgends gespeichert und gilt nur bis zum Neuladen der Seite. Das Modell muss Bilder lesen können, wie Gemma 4; in der Modellauswahl steht bei solchen „sieht Fotos“, und mit einem, das LM Studio als blind meldet, bleibt der Entwurf-Knopf aus.

Darunter lässt sich das Sprachmodell wählen: Die Liste zeigt alle Modelle, die in LM Studio heruntergeladen sind (ohne Embedding-Modelle), mit ihrer Größe, das eingestellte `Lyrics:Model` als „Standard“. Die Größe entspricht etwa dem Speicher, den das Modell braucht. Die Wahl merkt sich der Browser wie den Rest des Formulars; wird das Modell in LM Studio gelöscht, gilt wieder der Standard. Mit nur einem Modell bleibt die Auswahl ausgeblendet.

LM Studio muss dafür nicht geöffnet sein: Antwortet sein Server nicht, startet Tonwerk ihn mit `~/.lmstudio/bin/lms daemon up` und `lms server start`. Tonwerk lädt das Modell (Standard: `google/gemma-4-26b-a4b-qat`) erst für die Anfrage, mit einem Kontext von 30000 Tokens, und entlädt es gleich danach wieder; ein Modell, das in LM Studio schon geladen ist, nutzt es mit und lässt es geladen. Dieses Modell (rund 15 GB) hat einen Mac mit 24 GB zusammen mit den üblichen offenen Apps schon bis zum Einfrieren in den Swap getrieben; vor einem Entwurf also möglichst viel schließen, oder das kleine `google/gemma-4-e4b` eintragen. Weigert sich LM Studio wegen zu wenig Speicher (seine Schutzgrenze „insufficient system resources“, etwa bei dichten Modellen wie Qwen 27B), entlädt Tonwerk zuerst andere noch geladene Modelle und versucht es dann mit halbem Kontext, bis hinunter zu `Lyrics:MinContextLength`; erst dann zeigt die Oberfläche seine Meldung. Die Schutzgrenze selbst bleibt an. Außerdem gilt: Solange YuE2 rechnet, gibt es keinen Entwurf; ein ruhender Worker wird vorher beendet; und solange ein Entwurf entsteht, startet kein Song. Der erste Entwurf dauert durch das Laden des Modells etwas länger.

## Als Logic-Projekt laden

Die Bibliothek zeigt bei jedem Song mit Partitur einen Knopf **Auf der Logic-Seite öffnen**. Er öffnet die Seite **Logic** mit diesem Song und wandelt ihn gleich um, sodass die Vorschau da ist und sich vor dem Herunterladen noch alles einstellen lässt. Tonwerk baut das Projekt selbst, mit der Bibliothek `YueToLogic.Core` (früher der eigene Dienst [yue-to-logic-pro](https://github.com/Marcel-B/yue-to-logic-pro)); der Browser muss die FLAC also nicht erst herunter- und wieder hochladen. Zurück kommt ein ZIP mit dem `.logicx`-Projekt: Audio auf der ersten Spur, Gesang, Instrument und Akkorde als MIDI, Tempo, Takt und Abschnitte aus der Partitur. Hinweise (etwa eine Partitur, die länger ist als das Audio) zeigt die Seite nach dem Download an; wird aus einem Song kein Projekt (eine Partitur, die sich nicht lesen lässt, Audio ohne 48 kHz), steht dort der Grund.

**Die Seite Logic.** Sie nimmt einen Song aus der Bibliothek (Knopf beim Song, oder im Menü auswählen) oder eine eigene `score.abc`, auf Wunsch mit `audio.flac`. Dort lassen sich Spuren dazuerzeugen (Akkorde mit Muster und Umkehrung, Bass, Schlagzeug, Leittöne, Verdopplung), Oktaven, Groove (Swing, Humanize), Einzähler und eine Region je Abschnitt einstellen; Einstellungen lassen sich als Voreinstellung speichern. Die Vorschau zeigt die Noten als Pianoroll und spielt sie ab, in Chrome auf dem Mac auch über die angeschlossenen MIDI-Geräte (Web MIDI gibt es nur in Chromium-Browsern, und nur über HTTPS). Auf der Seite **Instrumente** (unter Werkzeuge) bekommt jedes Gerät einen Namen für seinen MIDI-Ausgang und Kanal, ein Drumcomputer dazu die Noten seiner Trommeln; auf der Seite Logic kann jede Spur eines davon spielen. Die MIDI-Datei, die Vorschau und das Logic-Projekt legen die Spur dann auf dessen Kanal, und im Projekt geht sie über Logics External Instrument an das Gerät, sofern die Vorlage den Ausgang kennt. Instrumente, Zuordnungen und Voreinstellungen liegen in `yueui.db`, Handy und Mac sehen also dieselben.

**Vorschau mit Aufnahme.** Hat der Song eine `audio.flac`, zeigt die Vorschau über den Spuren ihre Wellenform, am Takt ausgerichtet (mit Einzähler beginnt sie nach ihm), und spielt sie beim Abspielen mit; so hört und sieht man vor dem Export, ob Partitur und Aufnahme zusammenpassen. Ein Haken schaltet sie ab (dann wird die FLAC gar nicht erst geladen), ein Regler stellt ihre Lautstärke; beides merkt sich der Browser. Darunter zeigt ein großer Analyzer das Spektrum von allem, was im Browser klingt (Synthesizer und Aufnahme; was an MIDI-Geräte geht, hört der Browser nicht). Der Haken **Analyzer** blendet ihn aus; den Hintergrund zur Musik schaltet der Player, er bewegt sich dann auch zur Vorschau.

**MusicXML.** **MusicXML herunterladen** unter der Umwandlung liefert die Partitur mit denselben Einstellungen als `.musicxml` für MuseScore, Dorico oder eine Noten-App auf dem iPad: eine Notenzeile je Spur, die Akkordsymbole, Abschnitte und das Tempo über der ersten. Schlagzeug bleibt draußen; Noten, die über den Taktstrich oder in andere hineinreichen, werden übergebunden, die Notenprogramme ordnen sie beim Öffnen neu.

**Die Seite Instrumente.** Hier liegen die MIDI-Instrumente des Studios und die gespeicherten Browser-Klänge (Analog und FM): anlegen, bearbeiten, unter einem Namen sichern, löschen. Eine kleine Klaviatur über zwei Oktaven spielt den Klang im Editor, während er sich ändert, oder in Chrome ein MIDI-Instrument (▶ in seiner Zeile); am Computer auch mit den Tasten A bis K wie Logics Musical Typing, Z und X wechseln die Oktave. Welche Spur was spielt, wird weiter auf der Seite Logic gewählt. Über den Reglern zeigt ein Oszilloskop die Wellenform dessen, was gerade klingt (die Klaviatur), sodass man Pulsbreite, Filter oder den Index eines Modulators auch sieht. Unter **Effekte** hat jeder Klang ein Delay (Anteil, Zeit, Rückkopplung, Klangfarbe der Wiederholungen) und einen Hall (Anteil, Nachhall); sie gehören zum Klang, wirken in der Vorschau auf seine ganze Spur und klingen nach dem Loslassen aus. Bei 0 % sind sie aus. Ins Logic-Projekt kommen sie nicht, wie der ganze Browser-Klang.

**Klang im Browser.** In der Tabelle „Ausgänge je Spur“ der Seite Logic wählt jede Spur in einem Feld, was sie spielt: den Standardklang ihrer Art (beim Schlagzeug das Schlagzeug im Browser), einen gespeicherten Browser-Klang oder, in Chrome, ein MIDI-Instrument; Ausgang und Kanal kommen vom Instrument und werden nicht mehr von Hand gewählt. In Safari stehen keine MIDI-Instrumente zur Wahl (ein schon zugeordnetes bleibt stehen und klingt dort im Browser). Die Klänge selbst entstehen auf der Seite **Instrumente**: der analoge Synthesizer mit zwei Oszillatoren (Wellenform, Oktave, Verstimmung, Pegel; beim Puls auch Pulsbreite und PWM aus LFO und den beiden Hüllkurven) und Rauschen, Filter (Tiefpass, Hochpass, Bandpass mit Frequenz, Resonanz, Hüllkurve, Keytracking), je einer ADSR-Hüllkurve für Lautstärke und Filter und einem LFO; oder ein **FM**-Synthesizer wie Yamahas DX- und TX-Geräte: vier Operatoren mit je Frequenzverhältnis, Verstimmung, Pegel, Anschlagsdynamik und eigener Hüllkurve, acht Algorithmen, Feedback auf Operator 4 und ein LFO auf Tonhöhe, Modulationstiefe oder Lautstärke. Eine Spur folgt ihrem gespeicherten Klang: wer ihn dort ändert, hört das auf der Seite Logic sofort, auch während der Song läuft. Die Wahl liegt je Spur (nach ihrem Namen, also für jeden Song) in `yueui.db`. Der Synthesizer ist nur zum Anhören im Browser; MIDI-Datei und Logic-Projekt bleiben davon unberührt.

**Mixer.** Unter der Tabelle steht ein Mixer mit einem Kanalzug je Spur: Lautstärke (bis +6 dB), Panorama, Mute, Solo und eine Pegelanzeige, dazu der Master. Lautstärke und Panorama gelten nur für den Ton im Browser und werden sofort hörbar; Mute und Solo entscheiden, welche Spuren überhaupt spielen, auch auf MIDI-Geräten. Ein Klick auf den Wert unter dem Regler setzt ihn zurück, einer auf die rot leuchtende Pegelspitze löscht sie. Lautstärken und Panorama liegen in `yueui.db`, Solo gilt nur bis zum Neuladen.

**Instrumente aus yue-to-logic-pro übernehmen.** Solange der alte Server noch läuft, holt dieser Befehl (etwa auf dem Proxmox-Host, der beide erreicht) seine Instrumente, Zuordnungen und Voreinstellungen und gibt sie Tonwerk; zweimal ausgeführt ändert er nichts:

```sh
Y=http://192.168.2.73:8080/api
{ printf '{"instruments":'; curl -s $Y/instruments; printf ',"assignments":'; curl -s $Y/instruments/assignments; printf ',"presets":'; curl -s $Y/presets; printf '}'; } > ytl.json
curl -s -X POST -H 'Content-Type: application/json' --data @ytl.json https://<mac>.<tailnet>.ts.net:8443/api/logic/import
```

**Begleitstimmen.** Das Werkzeug **Begleitstimmen** (unter Werkzeuge) nimmt eine Melodie als MIDI-Datei (oder `score.abc`, oder einen Song der Bibliothek) und gibt eine MIDI-Datei mit einer Spur je Stimme zurück, nach einer Vorschau zum Anhören; mit der Logic-Vorlage hat es nichts zu tun. Dieselben Stimmen gibt es unter „Begleitstimmen“ auf der Seite Logic, zusammen mit allen anderen Optionen. Es entstehen aus der Gesangsmelodie weitere Stimmen, jede auf einer eigenen Spur: Terz darüber, Terz darunter, Sexte darunter (parallel in der Tonleiter, also mal große, mal kleine Terz; wo die Melodie auf einem Akkordton steht und die Parallele aus dem Akkord fiele, nimmt die Stimme den nächsten Akkordton) sowie Alt, Tenor und Bass eines vierstimmigen Chorsatzes mit der Melodie als Sopran (Akkordtöne darunter, möglichst kleine Schritte, Terz nicht weglassen, keine Quint- und Oktavparallelen) und ein **Bordun** (Drone): der Grundton der Tonart unter jeder Phrase, im Rhythmus der Melodie oder als „Bordun liegend“ gehalten. Alle anderen singen im Rhythmus der Melodie. Die Akkorde kommen aus der Partitur; hat sie keine, rät Tonwerk sie je halbem Takt aus der Melodie. Die Tonart ist die der Partitur, solange die Melodie dazu passt, sonst die, nach der die Melodie klingt, oder die im Feld „Tonart“ gewählte. „Nur im Refrain“ beschränkt das auf Abschnitte namens `chorus` oder `Refrain`. Statt einer `score.abc` lässt sich auch eine **MIDI-Datei** hochladen, etwa eine Melodie aus Logic (eine unbenannte Spur gilt als Gesang); nach „Konvertieren“ lädt der Knopf der MIDI-Datei die Stimmen herunter, und die Vorschau spielt sie. Ins Logic-Projekt kommen sie nur, wenn die Vorlage Spuren dieser Namen hat, sonst nur in die MIDI-Datei. Gesungen werden sie (noch) nicht.

**Zurück aus Logic.** Wer den Song in Logic weiterbearbeitet hat (Melodie, Akkorde, Tempo), wählt dort alle MIDI-Regionen aus und exportiert sie als MIDI-Datei (*Ablage → Exportieren → Auswahl als MIDI-Datei*); die Spurnamen `Vocal`, `Ins` und `Chords` müssen bleiben, an ihnen erkennt Tonwerk die Stimmen. Der Knopf **MIDI aus Logic als Partitur** beim Song macht daraus wieder eine `score.abc`; die steht dann mit Stil, Text und Seed des Songs im Formular, bereit für einen neuen Song. Dasselbe geht ohne Song über den Knopf neben dem Partiturfeld unter „Erweiterte Parameter“, etwa für eine Melodie, die in Logic entstanden ist. Spuren, die sich nicht zuordnen lassen, nennt die Oberfläche als Hinweis.

**Tonart ändern.** Ist der Gesang zu hoch oder zu tief, verschieben **−** und **+** unter dem Partiturfeld die ganze Partitur um je einen Halbton: Melodie, Begleitung, Akkordsymbole und Tonart (`K:`), in der neuen Tonart neu notiert. Daneben lässt sich die Tonart auch direkt wählen; die Partitur wandert dann auf dem kürzeren Weg dorthin (höchstens sechs Halbtöne tiefer oder fünf höher), − und + gehen von da aus weiter. Die Auswahl hat alle Dur- und Molltonarten: Wer von D-Dur nach d-Moll (oder umgekehrt) wechselt, bekommt die Noten umgeschrieben, Terz, Sexte und Septime einen Halbton tiefer (natürliches Moll), die Akkorde passend dazu (C wird Cm, G7 wird Gm7). Das ist eine einfache Näherung, klingt aber nach Moll. Der Umfang der Stimme `Vocal` steht dabei (etwa „Gesang A3–A4“). Ein Song mit anderer Tonart klingt auch mit gleichem Seed anders, denn die Partitur ist Teil der Eingabe. YuE2 singt diese Stimme in der notierten Lage; wie tief eine Stimme klingen kann, hängt aber auch vom Stil ab („male vocal“, „deep voice“). Das Transponieren passiert im Browser und braucht keinen Server.

## Mit anderer Stimme singen

YuE2 singt mit einer Stimme, die es selbst wählt. Mit [ChangeMyVoice](https://github.com/Marcel-B/ChangeMyVoice) (Seed-VC) und StemMyWav (Stem-Trennung), die beide auf demselben Mac laufen, singt Tonwerk einen fertigen Song mit einer Referenzstimme neu:

1. StemMyWav trennt `audio.flac` in trockenen Gesang, Hall und Instrumental.
2. ChangeMyVoice singt den trockenen Gesang mit der gewählten Stimme neu, auf Wunsch eine Oktave höher oder tiefer.
3. ffmpeg mischt die neue Stimme auf den Pegel der alten zurück unter das Instrumental, auf Wunsch mit dem Hall des Originals.

Das Ergebnis ist eine **Fassung** des Songs: eine FLAC im Datenordner von Tonwerk (`versions/` neben `yueui.db`), nicht im Song-Ordner von YuE Studio. Die Bibliothek zeigt die Fassungen unter dem Song, zum Abspielen, Herunterladen und Löschen; wird der Song gelöscht, gehen seine Fassungen mit. Gestartet wird sie über den Knopf **Mit Stimme singen** beim Song (Stimme, Tonlage, Stärke der Stimme, Qualität, Hall). Ist sie fertig, kommt eine Benachrichtigung.

Soll ein neuer Song gleich mit einer Stimme gesungen werden, wählt man sie schon im Formular unter **Danach mit Stimme singen** (mit Tonlage, Stärke und Qualität; der Hall des Originals bleibt wie im Dialog). Jeder Song des Laufs kommt dann, sobald er fertig ist, von selbst als Fassung in die Reihe; die Warteschlange zeigt die Stimme am Auftrag und am Song. Eine Neuberechnung (etwa ein Entwurf in voller Qualität) macht keine zweite Fassung.

Die Seite **Stimmen** zeigt die Sammlung von ChangeMyVoice (dieselbe wie in yue-to-logic-pro): anhören, löschen und neue Aufnahmen hochladen. Am besten 10 bis 25 Sekunden trockener Gesang ohne Musik; ChangeMyVoice behält nur die ersten 25 Sekunden. Darunter stehen die Fassungen, die gerade entstehen.

Darunter trennt **Stems** einen Song mit StemMyWav in seine Spuren: Song und Modell wählen (die Liste kommt von StemMyWav, sonst nur `Voice:StemModel`), auf Wunsch den Hall vom Gesang trennen, **Trennen**. Das wartet wie eine Fassung, bis YuE2 und das Textmodell den Speicher freigeben, und meldet sich per Benachrichtigung. Jede Spur lässt sich mit ihrer Wellenform anhören (ein Tipp in die Wellenform springt dorthin, beim Wechsel zu einer anderen Spur desselben Songs läuft die Zeit weiter) und als FLAC herunterladen. Die Dateien liegen in `stems/` neben der Datenbank, nicht im Songordner, und gehen mit dem Song. In der Bibliothek führt **In Stems trennen** beim Song direkt dorthin.

Ein Song braucht grob 10 bis 20 Minuten. Der Mac hat nicht genug Speicher für YuE2, Stem-Trennung und Seed-VC zugleich, deshalb geht immer nur eine Fassung, und erst wenn YuE2 nichts rechnet und kein Textentwurf läuft; ein untätiger YuE-Worker wird vorher beendet. Solange eine Fassung entsteht, warten neue Songs, Neuberechnungen und Textentwürfe in der Warteschlange.

**Einrichtung.** ChangeMyVoice braucht einen eigenen Schlüssel für Tonwerk (`scripts/neuer-zugang.sh` im ChangeMyVoice-Repository) und muss Anfragen von der Adresse zulassen, von der Tonwerk kommt (bei `Voice:BaseUrl` über die Tailscale-Adresse des Macs ist es diese). Den Schlüssel liest Tonwerk aus `Voice:ApiKey` oder einer Datei (`Voice:ApiKeyFile`). Der Schlüssel für StemMyWav liegt schon in `~/.config/stemmywav/mac-api-key`, dort sucht Tonwerk ihn von selbst. Die Tonlage braucht ChangeMyVoice mit Halbton-Versatz (`setup-inference.sh --with-f0`). Ohne `Voice:BaseUrl` gibt es weder die Seite noch den Knopf; ohne StemMyWav nur die Seite.

## Sprachlabor

YuE2 kann nur singen. Das **Sprachlabor** probiert aus, welches lokale Sprachmodell (Text-to-Speech über [mlx-audio](https://github.com/Blaizzy/mlx-audio)) einen Text natürlich spricht, etwa für einen Podcast:

1. Unter **Stimme** einen Satz vorlesen und aufnehmen (oder eine Datei wählen) und speichern. Der Wortlaut steht im Feld darüber, denn mehrere Modelle brauchen ihn. Der Server schneidet die Stille davor und danach ab und behält höchstens 30 Sekunden; 5 bis 15 sind am besten.
2. Einen Text eingeben, ein oder mehrere Modelle ankreuzen und **Sprechen**. Jedes Modell spricht den Text nacheinander mit der gewählten Stimme oder, mit **Stimme des Modells**, mit einer eigenen.
3. Die Ergebnisse stehen darunter zum Anhören und Herunterladen (WAV), mit Ladezeit, Sprechzeit und Speicherbedarf zum Vergleichen.

Angeboten werden Chatterbox Multilingual v3 (MIT), Qwen3-TTS 1.7B (Apache 2.0), Higgs Audio v2 (Apache 2.0), Higgs Audio v3 (nur für Forschung und nicht-kommerzielle Nutzung; Podcasts erlaubt, wenn Boson AI genannt wird) und MOSS-TTS Local v1.5 (Apache 2.0). Higgs Audio v3 steht zweimal in der Liste: einmal mit den Werten seiner Modellkarte (Temperatur 0.8, top-k 50) und einmal vorsichtiger (0.7, top-k 20), wobei jedes Stück eines langen Textes neben der Aufnahme das vorige als zweite Referenz bekommt, um nebeneinander zu hören, welches weniger sirrt und die Stimme ruhiger hält. Alle können Deutsch und klonen Stimmen. Die Liste lässt sich unter `Speech:Models` ersetzen (je Modell `id`, `label`, `repo`, `langCode`, `options`, `chunkCharacters` usw., siehe `Speech/SpeechModels.cs`). Jedes Modell sampelt mit den Werten, die seine Entwickler empfehlen, sofern `options` keine anderen nennt.

Chatterbox und die beiden Higgs-Modelle sprechen am Stück höchstens etwa 40 Sekunden und kommen bei langen Absätzen ins Rauschen. Ihnen gibt das Labor einen längeren Text in Stücken von höchstens 300 Zeichen, an Satzenden getrennt, und fügt die Stücke mit einer kurzen Pause zusammen. Eine Zeile ohne Satzzeichen am Ende, etwa eine Überschrift, bekommt dabei einen Punkt, sonst lassen die Modelle sie aus. Ohne gewählte Stimme klonen die Stücke nach dem ersten dessen Stimme, damit nicht jedes Stück anders klingt.

Ein Modell wird für jedes Ergebnis geladen und danach wieder freigegeben. Es wartet wie eine Fassung, bis YuE2, das Textmodell und Stimmumwandlungen den Speicher freigeben, und so lange warten diese. Die Seite **Warteschlange** listet laufende und wartende Sprachtests unter **Sprachlabor** (mit Abbrechen) und zeigt dann eine vierte Kachel **Sprache**. Beim ersten Mal lädt sich ein Modell herunter (3 bis 9 GB), das dauert.

**Einrichtung.** Das Labor braucht eine eigene Python-Umgebung mit mlx-audio, getrennt von der von YuE Studio. Dazu kommt PyTorch (etwa 1 GB), das nur Higgs Audio v3 braucht: Es liest seinen Audio-Codec über PyTorch. Fehlt ein Paket, sagt der Sprachtest das und nennt das Skript, das es nachinstalliert. Einmal auf dem Mac im Repository ausführen, nachdem `deploy/install.sh` gelaufen ist:

```sh
deploy/install-speech.sh          # Umgebung anlegen bzw. mlx-audio aktualisieren und Fehlendes nachinstallieren
deploy/install-speech.sh --test   # danach jedes Modell einen Testsatz sprechen lassen; lädt alle herunter (etwa 28 GB)
```

Umgebung und Modelle liegen unter `~/Library/Application Support/YuE UI/speech/` (`env/`, `models/`), Aufnahmen und Ergebnisse daneben in `speech/voices/` und `speech/takes/`. Das Skript nimmt `uv`, wenn es installiert ist, sonst ein Python ab 3.10 (das von macOS ist 3.9, dann `brew install python@3.12`). Für die Aufnahmen braucht der Server `ffmpeg`.

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
| `Lyrics:MinContextLength` (`Lyrics__MinContextLength`) | `8192` | Kleinster Kontext, auf den Tonwerk zurückgeht, wenn LM Studio das Modell wegen zu wenig Speicher ablehnt |
| `Lyrics:ApiToken` (`Lyrics__ApiToken`) | – | nur nötig, wenn in LM Studio „Require Authentication“ an ist |
| `Lyrics:Lms` (`Lyrics__Lms`) | `~/.lmstudio/bin/lms` | Kommandozeilenwerkzeug, mit dem Tonwerk den Server von LM Studio startet |
| `Logic:SplitSections` (`Logic__SplitSections`) | `false` | eine Region je Songabschnitt statt einer je Spur |
| `Queue:BundleWindow` (`Queue__BundleWindow`) | `00:20:00` | so lange dürfen Songs für YuE2 an einem wartenden Textentwurf oder einer wartenden Fassung vorbei; `00:00:00` hält die Reihenfolge streng ein |
| `Voice:BaseUrl` (`Voice__BaseUrl`) | – | ChangeMyVoice ohne `/api`, z. B. `http://100.93.85.52:5080`; leer schaltet Stimmen und Fassungen ab |
| `Voice:ApiKey` (`Voice__ApiKey`) | – | Schlüssel für ChangeMyVoice (`X-Api-Key`) |
| `Voice:ApiKeyFile` (`Voice__ApiKeyFile`) | – | oder eine Datei, in der er steht |
| `Voice:StemsBaseUrl` (`Voice__StemsBaseUrl`) | `http://127.0.0.1:5081` | API von StemMyWav auf dem Mac (nicht das Gateway) |
| `Voice:StemsApiKey` (`Voice__StemsApiKey`) | – | Schlüssel für StemMyWav; ohne ihn gilt `Voice:StemsApiKeyFile` |
| `Voice:StemsApiKeyFile` (`Voice__StemsApiKeyFile`) | `~/.config/stemmywav/mac-api-key` | Datei mit dem Schlüssel für StemMyWav |
| `Voice:StemModel` (`Voice__StemModel`) | `mel-roformer-kim-vocals` | Trennmodell von StemMyWav |
| `Speech:Root` (`Speech__Root`) | `~/Library/Application Support/YuE UI/speech` | Ordner des Sprachlabors (Python-Umgebung und Modelle) |
| `Speech:Python` (`Speech__Python`) | `<Root>/env/bin/python` | Python mit mlx-audio |
| `Speech:ModelCache` (`Speech__ModelCache`) | `<Root>/models` | Hugging-Face-Cache der Sprachmodelle (`HF_HOME`) |
| `Speech:Timeout` (`Speech__Timeout`) | `01:00:00` | so lange darf ein Ergebnis samt erstem Download dauern |
| `Push:DataPath` (`Push__DataPath`) | `~/Library/Application Support/YuE UI/push.json` | VAPID-Schlüssel und Abonnements für Benachrichtigungen |
| `Data:Path` (`Data__Path`) | `~/Library/Application Support/YuE UI/yueui.db` | SQLite-Datenbank von Tonwerk (Playlists, geänderte Titel, Bewertungen) |
| `Push:Subject` (`Push__Subject`) | `https://github.com/Marcel-B/YuE-UI` | Kontaktadresse (`mailto:` oder `https:`) für die Push-Dienste; Apple lehnt Adressen wie `mailto:ich@localhost` ab |

## API

| Methode | Pfad | |
|---|---|---|
| `GET` | `/api/status` | Worker-Zustand, Songs in Arbeit, Protokoll |
| `GET` | `/api/events` | dasselbe live als Server-Sent Events (`snapshot`, `song`, `worker`, `log`, `library`, `transcription`, `lyrics`, `version`, `speech`, `queue`, `ping`) |
| `POST` | `/api/generate` | neuer Lauf: `{ style, lyrics, title?, batch?, quality?: "draft"\|"full", cot?, seed?, instrumental?, engines?, draftSteps?, maxTokens?: 200–15000, abc?, fullSteps?: 1–64, abcSampling?, semanticSampling?, voice? }`; `voice` ist `{ voiceId, semiToneShift?, strength?, diffusionSteps?, keepReverb? }` wie bei den Fassungen und singt jeden fertigen Song danach mit dieser Stimme (`400` für eine unbekannte Stimme, `501` ohne Einrichtung); `abc` braucht `cot` "full" oder "melody", Sampling ist `{ temperature?, topP?, topK?, repetitionPenalty?, penaltyWindow? }`, über 9000 Tokens, `fullSteps` und Sampling nur mit der Worker-Erweiterung; antwortet `202`, ohne Inhalt, wenn der Lauf an den Worker ging, sonst mit dem wartenden Auftrag `{ id, kind, title, createdAt, songId, batch, quality, revision, voiceLabel }` |
| `POST` | `/api/songs/{run}/{song}/render` | Song aus seinen Tokens neu synthetisieren, z. B. einen Entwurf in voller Qualität; `202` wie bei `generate` |
| `GET` | `/api/queue` | wartende Aufträge in Reihenfolge (auch im Snapshot und als `queue`-Event) |
| `DELETE` | `/api/queue/{id}` | wartenden Auftrag herausnehmen; `404`, wenn er nicht (mehr) wartet |
| `POST` | `/api/queue/{id}/move` | `{ offset }`: wartenden Auftrag um so viele Plätze verschieben, negativ nach vorn |
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
| `GET` | `/api/songs/{run}/{song}/audio` | FLAC (Range-fähig; `?download=true` als Download, eine Kopie mit Titel, Songtext und Cover in den Tags) |
| `GET` | `/api/songs/{run}/{song}/score` | `score.abc` |
| `GET` | `/api/songs/{run}/{song}/stream` | Was der Player spielt: AAC-Kopie mit 192 kbit/s (Range-fähig, beim ersten Mal erzeugt), ohne Encoder die FLAC |
| `GET` | `/api/songs/{run}/{song}/share` | Song als kleine AAC (`.m4a`, 128 kbit/s) zum Teilen, mit Titel, Songtext und Cover; `501`, wenn weder `afconvert` noch `ffmpeg` da ist |
| `POST` | `/api/songs/{run}/{song}/export` | Song als MP3, M4A oder FLAC mit Tags und Cover (Formular: `format` = `mp3`/`m4a`/`flac`, `artist`, `genre`, `cover` als JPEG/PNG bis 5 MB, ohne `cover` das eigene Cover des Songs); `501`, wenn der Encoder fehlt |
| `GET` | `/api/songs/{run}/{song}/cover` | Das eigene Cover des Songs; `404` ohne |
| `PUT` | `/api/songs/{run}/{song}/cover` | Setzt das Cover (Formular: `cover` als JPEG/PNG bis 5 MB) |
| `DELETE` | `/api/songs/{run}/{song}/cover` | Zurück zum gezeichneten Cover |
| `GET` | `/api/songs/{run}/{song}/zip` | FLAC (getaggt wie der Download) und ABC des Songs als ZIP |
| `GET` | `/api/runs/{run}/zip` | FLAC (getaggt) und ABC aller Songs des Laufs als ZIP |
| `GET` | `/api/songs/{run}/{song}/logic` | Song als Logic-Projekt (ZIP mit `.logicx`), Hinweise im Header `X-YueToLogic-Diagnostics`; `422`, wenn daraus kein Projekt wird, mit dem Grund |
| `POST` | `/api/logic/convert` | Multipart: `song` (`<run>/songN`) oder `file` (`score.abc` oder MIDI-Datei, die erst zur Partitur wird), dazu `options` (ConversionOptions als JSON): die umgewandelte Partitur mit Noten und MIDI-Datei (Base64) für die Seite Logic; `422` mit den Diagnosen, wenn sie sich nicht lesen lässt |
| `POST` | `/api/logic/musicxml` | Multipart wie oben, dazu `name`: die Partitur als MusicXML (`.musicxml`, ohne Schlagzeug); `422` mit den Diagnosen |
| `POST` | `/api/logic/export` | Multipart wie oben, dazu `audio` (FLAC, nur mit `file`), `name`, `splitSections`, `instruments` (Spur → Name, Port, Kanal als JSON): Logic-Projekt als ZIP, Hinweise im Header; `422` mit den Diagnosen |
| `GET` / `PUT` / `DELETE` | `/api/logic/presets[/{name}]` | Voreinstellungen der Seite Logic (`PUT` mit `{ form }`) |
| `GET` / `PUT` / `DELETE` | `/api/logic/synths/presets[/{name}]` | Gespeicherte Klänge des Browser-Synthesizers (`PUT` mit `{ patch }`) |
| `GET` / `PUT` / `DELETE` | `/api/logic/synths/tracks[/{track}]` | Klang je Spur (`PUT` mit `{ patch, preset }`; `DELETE` gibt der Spur den Standardklang zurück) |
| `GET` / `PUT` | `/api/logic/synths/mixer` | Mixer der Vorschau (`PUT` mit `{ settings }`: Master und Lautstärke/Panorama je Spur) |
| `POST` | `/api/logic/import` | Instrumente, Zuordnungen und Voreinstellungen aus yue-to-logic-pro übernehmen (`{ instruments, assignments, presets }`, wie dessen `GET`-Routen sie liefern) |
| `GET` / `POST` | `/api/instruments` | Instrumente der Seite Logic (Name, Port, Kanal, `kind` `Synth` oder `DrumMachine`, `drums`); `409` bei doppeltem Namen |
| `PUT` / `DELETE` | `/api/instruments/{id}` | Instrument ändern oder löschen (mit seinen Zuordnungen) |
| `GET` / `PUT` | `/api/instruments/assignments[/{track}]` | Welche Spur welches Instrument spielt (`PUT` mit `{ instrumentId }`, `null` nimmt es weg) |
| `POST` | `/api/midi/abc` | Multipart-Feld `file`: MIDI-Datei (bis 4 MB), zurück in eine Partitur gewandelt: `{ abc, warnings }`; `422`, wenn keine Partitur daraus wird, `413` für zu große Dateien |
| `DELETE` | `/api/songs/{run}/{song}` | Song löschen, mit dem letzten auch den Lauf; `409`, solange der Worker daran arbeitet |
| `PUT` | `/api/songs/{run}/{song}/rating` | Song bewerten (`{"rating": 1…5}`, `null` oder `0` nimmt die Bewertung weg); `400` außerhalb von 1 bis 5 |
| `PUT` | `/api/runs/{run}/title` | Lauf umbenennen (`{"title": "…"}`, leer = ursprünglicher Titel); Ordner und Song-IDs bleiben |
| `DELETE` | `/api/runs/{run}` | Lauf mit allen Songs löschen; `409`, solange der Worker an einem davon arbeitet |
| `GET` | `/api/storage` | `{ freeBytes, totalBytes }` des Datenträgers der Bibliothek |
| `GET` | `/api/lyrics/models` | Modelle für Textentwürfe: `{ default, models: [{ id, name, sizeBytes, loaded, vision }] }` (`vision`: kann Fotos lesen, `null`, wenn der Server es nicht sagt); startet den Server von LM Studio bei Bedarf, `503`, wenn er nicht erreichbar ist |
| `POST` | `/api/lyrics` | Songtext entwerfen: `{ keywords, style?, language?, model?, image? }` (ohne `model` das eingestellte; `image` ein Foto als `data:image/jpeg;base64,…`-URL, dann ist `keywords` optional); mit `lyrics` und `instruction` statt dessen einen vorhandenen Text wie angewiesen überarbeiten (`keywords` optional, kein `image`); antwortet `202` mit `{ id, stage }`, der Entwurf kommt als `lyrics`-Event (`writing`, dann `done` mit `lyrics` oder `failed` mit `message`); solange YuE2 rechnet oder schon ein Entwurf entsteht, wartet er in der Warteschlange (`stage` ist dann `queued`) |
| `GET` | `/api/voice` | `{ voicesConfigured, conversionConfigured }`: ob ChangeMyVoice bzw. dazu StemMyWav eingerichtet ist |
| `GET` | `/api/voices` | Referenzstimmen von ChangeMyVoice: `[{ id, label, seconds, createdAt }]` |
| `POST` | `/api/voices` | Stimme anlegen: Formular mit `label` und `file` (Audio bis 64 MB) |
| `GET` | `/api/voices/{id}/audio` | Aufnahme der Stimme, wie ChangeMyVoice sie behalten hat |
| `DELETE` | `/api/voices/{id}` | Stimme löschen; `409`, solange ein Auftrag von ChangeMyVoice sie braucht |
| `POST` | `/api/songs/{run}/{song}/versions` | Song mit einer Stimme neu singen: `{ voiceId, semiToneShift?: -24–24, strength?: 0–1, diffusionSteps?: 10–100, keepReverb? }`; antwortet `202`, der Fortschritt kommt als `version`-Event (`queued`, `separating`, `converting`, `mixing`, dann `done` oder `failed`); `400` für eine unbekannte Stimme, `501` ohne Einrichtung |
| `GET` | `/api/songs/{run}/{song}/versions/{id}/audio` | fertige Fassung als FLAC (Range-fähig; `?download=true` als getaggter Download wie beim Song) |
| `GET` | `/api/songs/{run}/{song}/versions/{id}/stream` | fertige Fassung als AAC-Kopie für den Player, wie beim Song |
| `DELETE` | `/api/songs/{run}/{song}/versions/{id}` | Fassung abbrechen oder löschen |
| `GET` | `/api/speech` | Sprachlabor: `{ installed, python, ffmpegInstalled, models, voices, takes }` (Modelle mit `downloaded`, Ergebnisse neueste zuerst) |
| `POST` | `/api/speech/voices` | Stimme aufnehmen: Formular mit `label`, `transcript` (Wortlaut) und `file` (jedes Format, das ffmpeg liest, bis 50 MB); `400` bei weniger als 2 Sekunden Sprache |
| `GET` / `DELETE` | `/api/speech/voices/{id}[/audio]` | Aufnahme als WAV (24 kHz mono) bzw. löschen |
| `POST` | `/api/speech/takes` | `{ text, voiceId?, models }`: ein Ergebnis je Modell, antwortet `202` mit ihnen; der Fortschritt kommt als `speech`-Event (`queued`, `loading`, `speaking`, dann `done` oder `failed` mit `message`); `501` ohne mlx-audio |
| `GET` | `/api/speech/takes/{id}/audio` | fertiges Ergebnis als WAV (`?download=true` als Download) |
| `DELETE` | `/api/speech/takes/{id}` | Ergebnis abbrechen oder löschen |
| `GET` | `/api/playlists` | `[{ id, name, songIds }]`: alle Playlists, älteste zuerst, Songs in Reihenfolge (`run/songN`), gelöschte weggelassen |
| `POST` | `/api/playlists` | `{ name }`: neue, leere Playlist (`201`) |
| `PUT` | `/api/playlists/{id}/name` | `{ name }`: umbenennen (1 bis 100 Zeichen) |
| `PUT` | `/api/playlists/{id}/songs` | `{ songIds }`: Songs der Playlist ersetzen; `400` für einen Song, den es nicht gibt |
| `DELETE` | `/api/playlists/{id}` | Playlist löschen (die Songs bleiben); `409` für die letzte |
| `GET` | `/api/push` | `{ publicKey }`: VAPID-Schlüssel für `pushManager.subscribe` |
| `POST` | `/api/push/subscriptions` | Browser benachrichtigen: `PushSubscription.toJSON()` plus `language` (`de`/`en`) |
| `DELETE` | `/api/push/subscriptions` | `{ endpoint }`: Abonnement entfernen |
| `POST` | `/api/push/test` | `{ endpoint }`: Test-Nachricht an genau diesen Browser; `404`, wenn er nicht abonniert ist |

OpenAPI unter `/api/openapi`.

## Roadmap

Ideen und geplante Änderungen, ohne feste Reihenfolge. Erledigtes abhaken oder löschen.

- [x] Suche in der Bibliothek: nach Titel und im Volltext (Style und Lyrics)
- [x] Songs bewerten (1 bis 5 Sterne)
- [x] Bibliothek sortieren (Datum, Sterne, Dauer)
- [x] Songtext-Entwurf mit einer kurzen Anweisung überarbeiten lassen
- [x] Songs vom Handy teilen (kleine AAC statt FLAC)
- [x] Export als MP3, M4A oder FLAC mit Songtext, Cover, Titel und Interpret
- [x] Cover bleibt beim Song (Bibliothek, Player, Sperrbildschirm, Export)
- [x] Mehrere Playlists
- [x] Songs mit einer anderen Stimme neu singen (Stem-Trennung, Seed-VC, Remix) und Stimmen hochladen
- [x] Gemeinsame Warteschlange: Songs, Neuberechnungen und Textentwürfe warten auf den Speicher statt abgelehnt zu werden
- [x] Warteschlange nach Modell bündeln (gleiche Arten zusammen, solange nichts zu lange wartet)
- [x] yue-to-logic-pro aufgenommen: Logic-Export im eigenen Prozess, Seite Logic mit Optionen, Instrumenten und Web MIDI
- [x] Synthesizer im Browser für die Vorschau der Seite Logic, Klang je Spur und gespeicherte Klänge
- [x] Mixer für die Vorschau im Browser
- [x] Oszilloskop, Delay und Hall für die Browser-Synthesizer
- [x] Vorschau mit Aufnahme und Wellenform, MusicXML-Export
- [x] Spektrum-Analyzer im Player und in der Vorschau, Hintergrund zur Musik
- [x] Stimme schon im Formular wählen; die Fassung entsteht dann von selbst, sobald der Song fertig ist
- [ ] Eine Fassung statt der Originalstimme ins Logic-Projekt
- [x] PrimeVue-Importe optimieren (nur benötigte Komponenten, kleineres Bundle)
- [x] Restliche Oberfläche auf PrimeVue umstellen (`.button`, `.card`, `.link` und Eingabefelder aus `style.css` ablösen)
