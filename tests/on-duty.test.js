'use strict';

/**
 * 在岗模式：生效条件、draft / auto、敏感词、待确认队列（契约 §4 / §11）。
 * 用假模型和临时目录，不连真实微信、不写真实身份。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { Bot } = require('../src/pipeline/chat');
const { Store } = require('../src/memory/store');
const { DEFAULT_CONFIG } = require('../src/infra/config');
const selfModel = require('../src/persona/selfModel');
const access = require('../src/security/access');
const { SelfService } = require('../src/modules/self/service');
const { ReminderService } = require('../src/modules/reminders');

function makeWorld({ onDuty = {}, policy = {}, model = null } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-on-duty-'));
  const cfg = JSON.parse(JSON.stringify(DEFAULT_CONFIG));
  cfg.root = root;
  cfg.paths = { dataDir: path.join(root, 'data'), settingsFile: path.join(root, 'data', 'settings.json') };
  cfg.persona.master = { names: [], wxids: [] };
  cfg.env.timezone = 'Asia/Shanghai';
  cfg.onDuty = {
    ...cfg.onDuty,
    enabled: true,
    workingHours: { start: '09:00', end: '19:00', days: [0, 1, 2, 3, 4, 5, 6] },
    ...onDuty,
  };
  selfModel.ensureDefaultFiles(cfg);
  selfModel.save(cfg, model || {
    identity: { name: '老王', job: '后端工程师', city: '杭州' },
    voice: { tone: '话少、直接' },
    knowledge: [
      { id: 'k-work', title: '项目', text: '订单中心重构，12 月上线', scopes: ['work'], importance: 0.9 },
      { id: 'k-family', title: '家事', text: '周末回老家看父母', scopes: ['family'], importance: 0.9 },
      { id: 'k-private', title: '私事', text: '存款五十万', scopes: ['private'], importance: 0.9 },
      { id: 'k-public', title: '公开', text: '你好，我是老王', scopes: ['public'], importance: 0.5 },
    ],
  });
  access.saveDoc(cfg, access.normalizeDoc({
    defaults: { scopes: ['public'], onDuty: 'off' },
    contacts: {
      'u-work': {
        uid: 'u-work',
        name: '张经理',
        scopes: ['work'],
        onDuty: 'auto',
        canReceiveAssets: true,
        addressAs: '张经理',
        toneOverride: '对客户更客气',
        tabooTopics: ['涨薪'],
      },
      'u-friend': { uid: 'u-friend', name: '朋友', scopes: ['friend'], onDuty: 'auto' },
      'u-draft': { uid: 'u-draft', name: '草稿对象', scopes: ['work'], onDuty: 'draft' },
      ...policy,
    },
  }));
  const store = new Store({ file: path.join(root, 'data', 'store.json'), limits: {} });
  return {
    root, cfg, store,
    cleanup() {
      try { store.close(); } catch { /* ignore */ }
      fs.rmSync(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 60 });
    },
  };
}

function fakeLlm(reply = '我确认下。') {
  const calls = [];
  return {
    isMock: false,
    provider: 'mock',
    model: 'mock-1',
    calls,
    async complete(req) {
      calls.push(req);
      return typeof reply === 'function' ? reply(req) : reply;
    },
    reload() {},
    describe() { return {}; },
  };
}

/** 只实现待确认队列用到的两个接口：入队 + 按 id 回读回执。 */
function fakeReminders() {
  let seq = 0;
  const rows = [];
  return {
    items: rows,
    enqueueNow(input = {}) {
      seq += 1;
      const row = {
        id: `r-${seq}`,
        status: 'pending',
        error: '',
        finishedAt: 0,
        text: String(input.text || ''),
      };
      rows.push(row);
      return { ...row };
    },
    finish(id, status, error = '') {
      const row = rows.find((item) => item.id === id);
      assert.ok(row, '应该有提醒记录 ' + id);
      row.status = status;
      row.error = error;
      row.finishedAt = Date.now();
      return row;
    },
    delivery(id) {
      const row = rows.find((item) => item.id === id);
      return row ? { status: row.status, error: row.error, finishedAt: row.finishedAt } : null;
    },
  };
}

