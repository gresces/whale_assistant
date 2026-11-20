'use strict';

/**
 * 提示词画布 HTTP 契约（docs/人设提示词画布.md §2）：
 * GET 只读、POST /block 直接回新预览、新增/删除自定义段落、错误形状。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { startServer } = require('../server');
const { buildPromptParts } = require('../src/persona');

async function boot(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-blocks-api-'));
  fs.writeFileSync(path.join(root, 'config.json'), JSON.stringify({
    env: { autoDetect: false },
    watchConfig: false,
    log: { level: 'error' },
  }));
  const inst = await startServer({ root, env: { WHALE_LOG_FILE: '' }, port: 0, openBrowser: false });
  t.after(async () => {
    await inst.close();
    fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 40 });
  });
  const get = async (p) => {
    const res = await fetch(inst.url + p);
    return { status: res.status, body: await res.json() };
  };
  const post = async (p, body) => {
    const res = await fetch(inst.url + p, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
    return { status: res.status, body: await res.json() };
  };
  const del = async (p) => {
    const res = await fetch(inst.url + p, { method: 'DELETE' });
    return { status: res.status, body: await res.json() };
  };
  return { root, inst, get, post, del };
}

function directSystem(runtime, chatType, isMaster) {
  return buildPromptParts({
    config: runtime.config,
    msg: {
      chatType,
      isMaster,
      senderName: isMaster ? '主人' : (chatType === 'group' ? '群友' : '朋友'),
      chatTitle: chatType === 'group' ? '一个微信群' : '',
    },
  });
}

test('GET /api/persona/prompt：段落映射、system 同源、只读无副作用', async (t) => {
  const world = await boot(t);
  const configFile = path.join(world.root, 'config.json');
  const beforeConfig = fs.readFileSync(configFile, 'utf8');
  const personaFile = path.join(world.inst.runtime.config.paths.dataDir, 'persona.json');

  const res = await world.get('/api/persona/prompt?chatType=private&isMaster=1');
  assert.equal(res.status, 200);
  assert.equal(res.body.ok, true);
  assert.equal(res.body.chatType, 'private');
  assert.equal(res.body.isMaster, true);
  assert.deepEqual(res.body.blocks.map((b) => b.id), [
    'identity', 'personality', 'background', 'speech', 'behavior', 'custom', 'memory',
  ]);
  assert.equal(typeof res.body.system, 'string');
  assert.ok(res.body.system.length > 200);
  assert.equal(typeof res.body.tail, 'string');
  assert.ok(Number(res.body.tokens) > 0);
  assert.ok(Array.isArray(res.body.warnings));

  const direct = directSystem(world.inst.runtime, 'private', true);
  assert.equal(res.body.system, direct.system, '接口必须直接调用 buildPromptParts，不能自己拼');
  assert.equal(res.body.tail, direct.tail);

  const group = await world.get('/api/persona/prompt?chatType=group&isMaster=0');
  assert.equal(group.body.chatType, 'group');
  assert.equal(group.body.isMaster, false);
  assert.equal(group.body.system, directSystem(world.inst.runtime, 'group', false).system);
  assert.match(group.body.blocks.find((b) => b.id === 'speech').derived, /^群聊最多/);

  // GET 只能读：配置、人设文件都不能被创建或改写。
  assert.equal(fs.readFileSync(configFile, 'utf8'), beforeConfig, 'GET 不能改 config.json');
  assert.equal(fs.existsSync(personaFile), false, 'GET 不能凭空创建 persona.json');
});

test('POST /api/persona/block：保存后直接返回新 blocks/system/tokens，GET 能读到', async (t) => {
  const world = await boot(t);
  const saved = await world.post('/api/persona/block', { id: 'personality', text: '冷静、直接、少用感叹号。' });
  assert.equal(saved.status, 200, saved.body.error);
  assert.equal(saved.body.ok, true);
  assert.equal(saved.body.blocks.find((b) => b.id === 'personality').text, '冷静、直接、少用感叹号。');
  assert.match(saved.body.system, /冷静、直接、少用感叹号。/);
  assert.equal(saved.body.tokens, require('../src/memory/tokens').estimateBudgetTokens(saved.body.system));

  const current = await world.get('/api/persona');
  assert.equal(current.status, 200);
  assert.equal(current.body.persona.personality, '冷静、直接、少用感叹号。');
  assert.ok(Array.isArray(current.body.persona.customBlocks), 'GET /api/persona 只加字段，不破坏旧结构');

  const preview = await world.get('/api/persona/prompt?chatType=private&isMaster=1');
  assert.equal(preview.body.system, saved.body.system);
  assert.ok(preview.body.system.includes('冷静、直接、少用感叹号。'));
});

test('POST /api/persona/blocks 与 DELETE：新增段落进入提示词，删除后立刻消失', async (t) => {
  const world = await boot(t);
  const created = await world.post('/api/persona/blocks', { title: '关于我的工作', text: '我在医院上班。' });
  assert.equal(created.status, 200, created.body.error);
  assert.match(created.body.id, /^custom-[0-9a-f]{8}$/);
  assert.equal(created.body.blocks.find((b) => b.id === created.body.id).label, '关于我的工作');
  assert.match(created.body.system, /【关于我的工作】/);
  assert.match(created.body.system, /我在医院上班。/);

  const preview = await world.get('/api/persona/prompt?chatType=group&isMaster=1');
  const block = preview.body.blocks.find((b) => b.id === created.body.id);
  assert.ok(block, '自定义段落要出现在群聊预览里');
  assert.equal(block.text, '我在医院上班。');
  assert.equal(block.maxLength, 2000);

  const removed = await world.del('/api/persona/blocks/' + created.body.id);
  assert.equal(removed.status, 200, removed.body.error);
  assert.ok(!removed.body.blocks.some((b) => b.id === created.body.id));
  assert.doesNotMatch(removed.body.system, /我在医院上班。/);

  const again = await world.del('/api/persona/blocks/' + created.body.id);
  assert.equal(again.status, 400);
  assert.equal(again.body.ok, false);
});

test('POST /api/persona/block：超长 / 敏感 / 固定段拒绝，且返回原因', async (t) => {
  const world = await boot(t);

  const tooLong = await world.post('/api/persona/block', { id: 'background', text: '背'.repeat(501) });
  assert.equal(tooLong.status, 400);
  assert.match(tooLong.body.error, /500 字/);

  const secret = await world.post('/api/persona/block', { id: 'custom', text: '密码是 123456' });
  assert.equal(secret.status, 400);
  assert.match(secret.body.error, /拒绝保存/);

  const fixed = await world.post('/api/persona/block', { id: 'identity', text: '换个身份' });
  assert.equal(fixed.status, 400);
  assert.match(fixed.body.error, /不是可编辑文本/);

  const missing = await world.post('/api/persona/block', { id: 'custom-ffffffff', text: '不存在' });
  assert.equal(missing.status, 400);
  assert.match(missing.body.error, /不存在/);

  const title = await world.post('/api/persona/blocks', { title: '标'.repeat(41), text: '正文' });
  assert.equal(title.status, 400);
  assert.match(title.body.error, /40 字/);
});
