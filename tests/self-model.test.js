'use strict';

/**
 * 自我模型：校验、原子写、淘汰、敏感内容拒绝、预算裁剪（契约 §2 / §6）。
 * 全部使用占位数据，不写真实身份信息。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const selfModel = require('../src/persona/selfModel');
const access = require('../src/security/access');
const { estimateTokens } = require('../src/infra/textBudget');

function makeConfig() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-self-model-'));
  return {
    root,
    paths: { dataDir: path.join(root, 'data') },
    persona: { name: '助手' },
    env: { timezone: '' },
    cleanup() { fs.rmSync(root, { recursive: true, force: true, maxRetries: 6, retryDelay: 60 }); },
  };
}

test('首次访问创建四份默认文件，已有内容不覆盖', () => {
  const cfg = makeConfig();
  try {
    selfModel.ensureDefaultFiles(cfg);
    for (const file of [
      path.join(cfg.paths.dataDir, 'self-model.json'),
      path.join(cfg.paths.dataDir, 'contact-policy.json'),
      path.join(cfg.paths.dataDir, 'share', 'manifest.json'),
      path.join(cfg.paths.dataDir, 'on-duty', 'pending.json'),
    ]) {
      assert.equal(fs.existsSync(file), true, file + ' 应该被创建');
    }
    const file = path.join(cfg.paths.dataDir, 'self-model.json');
    fs.writeFileSync(file, JSON.stringify({ identity: { name: '已有名字' } }), 'utf8');
    selfModel.ensureDefaultFiles(cfg);
    assert.equal(selfModel.load(cfg).identity.name, '已有名字');
  } finally { cfg.cleanup(); }
});

test('字段白名单 + 长度截断 + 非法 id 丢弃', () => {
  const cfg = makeConfig();
  try {
    const longText = '甲'.repeat(900);
    const checked = selfModel.validate({
      identity: { name: '老王', unknownField: '应丢弃', aliases: ['王工', '王工'] },
      voice: { tone: '话少' },
      knowledge: [
        { id: 'k-ok', title: '项目', text: longText, scopes: ['work'], evil: 'x' },
        { id: 'BAD ID', title: '坏', text: '正文', scopes: ['work'] },
      ],
      unknownTop: 1,
    });
    assert.equal(checked.rejected.length, 0);
    assert.equal(checked.model.knowledge.length, 1);
    assert.equal(checked.model.knowledge[0].text.length, 800);
    assert.deepEqual(checked.model.identity.aliases, ['王工']);
    assert.equal(checked.model.identity.unknownField, undefined);
    assert.ok(checked.warnings.some((w) => /超过 800 字/.test(w)));
    assert.ok(checked.warnings.some((w) => /id 不合法/.test(w)));
    assert.ok(checked.warnings.some((w) => /未知字段/.test(w)));
  } finally { cfg.cleanup(); }
});

test('单类上限按 importance + updatedAt 淘汰', () => {
  const cfg = makeConfig();
  try {
    const knowledge = [];
    for (let i = 0; i < 600; i += 1) {
      knowledge.push({
        id: `k-${i}`,
        title: `条目${i}`,
        text: `正文${i}`,
        scopes: ['work'],
        importance: i / 600,
        updatedAt: i,
      });
    }
    const saved = selfModel.save(cfg, { knowledge });
    assert.equal(saved.model.knowledge.length, 500);
    const ids = new Set(saved.model.knowledge.map((k) => k.id));
    assert.equal(ids.has('k-0'), false, '最低 importance 的条目应被淘汰');
    assert.equal(ids.has('k-599'), true, '最高的应保留');
  } finally { cfg.cleanup(); }
});

test('敏感内容整次拒绝，不留下半截文件', () => {
  const cfg = makeConfig();
  try {
    // 拼出假密钥：字面量会被隐私扫描当成真密钥，测试夹具不该触发它
    const fakeKey = 'sk-' + 'a'.repeat(16);
    for (const text of ['密码是 abc12345', '卡号 6222 0212 3456 7890', fakeKey]) {
      assert.throws(
        () => selfModel.save(cfg, { knowledge: [{ id: 'k-1', title: '敏感', text, scopes: ['work'] }] }),
        /敏感内容/,
        text
      );
    }
    const file = path.join(cfg.paths.dataDir, 'self-model.json');
    const before = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
    assert.ok(!before.includes('abc12345'));
  } finally { cfg.cleanup(); }
});

test('原子写：落盘是合法 JSON，不留 .tmp', () => {
  const cfg = makeConfig();
  try {
    selfModel.save(cfg, { identity: { name: '老王' }, knowledge: [] });
    selfModel.save(cfg, { identity: { name: '老王', city: '杭州' }, knowledge: [] });
    const file = path.join(cfg.paths.dataDir, 'self-model.json');
    const parsed = JSON.parse(fs.readFileSync(file, 'utf8'));
    assert.equal(parsed.identity.city, '杭州');
    assert.equal(parsed.version, 1);
    const leftovers = fs.readdirSync(path.dirname(file)).filter((name) => name.includes('.tmp'));
    assert.deepEqual(leftovers, []);
  } finally { cfg.cleanup(); }
});

test('损坏文件不被覆盖，读回默认模型', () => {
  const cfg = makeConfig();
  try {
    const file = path.join(cfg.paths.dataDir, 'self-model.json');
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, '{not json', 'utf8');
    const model = selfModel.load(cfg);
    assert.equal(model.knowledge.length, 0);
    assert.equal(fs.readFileSync(file, 'utf8'), '{not json', 'data/ 是用户数据，坏文件也不能覆盖');
  } finally { cfg.cleanup(); }
});

test('预算 ≤ 900 token，按 assets → templates → stances → knowledge 反向丢', () => {
  const cfg = makeConfig();
  try {
    const knowledge = [];
    for (let i = 0; i < 30; i += 1) {
      knowledge.push({
        id: `k-${i}`,
        title: `项目${i}`,
        text: '这是一段比较长的项目说明，用来占满预算。'.repeat(8),
        scopes: ['work'],
        importance: 0.9,
      });
    }
    selfModel.save(cfg, {
      knowledge,
      stances: [{ topic: '加班', position: '能不加班就不加班。'.repeat(10), scopes: ['work'] }],
      templates: [{ id: 't-1', title: '客户问进度', text: '按计划推进。'.repeat(10), scopes: ['work'] }],
    });
    const policy = access.policyFor(access.normalizeDoc({
      contacts: { u1: { uid: 'u1', scopes: ['work'], canReceiveAssets: true } },
    }), 'u1');
    const assets = [{ id: 'a-1', title: '架构图', kind: 'image', scopes: ['work'], note: '对外版本'.repeat(20) }];
    const block = selfModel.buildBlock(selfModel.load(cfg), policy, {
      query: '项目', budgetTokens: 200, assets, pending: [],
    });
    assert.ok(block.length > 0);
    assert.ok(estimateTokens(block) <= 200, '实际 ' + estimateTokens(block));
    assert.equal(block.includes('【可发送资产】'), false, 'assets 应该先被丢');
    assert.equal(block.includes('【可用话术】'), false, 'templates 第二个被丢');
    assert.equal(block.includes('【立场】'), false, 'stances 第三个被丢');
    assert.ok(block.includes('【授权信息】'), 'knowledge 最后才丢，应至少保留一条');
  } finally { cfg.cleanup(); }
});

test('private / 不可见项在进入候选集之前就被剔除', () => {
  const cfg = makeConfig();
  try {
    selfModel.save(cfg, {
      knowledge: [
        { id: 'k-work', title: '项目', text: '订单中心', scopes: ['work'] },
        { id: 'k-private', title: '私事', text: '存款五十万', scopes: ['private'] },
        { id: 'k-family', title: '家事', text: '周末回老家', scopes: ['family'] },
      ],
    });
    const policy = access.policyFor(access.normalizeDoc({
      contacts: { u1: { uid: 'u1', scopes: ['work'] } },
    }), 'u1');
    const block = selfModel.buildBlock(selfModel.load(cfg), policy, { query: '存款 回老家 订单' });
    assert.match(block, /订单中心/);
    assert.doesNotMatch(block, /存款五十万/);
    assert.doesNotMatch(block, /周末回老家/);
  } finally { cfg.cleanup(); }
});

test('身份 / 语气 / 强制规则：披露策略只改第 4 条', () => {
  const model = selfModel.validate({
    identity: { name: '老王', job: '后端工程师', city: '杭州', company: '某公司' },
    voice: { tone: '话少、直接', catchphrases: ['行', '我看看'], tabooWords: ['亲'] },
  }).model;
  const identity = selfModel.identityLines(model).join('\n');
  assert.match(identity, /老王/);
  assert.match(identity, /后端工程师/);
  assert.match(identity, /杭州/);

  const voice = selfModel.voiceLines(model, { toneOverride: '对这个人更客气', addressAs: '张经理', tabooTopics: ['涨薪'] }).join('\n');
  assert.match(voice, /对这个人更客气/);
  assert.match(voice, /张经理/);
  assert.match(voice, /涨薪/);

  for (const mode of ['never', 'onAsk', 'always']) {
    const rules = selfModel.ruleLines(model, { discloseAI: mode }).join('\n');
    assert.match(rules, /在岗规则/);
    assert.match(rules, /不透露任何其他会话/);
    if (mode === 'never') assert.match(rules, /是本人/);
    if (mode === 'onAsk') assert.match(rules, /分身代回/);
    if (mode === 'always') assert.match(rules, /主动说明/);
  }
});

test('资产标记解析：剥干净、只采用第一个', () => {
  const parsed = selfModel.parseAssetMarks('好的，发你了 [[asset:a-1]] 还有 [[asset:架构图]]');
  assert.equal(parsed.text, '好的，发你了  还有');
  assert.equal(parsed.marker, 'a-1');
  assert.equal(parsed.count, 2);
  assert.doesNotMatch(parsed.text, /asset/);
});
