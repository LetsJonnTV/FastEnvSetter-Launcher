import { execFile, spawn } from 'node:child_process';
import * as vscode from 'vscode';

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
  return new Promise((resolve, reject) => {
    execFile(executable(), args, { windowsHide: true, encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 }, (error, stdout) => {
      if (error && typeof error.code !== 'number') {
        reject(new Error(`FastEnvSetter.exe wurde nicht gefunden (${error.message}).`));
        return;
      }
      resolve({ code: error ? Number(error.code) : 0, stdout: stdout.trim() });
    });
  });
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

  update(projects: Project[], launcherRunning: boolean): void {
    this.projects = projects;
    this.launcherRunning = launcherRunning;
    this.changed.fire(undefined);
  }

  getTreeItem(node: Node): vscode.TreeItem {
    if (node.kind === 'project') {
      const running = node.project.services.filter((s) => s.state === 'Running').length;
      const item = new vscode.TreeItem(node.project.name, vscode.TreeItemCollapsibleState.Collapsed);
      item.description = `${running}/${node.project.services.length}`;
      item.contextValue = 'project';
      item.iconPath = new vscode.ThemeIcon(node.project.favorite ? 'star-full' : 'folder');
      return item;
    }

    const item = new vscode.TreeItem(node.service.name);
    item.description = node.service.message ?? node.service.state;
    item.contextValue = 'service';
    item.iconPath = stateIcon(node.service.state);
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
  status.command = 'fastenvsetter.quickStart';

  const refresh = async (): Promise<void> => {
    try {
      const result = await run(['status', '--json', '--no-start']);
      const running = result.code !== NOT_RUNNING_CODE;
      const projects: Project[] = running && result.code === 0 ? (JSON.parse(result.stdout) as Project[]) : provider.projects;
      provider.update(projects, running);
      const active = projects.flatMap((p) => p.services).filter((s) => ACTIVE_STATES.has(s.state)).length;
      status.text = `$(zap) ${active} aktiv`;
      status.tooltip = running ? 'FastEnvSetter: Projekt starten' : 'FastEnvSetter läuft nicht. Klicken startet ein Projekt.';
      status.show();
    } catch {
      status.text = '$(warning) FastEnvSetter';
      status.tooltip = 'FastEnvSetter.exe nicht gefunden. Pfad unter „fastenvsetter.executablePath“ setzen.';
      status.show();
    }
  };

  const guarded = (action: () => Promise<void>) => async (): Promise<void> => {
    try {
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
    const projects = JSON.parse(await runOrThrow(['list', '--json'])) as Project[];
    const choice = await vscode.window.showQuickPick(
      projects.map((project) => ({ label: project.name, project })),
      { placeHolder },
    );
    return choice?.project;
  };

  context.subscriptions.push(
    output,
    status,
    vscode.window.registerTreeDataProvider('fastenvsetter.projects', provider),
    vscode.commands.registerCommand('fastenvsetter.refresh', refresh),
    vscode.commands.registerCommand('fastenvsetter.open', () => {
      spawn(executable(), [], { detached: true, stdio: 'ignore', windowsHide: false }).unref();
    }),
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
  context.subscriptions.push({ dispose: () => clearInterval(timer) });
  void refresh();
}

export function deactivate(): void {
  // Die Dienste gehören dem Launcher und laufen unabhängig von VS Code weiter.
}
