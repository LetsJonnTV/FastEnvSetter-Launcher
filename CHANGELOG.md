# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2026-11-10

### Added

- Projekt-Quellordner und mehrere relative VS-Code-Editorziele mit optionalen Editorprofilen; lokale GUID-basierte Workspace-/Multi-Root-Zuordnung unter Beachtung von Workspace Trust.
- Desktop-Entwicklungstabs fuer Aufgaben/Tests, Env-Dateiquellen und Git mit selektivem Staging, lokaler Diffpruefung und bestaetigtem Index-Commit.
- Task-Vorschlaege fuer .NET, Python uv/pip/venv/pytest, npm/pnpm/yarn, Cargo, Maven/Gradle und CMake/CTest; Erkennung startet und installiert nichts. Aufgaben mit erfolgreichem Abhaengigkeitsabschluss, Zeitlimit, Abbruch, Exitcodes und maskierten Abschlusslogs.
- Projektbezogene Env-Dateiquellen mit Schluesselvorschau und geordneter Laufzeitueberlagerung; Example-Dateien bleiben ausschliesslich Vorschau.
- Docker-/Podman-Compose-Runner und niedrigschichtige Build-/Up-/Stop-/Status-/Log-Befehlsplaene mit expliziter Providerwahl.
- Konfigurations-API v1 und MCP-CRUD mit Revision, persistenter Request-Deduplizierung (maximal 10.000 Belege), Gesamtvalidierung und atomaren Batches. Neue Listenoperationen: `set_project_tasks`, `set_project_env_files`, `set_editor_targets`.
- Projektgebundene Entwicklungs-API fuer Task-Erkennung und -Ausfuehrung, Git-Aktionen, Metadaten-Snapshots und lokale Release-Datensaetze.
- Separate lokale, globale Opt-ins fuer externe Konfigurationsaenderungen, Task-Ausfuehrung und Git-Aenderungen; standardmaessig ausgeschaltet.
- Niedrigschichtige GitHub-/GitLab-Metadaten-/Release-Dienste sowie Deploymentplanung, Approval-Pruefung und Journal fuer eingeschraenktes JSON-/Digest-Compose, Kubernetes-JSON-Deployments und SSH-Artefakttransfer; kein fertiger Desktop-/MCP-Publish-/Deploy-Workflow und keine bestaetigten Live-Zieltests.

### Changed

- README, API-Wiki und Sicherheitsdokumentation unterscheiden implementierte Bedienpfade und Dienste von offenen Integrationen.
- Datenschema 3 speichert Projektmetadaten und Konfigurationsrevisionen. Vor der Migration wird die bisherige Datendatei gesichert; aeltere Launcher koennen das neue Format nicht laden.

### Security

- MCP-Start/-Stop/-Restart pruefen die separate globale Task-Freigabe im Launcher; ein reproduzierbarer MCP-Transporttest prueft Default-deny und freigegebene leere Projekte. Same-User-IPC und Code-Plugins sind keine Sandbox; direkte lokale CLI-Aktionen bleiben vertrauenswuerdig.
- Die Entwicklungs-API haelt rohe Task-Logs und Diff-Inhalte zurueck; die genaue Inhaltspruefung erfolgt lokal. Ihre maximal 256 Operationsbelege sind nur im Speicher und gehen beim Launcher-Neustart verloren. Unbekannte Operationen duerfen nicht automatisch erneut ausgefuehrt werden.
- Snapshots sind Metadatenstaende, keine vollstaendigen Projekt- oder Konfigurationsbackups. Autonomes Deployment bleibt ueber die Entwicklungs-API gesperrt.

## [1.0.1] - 2026-11-09

### Fixed

- Ein Doppelklick auf die Exe blieb wirkungslos, solange die VS Code Extension den Status abfragte.
- Statusabfragen der Extension liefen bei beendetem Launcher jeweils rund 20 Sekunden und häuften Prozesse an.

## [1.0.0] - 2026-11-09

### Added

- Windows-Launcher mit Projekten aus einem oder mehreren Diensten.
- Runner für PowerShell 7 und 5.1, Python, Node.js, .NET und Docker Compose mit automatischer Erkennung.
- Env-Profile mit Vererbung, mehreren Profilen pro Projekt, dienstspezifischen Variablen sowie `.env`-Import und -Export.
- DPAPI-verschlüsselte Secrets und Tresor-Anbindung für Azure Key Vault, 1Password, Bitwarden und Vaultwarden.
- Abhängigkeiten zwischen Diensten, Bereitschaftsprüfung per Port oder URL, Vorbereitungsschritte, automatischer Neustart und Port-Konflikt-Erkennung.
- Tray-Icon, Befehlspalette, Suche, Favoriten, Tags und Logs mit ANSI-Farben, Filter, Export und Gesamtansicht.
- Kommandozeile mit JSON-Ausgabe und Weiterleitung an die laufende Instanz.
- MCP-Server mit Tools, Resources und Prompts für KI-Clients.
- VS Code Extension mit Baumansicht, Statusleiste und Befehlen.
- Plugin-System für Styles und Code mit Installation aus GHCR oder Datei, Aktivierung im Launcher, abgesichertem Modus und Absturz-Wiederherstellung.
- Erstes Plugin „Tiefblau“, Beispiel-Code-Plugin und Plugin-Wiki.
- Projekt-Export und -Import für Teams ohne lokale Secrets.
- Eigenes App-Icon sowie CI/CD-Workflows für Tests, Sicherheitsprüfungen, Builds und Releases.

[Unreleased]: https://github.com/LetsJonnTV/FastEnvSetter-Launcher/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/LetsJonnTV/FastEnvSetter-Launcher/compare/v1.0.1...v1.1.0
[1.0.1]: https://github.com/LetsJonnTV/FastEnvSetter-Launcher/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/LetsJonnTV/FastEnvSetter-Launcher/releases/tag/v1.0.0
