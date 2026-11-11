import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const app = path.join(root, 'src/FastEnvSetter.App/bin/Release/net10.0-windows/FastEnvSetter.exe');
const mcp = path.join(root, 'src/FastEnvSetter.Mcp/bin/Release/net10.0/FastEnvSetter.Mcp.exe');

async function stop(child) {
    if (child.exitCode !== null || child.signalCode !== null) return;
    const exited = new Promise(resolve => child.once('exit', resolve));
    child.kill();
    await exited;
}

async function waitForPipe(dataPath, child) {
    const deadline = Date.now() + 35000;
    while (true) {
        assert.equal(child.exitCode, null, 'Temporary launcher exited before its pipe was ready.');
        const probe = spawn(app, ['--data', dataPath, '--no-start', 'list', '--json'], { stdio: 'ignore' });
        const exitCode = await new Promise((resolve, reject) => {
            probe.once('error', reject);
            probe.once('close', code => resolve(code));
        });
        if (exitCode === 0) return;
        assert.ok(Date.now() < deadline, 'Temporary launcher pipe did not become ready.');
        await new Promise(resolve => setTimeout(resolve, 50));
    }
}

function client(child) {
    let sequence = 0;
    const pending = new Map();
    const lines = readline.createInterface({ input: child.stdout });
    lines.on('line', line => {
        let response;
        try { response = JSON.parse(line); }
        catch { for (const request of pending.values()) request.reject(new Error('Non-JSON MCP stdout.')); return; }
        if (response.id === undefined) return;
        const request = pending.get(response.id);
        if (!request) return;
        pending.delete(response.id);
        clearTimeout(request.timer);
        if (response.error) request.reject(new Error(JSON.stringify(response.error)));
        else request.resolve(response.result);
    });
    child.once('exit', () => {
        for (const request of pending.values()) {
            clearTimeout(request.timer);
            request.reject(new Error('MCP exited before responding.'));
        }
        pending.clear();
        lines.close();
    });
    return {
        request(method, params = {}) {
            const id = ++sequence;
            return new Promise((resolve, reject) => {
                const timer = setTimeout(() => { pending.delete(id); reject(new Error(`MCP timeout: ${method}`)); }, 30000);
                pending.set(id, { resolve, reject, timer });
                child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
            });
        },
        notify(method) { child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method }) + '\n'); },
    };
}

async function check(executionAllowed) {
    const folder = await mkdtemp(path.join(os.tmpdir(), 'fes-mcp-smoke-'));
    const dataPath = path.join(folder, 'data.json');
    const projectId = randomUUID();
    const serviceId = randomUUID();
    let launcher;
    let server;
    try {
        await writeFile(dataPath, JSON.stringify({
            SchemaVersion: 3,
            Settings: { AllowExternalConfigurationChanges: true, AllowExternalTaskExecution: executionAllowed },
            Projects: [{ Id: projectId, Name: 'Smoke', SourceRoot: folder,
                Services: executionAllowed ? [] : [{ Id: serviceId, Name: 'Never execute', RunnerId: 'node' }] }],
        }));
        launcher = spawn(app, ['--tray', '--safe-mode', '--data', dataPath], { stdio: 'ignore' });
        launcher.on('error', () => {});
        await waitForPipe(dataPath, launcher);
        server = spawn(mcp, ['--data', dataPath, '--launcher', app], { stdio: ['pipe', 'pipe', 'pipe'] });
        server.stderr.resume();
        const rpc = client(server);
        await rpc.request('initialize', { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'smoke-test', version: '1' } });
        rpc.notify('notifications/initialized');
        const tools = (await rpc.request('tools/list')).tools;
        for (const name of ['set_project_tasks', 'set_project_env_files', 'set_editor_targets', 'run_project_task'])
            assert.ok(tools.some(tool => tool.name === name), `Missing tool: ${name}`);
        const call = (name, args) => rpc.request('tools/call', { name, arguments: args });
        const text = response => (response.content ?? []).filter(item => item.type === 'text').map(item => item.text).join('\n');
        const listing = await call('list_projects', {});
        assert.ok(!listing.isError, text(listing));
        const projects = JSON.parse(text(listing));
        assert.equal(projects[0].sourceRoot, folder);
        assert.ok(Array.isArray(projects[0].editorTargets));
        const start = await call('start_project', { project: projectId });
        if (executionAllowed) {
            assert.ok(!start.isError, text(start));
            assert.match(text(start), /gestartet/);
        } else {
            for (const response of [start,
                await call('stop_project', { project: projectId }),
                await call('restart_service', { service: serviceId })]) {
                assert.equal(response.isError, true);
                assert.match(text(response), /permission_required/);
            }
        }
        console.log(`MCP smoke: ${tools.length} tools; execution ${executionAllowed ? 'allowed for empty project' : 'denied for start/stop/restart'}; metadata intact.`);
    } finally {
        if (server) await stop(server);
        if (launcher) await stop(launcher);
        await rm(folder, { recursive: true, force: true });
    }
}

assert.equal(process.platform, 'win32', 'This WPF/MCP smoke test requires Windows and Release builds.');
await check(false);
await check(true);