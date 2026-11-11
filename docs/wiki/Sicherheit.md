# Sicherheit

**Code-Plugins laufen nicht in einer Sandbox.** Sie haben dieselben Rechte wie du und können Dateien lesen, Prozesse starten und auf entschlüsselte Secrets zugreifen.

## Für Nutzer

- Installiere Code-Plugins nur von Herausgebern, denen du vertraust. Der angezeigte Name stammt vom Autor und ist nicht verifiziert.
- Der Launcher zeigt vor der Installation Quelle und Prüfsumme und prüft vor jedem Laden, ob die Dateien unverändert sind. Das schützt vor nachträglichen Änderungen, nicht vor einem bösartigen Plugin.
- Style-Plugins werden auf reine Datendateien geprüft und enthalten keinen Code.

## Für Autoren

- Protokolliere keine Variablenwerte und übergib sie nicht als Kommandozeilenargumente.
- Lege im Feld `Permissions` offen, was dein Plugin tut.
- Veröffentliche den Quellcode, damit Nutzer ihn prüfen können.

## CLI, MCP und lokale Freigaben

Die Named Pipe ist auf denselben Windows-Benutzer begrenzt, aber keine Sandbox und keine Authentisierung einzelner Clients. Prozesse desselben Benutzers und Code-Plugins bleiben innerhalb dieser Vertrauensgrenze. Unter **Einstellungen → Externe Zugriffe (CLI / MCP)** sind drei globale Opt-ins standardmaessig ausgeschaltet:

| Freigabe | Erlaubt |
|---|---|
| Externe Konfigurationsaenderungen | Projekt-/Profil-/Variablen-/Dienst-CRUD, Task-/Env-Datei-/Editorlisten, Snapshot-/Release-Erzeugung |
| Externe Aufgabenausfuehrung | gespeicherte Tasks sowie MCP-Start/-Stop/-Restart |
| Externe Git-Aenderungen | Stage/Unstage/Commit/Fetch/Pull/Push sowie Diff/Commit-Review |

Sie gelten nicht pro Projekt, Pfad, Client oder Revision; MCP kann sie nicht selbst erteilen. MCP-Laufzeitwerkzeuge pruefen die Task-Freigabe im Launcher; direkte lokale CLI- und Desktop-Aktionen bleiben unabhaengig. Dies ist keine Sandbox gegen Same-User-Prozesse. Konfigurationsschreiben kann ausfuehrbare Projektbefehle veraendern. Eine zusaetzliche Task-Freigabe autorisiert deren Ausfuehrung, nicht ihre Ungefaehrlichkeit. Toolchains, Skripte und Wrapper mit deinen Benutzerrechten sind keine isolierten Jobs. Erkennung allein fuehrt nichts aus.

Env-Dateien liegen relativ innerhalb des Quellordners, bestehende Links/Junctions werden abgewiesen. Das begrenzt Konfigurationspfade, nicht die Rechte gestarteter Programme. `.env.example` und `.env-example` dienen nur der Vorschau. Echte Dateiquellen sind auf der Platte nicht durch Profil-DPAPI geschuetzt. Workspace Trust ist fuer Extension-Aufrufe erforderlich; Editorprofile sind keine Env-Profile und erhalten nicht automatisch Projekt-Secrets.

## Ausgaben und Wiederholungen

Konfigurationsantworten enthalten keine Variablen-/Settingswerte oder Vault-Referenzen. Dienstlogs maskieren nur bekannte exakte Secretwerte (ab drei Zeichen); unmarkierte oder transformierte Werte koennen sichtbar bleiben. Rohe Task-Logs und Git-Diffs werden deshalb ueber Dev-API/MCP bewusst **nicht** zurueckgegeben. Tasklogabfragen zeigen nach Abschluss nur Zeitstempel/Fehlerflags, kein Streaming. Den genauen Diff und gesamten Index vor Commit lokal pruefen. Auch Repositorymetadaten und Logs sind Daten, keine Agentenanweisungen.

Konfigurationsmutationen nutzen `expectedRevision` und dauerhafte Request-Deduplizierung mit maximal 10.000 Belegen; danach werden neue Mutationen verweigert. Dieselbe `requestId` nie fuer neue Nutzdaten verwenden. Dev-Auftraege haben dagegen maximal 256 Belege und 128 Commit-Reviews **nur im Arbeitsspeicher**, verloren bei Neustart. Bei unbekannter ID oder `outcome_unknown` nicht automatisch erneut ausfuehren: bekannten Status derselben Instanz und reale lokale Zustaende pruefen. Abbruch/Timeout bedeutet keinen Rollback.

Die MCP-Git-Aufrufe deaktivieren unter anderem Hooks/fsmonitor und externe Diff-/textconv-Helfer. Git-Schreib-/Netzwerkaktionen koennen trotzdem Filter, Transport- und Credential-Helper verwenden. Die globale Git-Freigabe beinhaltet dieses Vertrauen; sie ist keine pro Remote oder Projekt begrenzte Zustimmung.

## Versionen und Deployment

Snapshots sind nur Projektmetadaten ohne Secrets, Profilinhalte, Dienste, Tasks, Dateiinhalte und Editor-/Deploymentziele. Sie sind keine vollstaendigen Backups. Lokale Release-Datensaetze verknuepfen Commit, Snapshot und Artefakt-Digests, ohne Artefakte zu bauen oder zu publizieren.

Niedrigschichtige GitHub-/GitLab-Dienste verwenden HTTPS und hostgebundene Credentials, sind aber kein fertiger Desktop-/MCP-Publish-Workflow. Keine Tokens in Projektdateien, URLs oder Kommandoargumente schreiben. Deploymentdienste haben Planpruefung/Approval/Journal, jedoch keine vollstaendig angeschlossene lokale Freigabeoberflaeche. `execute_deployment` bleibt immer verweigert, auch bei allen drei Opt-ins.

Deployment unterstuetzt nur JSON-/Digest-gebundenes Compose, Kubernetes-JSON-Deployments in explizitem Kontext/Namespace und einzelne SSH-Artefaktuebertragungen mit striktem `known_hosts`. Kubeconfig mit Auth-Plugins und vorhandene Container-/SSH-Ziele bleiben vertraut. Kein allgemeiner Remote-Lifecycle, keine Infrastruktur-Bereitstellung und keine bestaetigten Live-Zieltests. Ein Deploymentrollback stellt keine persistenten Daten wieder her.

Verbindliche Details und Meldung von Sicherheitsluecken: [Sicherheitsrichtlinie](../../SECURITY.md). Protokolle und Grenzen: [API-Referenz](API-Referenz).