const msg = (uid, text, extra = {}) => ({
  platform: 'wechat',
  chatId: uid,
  chatType: 'private',
  senderId: uid,
  senderName: uid === 'u-work' ? '张经理' : '朋友',
  text,
  ...extra,
});

test('生效条件：总开关 / 时段 / 对象策略 / 时区墙上时间', () => {
  const world = makeWorld();
  try {
    const { cfg } = world;
    const work = access.policyFor(access.loadDoc(cfg), 'u-work');
    const off = access.policyFor(access.loadDoc(cfg), 'u-unknown');
    const now = new Date(2026, 10, 20, 10, 0, 0); // 周五 10:00（Asia/Shanghai）
    assert.equal(selfModel.onDutyStatus(cfg, work, now.getTime()).active, true);
    assert.equal(selfModel.onDutyStatus(cfg, off, now.getTime()).reason, 'contact_off');
    cfg.onDuty.enabled = false;
    assert.equal(selfModel.onDutyStatus(cfg, work, now.getTime()).reason, 'disabled');
    cfg.onDuty.enabled = true;
    // UTC 01:00 = 上海 09:00；UTC 00:00 = 上海 08:00
    assert.equal(selfModel.onDutyStatus(cfg, work, Date.UTC(2026, 10, 20, 1, 0, 0)).active, true);
    assert.equal(selfModel.onDutyStatus(cfg, work, Date.UTC(2026, 10, 20, 0, 0, 0)).reason, 'outside_hours');
  } finally { world.cleanup(); }
});

test('draft：不调用发送层，生成后进待确认队列', async () => {
  const world = makeWorld();
  try {
    const llm = fakeLlm('我确认下，晚点回你。');
    const bot = new Bot({ config: world.cfg, store: world.store, llm, logger: null, mockChunkDelayMs: 0 });
    const result = await bot.handle(msg('u-draft', '现在的报价是多少？'));
    assert.equal(result.action, 'ignore');
    assert.equal(result.reason, 'on_duty_draft');
    const pending = selfModel.loadPending(world.cfg).items;
    assert.equal(pending.length, 1);
    assert.equal(pending[0].reason, 'draft');
    assert.equal(pending[0].uid, 'u-draft');
    assert.match(pending[0].text, /晚点回你/);
    const assistant = world.store.messages('wechat:u:u-draft', 20).filter((m) => m.role === 'assistant');
    assert.equal(assistant.length, 0, '没发出去的草稿不能写成助手回复');
  } finally { world.cleanup(); }
});

test('auto：正常发送；命中敏感词强制 sensitive 草稿', async () => {
  const world = makeWorld();
  try {
    const llm = fakeLlm('按计划推进。');
    const bot = new Bot({ config: world.cfg, store: world.store, llm, logger: null, mockChunkDelayMs: 0 });
    const ok = await bot.handle(msg('u-work', '项目进度怎么样？'));
    assert.equal(ok.action, 'reply');
    assert.match(ok.reply, /按计划/);
    assert.equal(selfModel.loadPending(world.cfg).items.length, 0);

    const risky = await bot.handle(msg('u-work', '帮我看下转账金额对不对'));
    assert.equal(risky.action, 'ignore');
    assert.equal(risky.reason, 'on_duty_draft');
    const pending = selfModel.loadPending(world.cfg).items;
    assert.equal(pending.length, 1);
    assert.equal(pending[0].reason, 'sensitive');
  } finally { world.cleanup(); }
});

test('待确认队列上限 200：先丢最旧的终态项，再丢最旧的', () => {
  const world = makeWorld();
  try {
    const cfg = world.cfg;
    const first = [];
    for (let i = 0; i < 200; i += 1) first.push(selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: `草稿${i}` }));
    assert.equal(selfModel.loadPending(cfg).items.length, 200);
    // 把最早两条标成已处理，再加 5 条：应优先丢这两条终态项
    selfModel.updatePending(cfg, first[0].id, { status: 'discarded' });
    selfModel.updatePending(cfg, first[1].id, { status: 'sent' });
    for (let i = 0; i < 5; i += 1) selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: `新草稿${i}` });
    const items = selfModel.loadPending(cfg).items;
    assert.equal(items.length, 200);
    assert.equal(items.some((item) => item.id === first[0].id), false);
    assert.equal(items.some((item) => item.id === first[1].id), false);
    assert.equal(items.some((item) => item.id === first[2].id), false, '第二条终态之后，最旧的 3 条也要丢');
    assert.equal(items.some((item) => item.id === first[5].id), true);
  } finally { world.cleanup(); }
});

