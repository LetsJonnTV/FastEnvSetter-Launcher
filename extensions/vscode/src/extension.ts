import { execFile, spawn } from 'node:child_process';
import * as vscode from 'vscode';
import { isProjectId, projectForRoot } from './binding';

interface Service {
  id: string;
  name: string;
  runner: string;
  state: string;
  message: string | null;
}

interface Project {
  id: string;
  name: string;
  favorite: boolean;
  sourceRoot?: string;
  editorTargets?: { id: string; name: string; path: string; profile: string }[];
  services: Service[];
}

type Node = { kind: 'project'; project: Project } | { kind: 'service'; project: Project; service: Service };

const NOT_RUNNING_CODE = 4;
const ACTIVE_STATES = new Set(['Waiting', 'Starting', 'Running', 'Stopping']);

function executable(): string {
  return vscode.workspace.getConfiguration('fastenvsetter').get<string>('executablePath', 'FastEnvSetter.exe');
}

interface CliResult {
  code: number;
  stdout: string;
}

function run(args: string[]): Promise<CliResult> {
  requireTrust();
  return new Promise((resolve, reject) => {
    execFile(executable(), args, { windowsHide: true, encoding: 'utf8', timeout: ['list', 'status'].includes(args[0]) ? 15000 : 120000, maxBuffer: 8 * 1024 * 1024 }, (error, stdout) => {
      if (error && (typeof error.code !== 'number' || error.killed)) {
        reject(new Error(`FastEnvSetter nicht erreichbar. executablePath und Launcher prüfen (${error.message}).`));
        return;
      }
      resolve({ code: error ? Number(error.code) : 0, stdout: stdout.trim() });
    });
  });
}

function requireTrust(): void {
  if (!vscode.workspace.isTrusted) {
    throw new Error('FastEnvSetter erfordert einen vertrauenswürdigen Workspace.');
  }
}

function parseProjects(json: string): Project[] {
  const value: unknown = JSON.parse(json);
  if (!Array.isArray(value) || !value.every((project: Project) => project && isProjectId(project.id)
    && typeof project.name === 'string' && Array.isArray(project.services)
    && project.services.every((service) => service && isProjectId(service.id) && typeof service.name === 'string' && typeof service.state === 'string'))) {
    throw new Error('Ungültige Projektantwort vom Launcher.');
  }
  return (value as Project[]).map((project) => ({ ...project, sourceRoot: typeof project.sourceRoot === 'string' ? project.sourceRoot : undefined }));
}

async function runOrThrow(args: string[]): Promise<string> {
  const result = await run(args);
  if (result.code !== 0) {
    throw new Error(result.stdout || `FastEnvSetter endete mit Code ${result.code}.`);
  }
  return result.stdout;
}

function stateIcon(state: string): vscode.ThemeIcon {
  switch (state) {
    case 'Running':
      return new vscode.ThemeIcon('pass-filled', new vscode.ThemeColor('testing.iconPassed'));
    case 'Failed':
    case 'Blocked':
      return new vscode.ThemeIcon('error', new vscode.ThemeColor('testing.iconFailed'));
    case 'Waiting':
    case 'Starting':
    case 'Stopping':
      return new vscode.ThemeIcon('loading~spin');
    default:
      return new vscode.ThemeIcon('circle-outline');
  }
}

class ProjectsProvider implements vscode.TreeDataProvider<Node> {
  private readonly changed = new vscode.EventEmitter<Node | undefined>();
  readonly onDidChangeTreeData = this.changed.event;
  projects: Project[] = [];
  launcherRunning = false;
  stale = false;

  update(projects: Project[], launcherRunning: boolean): void {
    this.projects = projects;
    this.launcherRunning = launcherRunning;
    this.changed.fire(undefined);
  }

