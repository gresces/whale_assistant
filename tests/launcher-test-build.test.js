'use strict';

/**
 * 测试用启动器必须能免 UAC 启动，正式启动器必须保持提权。
 *
 * 背景：正式版要读管理员运行的微信进程，所以 manifest 是 requireAdministrator，
 * 每次启动都弹 UAC。自动化测试和截图改用 -p:WhaleTestBuild=true 的 asInvoker 产物；
 * 这里锁住三件事：正式版别被改掉、测试版别被发布链路用上、产物别覆盖根目录 EXE。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const read = (rel) => fs.readFileSync(path.join(root, rel), 'utf8');

test('正式启动器保持 requireAdministrator', () => {
  const manifest = read('launcher/app.manifest');
  assert.match(manifest, /requestedExecutionLevel level="requireAdministrator"/);
  assert.doesNotMatch(manifest, /level="asInvoker"/);
});

test('测试启动器是 asInvoker，且不参与提权', () => {
  const manifest = read('launcher/app.test.manifest');
  assert.match(manifest, /requestedExecutionLevel level="asInvoker"/);
  assert.doesNotMatch(manifest, /level="requireAdministrator"/);
  // 说明里必须写清不能替换正式版，避免以后有人图省事直接覆盖
  assert.match(manifest, /不能替换根目录的正式/);
});

test('清单切换只由 WhaleTestBuild 控制，默认仍是正式清单', () => {
  const csproj = read('launcher/WhaleLauncher.csproj');
  assert.match(
    csproj,
    /<ApplicationManifest Condition="'\$\(WhaleTestBuild\)' == 'true'">app\.test\.manifest<\/ApplicationManifest>/,
  );
  assert.match(
    csproj,
    /<ApplicationManifest Condition="'\$\(WhaleTestBuild\)' != 'true'">app\.manifest<\/ApplicationManifest>/,
  );
});

test('只有测试构建脚本会打开 asInvoker，发布链路不受影响', () => {
  const allowed = new Set([
    'launcher/WhaleLauncher.csproj',
    'scripts/build-test-launcher.ps1',
    'scripts/publish-launcher.ps1',
    'tests/launcher-test-build.test.js',
    'docs/启动器测试与提权.md',
  ]);
  const hits = [];
  const walk = (dir) => {
    for (const entry of fs.readdirSync(path.join(root, dir), { withFileTypes: true })) {
      if (['node_modules', 'obj', 'bin', 'publish', '.git', 'artifacts', 'data'].includes(entry.name)) continue;
      const rel = path.posix.join(dir, entry.name);
      if (entry.isDirectory()) { walk(rel); continue; }
      if (!/\.(ps1|js|json|yml|yaml|csproj|md)$/.test(entry.name)) continue;
      if (rel.startsWith('tests/fixtures/')) continue;
      if (read(rel).includes('WhaleTestBuild')) hits.push(rel);
    }
  };
  for (const dir of ['launcher', 'scripts', 'tests', '.github', 'docs']) walk(dir);
  assert.deepEqual(hits.filter((rel) => !allowed.has(rel)), []);
});

test('测试构建产物落在 artifacts，拒绝覆盖正式 EXE', () => {
  const script = read('scripts/build-test-launcher.ps1');
  assert.match(script, /artifacts\\test-launcher/);
  assert.match(script, /拒绝覆盖根目录正式 EXE/);
  // 产物必须自证是 asInvoker：只看参数不够，obj 缓存会让旧清单混进产物
  assert.match(script, /requestedExecutionLevel level="asInvoker"/);
  assert.match(script, /requestedExecutionLevel level="requireAdministrator"/);
  assert.doesNotMatch(script, /Copy-Item[^\n]*WhaleLauncher\.exe[^\n]*root/i);
});
