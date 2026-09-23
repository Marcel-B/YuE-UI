# YuE UI

Eine Web-Oberfläche für [YuE Studio](https://github.com/multimodal-art-projection/YuE), die sich vom Handy oder Laptop aus bedienen lässt, zum Beispiel über Tailscale. Songs erzeugen, den Fortschritt live verfolgen, Entwürfe in voller Qualität rendern, anhören und herunterladen.

YuE UI bringt kein eigenes Modell mit. Es startet den Worker von YuE Studio (`src/tools/yue2_worker.py`) mit der Python-Umgebung, die die App installiert hat, und spricht ihn über sein JSON-Zeilen-Protokoll auf stdin/stdout an. Die Songs landen deshalb im selben Ordner wie die der App (`~/Music/YuE Studio`), und beide sehen dieselbe Bibliothek.

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
dotnet run --project src/YueUI.Api     # API auf 127.0.0.1:5090, SpaProxy startet Vite auf 127.0.0.1:5174/ui/
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

## YuE Studio und YuE UI gleichzeitig

Beide haben einen eigenen Worker und damit ein eigenes Modell im Speicher. Rechnen beide gleichzeitig, kann der Speicher knapp werden. Die Oberfläche zeigt deshalb einen Hinweis, solange die App geöffnet ist. **Worker beenden** in der Warteschlange gibt den Speicher von YuE UI sofort frei. Ansonsten entlädt der Worker das Modell nach zehn Minuten Leerlauf von selbst.

## Konfiguration

`appsettings.json` bzw. Umgebungsvariablen:

| Schlüssel | Standard | Bedeutung |
|---|---|---|
| `Urls` | `http://127.0.0.1:5090` | Adresse des Servers |
| `Yue:InstallRoot` (`Yue__InstallRoot`) | `~/Library/Application Support/YuE Studio` | Installation von YuE Studio (`env/`, `src/`, `models/`) |
| `Yue:OutputDir` (`Yue__OutputDir`) | `~/Music/YuE Studio` | Song-Bibliothek |

## API

| Methode | Pfad | |
|---|---|---|
| `GET` | `/api/status` | Worker-Zustand, Songs in Arbeit, Protokoll |
| `GET` | `/api/events` | dasselbe live als Server-Sent Events (`snapshot`, `song`, `worker`, `log`, `library`, `ping`) |
| `POST` | `/api/generate` | neuer Lauf: `{ style, lyrics, title?, batch?, quality?: "draft"\|"full", cot?, seed?, instrumental?, engines?, draftSteps?, maxTokens?: 200–9000, abc? }` (`abc` braucht `cot` "full" oder "melody") |
| `POST` | `/api/songs/{run}/{song}/render` | Song aus seinen Tokens neu synthetisieren, z. B. einen Entwurf in voller Qualität |
| `POST` | `/api/songs/{run}/{song}/cancel` | Song abbrechen |
| `POST` | `/api/stop` | alle Songs abbrechen |
| `POST` | `/api/worker/shutdown` | Worker-Prozess beenden (Speicher freigeben) |
| `GET` | `/api/library` | alle Läufe mit ihren Songs |
| `GET` | `/api/songs/{run}/{song}/audio` | FLAC (Range-fähig; `?download=true` als Download) |
| `GET` | `/api/songs/{run}/{song}/score` | `score.abc` |
| `GET` | `/api/songs/{run}/{song}/zip` | FLAC und ABC des Songs als ZIP |
| `GET` | `/api/runs/{run}/zip` | FLAC und ABC aller Songs des Laufs als ZIP |

OpenAPI unter `/api/openapi`.
