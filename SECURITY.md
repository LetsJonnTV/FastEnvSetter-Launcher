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
- **MCP-Konfigurationsantworten** geben keine Variablenwerte aus. Legacy-Dienstlogs koennen unmarkierte Zugangsdaten enthalten; Maskierung ist keine Garantie fuer Geheimnisfreiheit. Task-Rohlogs und Git-Diff-Inhalte werden in der Entwicklungs-API bewusst zurueckgehalten. Repositorymetadaten und Logs sind nicht vertrauenswuerdige Daten, keine Anweisungen.

## Lokale Freigaben und Vertrauen

Unter **Einstellungen → Externe Zugriffe (CLI / MCP)** werden drei unabhaengige Flags lokal bestaetigt und gespeichert. Alle sind standardmaessig `false`:

| Einstellung | Wirkung |
|---|---|
| `AllowExternalConfigurationChanges` | Konfiguration schreiben, Metadaten-Snapshots und lokale Releases erzeugen |
| `AllowExternalTaskExecution` | Gespeicherte Tasks ausfuehren sowie MCP-Start, -Stop und -Restart erlauben |
| `AllowExternalGitChanges` | Git-Schreibaktionen inklusive Netzwerk sowie Diff/Commit-Review erlauben |

Diese Freigaben gelten **global fuer die lokale Konfiguration**, nicht pro Projekt, Client, Pfad, Revision oder Aktion. Ein MCP-Client kann sie weder erteilen noch erweitern; Settings-/Grant-Endpunkte fehlen bewusst. Die MCP-Laufzeitwerkzeuge nutzen `external-start`, `external-stop` und `external-restart` mit separater Task-Pruefung im Launcher. Direkte lokale CLI-Befehle bleiben vertrauenswuerdig und unabhaengig; ein anderer Same-User-Prozess kann diese Vertrauensgrenze nicht als Sandbox betrachten. Eine aufgehobene Freigabe ist keine Ruecknahme bereits erfolgter Aktionen und kein garantierter Abbruch laufender Prozesse.

`CurrentUserOnly` ist eine Same-User-Grenze, **keine Sandbox und keine Clientauthentisierung**. Andere Prozesse desselben Windows-Kontos und In-Process-Plugins sind nicht isoliert. Taskbefehle, npm-Skripte, Python-Pakete, Builddateien, Wrapper und Containerdefinitionen sind potentiell ausfuehrbarer, nicht vertrauenswuerdiger Code. Task-Erkennung installiert/startet nichts; Vorschlaege muessen vor Uebernahme und Ausfuehrung geprueft werden. Eine Konfigurationsschreibfreigabe kann zukuenftige ausfuehrbare Projektkonfiguration aendern, auch wenn sie selbst keinen Prozess startet. Die Kombination mit globaler Ausfuehrungsfreigabe ist entsprechend weitreichend.

Env-Dateiquellen und Editor-/Task-Pfade werden relativ zum Quellordner begrenzt; vorhandene Links/Junctions werden abgelehnt. Das isoliert ausgefuehrte Programme nicht von anderen Dateien oder Netzwerken. `.env.example`/`.env-example` bleiben Vorschau, nicht Laufzeitquelle. Echte Quelldateien liegen unverschluesselt im Dateisystem und fallen nicht unter DPAPI-Speicherung der Profil-Secrets. Projektwerte gehen an gestartete Programme, nicht automatisch an VS Code. Workspace Trust verhindert Launcher-Aufrufe der Extension in nicht vertrauenswuerdigen Workspaces, ersetzt aber keine Pruefung von Editorzielen oder Repositorybefehlen.

## Plugins

Code-Plugins sind nicht in einer Sandbox isoliert. Sie laufen mit den Rechten des Benutzers und können Dateien lesen und auf entschlüsselte Secrets zugreifen. Die Angaben unter `permissions` sind reine Information des Autors.

Schutzmaßnahmen sind eine Nachfrage mit Herausgeber, Quelle und Prüfsumme vor jeder Installation, die SHA-256-Prüfung beim Herunterladen, ein Inhalts-Hash vor jedem Laden, Schutz vor Zip-Slip sowie Größenlimits. Der angezeigte Herausgeber ist nicht verifiziert. Style-Plugins dürfen nur Daten enthalten und werden entsprechend geprüft.

