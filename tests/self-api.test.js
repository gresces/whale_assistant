'use strict';

/**
 * 分身 HTTP API（契约 §7）：启动器 lane 实际要调的就是这些形状。
 * 只在本机回环下跑，接口返回里不能出现真实 wxid / 群号。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { startServer } = require('../server');

async function boot() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-self-api-'));
  fs.writeFileSync(path.join(root, 'config.json'), JSON.stringify({
    server: { port: 0, openBrowser: false },
    watchConfig: false,
    log: { level: 'error' },
  }));
  const inst = await startServer({ root, env: { WHALE_LOG_FILE: '' }, port: 0, openBrowser: false });
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

test('分身 API：自我模型 / 策略 / 资产 / 在岗的读写形状', async () => {
  const world = await boot();
  try {
    const self0 = await world.get('/api/self');
    assert.equal(self0.status, 200);
    assert.deepEqual(self0.body.scopes, ['public', 'friend', 'work', 'family', 'private']);
    assert.equal(self0.body.model.version, 1);
    assert.equal(self0.body.selfModel.version, 1, '启动器同时接受 selfModel 别名');

    const saved = await world.post('/api/self', {
      model: {
        identity: { name: '老王', job: '后端工程师', city: '杭州' },
        voice: { tone: '话少' },
      },
    });
    assert.equal(saved.status, 200);
    assert.equal(saved.body.model.identity.name, '老王');

    const policy = await world.post('/api/self/policy', {
      uid: 'u-placeholder',
      name: '张经理',
      scopes: ['work'],
      onDuty: 'draft',
      canReceiveAssets: true,
      discloseAI: 'onAsk',
      addressAs: '张经理',
      toneOverride: '对这个人更客气',
      tabooTopics: ['涨薪'],
    });
    assert.equal(policy.status, 200);
    assert.equal(policy.body.contact.uid, 'u-placeholder');
    assert.deepEqual(policy.body.contact.scopes, ['work']);
    assert.equal(policy.body.contact.addressAs, '张经理');
    assert.equal(policy.body.contact.toneOverride, '对这个人更客气');
    assert.deepEqual(policy.body.contact.tabooTopics, ['涨薪']);

    const list = await world.get('/api/self/policy');
    assert.equal(Array.isArray(list.body.contacts), true, 'contacts 必须是数组');
    assert.equal(list.body.contacts.length, 1);
    assert.equal(list.body.contacts[0].onDuty, 'draft');
    assert.equal(list.body.contacts[0].addressAs, '张经理');
    assert.equal(list.body.contacts[0].toneOverride, '对这个人更客气');
    assert.deepEqual(list.body.contacts[0].tabooTopics, ['涨薪']);

    // 资产导入：源文件放临时目录，导入后复制到 data/share/files
    const source = path.join(world.root, 'source.png');
    fs.writeFileSync(source, Buffer.from('89504e470d0a1a0a', 'hex'));
    const imported = await world.post('/api/self/assets', {
      sourcePath: source,
      title: '架构图',
      scopes: ['work'],
      keywords: ['订单中心'],
    });
    assert.equal(imported.status, 200);
    assert.equal(imported.body.asset.kind, 'image');
    const assets = await world.get('/api/self/assets');
    assert.equal(assets.body.assets.length, 1);
    assert.equal(assets.body.assets[0].audience, 1);
    assert.equal(assets.body.assets[0].availableTo, 1);
    assert.equal(assets.body.assets[0].title, '架构图');

    const removed = await world.del('/api/self/assets/' + encodeURIComponent(imported.body.asset.id));
    assert.equal(removed.status, 200);
    assert.equal(removed.body.removed, true);

    const duty = await world.get('/api/self/on-duty');
    assert.equal(duty.status, 200);
    assert.equal(duty.body.enabled, false);
    assert.equal(duty.body.workingHours.start, '09:00');
    assert.equal(duty.body.workingHours.end, '19:00');

    const toggled = await world.post('/api/self/on-duty', {
      enabled: true,
      workingHours: { start: '08:30', end: '18:30', days: [1, 2, 3, 4, 5] },
      sensitiveTopics: ['转账', '合同'],
    });
    assert.equal(toggled.status, 200);
    assert.equal(toggled.body.enabled, true);
    assert.equal(toggled.body.workingHours.start, '08:30');

    // 待确认回执：已发送的条目可以 dismiss，pending 不能直接清。
    const pendingFile = path.join(world.inst.runtime.config.paths.dataDir, 'on-duty', 'pending.json');
    fs.mkdirSync(path.dirname(pendingFile), { recursive: true });
    fs.writeFileSync(pendingFile, JSON.stringify({
      version: 1,
      items: [
        {
          id: 'p-1763600000000-ab12',
          key: 'wechat:u:wxid_placeholder',
          uid: 'wxid_placeholder',
          name: '张经理',
          text: '这条已经发出去了',
          reason: 'draft',
          assets: [],
          at: 1763600000000,
          status: 'sent',
          error: '',
          reminderId: 'r-sent',
          finishedAt: 1763600001000,
        },
      ],
    }), 'utf8');
    const dutyWithReceipt = await world.get('/api/self/on-duty');
    assert.equal(dutyWithReceipt.status, 200);
    assert.equal(dutyWithReceipt.body.pending.length, 0, 'pending 只含可操作项');
    assert.equal(dutyWithReceipt.body.items.length, 1);
    assert.deepEqual(dutyWithReceipt.body.items[0].delivery, {
      state: 'sent', error: '', at: 1763600001000,
    });
    const dismissed = await world.post('/api/self/on-duty/pending/p-1763600000000-ab12', { action: 'dismiss' });
    assert.equal(dismissed.status, 200);
    assert.equal(dismissed.body.item.status, 'discarded');
    const afterDismiss = await world.get('/api/self/on-duty');
    assert.equal(afterDismiss.body.items.length, 0);

    // 启动器对象列表：会话接口要能直接给出 uid/name/chatId/chatType
    world.inst.runtime.store.ensureSession('wechat:u:wxid_placeholder', {
      platform: 'wechat',
      chatType: 'private',
      title: '张经理',
    });
    const chats = await world.get('/api/memory/chats');
    assert.equal(chats.status, 200);
    const chat = chats.body.chats.find((row) => row.key === 'wechat:u:wxid_placeholder');
    assert.ok(chat);
    assert.equal(chat.uid, 'wxid_placeholder');
    assert.equal(chat.name, '张经理');
    assert.equal(chat.chatId, 'wechat:u:wxid_placeholder');
    assert.equal(chat.chatType, 'private');

    assert.deepEqual(duty.body.workingHours.days, [1, 2, 3, 4, 5]);
    assert.equal(Array.isArray(duty.body.sensitiveTopics), true);
    assert.equal(Array.isArray(duty.body.pending), true);

    const deleted = await world.del('/api/self/policy/u-placeholder');
    assert.equal(deleted.status, 200);
    assert.equal(deleted.body.removed, true);
  } finally {
    await world.inst.close();
    fs.rmSync(world.root, { recursive: true, force: true, maxRetries: 8, retryDelay: 60 });
  }
});
