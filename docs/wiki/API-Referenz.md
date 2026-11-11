# API-Referenz

Die Plugin-API 1.0 und die versionierten lokalen Konfigurations-/Entwicklungsprotokolle sind getrennte Vertraege. Die Protokollversion `1` ist weder die App-Version noch die gespeicherte Konfigurationsrevision.

## Plugin-API 1.0

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

## Lokale Konfigurations-API v1

`IConfigurationService.Execute(ConfigurationRequest)` bzw. `ExecuteJson` fuehrt gemeinsame Konfigurationsoperationen aus. MCP uebermittelt ueber die benutzergebundene Named Pipe genau `['api', '<JSON>']` (String-Array; einfache Anfuehrungszeichen hier nur zur Darstellung). Keine Secrets oder vollstaendigen JSON-Schreibanfragen als OS-Kommandozeilenargumente uebergeben.

JSON verwendet camelCase. Die Anfrage enthaelt `version: 1`, `operation` und passende GUIDs `projectId`, `profileId`, `serviceId`, `variableId` bzw. `runnerId`. Jede Mutation braucht eine nichtleere `requestId` und die aktuelle `expectedRevision`, Lesen nicht. Die Antwort lautet `{success, code, revision, data, entityId, replayed}`. Erst `configuration_capabilities` abfragen und `success`/`code` auswerten, nicht allein den Transport-Exitcode.

| Bereich | API-Operationen | Nutzdaten / MCP-Abweichung |
|---|---|---|
| Projekte | `list_projects`, `get_project`, `create_project`, `update_project`, `delete_project` | `project`: name, sourceRoot, isFavorite, tags; MCP `list_projects` nutzt die Legacy-Statusprojektion |
| Profile | `list_profiles`, `get_profile`, `create_profile`, `update_profile`, `delete_profile` | `profile`: name, parentId; MCP-Namen verwenden `env_profile`, z. B. `get_env_profile`; `list_env_profiles` ist Legacy |
| Variablen | `list_variables`, `get_variable`, `create_variable`, `update_variable`, `delete_variable` | `variable`: name, value, isSecret, secretRef; MCP verwendet `env_variable`, z. B. `create_env_variable`, plus `set_env_variable_reference` |
| Dienste | `list_services`, `get_service`, `create_service`, `update_service`, `delete_service` | `service`: name, runnerId, workingDirectory, settings, dependsOn, preSteps, readiness, outputMode, restartPolicy, startDelaySeconds, expectedPorts, openUrl |
| Auswahl | `set_project_profiles`, `set_variable_selection` | geordnete `profileIds` bzw. `disabledVariableIds`; optional serviceId |
| Aufgaben | `set_project_tasks` | `projectId`, komplette Liste `tasks`: id, name, kind, fileName, arguments, workingDirectory, dependsOn, timeoutSeconds |
| Env-Dateiquellen | `set_project_env_files` | `projectId`, komplette geordnete Liste `environmentFiles` relativer Pfade; Example-Dateien ausgeschlossen |
| Editorziele | `set_editor_targets` | `projectId`, komplette Liste `editorTargets`: id, name, path, profile; vorhandener relativer Ordner oder `.code-workspace` |
| Diagnose | `capabilities`, `list_runners`, `runner_schema`, `validate`, `preview_environment` | MCP: `configuration_capabilities`, `list_runners`, `get_runner_schema`, `validate_configuration`, `preview_environment` |
| Batch | `batch` | 1-100 `changes`, keine verschachtelten Batches; MCP `apply_configuration_batch` |

Die drei Listenoperationen **ersetzen** jeweils die gesamte Liste; eine leere Liste entfernt alle Eintraege. Task-/Editor-IDs werden dabei vom Aufrufer als eindeutige, nichtleere GUIDs geliefert, Task-Abhaengigkeiten beziehen sich auf diese IDs. Das ist anders als bei `create_project`/`create_profile`/`create_variable`/`create_service`, die IDs serverseitig vergeben. Tasks verwenden `ProjectTaskKind`: Prepare, Build, Test, Run (JSON standardmaessig numerisch 0-3); Argumente sind eine Liste, keine Shell-Befehlszeile. Task-Zeitlimits liegen zwischen 1 und 86.400 Sekunden. Arbeitsordner, dateibasierte Programme und Dateiquellen bleiben innerhalb von `sourceRoot`; Links/Junctions sind ausgeschlossen. Die API schreibt keine Env-Dateiinhalte und oeffnet keine Editoren.

Projekt-/Profil-/Dienst-Updates ersetzen ihre editierbaren Felder, ausgelassene Optionen erhalten Defaults. `update_project` ersetzt nicht Tasks, Env-Dateien oder Editorziele. Ein ausgelassener Variablenwert bleibt nur bei unveraendertem Secretmodus erhalten; `""` setzt einen Leerwert. `value` und `secretRef` sind gegenseitig ausgeschlossen. Variablenscope ist entweder `profileId` oder `projectId` plus `serviceId`.

