'use strict';

/**
 * 外部插件功能已整体移除：这里锁住“不再有插件宿主、插件路由、插件 UI 与插件功能开关”，
 * 防止后续又悄悄把插件入口挂回来。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const { createRouter } = require('../src/http/routes');
const { DEFAULT_CONFIG } = require('../src/infra/config');

const root = path.resolve(__dirname, '..');

test('外部插件系统已移除', () => {
  for (const rel of [
    'src/services/pluginHost.js',
    'src/services/javascriptPlugins.js',
    'src/services/javascriptPluginWorker.js',
    'src/services/messagePlugins.js',
    'src/modules/plugins/manifest.js',
    'launcher/Views/PluginMarketPage.xaml.cs',
    'launcher/Views/PluginToolPage.xaml.cs',
    'launcher/PluginMarket.cs',
    'launcher/PluginTrust.cs',
    'launcher/PluginHostActions.cs',
  ]) {
    assert.equal(fs.existsSync(path.join(root, rel)), false, rel + ' 应已删除');
  }

  const runtime = {};
  const router = createRouter(runtime);
  assert.deepEqual(router.routes.filter((r) => r.path.startsWith('/api/plugins')), [], '不应再注册插件 API');
  assert.deepEqual(
    router.patterns.filter((p) => /api\/plugins/.test(p.regex.source)),
    [],
    '不应再注册插件正则路由',
  );
  assert.equal(runtime.pluginApiRoutes, undefined, '不应再生成插件 API 清单');
  assert.equal(DEFAULT_CONFIG.features?.javascriptPlugins, undefined, '插件总开关应已移除');

  const app = fs.readFileSync(path.join(root, 'src', 'app.js'), 'utf8');
  assert.doesNotMatch(app, /pluginHost|javascriptPlugins|messagePlugins/, '组合根不应再装配插件宿主');
});
