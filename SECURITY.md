# Sicherheitsrichtlinie

## Unterstützte Versionen

| Version | Unterstützt |
|---|---|
| 1.0.x | Ja |
| < 1.0 | Nein |

## Schwachstelle melden

Bitte melde Schwachstellen **nicht** über öffentliche Issues. Nutze stattdessen die private Meldung über [GitHub Security Advisories](https://github.com/LetsJonnTV/FastEnvSetter-Launcher/security/advisories/new).

Hilfreich sind betroffene Version, Schritte zum Nachstellen und mögliche Auswirkungen. Bitte füge keine echten Secrets bei.

| Schritt | Ziel |
|---|---|
| Eingangsbestätigung | innerhalb von 5 Werktagen |
| Erste Einschätzung | innerhalb von 10 Werktagen |
| Korrektur kritischer Lücken | möglichst innerhalb von 30 Tagen |

Dies ist ein privat betreutes Projekt, die Zeiten sind daher Zielwerte. Nach der Korrektur wird die Lücke im Advisory veröffentlicht; auf Wunsch mit Namensnennung.

## Sicherheitsmodell

- **Lokale Secrets** sind per Windows DPAPI an Benutzerkonto und Rechner gebunden. Prozesse desselben Benutzers können sie grundsätzlich auslesen.
- **Tresorwerte** werden bei jedem Start über die jeweilige CLI geholt und nicht gespeichert.
- **Gestartete Programme** erhalten Secrets im Klartext als Umgebungsvariablen. Maskiert wird nur die Log-Ansicht des Launchers, und nur exakte Werte ab drei Zeichen.
- **IPC** läuft über eine Named Pipe, die auf den aktuellen Benutzer beschränkt ist.
- **MCP-Server** geben keine Variablenwerte aus. Logs gelten als nicht vertrauenswürdige Daten.

## Plugins

Code-Plugins sind nicht in einer Sandbox isoliert. Sie laufen mit den Rechten des Benutzers und können Dateien lesen und auf entschlüsselte Secrets zugreifen. Die Angaben unter `permissions` sind reine Information des Autors.

Schutzmaßnahmen sind eine Nachfrage mit Herausgeber, Quelle und Prüfsumme vor jeder Installation, die SHA-256-Prüfung beim Herunterladen, ein Inhalts-Hash vor jedem Laden, Schutz vor Zip-Slip sowie Größenlimits. Der angezeigte Herausgeber ist nicht verifiziert. Style-Plugins dürfen nur Daten enthalten und werden entsprechend geprüft.

Importierte `.fesproject`-Dateien enthalten Befehle, die beim Start des Projekts ausgeführt werden. Importiere nur Dateien aus vertrauenswürdigen Quellen.