  getTreeItem(node: Node): vscode.TreeItem {
    if (node.kind === 'project') {
      const running = node.project.services.filter((s) => s.state === 'Running').length;
      const item = new vscode.TreeItem(node.project.name, vscode.TreeItemCollapsibleState.Collapsed);
      const currentStatus = this.launcherRunning ? `${running}/${node.project.services.length}` : 'Launcher offline';
      item.description = this.stale ? 'Status veraltet' : currentStatus;
      item.tooltip = node.project.sourceRoot ?? node.project.name;
      item.contextValue = 'project';
      item.iconPath = new vscode.ThemeIcon(node.project.favorite ? 'star-full' : 'folder');
      return item;
    }

    const item = new vscode.TreeItem(node.service.name);
    item.description = this.stale || !this.launcherRunning ? 'Status unbekannt' : node.service.message ?? node.service.state;
    item.contextValue = 'service';
    item.iconPath = this.stale || !this.launcherRunning ? new vscode.ThemeIcon('question') : stateIcon(node.service.state);
    return item;
  }

  getChildren(node?: Node): Node[] {
    if (!node) {
      return this.projects.map((project) => ({ kind: 'project', project }));
    }
    return node.kind === 'project' ? node.project.services.map((service) => ({ kind: 'service', project: node.project, service })) : [];
  }
}

