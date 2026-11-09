# Mitwirken

Danke für dein Interesse an FastEnvSetter.

## Einrichtung

Voraussetzungen: Windows, .NET SDK 10 und Node.js 20 oder neuer.

```powershell
dotnet build
dotnet test
cd extensions/vscode; npm ci; npm run lint; npm run typecheck
```

Mit `--data <Datei>` startet die App mit eigenen Testdaten und lässt deine echten Daten unberührt:

```powershell
dotnet run --project src/FastEnvSetter.App -- --data C:\Temp\fes-test.json
```

## Ablauf

1. Für größere Änderungen zuerst ein Issue öffnen.
2. Einen Branch von `master` anlegen.
3. Tests ergänzen und vor dem Push `dotnet format` ausführen.
4. Benutzerrelevante Änderungen unter `[Unreleased]` in `CHANGELOG.md` eintragen.
5. Einen Pull Request öffnen; CI muss grün sein.

## Richtlinien

- Neue Sprachen und Features gehören möglichst als `IRunner` oder `IFeatureModule` oder als Plugin umgesetzt, nicht als Sonderfall im Kern.
- Variablenwerte dürfen nie in Logs, Kommandozeilenargumente, CLI-Ausgaben oder MCP-Antworten gelangen.
- Änderungen an `IFastEnvPlugin` und anderen öffentlichen Verträgen müssen `PluginApi.Version` beachten.

## Release

Die Version steht in `Directory.Build.props` und `extensions/vscode/package.json`. `scripts/check-version.ps1` prüft beide. Ein Tag `vX.Y.Z` löst den Release aus, Plugins werden mit `plugin-<name>-vX.Y.Z` veröffentlicht.

Schwachstellen bitte wie in [SECURITY.md](SECURITY.md) beschrieben melden.
