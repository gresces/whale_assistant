'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { startServer } = require('../server');
const presets = require('../src/persona/presets');
const { saveLocalConfiguration, readSnapshot } = require('../src/services/launcherSnapshot');

async function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-portable-'));
  fs.writeFileSync(path.join(root, 'config.json'), JSON.stringify({
    watchConfig: false, env: { autoDetect: false }, features: { memorySnapshot: false },
    persona: { master: { names: ['private-owner'], wxids: ['wxid_test_owner'] } },
  }));
  const app = await startServer({ root, env: { WHALE_LOG_FILE: '' }, port: 0 });
  t.after(async () => { await app.close(); fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }); });
  return { ...app, root };
}

async function post(app, route, body) {
  const response = await fetch(app.url + route, { method: 'POST', headers: { 'content-type': 'application/json',
    ...(route === '/api/care/sent' ? { 'x-bot-token': app.runtime.gateways.get('wechat').options.inboundToken, 'x-whale-bridge': '1' } : {}) }, body: JSON.stringify(body) });
  const result = await response.json();
  assert.equal(result.ok, true, result.error);
  return result;
}

const fields = {
  name: '小鱼', age: 31, job: '画家', appearance: '', personality: '沉静',
  background: '', customPrompt: '', ownerName: '老大', aliases: ['小鱼儿'],
  masterRoast: true, proactive: false, opinions: false, emoji: '少', emojiChance: 0.8,
  privateMaxChars: 188, groupMaxChars: 88, historyTurns: 350, groupHistoryTurns: 250,
  maxBubbles: 4, discussionMaxChars: 600,
};

test('单文件完整保存表达设置，导出白名单不包含账号与运行配置', async t => {
  const app = await fixture(t);
  await post(app, '/api/persona', fields);
  const result = await post(app, '/api/persona/preset/save', { name: '安静的小鱼' });
  const file = path.join(app.root, 'data/personas', result.preset.id + '.json');
  const saved = JSON.parse(fs.readFileSync(file, 'utf8'));
  assert.equal(saved.personaName, fields.name);
  for (const key of Object.keys(fields).filter(k => k !== 'name')) assert.deepEqual(saved[key], fields[key], key);
  const exported = (await post(app, '/api/persona/export', {})).persona;
  assert.equal(exported.schemaVersion, 1);
  // customBlocks 是可分享字段（契约 v1）；空数组也要显式往返，切预设时才能清空旧段落。
  assert.deepEqual(presets.toPatch(exported), { ...fields, customBlocks: [] });
  assert.doesNotMatch(JSON.stringify(exported), /private-owner|wxid_test_owner|apiKey|permissions|mutedGroups/);
});

test('本地自建预设可离线导入', async t => {
  const app = await fixture(t);
  const created = await post(app, '/api/persona/preset/save', { name: '离线自建' });
  await post(app, '/api/persona', { customPrompt: '离线正文' });
  const result = await saveLocalConfiguration(app.root, { path: '/api/persona/preset', body: { id: created.preset.id } }, {});
  assert.equal(result.ok, true);
  const exported = await saveLocalConfiguration(app.root, { path: '/api/persona/export', body: {} }, {});
  assert.equal(exported.persona.customPrompt, '离线正文');
  const snapshot = readSnapshot(app.root, {});
  assert.equal(snapshot['/api/persona'].persona.customPrompt, '离线正文');
});

test('人设页气泡数保存后读取和离线重载一致', async t => {
  const app = await fixture(t);
  await post(app, '/api/persona', { maxBubbles: 5, historyTurns: 180 });
  const current = await (await fetch(app.url + '/api/persona')).json();
  assert.equal(current.persona.maxBubbles, 5);
  assert.equal(readSnapshot(app.root, {})['/api/persona'].persona.maxBubbles, 5);
});

test('自定义段落随人设包导出 / 导入往返，显式空数组能清空', async t => {
  const app = await fixture(t);
  const blocks = [
    { id: 'custom-1234abcd', title: '关于我的工作', text: '我在医院上班。' },
    { id: 'custom-5678abcd', title: '作息', text: '晚上十一点后不回消息。' },
  ];
  await post(app, '/api/persona', { customBlocks: blocks });
  const exported = (await post(app, '/api/persona/export', {})).persona;
  assert.deepEqual(exported.customBlocks, blocks, '导出包要带自定义段落');

  // 模拟离线导入：把导出的人设包落成一份自建预设，再应用。
  const presetId = 'user-blocks-roundtrip';
  const dir = path.join(app.root, 'data/personas');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, presetId + '.json'), JSON.stringify({
    ...exported, id: presetId, name: '往返', source: 'user',
  }));
  await post(app, '/api/persona', { sourcePreset: '', customBlocks: [] });
  assert.deepEqual(app.runtime.config.persona.customBlocks, [], '显式空数组能清空');
  const applied = await post(app, '/api/persona/preset', { id: presetId });
  assert.deepEqual(applied.persona.customBlocks, blocks, '导入后段落完全一致');
  assert.deepEqual(app.runtime.config.persona.customBlocks, blocks);
});

test('主动搭话成功记录进入对应私聊上下文', async t => {
  const app = await fixture(t);
  await post(app, '/api/care/sent', { chat: 'contact-test', text: '刚才主动说的话', group: false });
  const messages = app.runtime.store.messages('wechat:u:contact-test', 10);
  assert.equal(messages.at(-1).role, 'assistant');
  assert.equal(messages.at(-1).text, '刚才主动说的话');
  assert.equal(app.runtime.store.messages('wechat:g:contact-test', 10).length, 0);
  const receipt = { id: 'same-receipt', chat: 'contact-test', text: '有回执的主动消息' };
  await post(app, '/api/care/sent', receipt);
  const repeated = await post(app, '/api/care/sent', receipt);
  assert.equal(repeated.duplicate, true);
  assert.equal(app.runtime.store.messages('wechat:u:contact-test', 10).filter(m => m.text === receipt.text).length, 1);
  app.runtime.gateways.get('wechat').options.inboundToken = 'test-only-token';
  const rejected = await fetch(app.url + '/api/care/sent', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(receipt) });
  assert.equal(rejected.status, 403);
});
