const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { buildSync } = require('esbuild');
const moduleObject = { exports: {} };
const bundled = buildSync({ entryPoints: [path.join(__dirname, '../src/binding.ts')], bundle: true, write: false, platform: 'node', format: 'cjs' });
new Function('module', 'exports', 'require', bundled.outputFiles[0].text)(moduleObject, moduleObject.exports, require);
const { projectForRoot, isProjectId } = moduleObject.exports;

test('explicit GUID binding wins and survives renamed roots', () => {
  const project = { id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', sourceRoot: path.resolve('other') };
  assert.equal(projectForRoot(path.resolve('root'), [project], project.id.toUpperCase()), project);
  assert.equal(projectForRoot(path.resolve('root'), [project], 'deleted'), undefined);
});

test('legacy metadata is tolerated; ambiguous roots never guess', () => {
  const root = path.resolve('root');
  const project = { id: 'one', sourceRoot: root };
  assert.equal(projectForRoot(root, [{ id: 'legacy' }]), undefined);
  assert.equal(projectForRoot(root, [project]), project);
  assert.equal(projectForRoot(root, [project, { id: 'two', sourceRoot: root }]), undefined);
});

test('multi-root folders resolve independently and reject non GUID IDs', () => {
  const projects = [{ id: 'one', sourceRoot: path.resolve('first') }, { id: 'two', sourceRoot: path.resolve('second') }];
  assert.equal(projectForRoot(path.resolve('second'), projects), projects[1]);
  assert.equal(isProjectId('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'), true);
  assert.equal(isProjectId('name & command'), false);
});