Profil-Loeschung entfernt Zuweisungen und haengt Kinder an dessen Elternprofil um; Variablen-Loeschung bereinigt Auswahllisten. Dienst-Loeschung entfernt Abhaengigkeitskanten in gestoppten Diensten. Aktive Dienste blockieren betroffene Dienst-/Projektbearbeitung und Task-/Env-Dateilistenersetzung. Umgebungswechsel aendern keine bereits gestarteten Prozesse.

Schreiben erfordert die lokale globale Freigabe `AllowExternalConfigurationChanges`. Es validiert den Gesamtzustand und persistiert vor Erfolg atomar ueber den Store; Batches erhalten eine gemeinsame Revision. Neue serverseitige IDs koennen innerhalb desselben Batches nicht referenziert werden. Die Antwort liefert nur die letzte betroffene `entityId`; neue Entitaeten anschliessend auflisten. Direkte mutable UI-/Plugin-Aenderungen sind nicht vollstaendig in diese Transaktion eingebunden.

Persistente Replay-Belege enthalten Request-ID, Revision und Entitaets-ID, keine Nutzdaten. Dieselbe ID liefert den alten Beleg auch bei anderem Payload: niemals fuer einen neuen Auftrag wiederverwenden. Bei 10.000 Belegen verweigert die API mit `replay_capacity_reached` weitere Mutationen, statt alte Belege zu verdraengen. Ein Restore/Zuruecksetzen der Belege schraenkt den Replay-Schutz ein.

Antworten enthalten keine Variablenwerte, verschluesselten Werte, Vault-Referenzen oder Runner-Settingswerte. Projektansichten zeigen sichere Task-/Editor-Metadaten und Dateiquellenanzahl/-Example-Flags, nicht komplette ausfuehrbare Einstellungen. `preview_environment` zeigt Gewinner/Provenienz gespeicherter Profile und Dienst-Overrides, **keine** aufgeloeste Hostumgebung, Datei- oder Vaultwerte. `preview_env_files` ist eine separate Dev-API-Vorschau ohne Werte.

Anfragen sind auf 131.072 Zeichen/Tiefe 24, Antworten auf 524.288 Zeichen begrenzt. Typische Fehler: `invalid_request`, `unknown_operation`, `permission_required`, `revision_conflict`, `not_found`, `missing_reference`, `cycle_detected`, `validation_failed`, `value_required`, `runner_not_found`, `service_active`, `persistence_failed`, `replay_capacity_reached`.

## Entwicklungs-API v1

`IDevelopmentService.ExecuteJsonAsync` erhaelt ueber die Pipe `['dev', '<JSON>']`. Jede Anfrage braucht `version: 1`, `operation` und eine vorhandene `projectId` (auch `capabilities`). Antwort: `{success, code, data, version}`. Diese Antwort hat keine globale `revision` auf oberster Ebene; die aktuelle Revision ueber Capabilities bzw. die Konfigurations-API lesen.

| Bereich | Dev-Operationen | MCP-Namen / Grenzen |
|---|---|---|
| Faehigkeiten | `capabilities` | `development_capabilities`; globale Opt-ins und Grenzen |
| Tasks / Env | `discover_project_tasks`, `list_project_tasks`, `run_project_task`, `preview_env_files` | gleiche Namen; Erkennung speichert/startet nichts; Metadaten ohne Befehle/Variablenwerte |
| Operationen | `get_operation`, `get_operation_logs`, `cancel_operation` | `get_development_operation`, `get_development_operation_logs`, `cancel_development_operation` |
| Git lesen | `get_git_status`, `get_git_history`, `get_git_branches`, `get_git_diff` | gleiche Namen; Historie bis 100; Diff liefert nur Zeilenstatistik und braucht Git-Freigabe |
| Index / Commit | `stage_git_files`, `unstage_git_files`, `review_git_commit`, `commit_git_changes` | gleiche Namen; explizite relative Pfade, vorheriger Review-Fingerprint, kein automatisches Stage/Review |
| Git-Netzwerk | `fetch_git`, `pull_git`, `push_git` | gleiche Namen; konfigurierter Remote (Default origin), Pull nur Fast-Forward, kein Force-Push |
| Versionen | `list_project_snapshots`, `create_project_snapshot`, `list_project_releases`, `create_project_release` | gleiche Namen; lokale Metadaten, kein Remote-Publish |
| Deployment | `execute_deployment` | immer `permission_required` |