Importierte `.fesproject`-Dateien enthalten Befehle, die beim Start des Projekts ausgeführt werden. Importiere nur Dateien aus vertrauenswürdigen Quellen.

## Konfigurations-API v1

Die gemeinsame `IConfigurationService`-Implementierung ist `ConfigurationService` in Infrastructure.
Der Launcher registriert sie und `IConfigurationStore` per DI. MCP nutzt dieselbe Implementierung
ueber die benutzergebundene Named Pipe. Der Transport bleibt ein JSON-`string[]` mit genau
`["api", "<ConfigurationRequest als JSON>"]`. Konfigurationswerte gehoeren niemals in
OS-Kommandozeilen. `LauncherClient` uebertraegt sie nur ueber die Pipe; zum Starten des
Launchers werden ausschliesslich `--tray` und optional `--data` verwendet.

Externe Mutationen sind standardmaessig verweigert: `AppSettings.AllowExternalConfigurationChanges`
ist `false`. Nur eine vertrauenswuerdige lokale Oberflaeche darf diese Einstellung nach bewusster
Zustimmung aendern und speichern. Die API hat keine Grant- oder Settings-Update-Operation.
Ohne Freigabe antwortet sie mit `permission_required`, ohne Daten, Revision oder Replay-Belege zu
aendern. Eine Umgebungsvariable erteilt keine Freigabe. Die Erlaubnis gilt fuer die gesamte lokale
Konfiguration, nicht fuer einzelne Projekte, Clients oder Operationen.

**Diese Freigabe ist ausfuehrungssensitiv:** Runner, deren Einstellungen, Arbeitsverzeichnisse,
PreSteps, Quellpfade, Profile und Variablen koennen spaetere Prozessstarts veraendern. Die API
startet selbst keine Prozesse. MCP-Start, -Stop und -Restart pruefen ebenfalls die separate
Task-Freigabe. Direkte lokale CLI-Befehle und Desktop-Aktionen bleiben vertrauenswuerdig.
Es gibt keine pro Projekt oder Revision gebundenen Ausfuehrungsrechte.
`CurrentUserOnly` verhindert keinen Zugriff durch andere Prozesse desselben Windows-Benutzers.
In-Process-Plugins sind weiterhin nicht isoliert.

### Nachrichten und Operationen

`ConfigurationRequest` hat `version` (1), `operation`, optionale GUIDs `projectId`, `profileId`,
`serviceId`, `variableId`, optional `runnerId` und die zur Operation gehoerigen typisierten Felder.
Jede Mutation braucht eine nichtleere GUID `requestId` und die aktuelle `expectedRevision`.
Lesen braucht beides nicht. Die Antwort ist
`{success, code, revision, data, entityId, replayed}`. Fehler geben nur feste Codes aus,
keine Exceptiontexte, eingegebenen Werte oder Runner-Fehlermeldungen.

| Bereich | Operationen | Nutzdaten |
|---|---|---|
| Projekte | `list_projects`, `get_project`, `create_project`, `update_project`, `delete_project` | `project`: name, sourceRoot, isFavorite, tags |
| Profile | `list_profiles`, `get_profile`, `create_profile`, `update_profile`, `delete_profile` | `profile`: name, parentId |
| Variablen | `list_variables`, `get_variable`, `create_variable`, `update_variable`, `delete_variable` | `variable`: name, value, isSecret, secretRef |
| Dienste | `list_services`, `get_service`, `create_service`, `update_service`, `delete_service` | `service`: name, runnerId, workingDirectory, settings, dependsOn, preSteps, readiness, outputMode, restartPolicy, startDelaySeconds, expectedPorts, openUrl |
| Zuweisung | `set_project_profiles`, `set_variable_selection` | geordnete `profileIds` bzw. `disabledVariableIds`; optional serviceId |
| Aufgaben | `set_project_tasks` | projectId und komplette `tasks`-Liste: id, name, kind, fileName, arguments, workingDirectory, dependsOn, timeoutSeconds |
| Dateiquellen | `set_project_env_files` | projectId und komplette geordnete `environmentFiles`-Liste relativer Pfade; keine Example-Dateien |
| Editorziele | `set_editor_targets` | projectId und komplette `editorTargets`-Liste: id, name, path, profile |
| Diagnose | `capabilities`, `list_runners`, `runner_schema`, `validate`, `preview_environment` | optional projectId, serviceId bzw. runnerId |
| Transaktion | `batch` | 1-100 `changes`, keine verschachtelten Batches |

