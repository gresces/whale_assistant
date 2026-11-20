'use strict';

/**
 * 提示词画布契约 v1（docs/人设提示词画布.md）：
 * 段落映射、自定义段落增删与上限、敏感/超长拒绝、空段跳过、
 * 旧配置逐字节不变，以及预览与 buildPromptParts 完全一致。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { DEFAULT_CONFIG } = require('../src/infra/config');
const { buildPromptParts } = require('../src/persona');
const { buildBlockList, buildPreview } = require('../src/persona/blocks');
const customBlocks = require('../src/persona/customBlocks');
const personaStore = require('../src/persona/store');
const presets = require('../src/persona/presets');
const { portableFields } = require('../src/persona/portable');
const service = require('../src/modules/persona/service');

function freshConfig(extra = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-blocks-'));
  const config = JSON.parse(JSON.stringify(DEFAULT_CONFIG));
  config.paths = { dataDir: dir, configFile: path.join(dir, 'config.json') };
  fs.writeFileSync(config.paths.configFile, '{}\n', 'utf8');
  config.root = dir;
  Object.assign(config.persona, extra);
  return config;
}

function runtimeWith(config) {
  return { config, logger: { info() {}, warn() {} } };
}

test('段落顺序固定：identity → personality → background → speech → behavior → custom → custom-* → memory', () => {
  const config = freshConfig({
    customBlocks: [
      { id: 'custom-1111aaaa', title: '关于我的工作', text: '我在医院上班。' },
      { id: 'custom-2222bbbb', title: '称呼习惯', text: '别叫我宝贝。' },
    ],
  });
  const blocks = buildBlockList(config);
  assert.deepEqual(blocks.map((b) => b.id), [
    'identity', 'personality', 'background', 'speech', 'behavior', 'custom',
    'custom-1111aaaa', 'custom-2222bbbb', 'memory',
  ]);
  const byId = Object.fromEntries(blocks.map((b) => [b.id, b]));
  assert.equal(byId.identity.kind, 'form');
  assert.deepEqual(byId.identity.fields, ['name', 'age', 'job', 'appearance', 'ownerName']);
  assert.equal(byId.personality.kind, 'text');
  assert.equal(byId.personality.text, DEFAULT_CONFIG.persona.personality);
  assert.equal(byId.personality.defaultText, DEFAULT_CONFIG.persona.personality);
  assert.equal(byId.personality.maxLength, 500);
  assert.equal(byId.background.defaultText, DEFAULT_CONFIG.persona.background);
  assert.equal(byId.custom.defaultText, '');
  assert.equal(byId.custom.maxLength, customBlocks.CUSTOM_PROMPT_MAX);
  assert.equal(byId['custom-1111aaaa'].custom, true);
  assert.equal(byId['custom-1111aaaa'].label, '关于我的工作');
  assert.equal(byId['custom-1111aaaa'].text, '我在医院上班。');
  assert.equal(byId['custom-1111aaaa'].maxLength, 2000);
  assert.equal(byId.memory.kind, 'link');
});

test('speech 的 derived 是跟着私聊 / 群聊走的一句人话', () => {
  const config = freshConfig();
  const privateSpeech = buildBlockList(config, { chatType: 'private' }).find((b) => b.id === 'speech');
  assert.equal(privateSpeech.derived, '私聊最多 100 字 · 最多 2 条 · 颜文字 0%');
  assert.equal(privateSpeech.kind, 'chips');
  assert.deepEqual(privateSpeech.fields, ['privateMaxChars', 'groupMaxChars', 'maxBubbles', 'emojiChance']);
  config.chat.groupMaxChars = 55;
  config.chat.maxBubbles = 4;
  config.chat.emojiChance = 0.12;
  const groupSpeech = buildBlockList(config, { chatType: 'group' }).find((b) => b.id === 'speech');
  assert.equal(groupSpeech.derived, '群聊最多 55 字 · 最多 4 条 · 颜文字 12%');
});

test('自定义段落插在补充要求之后、表达偏好之前，空正文整段跳过', () => {
  const config = freshConfig({
    customPrompt: '补充要求正文：保持简洁。',
    customBlocks: [
      { id: 'custom-aaaa1111', title: '关于我的工作', text: '我在医院上班。' },
      { id: 'custom-bbbb2222', title: '空段标题', text: '   ' },
      { id: 'custom-cccc3333', title: '作息', text: '晚上十一点后不回消息。' },
    ],
  });
  const { system } = buildPromptParts({
    config,
    msg: { chatType: 'private', isMaster: true, senderName: '主人' },
    now: new Date('2024-05-01T10:00:00'),
  });
  const at = (needle) => {
    const index = system.indexOf(needle);
    assert.ok(index >= 0, `提示词里应该有「${needle}」`);
    return index;
  };
  assert.ok(at('补充要求正文') < at('【关于我的工作】'));
  assert.ok(at('【关于我的工作】') < at('我在医院上班。'));
  assert.ok(at('我在医院上班。') < at('【作息】'));
  assert.ok(at('【作息】') < at('晚上十一点后不回消息。'));
  assert.ok(at('晚上十一点后不回消息。') < at('【当前基础人设】'));
  assert.ok(at('【当前基础人设】') < at('【当前交流偏好】'));
  assert.doesNotMatch(system, /空段标题/);
});

test('旧配置没有 customBlocks 时输出与空数组逐字节一致', () => {
  const base = freshConfig({ customPrompt: '原来的补充要求。' });
  const without = JSON.parse(JSON.stringify(base));
  delete without.persona.customBlocks;
  const empty = JSON.parse(JSON.stringify(base));
  empty.persona.customBlocks = [];

  const msg = { chatType: 'private', isMaster: true, senderName: '主人', text: '在吗' };
  const now = new Date('2024-05-01T10:00:00');
  const a = buildPromptParts({ config: without, msg, now });
  const b = buildPromptParts({ config: empty, msg, now });
  assert.equal(a.system, b.system);
  assert.equal(a.tail, b.tail);
  assert.doesNotMatch(a.system, /custom-|【关于我的工作】/);
});

test('预览的 system / tail 与直接调用 buildPromptParts 完全相等（同参数快照）', () => {
  for (const chatType of ['private', 'group']) {
    for (const isMaster of [true, false]) {
      const config = freshConfig({
        customPrompt: '补充要求。',
        customBlocks: [{ id: 'custom-1234abcd', title: '关于我', text: '我是程序员。' }],
      });
      const preview = buildPreview(config, { chatType, isMaster });
      const direct = buildPromptParts({
        config,
        msg: {
          chatType,
          isMaster,
          senderName: isMaster ? '主人' : (chatType === 'group' ? '群友' : '朋友'),
          chatTitle: chatType === 'group' ? '一个微信群' : '',
        },
      });
      assert.equal(preview.system, direct.system, `${chatType}/${isMaster} system 必须同源`);
      assert.equal(preview.tail, direct.tail);
      assert.equal(preview.tokens, require('../src/memory/tokens').estimateBudgetTokens(direct.system));
    }
  }
});

test('自定义段落：新增 / 编辑 / 删除都返回更新后的预览，10 段封顶', () => {
  const runtime = runtimeWith(freshConfig());
  const created = service.createPersonaBlock(runtime, { title: '关于我的工作', text: '我在医院上班。' });
  assert.equal(created.ok, true);
  assert.match(created.id, /^custom-[0-9a-f]{8}$/);
  assert.ok(created.blocks.some((b) => b.id === created.id && b.label === '关于我的工作'));
  assert.match(created.system, /【关于我的工作】/);
  assert.match(created.system, /我在医院上班。/);
  assert.ok(Number(created.tokens) > 0);

  const edited = service.savePersonaBlock(runtime, { id: created.id, text: '我在学校上班。' });
  assert.equal(edited.ok, true);
  assert.equal(edited.blocks.find((b) => b.id === created.id).text, '我在学校上班。');
  assert.match(edited.system, /我在学校上班。/);
  assert.doesNotMatch(edited.system, /我在医院上班。/);

  for (let i = 0; i < 9; i += 1) {
    assert.equal(service.createPersonaBlock(runtime, { title: `第 ${i} 段`, text: `正文 ${i}` }).ok, true);
  }
  const eleventh = service.createPersonaBlock(runtime, { title: '第十一段', text: '不该成功' });
  assert.equal(eleventh.ok, false);
  assert.match(eleventh.error, /最多 10 段/);

  const removed = service.deletePersonaBlock(runtime, created.id);
  assert.equal(removed.ok, true);
  assert.ok(!removed.blocks.some((b) => b.id === created.id));
  assert.doesNotMatch(removed.system, /我在学校上班。/);
  assert.equal(service.deletePersonaBlock(runtime, created.id).ok, false, '重复删除要报不存在');
});

test('超长 / 敏感 / 不可编辑的段落整次拒绝，不写盘', () => {
  const runtime = runtimeWith(freshConfig());
  const longTitle = service.createPersonaBlock(runtime, { title: '标'.repeat(41), text: '正文' });
  assert.equal(longTitle.ok, false);
  assert.match(longTitle.error, /40 字/);

  const longText = service.createPersonaBlock(runtime, { title: '长文', text: '字'.repeat(2001) });
  assert.equal(longText.ok, false);
  assert.match(longText.error, /2000 字/);

  const secret = service.createPersonaBlock(runtime, { title: '凭据', text: '密码是 123456' });
  assert.equal(secret.ok, false);
  assert.match(secret.error, /拒绝保存/);

  const identity = service.savePersonaBlock(runtime, { id: 'identity', text: '换个身份' });
  assert.equal(identity.ok, false);
  assert.match(identity.error, /不是可编辑文本/);

  const personality = service.savePersonaBlock(runtime, { id: 'personality', text: '性'.repeat(501) });
  assert.equal(personality.ok, false);
  assert.match(personality.error, /500 字/);

  const personaFile = path.join(runtime.config.paths.dataDir, 'persona.json');
  assert.equal(fs.existsSync(personaFile), false, '被拒绝的写入不能留下 persona.json');
});

test('空段落不进提示词，但保留在配置里等待继续编辑', () => {
  const config = freshConfig();
  personaStore.save(config, {
    customBlocks: [
      { id: 'custom-aaaa0001', title: '还没写', text: '' },
      { id: 'custom-bbbb0002', title: '已写', text: '实际内容。' },
    ],
  });
  const { system } = buildPreview(config, { chatType: 'private', isMaster: true });
  assert.doesNotMatch(system, /【还没写】/);
  assert.match(system, /【已写】/);
  assert.match(system, /实际内容。/);
  assert.equal(config.persona.customBlocks.length, 2, '空段仍然留着，只是不进提示词');
});

test('坏段落数据在进入提示词前被过滤：id 不合法 / 重复 / 超过 10 段', () => {
  assert.deepEqual(customBlocks.normalizeBlocks([
    { id: 'custom-1234abcd', title: '好', text: '正文' },
    { id: 'custom-1234abcd', title: '重复', text: '正文' },
    { id: 'custom-zzzzzzzz', title: '坏 id', text: '正文' },
    { id: 'personality', title: '冒充固定段', text: '正文' },
    { id: 'custom-0000aaaa', title: '', text: '   ' },
  ]), [{ id: 'custom-1234abcd', title: '好', text: '正文' }]);
  const many = Array.from({ length: 12 }, (_, i) => ({
    id: `custom-${String(i).padStart(8, '0')}`, title: `段 ${i}`, text: `正文 ${i}`,
  }));
  assert.equal(customBlocks.normalizeBlocks(many).length, 10);
  assert.equal(customBlocks.promptLines([]).length, 0);
  assert.deepEqual(customBlocks.promptLines([{ id: 'custom-1234abcd', title: '标题', text: '正文' }]), ['【标题】', '正文']);
  assert.deepEqual(customBlocks.promptLines([{ id: 'custom-1234abcd', title: '标题', text: '' }]), []);
});

test('人设导出 → 导入往返：customBlocks 完全一致', () => {
  const source = freshConfig({
    customPrompt: '补充要求。',
    customBlocks: [
      { id: 'custom-1111aaaa', title: '关于我的工作', text: '我在医院上班。' },
      { id: 'custom-2222bbbb', title: '作息', text: '晚上十一点后不回消息。' },
    ],
  });
  const exported = presets.fromConfig(source);
  assert.deepEqual(exported.customBlocks, source.persona.customBlocks, '导出包要带自定义段落');

  const patch = presets.toPatch(exported);
  assert.deepEqual(patch.customBlocks, exported.customBlocks, '导入 patch 要原样带回段落');

  const target = freshConfig();
  personaStore.save(target, patch);
  assert.deepEqual(target.persona.customBlocks, exported.customBlocks, '应用后段落完全一致');
  assert.match(JSON.parse(fs.readFileSync(path.join(target.paths.dataDir, 'persona.json'), 'utf8')).customBlocks[0].title, /关于我的工作/);
});

test('导入包的超限 / 坏条目：裁到 10 段、丢坏数据、正文裁到 2000 字', () => {
  const many = Array.from({ length: 12 }, (_, i) => ({
    id: `custom-${String(i).padStart(8, '0')}`,
    title: `段 ${i}`,
    text: `正文 ${i}`,
  }));
  assert.equal(portableFields({ customBlocks: many }).customBlocks.length, 10, '便携字段最多 10 段');
  assert.equal(presets.fromConfig(freshConfig({ customBlocks: many })).customBlocks.length, 10, '导出包最多 10 段');

  const bad = portableFields({
    customBlocks: [
      null,
      '不是对象',
      { id: 'custom-zzzzzzzz', title: '坏 id', text: '正文' },
      { id: 'custom-1234abcd', title: '好', text: '字'.repeat(2001) },
      { id: 'custom-1234abcd', title: '重复', text: '正文' },
      { id: 'custom-0000aaaa', title: '', text: '   ' },
    ],
  }).customBlocks;
  assert.equal(bad.length, 1, '坏条目丢弃，不能整包失败');
  assert.equal(bad[0].id, 'custom-1234abcd');
  assert.equal(bad[0].text.length, 2000, '正文按现有上限裁剪');
  assert.equal(bad[0].title.length, 1);
});

test('显式空数组能清空上一份预设的自定义段落', () => {
  const target = freshConfig({
    customBlocks: [{ id: 'custom-1234abcd', title: '上一份', text: '旧段落。' }],
  });
  const patch = presets.toPatch({ schemaVersion: 1, customBlocks: [] });
  assert.deepEqual(patch, { customBlocks: [] }, '空数组必须作为显式覆盖出现在 patch 里');
  personaStore.save(target, patch);
  assert.deepEqual(target.persona.customBlocks, [], '切换预设后不能残留旧段落');
  const file = JSON.parse(fs.readFileSync(path.join(target.paths.dataDir, 'persona.json'), 'utf8'));
  assert.deepEqual(file.customBlocks, []);
});

test('整包应用（POST /api/persona 导入路径）也能写入 / 清空 customBlocks', async () => {
  const config = freshConfig();
  const runtime = { config, llm: { isMock: true }, logger: { info() {}, warn() {} } };
  const applied = await service.applyPersonaPatch(runtime, {
    customBlocks: [{ id: 'custom-1234abcd', title: '导入', text: '导入正文。' }],
  });
  assert.equal(applied.ok, true);
  assert.deepEqual(config.persona.customBlocks, [{ id: 'custom-1234abcd', title: '导入', text: '导入正文。' }]);

  const cleared = await service.applyPersonaPatch(runtime, { customBlocks: [] });
  assert.equal(cleared.ok, true);
  assert.deepEqual(config.persona.customBlocks, []);
});

test('预设列表的 chars 统计包含自定义段落正文', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'whale-preset-chars-'));
  const dir = path.join(root, 'assets', 'personas');
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'p-chars.json'), JSON.stringify({
    id: 'p-chars',
    name: '统计',
    customPrompt: 'ab',
    customBlocks: [
      { id: 'custom-1234abcd', title: '标题', text: '12345' },
      { id: 'custom-5678abcd', title: '标题2', text: '678' },
    ],
  }));
  const items = presets.list({ paths: { root } });
  assert.equal(items.find((item) => item.id === 'p-chars').chars, 10, '2 + 5 + 3');
});

