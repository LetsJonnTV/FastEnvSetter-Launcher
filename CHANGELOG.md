# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/LetsJonnTV/FastEnvSetter-Launcher/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/LetsJonnTV/FastEnvSetter-Launcher/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/LetsJonnTV/FastEnvSetter-Launcher/releases/tag/v1.0.0