`run_project_task` benoetigt `taskId` und `AllowExternalTaskExecution`. Auch MCP-Start/-Stop/-Restart pruefen diese Freigabe ueber `external-start`, `external-stop` und `external-restart` im Launcher. Direkte lokale CLI-/Desktop-Aktionen sind davon unabhaengig. Schreibende Git-Aktionen sowie Diff/Review benoetigen `AllowExternalGitChanges`. Snapshot-/Release-Erzeugung braucht `AllowExternalConfigurationChanges` und `expectedRevision`. Alle drei Flags sind lokal, global, standardmaessig aus; die APIs koennen keine Rechte erteilen. Sie sind keine Isolation gegen andere Prozesse desselben Benutzers.

Task-, Git-Schreib-, Snapshot- und Release-Auftraege brauchen `requestId`; die Queue antwortet sofort mit `data.operationId = requestId`. Status mit `operationId` abfragen: `queued`, `running`, `succeeded`, `failed`, `cancelled`; das innere `result` gesondert pruefen. Abbruch fordert Cancellation an, macht externe Wirkungen aber nicht rueckgaengig. Bei veraenderter Konfiguration kann der Auftrag vor Ausfuehrung mit `configuration_changed` oder `revision_conflict` scheitern.

Hoechstens **256 Operationsbelege** und **128 Commit-Reviews** bleiben im Speicher des aktuellen Launchers, auch abgeschlossene Operationen zaehlen. Sie gehen bei Neustart verloren. Derselbe Request mit derselben ID wird dort dedupliziert, anderer Inhalt ergibt `request_id_conflict`. Bei Pipe-Abbruch kann MCP `outcome_unknown` melden: bekannten Status auf derselben Instanz pruefen, bei unbekannter ID **nicht automatisch erneut ausfuehren**. Eine fehlende Antwort beweist weder Erfolg noch Nichtexekution. Das ist nicht der dauerhafte 10.000-Beleg-Vertrag der Konfigurations-API.

Task-Erkennung liefert Vorschlaege ohne rohe Kommandos und persistiert sie nicht. Zum Speichern braucht der Client explizite, gepruefte Taskdefinitionen fuer `set_project_tasks` oder die lokale Desktop-Uebernahme; es gibt keinen blinden Accept-by-Suggestion-ID-Vertrag. `get_operation_logs` liefert nach Taskabschluss Zeitstempel/Fehlerflags und `[output withheld]`, keine rohen Logs und kein Streaming. Git-Diff-/Review-Antworten enthalten nur hinzugefuegte/entfernte Zeilenanzahl. Den vollstaendigen Index vor `commit_git_changes` lokal pruefen; Fingerprint schuetzt vor Zustandswechseln, ersetzt aber keine Inhaltspruefung.

## Versionen, Hosting und Deployment

Die globale App-Konfigurationsrevision, projektbezogene Snapshot-Revision und Git-SHA sind verschieden. Snapshots erfassen nur Name, Quellordner, Favorit, Profilzuweisungen und deaktivierte Variablen-IDs samt Hash. Keine Secrets, Profilinhalte, Dienste, Tasks, Editor-/Deploymentziele oder Quelldateien: **kein vollstaendiges Backup**. Restore-Helfer existieren niedrigschichtig; ein Restore-Endpunkt in Dev-API/MCP fehlt.

`create_project_release` benoetigt `releaseVersion`, einen im Repository vorhandenen vollstaendigen `commitSha`, eine vorhandene Snapshot-`configurationRevision`, mindestens einen benannten `artifactDigests`-Eintrag (`sha256:<64 Hexzeichen>`), `expectedRevision` und `requestId`. Das speichert eine lokale Bindung, baut keine Artefakte und beweist weder deren Verfuegbarkeit noch einen vollstaendigen Konfigurationsstand.

`GitHostingService` implementiert GitHub-/GitLab-Metadaten und Release-Publikation mit HTTPS, hostgebundenen Credentials, begrenzter Pagination und Timeouts. PR-/MR-Links sind keine vollstaendige Reviewverwaltung. Kein durchgaengiger Desktop-/MCP-Publish-Workflow ist angeschlossen.

`DeploymentService` und Journal enthalten Plan-/Digest-Pruefung, explizite Approval-Policy und Behandlung unklarer Ausgaenge. Compose ist ein eingeschraenktes JSON-/Digest-Subset ohne Build/Interpolation/Bind-Mounts oder implizites Pull/Volume-Loeschen; bestehender Kontext/Connection ist Pflicht. Kubernetes akzeptiert JSON-Deployments bzw. Listen davon mit explizitem Namespace/Kontext und Digest-Images; Rollback nur auf eine explizite bestehende Deploymentrevision, nicht fuer Datenbanken. SSH uebertraegt ein einzelnes Artefakt mit vorhandenem `known_hosts`, bietet Dateistatus/-Logs, aber keinen Remote-Start oder Rollback. Das sind keine Sandboxen und keine fertig integrierte Deploymentoberflaeche. Live-Zieltests sind nicht bestaetigt; autonomes MCP-Deployment bleibt gesperrt.
