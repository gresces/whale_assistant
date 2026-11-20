'use strict';

/**
 * 桥接通用附件通道的内核侧契约：
 *   enqueueAsset → pending 带 kind/title → claim 只认 data/share/files；
 *   路径逃逸 / 链接 / 超限 / 扩展名不符都拒绝；动作账不写成「表情包」。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { StickerService } = require('../src/modules/stickers/service');
const { DEFAULT_CONFIG } = require('../src/infra/config');
const { startServer } = require('../server');

function makeWorld({ stickers = true, shareAssets = true } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-share-queue-'));
  const cfg = JSON.parse(JSON.stringify(DEFAULT_CONFIG));
  cfg.root = root;
  cfg.paths = { dataDir: path.join(root, 'data') };
  cfg.features.stickers = stickers;
  cfg.features.shareAssets = shareAssets;
  const files = path.join(root, 'data', 'share', 'files');
  fs.mkdirSync(files, { recursive: true });
  const png = path.join(files, 'a-test.png');
  fs.writeFileSync(png, Buffer.from('89504e470d0a1a0a', 'hex'));
  return { root, cfg, files, png, cleanup() { fs.rmSync(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 60 }); } };
}

function memoryStore() {
  const events = [];
  return {
    events,
    appendEvent(key, event) { events.push({ key, ...event }); return { ts: Date.now(), ...event }; },
    updateEvent() { return null; },
  };
}

test('资产入队：pending 带 kind/title，claim 通过', () => {
  const world = makeWorld();
  try {
    const service = new StickerService(world.cfg, { logger: null });
    const job = service.enqueueAsset({
      path: world.png,
      title: '架构图',
      chat: '张经理',
      chatUsername: 'wxid_placeholder',
      key: 'wechat:u:wxid_placeholder',
    });
    assert.equal(job.kind, 'file');
    assert.equal(job.title, '架构图');
    assert.equal(path.isAbsolute(job.path), true);
    const pending = service.pending();
    assert.equal(pending.length, 1);
    assert.equal(pending[0].kind, 'file');
    assert.equal(pending[0].title, '架构图');
    const claimed = service.claim(job.id);
    assert.equal(claimed.kind, 'file');
    assert.equal(claimed.title, '架构图');
    assert.equal(claimed.path, path.resolve(world.png));
  } finally { world.cleanup(); }
});

test('资产路径：相对路径 / 逃逸 / 扩展名 / 超限都拒绝', () => {
  const world = makeWorld();
  try {
    const service = new StickerService(world.cfg, { logger: null });
    assert.throws(() => service.enqueueAsset({ path: 'data/share/files/a-test.png', chat: 'x' }), /绝对路径/);
    assert.throws(() => service.enqueueAsset({ path: path.join(world.root, 'evil.png'), chat: 'x' }), /data\/share\/files/);
    assert.throws(() => service.enqueueAsset({ path: path.join(world.root, 'data', 'share', '..', 'evil.png'), chat: 'x' }), /data\/share\/files/);

    const exe = path.join(world.files, 'a-test.exe');
    fs.writeFileSync(exe, 'MZ');
    assert.throws(() => service.enqueueAsset({ path: exe, chat: 'x' }), /类型不支持/);

    const big = path.join(world.files, 'big.txt');
    fs.writeFileSync(big, Buffer.alloc(20 * 1024 * 1024 + 1, 0x61));
    assert.throws(() => service.enqueueAsset({ path: big, chat: 'x' }), /20 MiB/);
  } finally { world.cleanup(); }
});

test('资产路径：符号链接 / 目录联接拒绝', (t) => {
  const world = makeWorld();
  try {
    const service = new StickerService(world.cfg, { logger: null });
    let candidate = '';
    let fileLinkError = null;
    const link = path.join(world.files, 'link.png');
    try {
      fs.symlinkSync(world.png, link, 'file');
      candidate = link;
    } catch (err) {
      fileLinkError = err;
    }
    if (!candidate) {
      // Windows 普通用户不能建文件符号链接；目录 junction 不需要管理员权限，
      // 用一个指向 files 外部的目录联接覆盖同一条「解析后越界」断言。
      const outside = path.join(world.root, 'outside');
      fs.mkdirSync(outside, { recursive: true });
      fs.writeFileSync(path.join(outside, 'link.png'), Buffer.from('89504e470d0a1a0a', 'hex'));
      try {
        const junction = path.join(world.files, 'link-outside');
        fs.symlinkSync(outside, junction, process.platform === 'win32' ? 'junction' : 'dir');
        candidate = path.join(junction, 'link.png');
      } catch (junctionError) {
        t.skip('文件符号链接（' + (fileLinkError && fileLinkError.code) + '）和目录 junction（'
          + (junctionError && junctionError.code) + '）都创建失败');
        return;
      }
    }
    assert.throws(() => service.enqueueAsset({ path: candidate, chat: 'x' }),
      /符号链接|目录联接|指向了 data\/share\/files 之外/);
  } finally { world.cleanup(); }
});

test('claim 二次校验：入队后文件被换到白名单外要失败', () => {
  const world = makeWorld();
  try {
    const service = new StickerService(world.cfg, { logger: null });
    const job = service.enqueueAsset({ path: world.png, title: '图', chat: 'x', chatUsername: 'u1' });
    // 模拟进程内路径被篡改：claim 必须重新走一轮白名单校验。
    service.jobs.get(job.id).path = path.join(world.root, 'evil.png');
    assert.throws(() => service.claim(job.id), /data\/share\/files/);
    assert.equal(service.jobs.get(job.id).status, 'failed');
  } finally { world.cleanup(); }
});

test('动作账：资产记 title，不写成表情包', () => {
  const world = makeWorld();
  try {
    const store = memoryStore();
    const service = new StickerService(world.cfg, { logger: null, store });
    service.enqueueAsset({
      path: world.png,
      title: '订单中心架构图',
      chat: '张经理',
      chatUsername: 'u1',
      key: 'wechat:u:u1',
    });
    assert.equal(store.events.length, 1);
    assert.equal(store.events[0].type, 'asset');
    assert.equal(store.events[0].detail, '订单中心架构图');
    assert.equal(store.events[0].status, 'queued');
    assert.doesNotMatch(store.events[0].detail, /表情包/);
  } finally { world.cleanup(); }
});

test('gate：关闭表情包但开启 shareAssets 时仍返回启用', () => {
  const world = makeWorld({ stickers: false, shareAssets: true });
  try {
    const service = new StickerService(world.cfg, { logger: null });
    const gate = service.gate();
    assert.equal(gate.ok, true);
    assert.equal(gate.enabled, true);
    assert.equal(gate.stickerEnabled, false);
    assert.equal(gate.shareAssets, true);
    assert.ok(fs.existsSync(path.join(world.root, 'data', 'share', 'files')));
    // 桥接轮询 pending 必须 200（列表可以为空），不能因为表情包关着就 404。
    assert.deepEqual(service.pendingForBridge(), []);
    const job = service.enqueueAsset({ path: world.png, title: '架构图', chat: '张经理', chatUsername: 'u1' });
    assert.equal(service.pendingForBridge()[0].kind, 'file');
    assert.equal(service.claim(job.id).kind, 'file');
  } finally { world.cleanup(); }
});

test('两个开关都关：gate 拒绝、资产入队拒绝', () => {
  const world = makeWorld({ stickers: false, shareAssets: false });
  try {
    const service = new StickerService(world.cfg, { logger: null });
    assert.throws(() => service.gate(), /未开启/);
    assert.throws(() => service.enqueueAsset({ path: world.png, chat: 'x' }), /未开启/);
  } finally { world.cleanup(); }
});

test('HTTP：表情包关、shareAssets 开时 gate / pending 仍可用，且建出 data/share/files', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-share-http-'));
  let inst;
  try {
    fs.writeFileSync(path.join(root, 'config.json'), JSON.stringify({
      server: { port: 0, openBrowser: false },
      watchConfig: false,
      log: { level: 'error' },
      features: { stickers: false, shareAssets: true },
    }));
    inst = await startServer({ root, env: { WHALE_LOG_FILE: '' }, port: 0, openBrowser: false });
    const headers = { 'x-bot-token': 'change-me-wechat' };
    const gate = await fetch(inst.url + '/api/stickers/gate', { headers });
    assert.equal(gate.status, 200);
    const gateBody = await gate.json();
    assert.equal(gateBody.enabled, true);
    assert.equal(gateBody.shareAssets, true);
    assert.equal(gateBody.stickerEnabled, false);
    const pending = await fetch(inst.url + '/api/stickers/pending', { headers });
    assert.equal(pending.status, 200);
    assert.deepEqual((await pending.json()).items, []);
    // 表情包自己的状态接口还是 404（没开表情包）
    const status = await fetch(inst.url + '/api/stickers/status', { headers });
    assert.equal(status.status, 404);
    assert.ok(fs.existsSync(path.join(root, 'data', 'share', 'files')));
  } finally {
    if (inst) await inst.close();
    fs.rmSync(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 60 });
  }
});
