'use strict';

/**
 * 风格测评集自检。
 *
 * 这里只验证「测评集本身」和「确定性指标」：
 *  - --check 能在不读配置、不调模型的情况下通过；
 *  - 隐私规则会拦住手机号 / wxid / 真实公司名；
 *  - 相同回复得满分，AI 味与禁忌词会拉低分数；
 *  - --json 明细里没有参考/生成正文。
 * --run 需要真实 Key，不在单测里跑，避免 CI 产生模型费用或外网依赖。
 */
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.resolve(__dirname, '..');
const script = path.join(root, 'scripts', 'eval-style.js');
const fixture = path.join(root, 'tests', 'fixtures', 'style-eval', 'samples.json');
const evalStyle = require('../scripts/eval-style');

const samples = JSON.parse(fs.readFileSync(fixture, 'utf8'));

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function runScript(args, options = {}) {
  return spawnSync(process.execPath, [script, ...args], {
    cwd: root,
    encoding: 'utf8',
    timeout: 30000,
    ...options,
  });
}

/** 只看 data/ 顶层：足够发现脚本偷偷新建或改写文件，又不受子目录并发影响。 */
function snapshotDataTopLevel() {
  const dir = path.join(root, 'data');
  if (!fs.existsSync(dir)) return { exists: false };
  const entries = {};
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      entries[entry.name] = 'dir';
      continue;
    }
    const stat = fs.statSync(path.join(dir, entry.name));
    entries[entry.name] = `${stat.size}:${stat.mtimeMs}`;
  }
  return { exists: true, entries };
}

test('测评集至少 20 条，覆盖 work / friend / family，id 不重复', () => {
  const result = evalStyle.validateSamples(samples);
  assert.equal(result.ok, true, result.errors.join('\n'));
  assert.ok(samples.length >= 20, '样例不足 20 条');
  const ids = new Set(samples.map((sample) => sample.id));
  assert.equal(ids.size, samples.length, 'id 有重复');
  for (const scene of evalStyle.SCENES) {
    const count = samples.filter((sample) => sample.scene === scene).length;
    assert.ok(count >= 3, `scene ${scene} 少于 3 条`);
  }
  for (const sample of samples) {
    const last = sample.context[sample.context.length - 1];
    assert.equal(last.role, 'them', `${sample.id} 最后一条 context 必须是 them`);
    assert.ok(sample.reference.trim(), `${sample.id} reference 为空`);
  }
});

test('参考回复本身不含 AI 味套话（否则测评基线就是错的）', () => {
  for (const sample of samples) {
    assert.deepEqual(evalStyle.findAiTone(sample.reference), [], sample.id);
  }
});

test('--check 通过，且不写 data/', () => {
  const before = snapshotDataTopLevel();
  const run = runScript(['--check']);
  const after = snapshotDataTopLevel();
  assert.equal(run.status, 0, run.stdout + run.stderr);
  assert.match(run.stdout, new RegExp(`样例数：${samples.length}`));
  assert.match(run.stdout, /格式检查：通过/);
  assert.match(run.stdout, /脱敏检查：通过/);
  assert.deepEqual(after, before, '--check 不应改动 data/ 下任何文件');
});

test('--check 会拒绝手机号 / wxid / 真实公司名，且不回显原文', () => {
  // 拼接构造，避免本文件自己成为 privacy-scan 的命中样本。
  const fakePhone = '138' + '0013' + '8000';
  const fakeWxid = 'wxid_' + 'abcdefghijklmn';

  const phone = clone(samples);
  phone[0].reference = `打 ${fakePhone} 找我`;
  const phoneResult = evalStyle.validateSamples(phone);
  assert.equal(phoneResult.ok, false);
  assert.ok(phoneResult.errors.some((line) => line.includes('手机号')), '要命中手机号规则');
  assert.equal(JSON.stringify(phoneResult.errors).includes(fakePhone), false, '错误信息不能回显手机号');

  const wxid = clone(samples);
  wxid[0].reference = `加我 ${fakeWxid}`;
  const wxidResult = evalStyle.validateSamples(wxid);
  assert.equal(wxidResult.ok, false);
  assert.ok(wxidResult.errors.some((line) => line.includes('wxid')), '要命中 wxid 规则');

  const company = clone(samples);
  company[0].reference = '我在腾讯做开发';
  const companyResult = evalStyle.validateSamples(company);
  assert.equal(companyResult.ok, false);
  assert.ok(companyResult.errors.some((line) => line.includes('公司名')), '要命中真实公司名规则');
});

test('--check 拒绝缺字段和「最后一条不是 them」的样例', () => {
  const short = evalStyle.validateSamples([{
    id: 'work-01',
    scene: 'work',
    context: [{ role: 'them', text: '在吗' }],
    reference: '在',
  }]);
  assert.equal(short.ok, false);
  assert.ok(short.errors.some((line) => line.includes('样例不足')), '要拦住不足 20 条');

  const badLast = clone(samples);
  badLast[0].context[badLast[0].context.length - 1].role = 'me';
  const lastResult = evalStyle.validateSamples(badLast);
  assert.equal(lastResult.ok, false);
  assert.ok(lastResult.errors.some((line) => line.includes('最后一条 context')), '要拦住以 me 结尾的上下文');
});