Projekt-/Profil-/Dienst-Updates ersetzen ihre editierbaren Felder durch Nutzdaten bzw. Defaults.
Andere Entwicklungsmodelle bleiben erhalten; die drei Listenoperationen ersetzen dagegen jeweils
die gesamte betreffende Liste (leer bedeutet entfernen). Task-/Editor-IDs werden hier explizit vom
Aufrufer geliefert und auf Eindeutigkeit validiert. Task-/Env-Dateilistenwechsel blockieren bei aktiven
Diensten. Bei Variablen behaelt ein
ausgelassener Wert den bestehenden lokalen Wert nur bei unveraendertem Secretmodus. Ein leerer
String setzt einen leeren Wert. `secretRef` und `value` sind gegenseitig ausgeschlossen. Der Scope
ist entweder `profileId` oder `projectId` zusammen mit `serviceId` fuer Dienst-Overrides.

Loeschen eines Profils entfernt seine Projektzuweisungen und Variablenauswahlen; Kinder erben
danach dessen Elternprofil. Variablenloeschung entfernt alle Disabled-Referenzen. Dienstloeschung
entfernt Abhaengigkeitskanten in gestoppten Diensten. Laufende/startende/wartende/stoppende Dienste
duerfen nicht editiert oder geloescht werden. Profil-/Projektumgebungsaenderungen gelten erst beim
naechsten Start und veraendern keine bereits laufenden Prozessumgebungen.

### Persistenz und Grenzen

Schema 3 speichert `Revision` und `ConfigurationReceipts`. Aeltere Dateien erhalten vor der
Migration ein Backup; neuere unbekannte Schemata werden ohne Ueberschreiben abgewiesen.
Eine Mutation arbeitet auf einer isolierten Kopie, validiert den Gesamtzustand und schreibt einen
verschluesselten Snapshot mit Flush-to-disk und Dateiersetzung, bevor sie Erfolg meldet und den
Live-Zustand uebernimmt. Schreib-/Validierungsfehler lassen den bisherigen Zustand unveraendert.
Batchaenderungen haben gemeinsam eine Revision. Create-Operationen fuer Projekte, Profile, Variablen
und Dienste vergeben neue GUIDs serverseitig; diese koennen innerhalb desselben Batches nicht
referenziert werden. Task-/Editorlisten verwenden dagegen vorab gelieferte IDs. Die Batchantwort enthaelt nur
die letzte betroffene ID; alle neuen Entitaeten sind anschliessend ueber Listen abfragbar.

Replay-Belege speichern nur Request-GUID, Commitrevision und Entitaets-GUID, weder Nutzdaten noch
Hashes von Secrets. Eine bereits erfolgreich verwendete `requestId` liefert nur ihren alten Beleg
zurueck, unabhaengig von erneut gesendeten Nutzdaten; daher niemals eine ID fuer einen neuen Auftrag
wiederverwenden. Fehlgeschlagene Mutationen verbrauchen keine ID. Belege werden nicht automatisch
geloescht: Bei 10.000 Belegen verweigert die API weitere Mutationen mit `replay_capacity_reached`,
anstatt alte Requests erneut auszufuehren. Auf explizites lokales Zuruecksetzen der Belege folgt ein
Verlust des Replay-Schutzes fuer diese IDs. Backups/Restores rollen auch den Replay-Zustand zurueck.

