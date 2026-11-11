# FastEnvSetter für VS Code

Startet und stoppt [FastEnvSetter](https://github.com/LetsJonnTV/FastEnvSetter-Launcher)-Projekte direkt aus VS Code.

- **Command Palette:** `FastEnvSetter: Projekt starten`, `Projekt stoppen`, `Launcher öffnen`
- **Explorer:** Ansicht „FastEnvSetter“ mit Projekten, Diensten, Status, Start/Stopp und Logs
- **Statusleiste:** Anzahl aktiver Dienste, ein Klick startet das zugeordnete Projekt

## Voraussetzung

Die FastEnvSetter.exe muss installiert sein. Liegt sie nicht im `PATH`, trage den Pfad unter `fastenvsetter.executablePath` ein.

## Workspace-Zuordnung

`FastEnvSetter: Workspace zuordnen` verbindet einen lokalen Workspace-Ordner mit der stabilen Projekt-GUID. In Multi-Root-Workspaces wird der Ordner ausdrücklich ausgewählt. Die Zuordnung bleibt bei einer Umbenennung des Projekts erhalten und wird lokal im Workspace-Zustand von VS Code gespeichert, nicht in der Projektkonfiguration.

`Zugeordnetes Projekt starten` und `Zugeordnetes Projekt stoppen` verwenden diese Zuordnung. `Workspace-Zuordnung entfernen` entfernt sie. Ohne explizite Zuordnung wird nur eine eindeutige, exakt passende `sourceRoot` aus der CLI verwendet. Alte Launcher ohne dieses Metadatum funktionieren weiterhin über die explizite Zuordnung. Gelöschte Projekt-GUIDs werden nicht durch ähnlich benannte Projekte ersetzt.

## Sicherheit und Status

In nicht vertrauenswürdigen Workspaces wird kein Launcher-Prozess ausgeführt, auch keine automatische Statusabfrage. CLI-Aufrufe verwenden Argumentlisten ohne Shell. Projektvariablen oder Secrets werden nicht in VS Code übernommen. Remote-Workspace-Ordner werden nicht an eine lokale Launcher-EXE übergeben.

`Launcher offline` bedeutet: Der Launcher meldet Exitcode 4; gespeicherte Projekte sind verfügbar, Dienstzustände unbekannt. `Status veraltet` bedeutet: Abfrage, CLI oder JSON-Antwort sind fehlgeschlagen; vorhandene Einträge sind kein aktueller Laufzeitstatus. Es läuft höchstens eine automatische Aktualisierung gleichzeitig. Leseabfragen haben 15 Sekunden, Aktionen 120 Sekunden Timeout; ein Timeout bestätigt keinen Abbruch der Aktion im Launcher.

Editorziele mit Ordner, `.code-workspace` und optionalem VS-Code-Profil werden im Desktop-Projekteditor gepflegt und geöffnet. Ein neuer Profilname kann ein neues VS-Code-Profil anlegen. Die aktuelle CLI bietet keinen Editor-/Development-Befehl; deshalb registriert diese Erweiterung keine vorgetäuschten entsprechenden Aktionen.

## Integration

Die optionale CLI-Projektion für `list/status --json` heißt `sourceRoot: string` und `editorTargets: [{ id, name, path, profile }]`. Namen sind camelCase; fehlende Felder bleiben kompatibel. Die GUID-Zuordnung benötigt nur die bestehenden Felder `id`, `name` und `services`.

Desktop-/Core-APIs dieses Arbeitspakets:

- `EditorLauncher.BuildStart(Project, EditorTarget, string? executablePath = null)` liefert einen geprüften `ProcessStartInfo` mit `ArgumentList`.
- `EditorLauncher.Open(Project, EditorTarget, string? executablePath = null)` startet die native `Code.exe`. Keine DI-Registrierung ist nötig; die Projekt-ViewModels verwenden ihn direkt. Eine spätere DI-Registrierung als Singleton ist möglich. `ProjectActions` liegt tatsächlich in der gemeinsam genutzten `Shell.cs` und wurde nicht geändert; die bisherige `OpenVsCode`-Aktion bleibt bestehen.
- `ProjectTransfer.ResolvePath(rootFolder, path)` prüft Pfadgrenzen und lehnt bestehende Reparse-Points ab.
- `ProjectTransfer.Duplicate(Project)` kopiert alle Modellfelder, remappt verschachtelte IDs und Abhängigkeiten, behält externe Env-Profilzuordnungen bei.
- `ProjectTransfer.Export(project, profiles, rootFolder, includeSecretReferences: false)` ist der sichere UI-Export ohne lokale Geheimwerte und Tresorreferenzen. Der bestehende Drei-Parameter-Overload behält aus Kompatibilitätsgründen Tresorreferenzen bei. Import entfernt geschützte Werte und Tresorreferenzen auch aus fremden Paketen.

Pfadfelder von Editoren, Tasks, Env-Dateien und Deployments werden relativ zum Quellordner transportiert; dieser liegt relativ zum ausgewählten Transfer-Wurzelordner. Pfade außerhalb dieser Grenzen werden abgelehnt, nicht stillschweigend geleert. Befehlsargumente, Remote-Pfade und beliebige Runner-Settings sind keine übertragbaren lokalen Pfadfelder und werden nicht umgeschrieben. Ein Import startet keine Befehle.

Die WPF-Setter und Projektaktionen schreiben weiterhin direkt in `IAppDataStore.Data` mit `RequestSave`; sie wurden nicht auf eine konkurrierende Konfigurations-API umgestellt. Die spätere Integration muss diese Setter an die gemeinsam verantwortete Mutations-API anbinden. Task-/Git-/Deployment-Oberflächen sind nicht Teil dieses Editor-Arbeitspakets.

## Entwicklung

Aus dem Erweiterungsverzeichnis: `npm test`, `npm run lint`, `npm run typecheck`, `npm run build`. Die Tests prüfen die reine Zuordnungslogik ohne laufenden VS-Code-Host. Ein interaktiver Extension-Host-/WPF-Smoke-Test ist zusätzlich erforderlich.