test('待确认 send / discard：不自动重试，状态机不回退', () => {
  const world = makeWorld();
  try {
    const cfg = world.cfg;
    const item = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', name: '张经理', text: '我确认下。' });
    let calls = 0;
    const failing = { enqueueNow() { calls += 1; throw new Error('队列忙'); } };
    const service = new SelfService(cfg, { logger: null, runtime: { config: cfg, reminders: failing, store: world.store } });
    assert.throws(() => service.sendPending({ config: cfg, reminders: failing, store: world.store }, item.id, {}), /队列忙/);
    assert.equal(calls, 1, '失败后不能自动重试');
    assert.equal(selfModel.loadPending(cfg).items[0].status, 'pending');

    const ok = { enqueueNow() { calls += 1; return { id: 'r-1' }; } };
    const sent = service.sendPending({ config: cfg, reminders: ok, store: world.store }, item.id, { text: '我确认后回复你。' });
    assert.equal(sent.status, 'queued');
    assert.equal(sent.reminderId, 'r-1');
    assert.equal(sent.text, '我确认后回复你。', '编辑后的文本要覆盖原草稿');
    const storedItem = selfModel.loadPending(cfg).items.find((row) => row.id === item.id);
    assert.equal(storedItem.text, '我确认后回复你。');
    const remembered = world.store.messages('wechat:u:u-work', 20)
      .filter((message) => message.role === 'assistant');
    assert.ok(remembered.some((message) => message.text === '我确认后回复你。'), '写进会话记忆的也必须是编辑后的文本');
    assert.equal(calls, 2);
    assert.throws(() => service.sendPending({ config: cfg, reminders: ok, store: world.store }, item.id, {}), /已经处理过/);

    const other = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: '另一条' });
    const discarded = service.discardPending(other.id);
    assert.equal(discarded.status, 'discarded');
    assert.throws(() => service.discardPending(other.id), /已经处理过/);
  } finally { world.cleanup(); }
});

test('待确认回执：queued → sent / failed / uncertain 持久化，重启不丢', () => {
  const world = makeWorld();
  try {
    const cfg = world.cfg;
    const reminders = fakeReminders();
    const runtime = { config: cfg, reminders, store: world.store };
    const service = new SelfService(cfg, { logger: null, runtime });
    const add = (text, at) => {
      const item = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', name: '张经理', text });
      selfModel.updatePending(cfg, item.id, { at });
      return item;
    };
    const sent = add('已发送的草稿', 1000);
    const uncertain = add('结果不确定的草稿', 2000);
    const failed = add('发送失败的草稿', 3000);
    service.sendPending(runtime, sent.id, {});
    service.sendPending(runtime, uncertain.id, {});
    service.sendPending(runtime, failed.id, {});
    reminders.finish('r-1', 'done');
    reminders.finish('r-2', 'unconfirmed', '发送结果不确定');
    reminders.finish('r-3', 'failed', '桥接拒绝');

    const snap = service.onDutySnapshot();
    assert.equal(snap.pending.length, 0, '终态不再出现在可操作队列');
    assert.deepEqual(snap.items.map((row) => row.id), [failed.id, uncertain.id, sent.id]);
    assert.deepEqual(snap.items.map((row) => row.status), ['failed', 'uncertain', 'sent']);
    assert.deepEqual(snap.items.map((row) => row.delivery.state), ['failed', 'uncertain', 'sent']);
    assert.equal(snap.items[0].delivery.error, '桥接拒绝');
    assert.equal(snap.items[1].delivery.error, '发送结果不确定');
    assert.ok(snap.items[2].delivery.at > 0);

    // 终态必须落盘，而不是每次 GET 重新猜。
    const stored = Object.fromEntries(selfModel.loadPending(cfg).items.map((row) => [row.id, row]));
    assert.equal(stored[sent.id].status, 'sent');
    assert.equal(stored[uncertain.id].status, 'uncertain');
    assert.equal(stored[uncertain.id].error, '发送结果不确定');
    assert.equal(stored[failed.id].status, 'failed');
    assert.equal(stored[failed.id].error, '桥接拒绝');
    assert.ok(stored[failed.id].finishedAt > 0);

    // 重启：新建服务、提醒队列里仍能找到同一条回执，状态不能回退。
    const restarted = new SelfService(cfg, { logger: null, runtime: { config: cfg, reminders } });
    const again = restarted.onDutySnapshot();
    assert.deepEqual(again.items.map((row) => row.delivery.state), ['failed', 'uncertain', 'sent']);
  } finally { world.cleanup(); }
});