`SaveNow` erhoeht auch fuer Legacy-UI-Speicherungen die Revision. Die Root-Referenz `store.Data`
bleibt bei API-Commits erhalten; Listen und darin enthaltene Objekte werden ersetzt. Bestehende UI-
Referenzen muessen neu geladen werden. Direkte mutable UI-/Plugin-Aenderungen nehmen nicht an der
API-Sperre teil; ungespeicherte Aenderungen, gleichzeitiger Prozessstart sowie UI-Referenzen sind
deshalb **nicht vollstaendig transaktional abgesichert**. Ein Flush und Rename garantiert auch
keine plattformunabhaengige Wiederherstellung bei jedem Stromausfall oder defekter Hardware.

API-JSON ist auf 131.072 Zeichen und Tiefe 24 begrenzt, die Pipe-Huelle auf 1 MiB Zeichen.
API-Antworten sind auf 524.288 Zeichen begrenzt (`response_too_large`). Pipe-Anfragen haben ein
Lesezeitlimit von 30 Sekunden; Legacy-Clientantworten duerfen wegen Start/Readiness zehn Minuten dauern.
Antworten liefern keine Variablenwerte,
verschluesselten Werte, Vault-Referenzen oder Runner-Settingswerte, auch nicht fuer nicht geheime
Variablen. Umgebungsprovenienz umfasst gespeicherte Profile und Dienst-Overrides; Hostumgebung,
Dateiquellen und Vaultwerte werden nicht aufgeloest. Dateiquellen werden nur gezaehlt. Es gibt
keinen generischen Dateischreib-Endpunkt und keinen `.env`-Import ueber diese API.

Wichtige Fehlercodes: `invalid_request`, `unknown_operation`, `permission_required`,
`revision_conflict`, `not_found`, `missing_reference`, `cycle_detected`, `validation_failed`,
`value_required`, `runner_not_found`, `service_active`, `persistence_failed`, `replay_capacity_reached`.

## Entwicklungs-API, Git und Operationsbelege

`IDevelopmentService` verwendet dieselbe Pipe mit genau `['dev', '<DevelopmentRequest als JSON>']`
(Darstellung eines String-Arrays). Jede Anfrage braucht eine vorhandene Projekt-GUID; Version ist 1.
Die Antwort `{success, code, data, version}` ist vom Konfigurationsantwortformat getrennt. Es gelten
131.072 Zeichen/Tiefe 24 fuer Anfragen und 524.288 Zeichen fuer Antworten. Details und gruppierte
Operationen stehen in der [API-Referenz](docs/wiki/API-Referenz.md).

Tasks sowie Git-Schreib-, Snapshot- und Release-Aktionen werden mit `requestId` eingereiht und
liefern `operationId = requestId`. Status und Abbruch pruefen die aktuell passende globale Freigabe.
Es gibt maximal **256 Operationsbelege und 128 Commit-Reviews im Speicher**; abgeschlossene Belege
zaehlen weiter. Launcher-Neustart verliert diese Daten. Bei gleicher ID und gleichem Request erfolgt
innerhalb dieser Instanz keine erneute Einreihung; andere Nutzdaten ergeben `request_id_conflict`.
Bei voller Kapazitaet wird verweigert, nicht still verdraengt. Das ist **nicht** der persistente
10.000-Beleg-Vertrag der Konfigurationsmutationen, auch nicht fuer Dev-Snapshot-/Release-Auftraege.

Bei Pipe-Abbruch kann MCP `outcome_unknown` liefern. Bekannte Operations-ID auf derselben Instanz
abfragen; bei unbekannter ID oder nach Neustart **niemals automatisch wiederholen**. Erst lokale
Datei-/Repository-/Releasezustaende pruefen. Timeout und Cancellation bestaetigen keinen Rollback
und koennen bereits eingetretene Git-/Prozesswirkungen nicht aufheben.