export function activate(context: vscode.ExtensionContext): void {
  const provider = new ProjectsProvider();
  const output = vscode.window.createOutputChannel('FastEnvSetter');
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 20);
  status.command = 'fastenvsetter.startAssociated';
  let pending: Promise<void> | undefined;
  let disposed = false;

  const refreshOnce = async (): Promise<void> => {
    if (!vscode.workspace.isTrusted) {
      provider.update([], false);
      status.text = '$(lock) FastEnvSetter';
      status.tooltip = 'Workspace nicht vertrauenswürdig';
      status.show();
      return;
    }
    try {
      const result = await run(['status', '--json', '--no-start']);
      if (disposed) { return; }
      const running = result.code === 0;
      if (!running && result.code !== NOT_RUNNING_CODE) {
        throw new Error(result.stdout || `Statusabfrage: ${result.code}`);
      }
      const projects = parseProjects(running ? result.stdout : await runOrThrow(['list', '--json', '--no-start']));
      if (disposed) { return; }
      provider.stale = false;
      provider.update(projects, running);
      const active = projects.flatMap((p) => p.services).filter((s) => ACTIVE_STATES.has(s.state)).length;
      status.text = running ? `$(zap) ${active} aktiv` : '$(circle-slash) FastEnvSetter offline';
      status.tooltip = running ? 'FastEnvSetter: Zugeordnetes Projekt starten' : 'Launcher offline; gespeicherte Projekte verfügbar, Dienststatus unbekannt.';
      status.show();
    } catch (error) {
      if (disposed) { return; }
      provider.stale = true;
      provider.update(provider.projects, false);
      status.text = '$(warning) FastEnvSetter veraltet';
      status.tooltip = error instanceof Error ? error.message : String(error);
      status.show();
    }
  };

  const refresh = (): Promise<void> => {
    pending ??= refreshOnce().finally(() => { pending = undefined; });
    return pending;
  };

  const guarded = (action: () => Promise<void>) => async (): Promise<void> => {
    try {
      requireTrust();
      await action();
    } catch (error) {
      void vscode.window.showErrorMessage(error instanceof Error ? error.message : String(error));
    }
    await refresh();
  };

  const startProject = (project: Project): Promise<void> =>
    guarded(async () => {
      await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: `Starte ${project.name}` },
        () => runOrThrow(['start', project.id]),
      );
    })();

  const stopProject = (project: Project): Promise<void> => guarded(async () => void (await runOrThrow(['stop', project.id])))();

  // Ohne laufenden Launcher liefert nur „list“ die gespeicherten Projekte.
  const pick = async (placeHolder: string): Promise<Project | undefined> => {
    const projects = parseProjects(await runOrThrow(['list', '--json', '--no-start']));
    const choice = await vscode.window.showQuickPick(
      projects.map((project) => ({ label: project.name, project })),
      { placeHolder },
    );
    return choice?.project;
  };

  const folder = async (): Promise<vscode.WorkspaceFolder | undefined> => {
    const folders = vscode.workspace.workspaceFolders ?? [];
    if (!folders.length) { throw new Error('Zuerst einen Workspace-Ordner öffnen.'); }
    const selected = folders.length === 1 ? folders[0] : await vscode.window.showWorkspaceFolderPick();
    if (selected && selected.uri.scheme !== 'file') { throw new Error('Lokale Launcher-EXE unterstützt keine Remote-Workspace-Pfade.'); }
    return selected;
  };
  const associated = async (): Promise<Project | undefined> => {
    const selected = await folder();
    if (!selected) { return undefined; }
    const bindings = context.workspaceState.get<Record<string, string>>('projectBindings', {});
    const project = projectForRoot(selected.uri.fsPath, parseProjects(await runOrThrow(['list', '--json', '--no-start'])), bindings[selected.uri.toString()]);
    if (!project) { throw new Error('Keine eindeutige Projektzuordnung. „FastEnvSetter: Workspace zuordnen“ ausführen.'); }
    return project;
  };

  context.subscriptions.push(
    output,
    status,
    vscode.window.registerTreeDataProvider('fastenvsetter.projects', provider),
    vscode.commands.registerCommand('fastenvsetter.refresh', refresh),
    vscode.workspace.onDidGrantWorkspaceTrust(() => void refresh()),
    vscode.commands.registerCommand('fastenvsetter.associate', guarded(async () => {
      const selected = await folder();
      if (!selected) { return; }
      const project = await pick('Projekt für diesen Workspace-Ordner');
      if (!project) { return; }
      const bindings = { ...context.workspaceState.get<Record<string, string>>('projectBindings', {}) };
      bindings[selected.uri.toString()] = project.id;
      await context.workspaceState.update('projectBindings', bindings);
      void vscode.window.showInformationMessage(`${selected.name}: ${project.name} zugeordnet.`);
    })),
    vscode.commands.registerCommand('fastenvsetter.clearAssociation', guarded(async () => {
      const selected = await folder();
      if (!selected) { return; }
      const bindings = { ...context.workspaceState.get<Record<string, string>>('projectBindings', {}) };
      delete bindings[selected.uri.toString()];
      await context.workspaceState.update('projectBindings', bindings);
    })),
    vscode.commands.registerCommand('fastenvsetter.startAssociated', guarded(async () => {
      const project = await associated();
      if (project) { await runOrThrow(['start', project.id]); }
    })),
    vscode.commands.registerCommand('fastenvsetter.stopAssociated', guarded(async () => {
      const project = await associated();
      if (project) { await runOrThrow(['stop', project.id]); }
    })),
    vscode.commands.registerCommand('fastenvsetter.open', guarded(async () => {
      await new Promise<void>((resolve, reject) => {
        const child = spawn(executable(), [], { detached: true, stdio: 'ignore', windowsHide: false });
        child.once('error', reject);
        child.once('spawn', () => { child.unref(); resolve(); });
      });
    })),
    vscode.commands.registerCommand(
      'fastenvsetter.quickStart',
      guarded(async () => {
        const project = await pick('Projekt starten');
        if (project) {
          await startProject(project);
        }
      }),
    ),
    vscode.commands.registerCommand(
      'fastenvsetter.quickStop',
      guarded(async () => {
        const project = await pick('Projekt stoppen');
        if (project) {
          await stopProject(project);
        }
      }),
    ),
    vscode.commands.registerCommand('fastenvsetter.start', (node: Node) => startProject(node.project)),
    vscode.commands.registerCommand('fastenvsetter.stop', (node: Node) => stopProject(node.project)),
    vscode.commands.registerCommand('fastenvsetter.restart', (node: Node) =>
      node.kind === 'service' ? guarded(async () => void (await runOrThrow(['restart', node.service.id])))() : undefined,
    ),
    vscode.commands.registerCommand('fastenvsetter.logs', (node: Node) =>
      node.kind === 'service'
        ? guarded(async () => {
            const text = await runOrThrow(['logs', node.service.id, '--tail', '500']);
            output.clear();
            output.appendLine(text);
            output.show(true);
          })()
        : undefined,
    ),
  );

  const seconds = vscode.workspace.getConfiguration('fastenvsetter').get<number>('refreshSeconds', 5);
  const timer = setInterval(() => void refresh(), Math.max(1, seconds) * 1000);
  context.subscriptions.push({ dispose: () => { disposed = true; clearInterval(timer); } });
  void refresh();
}

export function deactivate(): void {
  // Die Dienste gehören dem Launcher und laufen unabhängig von VS Code weiter.
}
