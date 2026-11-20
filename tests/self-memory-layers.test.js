'use strict';

/**
 * 记忆分层 v1.1（契约 §11）：
 *   - 对象披露只写 L2，永远不进 L1；
 *   - 主人事实进 L1 时默认继承当前对象的域，绝不默认 public；
 *   - L2 的 toneOverride 只对当前对象生效；
 *   - 重启后 L1 / L2 不串。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const selfModel = require('../src/persona/selfModel');
const access = require('../src/security/access');
const personCard = require('../src/memory/personCard');
const { Store } = require('../src/memory/store');
const { Bot } = require('../src/pipeline/chat');
const { DEFAULT_CONFIG } = require('../src/infra/config');

function makeConfig() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-layers-'));
  const cfg = JSON.parse(JSON.stringify(DEFAULT_CONFIG));
  cfg.root = root;
  cfg.paths = { dataDir: path.join(root, 'data'), storeFile: path.join(root, 'data', 'store.json') };
  cfg.persona.master = { names: [], wxids: [] };
  cfg.env = { timezone: '' };
  cfg.features.selfModel = true;
  cfg.features.shareAssets = true;
  cfg.onDuty.enabled = false;
  selfModel.ensureDefaultFiles(cfg);
  access.saveDoc(cfg, access.normalizeDoc({
    contacts: {
      A: { uid: 'A', name: '对象 A', scopes: ['work'], onDuty: 'auto', canReceiveAssets: true },
      B: { uid: 'B', name: '对象 B', scopes: ['friend'], onDuty: 'auto', canReceiveAssets: true },
    },  }));
  return {
    cfg,
    policy(uid) { return access.policyFor(access.loadDoc(cfg), uid); },
    cleanup() { fs.rmSync(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 60 }); },
  };
}

test('对象 A 披露的预算只进 A 的 L2，B 的上下文里不出现，L1 里也没有', () => {
  const world = makeConfig();
  try {
    const card = personCard.newCard({ chatId: 'wechat:u:A', uid: 'A', name: '对象 A' });
    const pref = personCard.addPreference(card, {
      text: '他们部门预算 50 万',
      about: 'other',
      scopes: ['work'],
      confidence: 0.9,
      source: 'msg_1',
    }, { chatId: 'wechat:u:A' });
    assert.equal(pref.about, 'other');

    const model = selfModel.save(world.cfg, { knowledge: [] }).model;
    const blockA = selfModel.buildBlock(model, world.policy('A'), {
      query: '预算',
      l2: { preferences: [personCard.preferenceToNote(pref)], threads: [], overrides: null },
    });
    assert.match(blockA, /50 万/);
    assert.match(blockA, /\[关系\]/);

    const blockB = selfModel.buildBlock(model, world.policy('B'), { query: '预算', l2: null });
    assert.doesNotMatch(blockB, /50 万/);
    assert.equal(selfModel.load(world.cfg).knowledge.length, 0, '对象披露不能自动写进 L1');
  } finally { world.cleanup(); }
});

test('主人事实默认只对 A 的域可见；改成 public 后 B 才可见', () => {
  const world = makeConfig();
  try {
    const scopesA = world.policy('A').scopes;
    assert.deepEqual(scopesA, ['work']);
    const written = selfModel.rememberKnowledge(world.cfg, {
      title: '在做订单中心',
      text: '我在做订单中心重构',
      scopes: scopesA, // 契约：默认继承当前对象的域，绝不默认 public
      source: { uid: 'A', chatId: 'wechat:u:A', messageId: 'msg_2', at: 1763600000000 },
    });
    assert.equal(written.created, true);

    let model = selfModel.load(world.cfg);
    assert.match(selfModel.buildBlock(model, world.policy('A'), { query: '订单中心' }), /订单中心/);
    assert.doesNotMatch(selfModel.buildBlock(model, world.policy('B'), { query: '订单中心' }), /订单中心/);

    model.knowledge[0].scopes = ['public'];
    selfModel.save(world.cfg, model);
    model = selfModel.load(world.cfg);
    assert.match(selfModel.buildBlock(model, world.policy('B'), { query: '订单中心' }), /订单中心/);
    assert.match(selfModel.buildBlock(model, world.policy('A'), { query: '订单中心' }), /订单中心/);
  } finally { world.cleanup(); }
});

test('L2 toneOverride / addressAs 只对 A 生效，不写回 L1', () => {
  const world = makeConfig();
  try {
    const model = selfModel.save(world.cfg, {
      identity: { name: '老王' },
      voice: { tone: '默认语气：话少' },
    }).model;
    const overrides = { toneOverride: '对 A 更客气', addressAs: '张经理', tabooTopics: ['涨薪'] };
    const voiceA = selfModel.voiceLines(model, overrides).join('\n');
    const voiceB = selfModel.voiceLines(model, null).join('\n');
    assert.match(voiceA, /对 A 更客气/);
    assert.match(voiceA, /张经理/);
    assert.match(voiceA, /涨薪/);
    assert.doesNotMatch(voiceB, /对 A 更客气/);
    assert.match(voiceB, /默认语气：话少/);

    const blockA = selfModel.buildBlock(model, world.policy('A'), {
      query: '在吗',
      l2: { preferences: [], threads: [], overrides },
    });
    const blockB = selfModel.buildBlock(model, world.policy('B'), {
      query: '在吗',
      l2: { preferences: [], threads: [], overrides: null },
    });
    assert.match(blockA, /【对这个人】/);
    assert.match(blockA, /张经理/);
    assert.doesNotMatch(blockB, /张经理/);
    // 覆盖值不能反向写回 L1
    assert.equal(selfModel.load(world.cfg).voice.tone, '默认语气：话少');
  } finally { world.cleanup(); }
});

test('写入分流：主人发言进 L1，对象披露只进 L2', async () => {
  const world = makeConfig();
  const store = new Store({ file: world.cfg.paths.storeFile, limits: {} });
  try {
    const cfg = world.cfg;
    cfg.onDuty.enabled = true;
    cfg.onDuty.workingHours = { start: '00:00', end: '23:59', days: [0, 1, 2, 3, 4, 5, 6] };
    cfg.persona.master = { names: [], wxids: ['wxid_master_placeholder'] };
    access.saveDoc(cfg, access.normalizeDoc({
      contacts: {
        A: { uid: 'A', name: '对象 A', scopes: ['work'], onDuty: 'auto' },
        B: { uid: 'B', name: '对象 B', scopes: ['friend'], onDuty: 'auto' },
        wxid_master_placeholder: { uid: 'wxid_master_placeholder', scopes: ['work'], onDuty: 'off' },
      },
    }));
    const bot = new Bot({ config: cfg, store, llm: { isMock: true }, logger: null, mockChunkDelayMs: 0 });
    await bot.handle({
      platform: 'wechat',
      chatId: 'wxid_master_placeholder',
      chatType: 'private',
      senderId: 'wxid_master_placeholder',
      senderName: '主人',
      text: '记住：我在做订单中心重构',
    });
    let model = selfModel.load(cfg);
    assert.equal(model.knowledge.length, 1);
    assert.deepEqual(model.knowledge[0].scopes, ['work']);

    await bot.handle({
      platform: 'wechat',
      chatId: 'A',
      chatType: 'private',
      senderId: 'A',
      senderName: '对象 A',
      text: '记住：我喜欢50万的预算',
    });
    model = selfModel.load(cfg);
    assert.equal(model.knowledge.length, 1, '对象披露不能进 L1');
    const profileA = store.profile('wechat:u:A', 'A', '对象 A');
    const note = profileA.notes.find((item) => /50\s*万/.test(item.text));
    assert.ok(note);
    assert.equal(note.about, 'other');
  } finally {
    store.close();
    world.cleanup();
  }
});

test('重启后 L1 / L2 不串：A 的关系卡和 B 的空卡分得开', () => {
  const world = makeConfig();
  try {
    // L1：只有 work 域的知识
    selfModel.rememberKnowledge(world.cfg, {
      title: '在做订单中心',
      text: '我在做订单中心重构',
      scopes: ['work'],
      source: { uid: 'A', chatId: 'wechat:u:A', messageId: 'msg_3', at: 1763600000000 },
    });
    // L2：A 的卡写一条披露 + 语气覆盖
    const store1 = new Store({ file: world.cfg.paths.storeFile, limits: {} });
    const keyA = 'wechat:u:A';
    const chatA = store1.chatStore.ensureChat(keyA, { platform: 'wechat', chatType: 'private', title: 'A' });
    const cardA = store1.chatStore.ensurePerson(chatA, 'A', '对象 A');
    personCard.addPreference(cardA, {
      text: '他们部门预算 50 万',
      about: 'other',
      scopes: ['work'],
      source: 'msg_4',
    }, { chatId: keyA });
    cardA.overrides = { toneOverride: '对 A 更客气', addressAs: 'A', tabooTopics: [] };
    store1.chatStore.saveCard(chatA, cardA);
    store1.close();

    // 模拟重启：重新建 Store / 重新 load self-model
    const store2 = new Store({ file: world.cfg.paths.storeFile, limits: {} });
    try {
      const profileA = store2.profile(keyA, 'A', '对象 A');
      assert.ok(profileA.notes.some((note) => /50 万/.test(note.text) && note.about === 'other'));
      assert.ok(Array.isArray(profileA.notes.find((note) => /50 万/.test(note.text)).scopes));
      assert.equal(profileA.overrides.toneOverride, '对 A 更客气');

      const profileB = store2.profile('wechat:u:B', 'B', '对象 B');
      assert.equal(profileB.notes.length, 0, 'B 不能读到 A 的 L2');
      assert.equal(profileB.overrides.toneOverride, '');

      const model = selfModel.load(world.cfg);
      assert.equal(model.knowledge.length, 1);
      assert.deepEqual(model.knowledge[0].scopes, ['work']);
      assert.match(selfModel.buildBlock(model, world.policy('A'), { query: '订单中心' }), /订单中心/);
      assert.doesNotMatch(selfModel.buildBlock(model, world.policy('B'), { query: '订单中心' }), /订单中心/);
      // A 的 L2 只在带着 A 的卡时才出现
      const blockB = selfModel.buildBlock(model, world.policy('B'), {
        query: '预算',
        l2: { preferences: profileB.notes, threads: [], overrides: null },
      });
      assert.doesNotMatch(blockB, /50 万/);
    } finally { store2.close(); }
  } finally { world.cleanup(); }
});