test('待确认回执：真实提醒队列 done → sent、failed → failed', () => {
  const world = makeWorld();
  try {
    const cfg = world.cfg;
    const clock = { ms: Date.now() };
    const reminders = new ReminderService(cfg, { logger: null, now: () => clock.ms });
    const runtime = { config: cfg, reminders, store: world.store };
    const service = new SelfService(cfg, { logger: null, runtime });

    const okItem = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: '成功草稿' });
    selfModel.updatePending(cfg, okItem.id, { at: 1000 });
    service.sendPending(runtime, okItem.id, {});
    const okDue = reminders.due(clock.ms);
    assert.equal(okDue.length, 1);
    reminders.ack(okDue[0].id, { ok: true });

    const failItem = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: '失败草稿' });
    selfModel.updatePending(cfg, failItem.id, { at: 2000 });
    service.sendPending(runtime, failItem.id, {});
    const first = reminders.due(clock.ms);
    assert.equal(first.length, 1);
    reminders.ack(first[0].id, { ok: false, error: '微信忙' });
    clock.ms += 5 * 60 * 1000;
    const retry = reminders.due(clock.ms);
    assert.equal(retry.length, 1);
    reminders.ack(retry[0].id, { ok: false, error: '还是发不出去' });

    const snap = service.onDutySnapshot();
    assert.deepEqual(snap.items.map((row) => row.id), [failItem.id, okItem.id]);
    assert.deepEqual(snap.items.map((row) => row.status), ['failed', 'sent']);
    assert.deepEqual(snap.items.map((row) => row.delivery.state), ['failed', 'sent']);
    assert.equal(snap.items[0].delivery.error, '还是发不出去');
    assert.ok(snap.items[0].delivery.at > 0);
    assert.ok(snap.items[1].delivery.at > 0);
    const stored = Object.fromEntries(selfModel.loadPending(cfg).items.map((row) => [row.id, row]));
    assert.equal(stored[okItem.id].status, 'sent');
    assert.equal(stored[failItem.id].status, 'failed');
  } finally { world.cleanup(); }
});

test('待确认回执：还在队列或提醒记录已清理时保持 queued', () => {
  const world = makeWorld();
  try {
    const cfg = world.cfg;
    const reminders = fakeReminders();
    const runtime = { config: cfg, reminders, store: world.store };
    const service = new SelfService(cfg, { logger: null, runtime });
    const active = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: '还在队列' });
    const missing = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: '提醒丢了' });
    selfModel.updatePending(cfg, active.id, { at: 1000 });
    selfModel.updatePending(cfg, missing.id, { at: 2000 });
    service.sendPending(runtime, active.id, {});
    service.sendPending(runtime, missing.id, {});
    // 第二条模拟提醒记录被清理：delivery 返回 null，不能臆造 failed。
    reminders.items.splice(reminders.items.findIndex((row) => row.id === 'r-2'), 1);

    const snap = service.onDutySnapshot();
    assert.deepEqual(snap.items.map((row) => row.status), ['queued', 'queued']);
    assert.deepEqual(snap.items.map((row) => row.delivery.state), ['queued', 'queued']);
    const stored = Object.fromEntries(selfModel.loadPending(cfg).items.map((row) => [row.id, row]));
    assert.equal(stored[active.id].status, 'queued');
    assert.equal(stored[missing.id].status, 'queued');
  } finally { world.cleanup(); }
});

