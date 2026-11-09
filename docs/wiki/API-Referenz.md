# API-Referenz (API 1.0)

Alle Typen liegen in `FastEnvSetter.Core`, sofern nicht anders angegeben.

| Vertrag | Zweck |
|---|---|
| `IFastEnvPlugin` | Einstiegspunkt; erweitert `IFeatureModule` um `OnStarted` |
| `IFeatureModule` | `ConfigureServices` und `GetNavigation` |
| `IRunner` | Neue Sprache oder Startart; das Schema erzeugt die Eingabefelder automatisch |
| `ICommandSource` | Einträge für die Befehlspalette |
| `IAppEventHandler` | Reagiert auf `Started`, `Stopped`, `Crashed` (läuft im Hintergrund) |
| `ISecretProvider` | Weitere Tresore |
| `IThemeProvider` | Styles aus Code |
| `IProcessSupervisor` | Dienste starten, stoppen, Logs lesen; austauschbar |
| `IAppDataStore` | Zugriff auf Projekte, Profile und Einstellungen |
| `IPortInspector` | Port-Belegung ermitteln |
| `FastEnvSetter.Plugin.Sdk.IAppShell` | Hauptfenster |

Über `IServiceProvider` erreichst du außerdem alle öffentlichen Klassen der App, zum Beispiel `ThemeService`. Nur die oben genannten Verträge sind stabil.

## Versionierung

`PluginApi.Version` folgt semantischer Versionierung: Eine neue Nebenversion ergänzt die API, eine neue Hauptversion kann sie brechen. Plugins mit nicht passender `ApiVersion` werden nicht geladen.

## Neue Startart

| Member | Bedeutung |
|---|---|
| `Id`, `DisplayName`, `Icon` | Kennung, Anzeigename, Name eines WPF-UI-Symbols |
| `GetSettingsSchema()` | Eingabefelder (`Text`, `Choice`, `FilePath`, `FolderPath`, `Toggle`) |
| `BuildStart(service)` | Der zu startende Befehl als `ProcessCommand` |
| `Detect(folder)` | Optional: Vorschläge aus dem Arbeitsordner |
| `Validate(service)` | Optional: Meldungen zu fehlenden Angaben |
| `BuildStop(service)` | Optional: eigener Stopp-Befehl |
| `GetExpectedPorts(service)` | Optional: Ports für die Konfliktprüfung |

Übergib Secrets nie als Argument, sie kommen automatisch als Umgebungsvariablen an.
