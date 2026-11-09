# FastEnvSetter-Plugins

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