test('--check --samples 指向坏文件时 exit 1', (t) => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'style-eval-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'samples.json');
  fs.writeFileSync(file, JSON.stringify([{
    id: 'work-01',
    scene: 'work',
    context: [{ role: 'them', text: '在吗' }],
    reference: '在',
  }]), 'utf8');
  const run = runScript(['--check', '--samples', file]);
  assert.equal(run.status, 1, run.stdout + run.stderr);
  assert.match(run.stdout + run.stderr, /样例不足/);
});

test('没有 --check / --run 时按用法错误 exit 2', () => {
  const run = runScript([]);
  assert.equal(run.status, 2, run.stdout + run.stderr);
  assert.match(run.stdout + run.stderr, /--check/);
});

test('AI 味黑名单能分开「客服味」和正常短句', () => {
  const hits = evalStyle.findAiTone('作为一个AI，首先，我需要说明。其次，别熬夜。总之，希望能帮到你。');
  assert.ok(hits.includes('AI 自述'));
  assert.ok(hits.includes('连接词套话'));
  assert.ok(hits.includes('客服结尾'));
  assert.deepEqual(evalStyle.findAiTone('行，明早给你吧'), []);
  assert.deepEqual(evalStyle.findAiTone('首先我得说，这事真不怪我'), ['连接词套话']);
});

test('句子切分和单条指标是确定性的', () => {
  assert.deepEqual(evalStyle.splitSentences('去啊，几点集合？我看看。'), ['去啊，几点集合', '我看看']);

  const same = evalStyle.measurePair('去啊，几点集合？', '去啊，几点集合？');
  assert.equal(same.diff.avgChars, 0);
  assert.equal(same.diff.questionRatio, 0);
  assert.equal(same.phrase.coverage, 1);
  assert.equal(same.ref.questionRatio, 1, '问号要计入问句比例');
  assert.equal(evalStyle.measurePair('行', '行').ref.questionRatio, 0);

  const off = evalStyle.measurePair('行，明早给你吧', '作为一个AI，首先，我希望能帮到你。');
  assert.ok(off.aiToneHits > 0);
  assert.ok(off.diff.avgChars > 0);
  assert.ok(off.phrase.coverage < 1);
});

test('与参考完全相同的回复得 100 分', () => {
  const report = evalStyle.scoreReport(samples.map((sample) => ({
    id: sample.id,
    scene: sample.scene,
    reference: sample.reference,
    generated: sample.reference,
  })), { taboo: [] });
  assert.equal(report.score, 100);
  assert.equal(report.sampleCount, samples.length);
  assert.ok(report.samples.every((sample) => sample.score === 100));
});

test('假的 profile（taboo）会拉低分数，缺失时该项跳过', () => {
  const pairs = [
    { id: 'a', scene: 'work', reference: '行，明早给你吧', generated: '作为一个AI，首先，我希望能帮到你。' },
    { id: 'b', scene: 'friend', reference: '去啊，几点集合？', generated: '去啊，几点集合？' },
    { id: 'c', scene: 'family', reference: '好，到了我跟你说', generated: '好的，收到您的消息，我会及时处理。' },
  ];
  const without = evalStyle.scoreReport(pairs);
  const withTaboo = evalStyle.scoreReport(pairs, { taboo: ['收到您的消息', '及时处理'] });

  const withoutTaboo = without.metrics.find((row) => row.key === 'taboo');
  const withTabooRow = withTaboo.metrics.find((row) => row.key === 'taboo');
  assert.equal(withoutTaboo.skipped, true, '没有 taboo 列表时应跳过');
  assert.equal(withTabooRow.skipped, false);
  assert.ok(withTabooRow.gen >= 1, '要识别出禁忌词');
  assert.ok(withTaboo.score < without.score, '命中禁忌词后总分应下降');
  assert.equal(without.metrics.find((row) => row.key === 'aiTone').gen > 0, true);
});

test('--json 明细只有指标和 id，不含参考 / 生成正文或禁忌词', () => {
  const pairs = [
    { id: 'a', scene: 'work', reference: '行，明早给你吧', generated: '作为一个AI，首先，我希望能帮到你。' },
    { id: 'b', scene: 'friend', reference: '去啊，几点集合？', generated: '去啊，几点集合？' },
  ];
  const report = evalStyle.scoreReport(pairs, { taboo: ['及时处理'] });
  const detail = evalStyle.toJsonReport(report, { minScore: 60, passed: false });
  const text = JSON.stringify(detail);
  for (const pair of pairs) {
    assert.equal(text.includes(pair.reference), false, '明细不能含 reference 正文');
    assert.equal(text.includes(pair.generated), false, '明细不能含 generated 正文');
  }
  assert.equal(text.includes('及时处理'), false, '明细不能含禁忌词原文');
  assert.match(text, /"id":"a"/);
  assert.ok(detail.score >= 0 && detail.score <= 100);
});

test('权重总和为 1，分数始终落在 0~100', () => {
  const sum = Object.values(evalStyle.WEIGHTS).reduce((total, weight) => total + weight, 0);
  assert.equal(Math.round(sum * 100) / 100, 1);

  const worst = evalStyle.scoreReport(samples.map((sample) => ({
    id: sample.id,
    scene: sample.scene,
    reference: sample.reference,
    generated: '作为一个AI，首先，其次，总之，希望能帮到你。\n如果你需要，我可以帮你整理。\n需要注意的是，这只是建议。\n祝你工作顺利。',
  })), { taboo: ['AI', '建议'] });
  assert.ok(worst.score >= 0 && worst.score <= 100);
  assert.ok(worst.score < 60, '全程客服腔应该明显低于阈值');
  assert.equal(worst.metrics.find((row) => row.key === 'taboo').score, 0);
});
