# FastEnvSetter

FastEnvSetter verbindet Windows-Projekte mit Umgebungsprofilen, Diensten, Quellordnern und VS-Code-Editorzielen. Diese Dokumentation beschreibt vorhandene Oberflaechen und API-Grenzen, nicht die vollstaendige Umsetzung eines Entwicklungsplans.

## Entwicklungsablaeufe

Im Desktop-Projekteditor unter **Quellcode und VS Code** den Quellordner und relative Ordner-/`.code-workspace`-Ziele pflegen. Optionale VS-Code-Profile sind von Env-Profilen getrennt; unbekannte Namen koennen neue Editorprofile erzeugen. Die VS-Code-Extension bietet eine lokale GUID-basierte Workspace-Zuordnung und respektiert Workspace Trust, uebernimmt aber keine Projekt-Secrets.

Unter **Entwicklung** stehen **Aufgaben / Tests**, **Env-Dateien** und **Git** bereit. Task-Vorschlaege fuer .NET, Python uv/pip, npm/pnpm/yarn, Cargo, Maven/Gradle und CMake/CTest werden erst nach bewusster Uebernahme gespeichert und nach Bestaetigung ausgefuehrt. Env-Dateien liefern eine Schluesselvorschau ohne Werte; `.env.example` bleibt Vorschau. Git bietet Status, Historie, selektives Stage/Unstage sowie lokale Diff- und Indexpruefung vor dem Commit. Docker-/Podman-Compose-Runner setzen vorhandene Engines und Provider voraus.

Externe Konfigurationsaenderungen, Task-Ausfuehrung und Git-Aenderungen werden getrennt unter **Einstellungen → Externe Zugriffe (CLI / MCP)** freigegeben. Alle drei Freigaben sind standardmaessig aus und gelten global, nicht pro Projekt. Die [API-Referenz](API-Referenz) unterscheidet Konfigurations- und Entwicklungs-API von der Plugin-API; [Sicherheit](Sicherheit) beschreibt Vertrauensgrenzen und Ausgabelimits.

Snapshots sind ausschliesslich Projektmetadaten, keine kompletten Backups. Lokale Release-Datensaetze sind keine Remote-Veroeffentlichung. GitHub-/GitLab- und eingeschraenkte Deployment-Dienste existieren unterhalb der Oberflaechen; ein vollstaendiger Desktop-/MCP-Deploymentworkflow fehlt und autonomes MCP-Deployment bleibt verweigert. Live-Tests von Deploymentzielen sind nicht bestaetigt.

## Plugins

Mit Plugins passt du FastEnvSetter an deine Bedürfnisse an: von einer neuen Farbe bis zum Austausch des Hauptfensters.

| Art | Inhalt | Wirksam | Code-Warnung |
|---|---|---|---|
| `style` | Nur `plugin.json` und `theme.json` | sofort | nein |
| `code` | .NET-Assembly mit `IFastEnvPlugin` | nach Neustart | ja |

## Seiten

1. [Style-Plugin schreiben](Style-Plugin)
2. [Code-Plugin schreiben](Code-Plugin)
3. [API-Referenz](API-Referenz)
4. [Veröffentlichen in GHCR](Veroeffentlichen)
5. [Sicherheit](Sicherheit)

## Paketformat

Ein Plugin ist eine ZIP-Datei mit der Endung `.fesplugin`. `plugin.json` liegt direkt im Wurzelverzeichnis:

```json
{
  "Id": "mein-plugin",
  "Name": "Mein Plugin",
  "Version": "1.0.0",
  "Publisher": "Dein Name",
  "Description": "Was es tut.",
  "ApiVersion": "1.0",
  "Kind": "style"
}
```

| Feld | Regel |
|---|---|
| `Id` | Kleinbuchstaben, Ziffern, `.` und `-`, 2 bis 64 Zeichen |
| `Version` | `MAJOR.MINOR.PATCH` |
| `ApiVersion` | Hauptversion muss passen, Nebenversion darf nicht neuer sein als die App (aktuell `1.0`) |
| `Kind` | `style` oder `code` |
| `EntryAssembly` | Nur bei `code`: DLL-Name im Wurzelverzeichnis |
| `Permissions` | Optional, Information für Nutzer, **keine Sandbox** |

Wenn ein Plugin die App am Starten hindert, deaktiviert FastEnvSetter beim nächsten Start das zuletzt aktivierte Plugin. Alternativ startest du mit `FastEnvSetter --safe-mode` ganz ohne Plugins.
