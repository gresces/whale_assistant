'use strict';

/**
 * 资产标记：解析、权限拒绝、一条回复最多一个、路径逃逸拒绝（契约 §5）。
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

function makeWorld() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-asset-'));
  const cfg = JSON.parse(JSON.stringify(DEFAULT_CONFIG));
  cfg.root = root;
  cfg.paths = { dataDir: path.join(root, 'data') };
  cfg.features.stickers = true;
  cfg.onDuty.enabled = true;
  cfg.onDuty.workingHours = { start: '00:00', end: '23:59', days: [0, 1, 2, 3, 4, 5, 6] };
  cfg.persona.master = { names: [], wxids: [] };
  selfModel.ensureDefaultFiles(cfg);
  selfModel.save(cfg, { identity: { name: '老王' } });
  access.saveDoc(cfg, access.normalizeDoc({
    contacts: {
      'u-work': { uid: 'u-work', name: '张经理', scopes: ['work'], onDuty: 'auto', canReceiveAssets: true },
      'u-friend': { uid: 'u-friend', name: '朋友', scopes: ['friend'], onDuty: 'auto', canReceiveAssets: true },
      'u-noasset': { uid: 'u-noasset', name: '不收款', scopes: ['work'], onDuty: 'auto', canReceiveAssets: false },
    },
  }));
  const source = path.join(root, 'source.png');
  fs.writeFileSync(source, Buffer.from('89504e470d0a1a0a', 'hex'));
  const asset = selfModel.importAsset(cfg, {
    sourcePath: source,
    title: '架构图',
    scopes: ['work'],
    keywords: ['订单中心'],
    note: '对外版本',
  });
  const store = new Store({ file: path.join(root, 'data', 'store.json'), limits: {} });
  return {
    root, cfg, store, asset,
    policy(uid) { return access.policyFor(access.loadDoc(cfg), uid); },
    cleanup() {
      try { store.close(); } catch { /* ignore */ }
      fs.rmSync(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 60 });
    },
  };
}

function fakeStickerService() {
  const calls = [];
  const service = {
    jobs: new Map(),
    calls,
    enqueueAsset(input) {
      calls.push(input);
      const job = {
        id: `job-${calls.length}`,
        kind: 'file',
        status: 'queued',
        ...input,
      };
      service.jobs.set(job.id, job);
      return { ...job };
    },
    plan() { throw new Error('有资产时不应再挑表情包'); },
  };
  return service;
}

test('标记解析：剥掉全部标记，只取第一个', () => {
  const parsed = selfModel.parseAssetMarks('发你了 [[asset:a-1]] 还有 [[asset:架构图]]');
  assert.equal(parsed.marker, 'a-1');
  assert.equal(parsed.count, 2);
  assert.equal(parsed.text, '发你了  还有');
});

test('路径逃逸 / 绝对路径 / 符号链接一律拒绝', () => {
  const world = makeWorld();
  try {
    assert.equal(selfModel.assetPath(world.cfg, { path: '../evil.png' }), '');
    assert.equal(selfModel.assetPath(world.cfg, { path: 'files/../../evil.png' }), '');
    assert.equal(selfModel.assetPath(world.cfg, { path: 'C:/Windows/system32/evil.png' }), '');
    assert.equal(selfModel.assetPath(world.cfg, { path: 'files/../contact-policy.json' }), '');
    // 清单里被篡改成逃逸路径时，加载阶段就丢掉，解析不到
    const manifest = path.join(world.cfg.paths.dataDir, 'share', 'manifest.json');
    fs.writeFileSync(manifest, JSON.stringify({
      version: 1,
      assets: [{ id: 'a-evil', title: '逃逸', path: '../evil.png', kind: 'image', size: 10, scopes: ['work'] }],
    }), 'utf8');
    const resolved = selfModel.resolveAsset(world.cfg, 'a-evil', world.policy('u-work'));
    assert.equal(resolved.ok, false);
  } finally { world.cleanup(); }
});

test('权限：域不匹配或 canReceiveAssets=false 都拒绝', () => {
  const world = makeWorld();
  try {
    assert.equal(selfModel.resolveAsset(world.cfg, world.asset.id, world.policy('u-friend')).reason, 'denied');
    assert.equal(selfModel.resolveAsset(world.cfg, world.asset.id, world.policy('u-noasset')).reason, 'denied');
    const ok = selfModel.resolveAsset(world.cfg, '架构图', world.policy('u-work'));
    assert.equal(ok.ok, true);
    assert.equal(ok.asset.id, world.asset.id);
  } finally { world.cleanup(); }
});

test('一条回复最多一个资产；标记不进用户可见文本', () => {
  const world = makeWorld();
  try {
    const second = selfModel.importAsset(world.cfg, {
      sourcePath: path.join(world.root, 'source.png'),
      title: '第二张',
      scopes: ['work'],
    });
    const service = fakeStickerService();
    const bot = new Bot({ config: world.cfg, store: world.store, llm: { isMock: true }, logger: null, mockChunkDelayMs: 0 });
    bot.stickerService = service;
    const key = 'wechat:u:u-work';
    const decision = {
      action: 'reply',
      key,
      reason: 'test',
      msg: {
        platform: 'wechat',
        chatId: 'u-work',
        chatType: 'private',
        cleanText: '在吗',
        onDuty: { model: selfModel.load(world.cfg), policy: world.policy('u-work'), overrides: null },
      },
    };
    world.store.ensureSession(key, decision.msg);
    const bubbles = bot.finalize(decision, `发你了 [[asset:${world.asset.id}]] [[asset:${second.id}]]`);
    const visible = bubbles.join('\n');
    assert.doesNotMatch(visible, /\[\[asset/);
    assert.match(visible, /发你了/);
    assert.equal(service.calls.length, 1, '一条回复最多入队一个资产');
    const job = service.calls[0];
    assert.equal(job.title, '架构图');
    assert.equal(job.chatUsername, 'u-work');
    assert.equal(fs.existsSync(job.path), true, '资产路径必须指向 data/share/files 里的真实文件');
    assert.equal(job.path.startsWith(path.join(world.cfg.paths.dataDir, 'share', 'files')), true);
    assert.equal(decision.assetJob.id, 'job-1');
    assert.equal(bot.stickerFor({ assetJob: decision.assetJob }).id, 'job-1', '有资产时不再挑表情包');
  } finally { world.cleanup(); }
});

test('权限拒绝：不入队，并在动作账里记「权限拒绝」', () => {
  const world = makeWorld();
  try {
    const service = fakeStickerService();
    const bot = new Bot({ config: world.cfg, store: world.store, llm: { isMock: true }, logger: null, mockChunkDelayMs: 0 });
    bot.stickerService = service;
    const key = 'wechat:u:u-friend';
    const decision = {
      action: 'reply',
      key,
      reason: 'test',
      msg: {
        platform: 'wechat',
        chatId: 'u-friend',
        chatType: 'private',
        cleanText: '在吗',
        onDuty: { model: selfModel.load(world.cfg), policy: world.policy('u-friend'), overrides: null },
      },
    };
    world.store.ensureSession(key, decision.msg);
    const bubbles = bot.finalize(decision, `发你 [[asset:${world.asset.id}]]`);
    assert.doesNotMatch(bubbles.join('\n'), /\[\[asset/);
    assert.equal(service.calls.length, 0);
    const events = world.store.events(key, 12);
    assert.ok(events.some((event) => event.type === 'asset' && event.status === 'denied' && event.detail === '权限拒绝'));
  } finally { world.cleanup(); }
});