test('待确认 items 上限 50，最新在前；pending 只含可操作项', () => {
  const world = makeWorld();
  try {
    const cfg = world.cfg;
    const service = new SelfService(cfg, { logger: null, runtime: { config: cfg } });
    const ids = [];
    for (let i = 0; i < 55; i += 1) {
      const item = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: `草稿${i}` });
      selfModel.updatePending(cfg, item.id, { at: 1000 + i });
      ids.push(item.id);
    }
    const snap = service.onDutySnapshot();
    assert.equal(snap.pending.length, 55);
    assert.equal(snap.items.length, 50);
    assert.equal(snap.items[0].id, ids[54]);
    assert.equal(snap.items[49].id, ids[5]);
    assert.equal(snap.items.every((row) => row.status === 'pending'), true);
    assert.deepEqual(snap.items[0].delivery, { state: 'pending', error: '', at: 0 });
  } finally { world.cleanup(); }
});

test('dismiss：只清除非 pending 条目，终态转 discarded 后不再返回', () => {
  const world = makeWorld();
  try {
    const cfg = world.cfg;
    const service = new SelfService(cfg, { logger: null, runtime: { config: cfg } });
    const item = selfModel.addPending(cfg, { uid: 'u-work', key: 'wechat:u:u-work', text: '待处理' });
    assert.throws(() => service.dismissPending(item.id), (err) => err.statusCode === 409);
    selfModel.updatePending(cfg, item.id, { status: 'sent', finishedAt: 1200 });
    const dismissed = service.dismissPending(item.id);
    assert.equal(dismissed.status, 'discarded');
    const snap = service.onDutySnapshot();
    assert.equal(snap.pending.length, 0);
    assert.equal(snap.items.some((row) => row.id === item.id), false, '清除过的条目不再出现在历史里');
    assert.throws(() => service.dismissPending('p-0000-dead'), (err) => err.statusCode === 404);
  } finally { world.cleanup(); }
});

test('端到端（mock 模型）：work 对象只见 work/public；跨对象追问不泄漏', async () => {
  const world = makeWorld();
  try {
    const llm = fakeLlm((req) => {
      const prompt = [req.system, ...(req.turns || []).map((t) => t.content)].join('\n');
      if (/订单中心重构，12 月上线/.test(prompt)) return '目前按计划推进，本周给到联调版本。';
      return '这个不方便说。';
    });
    const bot = new Bot({ config: world.cfg, store: world.store, llm, logger: null, mockChunkDelayMs: 0 });

    const work = await bot.handle(msg('u-work', '在吗，项目进度怎么样？'));
    assert.equal(work.action, 'reply');
    const workCall = llm.calls.filter((call) => /在岗身份/.test(String(call.system || ''))).pop();
    assert.ok(workCall, '应该有一次在岗提示词调用');
    assert.match(workCall.system, /在岗身份/);
    assert.match(workCall.system, /老王/);
    assert.match(workCall.system, /对客户更客气/);
    assert.match(workCall.system, /张经理/);
    assert.doesNotMatch(workCall.system, /女仆|把你养大|你的主人/);
    const workPrompt = [workCall.system, ...(workCall.turns || []).map((t) => t.content)].join('\n');
    assert.match(workPrompt, /订单中心/);
    assert.match(workPrompt, /你好，我是老王/);
    assert.match(workPrompt, /涨薪/);
    assert.doesNotMatch(workPrompt, /周末回老家看父母/);
    assert.doesNotMatch(workPrompt, /存款五十万/);

    // 换一个 friend 对象追问 work 信息：候选集里根本不该出现
    const friend = await bot.handle(msg('u-friend', '老王他们那个订单中心项目做到哪了？'));
    assert.match(friend.reply, /不方便说/);
    const friendCall = llm.calls.filter((call) => /在岗身份/.test(String(call.system || ''))).pop();
    const friendPrompt = [friendCall.system, ...(friendCall.turns || []).map((t) => t.content)].join('\n');
    assert.doesNotMatch(friendPrompt, /订单中心重构/);
    assert.doesNotMatch(friendPrompt, /存款五十万/);
    assert.match(friendPrompt, /你好，我是老王/);
  } finally { world.cleanup(); }
});