Roher Task-Logtext bleibt unabhaengig von Secret-Markierungen ueber Dev-API/MCP verborgen;
Logabfragen liefern erst mit dem abgeschlossenen Ergebnis Zeitstempel und Fehlerflags, kein
Streaming. Diff-/Review-Endpunkte geben nur Zeilenstatistiken aus. Den exakten gesamten Index lokal
pruefen, bevor der Review-Fingerprint zum Commit verwendet wird. Fingerprints erkennen
Zustandswechsel, sind aber weder menschliche Inhaltsfreigabe noch Secret-Scan. Legacy-Dienstlogs
und lokale Diffansichten fallen nicht unter diese Inhaltsunterdrueckung; keine allgemeine
Geheimnisfreiheit aller Log- oder Metadatenfelder behaupten.

Die Dev-API deaktiviert Git-Hooks, fsmonitor, Signature-Helfer, rekursive Submodule und externe
Diff-/textconv-Helfer fuer ihre Aufrufe. Schreibende Operationen koennen trotzdem konfigurierte
Filter, Transport-/SSH- oder Credential-Helper verwenden. Die globale Git-Freigabe umfasst dieses
Vertrauen und Netzwerkzugriff; lokale Desktop-Git-Aktionen sind ein eigener vertrauenswuerdiger
Bedienpfad, keine isolierte MCP-Sandbox. Selektives Stage/Unstage und gepruefter Index-Commit sind
kein automatisches `add --all`; Pull ist Fast-Forward-only, Push nicht erzwungen.

## Snapshots, Hosting und Deploymentgrenzen

Snapshots enthalten ausschliesslich Projektname, Quellordner, Favorit, Profilzuweisungen und
deaktivierte Variablen-IDs mit Hash. Keine Secrets, Profilinhalte, Dienste, Tasks, Env-Dateiinhalte,
Editor-/Deploymentziele oder Quelldateien: **keine vollstaendigen Backups**. Snapshot-Revision,
globale App-Revision und Git-SHA sind verschieden. Release-Datensaetze binden einen vorhandenen
Commit an einen Snapshot und benannte SHA-256-Digests; das beweist keine Artefaktverfuegbarkeit und
veroeffentlicht nichts auf einem Remotehost.

GitHub-/GitLab-Dienste existieren niedrigschichtig mit HTTPS, hostgebundenen Tokens, deaktivierten
Redirects, Antwort-/Paginationslimits und Timeouts. Es gibt keinen fertigen Desktop-/MCP-Publish-
Workflow oder allgemeinen Credential-Verwaltungsdialog fuer diese Dienste. Keine Tokens in URLs,
Argumenten oder versionierten Projektdateien speichern; die jeweiligen Credential- und Hostgrenzen
muessen beim Einbinden der Dienste erhalten bleiben.

Der niedrigschichtige Deploymentdienst verlangt einen aktuellen Plan-/Quelldigest und explizite
Approval-Policy. Er hat ein persistentes Journal und behandelt unklare Ausgaenge als abzugleichen,
nicht als sicher wiederholbar. Das ist nicht die fluechtige Dev-Operationsqueue. Die Approval-
Integration in einen vollstaendigen Desktop-/MCP-Workflow fehlt: `execute_deployment` verweigert
weiterhin immer `permission_required`. Keine der drei globalen Freigaben erlaubt autonomes Deployment.

Compose akzeptiert nur ein eingeschraenktes selbststaendiges JSON-Manifest mit Digest-Images und
vorhandenem explizitem Kontext/Connection, kein beliebiges YAML, Build, Bind-Mounts, Includes oder
Interpolation; kein implizites Pull, Prune oder Volume-Loeschen. Diese Grenze betrifft Deployment,
nicht beliebige konfigurierte Compose-Runner. Kubernetes akzeptiert JSON-Deployments bzw. Listen
davon mit explizitem Kontext/Namespace und Digest-Images. Bestehende kubeconfig einschliesslich
moeglicher exec-Authentisierung bleibt vertraut; RBAC/TLS sind nicht durch Manifestpruefung ersetzt.
SSH unterstuetzt nur Artefaktupload und Dateistatus/-Logs, mit vorhandenem `known_hosts`, strikter
Hostkey-Pruefung und ohne Remote-App-Start oder Rollback. Cluster-/Hostbereitstellung fehlt;
Rollback ist kein Datenbackup. Live-Tests dieser Zielsysteme sind nicht bestaetigt.
