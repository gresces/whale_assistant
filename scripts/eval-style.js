#!/usr/bin/env node
'use strict';

/**
 * 「数字人分身」风格测评集：只评估，不改生成链路。
 *
 * 目标：把「像不像本人」从感觉变成可复现的数字。
 * 约束：
 *  - 只读样例与画像；默认不写任何文件（--json 才写，且只写指标与 id）。
 *  - 不打印、不落盘任何完整消息正文，终端只输出差值和分数。
 *  - --check 不读配置、不构造模型，可在 CI / 本地随时跑。
 *  - --run 复用 src/llm/client.js 与 src/persona/prompt.js 的真实构造路径，
 *    不另写一套「测评专用提示词」，也不做两段式生成（先草稿再改写）。
 */

const fs = require('node:fs');
const path = require('node:path');

const styleProfileLib = require('../src/persona/styleProfile');
const { errorMessage } = require('../src/infra/errors');
const privacyScan = require('./privacy-scan');

const DEFAULT_SAMPLES = path.join(__dirname, '..', 'tests', 'fixtures', 'style-eval', 'samples.json');
const SCENES = ['work', 'friend', 'family'];
const SCENE_LABELS = { work: '工作', friend: '朋友', family: '家人' };
const MIN_SAMPLES = 20;
const DEFAULT_MIN_SCORE = 60;
const MAX_TEXT_CHARS = 400;
const MAX_CONTEXT_TURNS = 12;

/**
 * 指标权重（写死，避免「调参把回归调绿」）。
 *
 * 为什么这样分：
 *  - 平均句长 / 语气词 / 短语覆盖 是「人耳一听就知道像不像」的强信号，权重最高；
 *  - 问句比例、emoji 比例 受单条内容影响大，给中等权重；
 *  - 方差、长回复比例、AI 味、禁忌词是约束项：不要求完全一致，但明显跑偏要扣分。
 *  - 权重总和固定为 1.00；某项不适用（参考回复没有短语、画像没有禁忌词）时，
 *    该项跳过，剩余权重按比例重新归一，不去拿 0 分凑数。
 */
const WEIGHTS = {
  avgSentenceChars: 0.16,
  sentenceVariance: 0.06,
  questionRatio: 0.11,
  particleDensity: 0.14,
  emojiRatio: 0.09,
  phraseCoverage: 0.17,
  aiTone: 0.10,
  longReplyRatio: 0.09,
  taboo: 0.08,
};

/**
 * 子分容差：差值达到容差时该项得 0 分，中间线性。
 * 数值按「聊天里一眼能看出的差距」定，不是精确统计显著性：
 *  - 平均句长差 8 字；
 *  - 句长方差差 60（字²）；
 *  - 问句 / emoji / 长回复比例差 30 个百分点；
 *  - 语气词密度差 6 个/100 字。
 */
const TOLERANCE = {
  avgSentenceChars: 8,
  sentenceVariance: 60,
  questionRatio: 0.3,
  emojiRatio: 0.3,
  particleDensity: 6,
  longReplyRatio: 0.3,
};

// 与 src/persona/styleProfile.js 的 PARTICLES 保持同一组字。
// 脚本被要求不改 src/，所以这里保留一份；两边改词表时一起改。
const PARTICLES = '啊呀吧嘛呢哦噢哈嘿诶唉嗯啦咯嘞呗';
const PARTICLE_SET = new Set(Array.from(PARTICLES));

// styleProfile 停用词表里最常撞车的子集；短语候选是 2~3 字，
// 只挡「没有风格信息」的通用词，不追求和画像完全同一份实现。
const STOP_PHRASES = new Set([
  '这个', '那个', '什么', '怎么', '这么', '那么', '就是', '不是', '没有', '可以',
  '我们', '你们', '他们', '她们', '自己', '现在', '今天', '明天', '昨天', '时候',
  '感觉', '觉得', '应该', '还是', '已经', '因为', '所以', '但是', '然后', '如果',
  '知道', '东西', '这样', '那样', '其实', '真的', '有点', '一下', '一个', '一些',
  '很多', '喜欢', '可能', '需要', '不要', '不会', '不能', '都是', '还有', '也是',
  '好的', '好吧', '在吗', '在呢', '你好', '谢谢', '为什么', '是不是', '有没有',
  '能不能', '会不会', '要不要', '怎么样', '怎么办', '就是说',
]);

/**
 * AI 味黑名单：命中即扣分。
 * 这些不是「禁止出现的词」，而是模型客服腔的典型标记；短句聊天里出现通常就是跑味。
 */
const AI_TONE_PATTERNS = [
  { label: 'AI 自述', re: /作为(?:一个|一名)?(?:AI|人工智能|语言模型|智能助手)/i },
  { label: '客服结尾', re: /希望(?:能|可以)?(?:帮到你|帮上你|对你有(?:所)?帮助)/ },
  { label: '客服开场', re: /(?:^|[。！？!?\n])\s*(?:当然可以|好的[，,]?以下是|以下是(?:关于|针对))/ },
  { label: '连接词套话', re: /首先|其次|总之|总的来说|综上所述|总而言之/ },
  { label: '书面腔', re: /需要注意的是|值得一提的是|不难看出|由此可见|值得注意的是/ },
  { label: '主动服务', re: /如果你需要|如果你还有|我可以帮你|我可以为你|有什么我可以帮你|请问还有(?:什么|其他)/ },
  { label: '祝福结尾', re: /祝你(?:生活愉快|工作顺利|一切顺利|天天开心)/ },
  { label: '号召口吻', re: /让我们(?:一起|来)/ },
];

const SEGMENT_RE = /[\p{L}\p{N}]+/gu;
const EMOJI_RE = /\p{Extended_Pictographic}/u;

function isPlainObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function charCount(text) {
  return Array.from(String(text == null ? '' : text)).length;
}

function normalize(text) {
  return String(text == null ? '' : text).replace(/\r\n?/g, '\n').trim();
}

/** 句子按换行和句末标点切；逗号不切，避免把一句话拆成很多「伪短句」。 */
function splitSentences(text) {
  const out = [];
  let buffer = '';
  for (const char of Array.from(normalize(text))) {
    if (/[\n。！？!?…；;]/.test(char)) {
      if (buffer.trim()) out.push(buffer.trim());
      buffer = '';
    } else {
      buffer += char;
    }
  }
  if (buffer.trim()) out.push(buffer.trim());
  return out;
}

function mean(values) {
  if (!values.length) return 0;
  return values.reduce((sum, value) => sum + value, 0) / values.length;
}

function variance(values) {
  if (!values.length) return 0;
  const avg = mean(values);
  return values.reduce((sum, value) => sum + (value - avg) ** 2, 0) / values.length;
}

function countParticles(text) {
  let count = 0;
  for (const char of Array.from(String(text == null ? '' : text))) {
    if (PARTICLE_SET.has(char)) count += 1;
  }
  return count;
}

function countQuestionMarks(text) {
  const matches = String(text == null ? '' : text).match(/[?？]/g);
  return matches ? matches.length : 0;
}

function hasEmoji(text) {
  return EMOJI_RE.test(String(text == null ? '' : text));
}

function nonEmptyLines(text) {
  return String(text == null ? '' : text).split('\n').filter((line) => line.trim()).length;
}

function sentenceStats(text) {
  const sentences = splitSentences(text);
  const lengths = sentences.map(charCount);
  const joined = sentences.join('');
  const compact = joined.replace(/\s+/g, '');
  const chars = charCount(compact);
  return {
    sentences: sentences.length,
    avgChars: mean(lengths),
    variance: variance(lengths),
    // 问号可能比句子多（「真的？你确定？」），比例封顶 1。
    // 注意用原文：splitSentences 会把标点切掉，不能拿 joined 数问号。
    questionRatio: Math.min(1, countQuestionMarks(text) / Math.max(1, sentences.length)),
    particleDensity: chars ? (countParticles(compact) / chars) * 100 : 0,
    emojiRatio: sentences.length
      ? sentences.filter((sentence) => hasEmoji(sentence)).length / sentences.length
      : 0,
    lines: nonEmptyLines(text),
  };
}

/**
 * 参考回复里的 top-N 短语：先取重复出现的 2~4 字片段，不够再用最长的 2~3 字片段补。
 * 不输出整句；这是「原话复用率」的候选集，不是分词结果。
 */
function topPhrasesOf(text, max = 5) {
  const whole = normalize(text);
  const counts = new Map();
  for (const segment of String(whole).match(SEGMENT_RE) || []) {
    const chars = Array.from(segment);
    for (let size = 2; size <= 4; size += 1) {
      for (let i = 0; i + size <= chars.length; i += 1) {
        const phrase = chars.slice(i, i + size).join('');
        if (phrase === whole) continue;
        if (STOP_PHRASES.has(phrase)) continue;
        if (/^\d+$/.test(phrase)) continue;
        counts.set(phrase, (counts.get(phrase) || 0) + 1);
      }
    }
  }
  const repeated = [...counts.entries()]
    .filter(([, count]) => count >= 2)
    .sort((a, b) => b[1] - a[1] || b[0].length - a[0].length || (a[0] < b[0] ? -1 : 1));
  // singles 只补 2~3 字片段：4 字片段太具体，容易把正常改写判成「不像」。
  const singles = [...counts.entries()]
    .filter(([phrase, count]) => count === 1 && Array.from(phrase).length <= 3)
    .sort((a, b) => b[0].length - a[0].length || (a[0] < b[0] ? -1 : 1));
  return [...repeated, ...singles].slice(0, max).map(([phrase]) => phrase);
}

function findAiTone(text) {
  const source = String(text == null ? '' : text);
  return AI_TONE_PATTERNS.filter(({ re }) => re.test(source)).map(({ label }) => label);
}

function closeness(diff, tolerance) {
  return Math.max(0, 1 - Math.min(1, Math.abs(diff) / tolerance));
}

function countScore(hits, samples) {
  return Math.max(0, 1 - Math.min(1, hits / Math.max(1, samples)));
}

function round(value, digits = 2) {
  const number = Number(value);
  if (!Number.isFinite(number)) return 0;
  const factor = 10 ** digits;
  return Math.round(number * factor) / factor;
}

function clamp01(value) {
  const number = Number(value);
  if (!Number.isFinite(number)) return 0;
  return Math.max(0, Math.min(1, number));
}

/** 单条样例：参考回复 vs 生成回复。内部含正文，只在内存里用。 */
function measurePair(reference, generated, { taboo = null } = {}) {
  const ref = sentenceStats(reference);
  const gen = sentenceStats(generated);
  const phrases = topPhrasesOf(reference);
  const matched = phrases.filter((phrase) => String(generated).includes(phrase));
  return {
    ref,
    gen,
    diff: {
      avgChars: gen.avgChars - ref.avgChars,
      variance: gen.variance - ref.variance,
      questionRatio: gen.questionRatio - ref.questionRatio,
      particleDensity: gen.particleDensity - ref.particleDensity,
      emojiRatio: gen.emojiRatio - ref.emojiRatio,
      longReply: (gen.lines > 3 ? 1 : 0) - (ref.lines > 3 ? 1 : 0),
    },
    phrase: {
      count: phrases.length,
      coverage: phrases.length ? matched.length / phrases.length : null,
    },
    aiToneHits: findAiTone(generated).length,
    tabooHits: Array.isArray(taboo) ? taboo.filter((word) => String(generated).includes(word)).length : 0,
    chars: { ref: charCount(reference), gen: charCount(generated) },
  };
}

function metricRow(key, label, ref, gen, diff, score, weight, skipped = false, note = '') {
  return { key, label, ref, gen, diff, score: clamp01(score), weight, skipped, note };
}

/** 单条样例的分数：同一套权重；不适用的指标跳过并重新归一。 */
function sampleRows(measure, { taboo = null } = {}) {
  const rows = [
    metricRow('avgSentenceChars', '平均句长',
      measure.ref.avgChars, measure.gen.avgChars, measure.diff.avgChars,
      closeness(measure.diff.avgChars, TOLERANCE.avgSentenceChars), WEIGHTS.avgSentenceChars),
    metricRow('sentenceVariance', '句长方差',
      measure.ref.variance, measure.gen.variance, measure.diff.variance,
      closeness(measure.diff.variance, TOLERANCE.sentenceVariance), WEIGHTS.sentenceVariance),
    metricRow('questionRatio', '问句比例',
      measure.ref.questionRatio, measure.gen.questionRatio, measure.diff.questionRatio,
      closeness(measure.diff.questionRatio, TOLERANCE.questionRatio), WEIGHTS.questionRatio),
    metricRow('particleDensity', '语气词密度',
      measure.ref.particleDensity, measure.gen.particleDensity, measure.diff.particleDensity,
      closeness(measure.diff.particleDensity, TOLERANCE.particleDensity), WEIGHTS.particleDensity),
    metricRow('emojiRatio', 'emoji 比例',
      measure.ref.emojiRatio, measure.gen.emojiRatio, measure.diff.emojiRatio,
      closeness(measure.diff.emojiRatio, TOLERANCE.emojiRatio), WEIGHTS.emojiRatio),
  ];
  if (measure.phrase.coverage == null) {
    rows.push(metricRow('phraseCoverage', '高频短语覆盖', null, null, null, 0,
      WEIGHTS.phraseCoverage, true, '参考回复没有可用短语'));
  } else {
    rows.push(metricRow('phraseCoverage', '高频短语覆盖',
      1, measure.phrase.coverage, measure.phrase.coverage - 1,
      measure.phrase.coverage, WEIGHTS.phraseCoverage));
  }
  rows.push(metricRow('aiTone', 'AI 味命中',
    0, measure.aiToneHits, measure.aiToneHits,
    measure.aiToneHits ? 0 : 1, WEIGHTS.aiTone));
  rows.push(metricRow('longReplyRatio', '长回复（>3 行）',
    measure.ref.lines > 3 ? 1 : 0, measure.gen.lines > 3 ? 1 : 0, measure.diff.longReply,
    measure.diff.longReply === 0 ? 1 : 0, WEIGHTS.longReplyRatio));
  if (taboo === null) {
    rows.push(metricRow('taboo', '禁忌词命中', null, null, null, 0,
      WEIGHTS.taboo, true, '画像里没有 taboo 列表'));
  } else {
    rows.push(metricRow('taboo', '禁忌词命中',
      0, measure.tabooHits, measure.tabooHits,
      measure.tabooHits ? 0 : 1, WEIGHTS.taboo));
  }
  return rows;
}

/** 语料级指标：把所有参考回复 / 生成回复的句子合并统计，避免短样例被平均掉。 */
function corpusRows(entries, { taboo = null } = {}) {
  const refSentences = entries.flatMap((entry) => splitSentences(entry.reference));
  const genSentences = entries.flatMap((entry) => splitSentences(entry.generated));
  const refLengths = refSentences.map(charCount);
  const genLengths = genSentences.map(charCount);
  const refText = entries.map((entry) => entry.reference).join('');
  const genText = entries.map((entry) => entry.generated).join('');
  const sampleCount = entries.length;

  const refQuestion = Math.min(1, countQuestionMarks(refText) / Math.max(1, refSentences.length));
  const genQuestion = Math.min(1, countQuestionMarks(genText) / Math.max(1, genSentences.length));
  const refCompact = refText.replace(/\s+/g, '');
  const genCompact = genText.replace(/\s+/g, '');
  const refParticle = charCount(refCompact) ? (countParticles(refCompact) / charCount(refCompact)) * 100 : 0;
  const genParticle = charCount(genCompact) ? (countParticles(genCompact) / charCount(genCompact)) * 100 : 0;
  const refEmoji = refSentences.length
    ? refSentences.filter((sentence) => hasEmoji(sentence)).length / refSentences.length
    : 0;
  const genEmoji = genSentences.length
    ? genSentences.filter((sentence) => hasEmoji(sentence)).length / genSentences.length
    : 0;
  const refLong = entries.filter((entry) => entry.measure.ref.lines > 3).length / Math.max(1, sampleCount);
  const genLong = entries.filter((entry) => entry.measure.gen.lines > 3).length / Math.max(1, sampleCount);

  const coverages = entries.map((entry) => entry.measure.phrase.coverage).filter((value) => value != null);
  const phraseCoverage = coverages.length ? mean(coverages) : null;
  const aiHits = entries.reduce((sum, entry) => sum + entry.measure.aiToneHits, 0);
  const tabooHits = entries.reduce((sum, entry) => sum + entry.measure.tabooHits, 0);

  const rows = [
    metricRow('avgSentenceChars', '平均句长',
      mean(refLengths), mean(genLengths), mean(genLengths) - mean(refLengths),
      closeness(mean(genLengths) - mean(refLengths), TOLERANCE.avgSentenceChars), WEIGHTS.avgSentenceChars),
    metricRow('sentenceVariance', '句长方差',
      variance(refLengths), variance(genLengths), variance(genLengths) - variance(refLengths),
      closeness(variance(genLengths) - variance(refLengths), TOLERANCE.sentenceVariance), WEIGHTS.sentenceVariance),
    metricRow('questionRatio', '问句比例',
      refQuestion, genQuestion, genQuestion - refQuestion,
      closeness(genQuestion - refQuestion, TOLERANCE.questionRatio), WEIGHTS.questionRatio),
    metricRow('particleDensity', '语气词密度',
      refParticle, genParticle, genParticle - refParticle,
      closeness(genParticle - refParticle, TOLERANCE.particleDensity), WEIGHTS.particleDensity),
    metricRow('emojiRatio', 'emoji 比例',
      refEmoji, genEmoji, genEmoji - refEmoji,
      closeness(genEmoji - refEmoji, TOLERANCE.emojiRatio), WEIGHTS.emojiRatio),
  ];
  if (phraseCoverage == null) {
    rows.push(metricRow('phraseCoverage', '高频短语覆盖', null, null, null, 0,
      WEIGHTS.phraseCoverage, true, '参考回复没有可用短语'));
  } else {
    rows.push(metricRow('phraseCoverage', '高频短语覆盖',
      1, phraseCoverage, phraseCoverage - 1, phraseCoverage, WEIGHTS.phraseCoverage));
  }
  rows.push(metricRow('aiTone', 'AI 味命中',
    0, aiHits, aiHits, countScore(aiHits, sampleCount), WEIGHTS.aiTone));
  rows.push(metricRow('longReplyRatio', '长回复（>3 行）',
    refLong, genLong, genLong - refLong,
    closeness(genLong - refLong, TOLERANCE.longReplyRatio), WEIGHTS.longReplyRatio));
  if (taboo === null) {
    rows.push(metricRow('taboo', '禁忌词命中', null, null, null, 0,
      WEIGHTS.taboo, true, '画像里没有 taboo 列表'));
  } else {
    // 禁忌词按「零容忍」处理：命中一次就整项 0 分，避免很多样例把一次命中稀释掉。
    rows.push(metricRow('taboo', '禁忌词命中',
      0, tabooHits, tabooHits, tabooHits ? 0 : 1, WEIGHTS.taboo));
  }
  return rows;
}

function scoreRows(rows) {
  const used = rows.filter((row) => !row.skipped);
  const weightSum = used.reduce((sum, row) => sum + row.weight, 0);
  const weighted = used.reduce((sum, row) => sum + row.score * row.weight, 0);
  return {
    score: weightSum > 0 ? round((weighted / weightSum) * 100, 1) : 0,
    weightSum: round(weightSum, 2),
  };
}

/**
 * @param {Array<{id?:string,scene?:string,reference:string,generated:string}>} pairs
 * @param {{taboo?:string[]|null}} [options] taboo=null 表示画像里没有禁忌词，跳过该项
 */
function scoreReport(pairs, { taboo = null } = {}) {
  if (!Array.isArray(pairs) || !pairs.length) throw new Error('没有可评分的样例');
  const entries = pairs.map((pair) => {
    const reference = normalize(pair.reference);
    const generated = normalize(pair.generated);
    return {
      id: pair.id || '',
      scene: pair.scene || '',
      reference,
      generated,
      measure: measurePair(reference, generated, { taboo }),
    };
  });
  const metrics = corpusRows(entries, { taboo });
  const total = scoreRows(metrics);
  const samples = entries.map((entry) => {
    const rows = sampleRows(entry.measure, { taboo });
    const scored = scoreRows(rows);
    return {
      id: entry.id,
      scene: entry.scene,
      rows,
      score: scored.score,
      weightSum: scored.weightSum,
      lines: { ref: entry.measure.ref.lines, gen: entry.measure.gen.lines },
    };
  });
  return { sampleCount: entries.length, metrics, score: total.score, weightSum: total.weightSum, samples };
}

/* ------------------------------------------------------------------ */
/* 测评集校验（不调模型）                                              */
/* ------------------------------------------------------------------ */

const ANY_PLACEHOLDER_RE = /^[a-z][a-z0-9-]{1,40}$/;
const KNOWN_ORGS_RE = /(腾讯|阿里|百度|字节跳动|京东|美团|华为|网易|拼多多|快手|滴滴|哔哩哔哩|新浪|搜狐|携程|比亚迪|蔚来)/;
const COMPANY_SUFFIX_RE = /[\u4e00-\u9fa5]{2,12}(?:股份有限公司|有限责任公司|有限公司|集团|科技公司|网络公司)/g;

function hasRealOrg(text) {
  if (KNOWN_ORGS_RE.test(String(text))) return true;
  for (const match of String(text).matchAll(COMPANY_SUFFIX_RE)) {
    // 「某公司」「某某科技公司」是允许的占位；其余按真实公司名处理。
    if (!/^某/.test(match[0])) return true;
  }
  return false;
}

/**
 * 样例格式 + 脱敏规则。错误信息只带序号 / id / 规则名，不回显正文。
 * @param {*} raw 解析后的 JSON（必须是数组）
 * @returns {{ok:boolean, errors:string[], samples:Array, stats:Object|null}}
 */
function validateSamples(raw) {
  const errors = [];
  if (!Array.isArray(raw)) {
    return { ok: false, errors: ['样例文件顶层必须是数组'], samples: [], stats: null };
  }
  if (raw.length < MIN_SAMPLES) {
    errors.push(`样例不足 ${MIN_SAMPLES} 条（当前 ${raw.length} 条）`);
  }

  const ids = new Set();
  const scenes = { work: 0, friend: 0, family: 0 };

  raw.forEach((sample, index) => {
    const where = `第 ${index + 1} 条`;
    if (!isPlainObject(sample)) {
      errors.push(`${where}：必须是对象`);
      return;
    }
    const id = typeof sample.id === 'string' ? sample.id.trim() : '';
    const label = ANY_PLACEHOLDER_RE.test(id) ? `${where}（${id}）` : where;

    if (!ANY_PLACEHOLDER_RE.test(id)) {
      errors.push(`${label}：id 必须是 2~41 位小写字母 / 数字 / 短横线`);
    } else if (ids.has(id)) {
      errors.push(`${label}：id 重复`);
    } else {
      ids.add(id);
    }

    if (!SCENES.includes(sample.scene)) {
      errors.push(`${label}：scene 必须是 work / friend / family 之一`);
    } else {
      scenes[sample.scene] += 1;
    }

    if (!Array.isArray(sample.context) || !sample.context.length) {
      errors.push(`${label}：context 至少 1 条`);
    } else {
      if (sample.context.length > MAX_CONTEXT_TURNS) {
        errors.push(`${label}：context 最多 ${MAX_CONTEXT_TURNS} 条`);
      }
      sample.context.forEach((turn, turnIndex) => {
        const turnLabel = `${label} context 第 ${turnIndex + 1} 条`;
        if (!isPlainObject(turn)) {
          errors.push(`${turnLabel}：必须是对象`);
          return;
        }
        if (turn.role !== 'them' && turn.role !== 'me') {
          errors.push(`${turnLabel}：role 必须是 them 或 me`);
        }
        if (typeof turn.text !== 'string' || !turn.text.trim()) {
          errors.push(`${turnLabel}：text 必须是非空字符串`);
        } else if (charCount(turn.text) > MAX_TEXT_CHARS) {
          errors.push(`${turnLabel}：text 超过 ${MAX_TEXT_CHARS} 字`);
        }
      });
      const last = sample.context[sample.context.length - 1];
      if (!isPlainObject(last) || last.role !== 'them') {
        errors.push(`${label}：最后一条 context 必须是 them（要生成的是本人对 TA 的回复）`);
      }
    }

    if (typeof sample.reference !== 'string' || !sample.reference.trim()) {
      errors.push(`${label}：reference 必须是非空字符串`);
    } else if (charCount(sample.reference) > MAX_TEXT_CHARS) {
      errors.push(`${label}：reference 超过 ${MAX_TEXT_CHARS} 字`);
    }

    if (sample.notes != null && (typeof sample.notes !== 'string' || charCount(sample.notes) > 200)) {
      errors.push(`${label}：notes 必须是不超过 200 字的字符串`);
    }

    // 脱敏：直接复用 privacy-scan 的规则，避免这里和出包检查各写一份。
    const serialized = JSON.stringify(sample);
    const findings = privacyScan.scanText(serialized, 'samples.json');
    for (const rule of new Set(findings.map((finding) => finding.rule))) {
      errors.push(`${label}：命中隐私规则「${rule}」`);
    }
    if (hasRealOrg(serialized)) {
      errors.push(`${label}：疑似真实公司名；请改成「某公司」这类占位`);
    }
  });

  for (const scene of SCENES) {
    if (scenes[scene] < 3) {
      errors.push(`scene「${scene}」少于 3 条，测评集会偏科`);
    }
  }

  return {
    ok: errors.length === 0,
    errors,
    samples: raw,
    stats: { total: raw.length, scenes },
  };
}

/* ------------------------------------------------------------------ */
/* 生成链路：复用真实 prompt / llm                                      */
/* ------------------------------------------------------------------ */

/**
 * 用真实的人设拼装路径构造一次生成请求。
 * 只强制两件事，保证测的是「分身」而不是默认客服：
 *  - features.styleProfile = true
 *  - persona.styleScope = 'all'（默认 owner 只在主人说话时注入，样例里发声的是对方）
 * 其余（人设、采样、长度限制）全部沿用当前 config。
 */
function buildPromptInputs(sample, { config, styleProfile = null, now = new Date(), timeZone = '' } = {}) {
  const persona = require('../src/persona');
  const { buildTurns } = require('../src/core/context');
  const context = sample.context;
  const current = context[context.length - 1];
  const base = now.getTime() - context.length * 60000;
  const history = context.slice(0, -1).map((turn, index) => ({
    role: turn.role === 'them' ? 'user' : 'assistant',
    name: turn.role === 'them' ? '对方' : (config.persona && config.persona.name) || '本人',
    text: turn.text,
    ts: base + index * 60000,
  }));
  const msg = {
    chatType: 'private',
    isMaster: false,
    senderName: '对方',
    text: current.text,
    cleanText: current.text,
    ts: now.getTime(),
  };
  const parts = persona.buildPromptParts({
    config,
    msg,
    profile: null,
    notes: [],
    noteReminders: [],
    sessionSummary: '',
    events: [],
    now,
    realNow: now,
    styleProfile,
  });
  const turns = buildTurns(msg, history, '', parts.tail, timeZone);
  return { system: parts.system, turns };
}

function tabooWordsFromProfile(raw) {
  if (!isPlainObject(raw)) return null;
  const candidates = [raw.taboo, raw.tabooWords, raw.custom && raw.custom.taboo];
  const list = candidates.find((value) => Array.isArray(value));
  if (!list) return null;
  return [...new Set(list.map((item) => String(item == null ? '' : item).trim()).filter(Boolean))].slice(0, 100);
}

function readTaboo(config) {
  try {
    const raw = JSON.parse(fs.readFileSync(styleProfileLib.profilePath(config), 'utf8').replace(/^\uFEFF/, ''));
    return tabooWordsFromProfile(raw);
  } catch {
    return null;
  }
}

/* ------------------------------------------------------------------ */
/* 输出                                                                */
/* ------------------------------------------------------------------ */

const WIDE_CHAR_RE = /[\u1100-\u115f\u2e80-\u303e\u3041-\u33ff\u3400-\u4dbf\u4e00-\u9fff\ua000-\ua4cf\uac00-\ud7a3\uf900-\ufaff\ufe10-\ufe19\ufe30-\ufe6b\uff00-\uff60\uffe0-\uffe6]/;

function displayWidth(text) {
  let width = 0;
  for (const char of String(text == null ? '' : text)) {
    width += WIDE_CHAR_RE.test(char) ? 2 : 1;
  }
  return width;
}

function padCell(text, width, mode = 'left') {
  const value = String(text == null ? '' : text);
  const pad = ' '.repeat(Math.max(0, width - displayWidth(value)));
  return mode === 'right' ? pad + value : value + pad;
}

function printTable(headers, rows, aligns = []) {
  const widths = headers.map((header, index) => Math.max(
    displayWidth(header),
    ...rows.map((row) => displayWidth(row[index])),
  ));
  console.log(headers.map((header, index) => padCell(header, widths[index], aligns[index])).join('  '));
  console.log(widths.map((width) => '-'.repeat(width)).join('  '));
  for (const row of rows) {
    console.log(row.map((cell, index) => padCell(cell, widths[index], aligns[index])).join('  '));
  }
}

function signed(value, digits = 1) {
  const number = Number(value) || 0;
  return `${number >= 0 ? '+' : ''}${number.toFixed(digits)}`;
}

function formatMetricValue(key, value) {
  if (value == null) return '—';
  switch (key) {
    case 'avgSentenceChars':
      return `${Number(value).toFixed(1)} 字`;
    case 'sentenceVariance':
      return Number(value).toFixed(1);
    case 'questionRatio':
    case 'emojiRatio':
    case 'longReplyRatio':
    case 'phraseCoverage':
      return `${Math.round(Number(value) * 100)}%`;
    case 'particleDensity':
      return `${Number(value).toFixed(1)}/100`;
    case 'aiTone':
    case 'taboo':
      return String(Math.round(Number(value)));
    default:
      return String(value);
  }
}

function formatMetricDiff(key, value) {
  if (value == null) return '—';
  switch (key) {
    case 'avgSentenceChars':
      return `${signed(value, 1)} 字`;
    case 'sentenceVariance':
      return signed(value, 1);
    case 'questionRatio':
    case 'emojiRatio':
    case 'longReplyRatio':
    case 'phraseCoverage':
      return `${signed(Number(value) * 100, 1)}%`;
    case 'particleDensity':
      return signed(value, 1);
    case 'aiTone':
    case 'taboo':
      return signed(value, 0);
    default:
      return String(value);
  }
}

function sampleTableRows(report) {
  return report.samples.map((sample) => {
    // 取单条指标只用于表格展示；分数已在 sampleRows 里按同一套权重算好。
    const byKey = new Map(sample.rows.map((row) => [row.key, row]));
    const diffOf = (key) => (byKey.get(key) ? byKey.get(key).diff : null);
    const coverage = byKey.get('phraseCoverage').skipped ? '—'
      : `${Math.round(byKey.get('phraseCoverage').gen * 100)}%`;
    return [
      sample.id,
      SCENE_LABELS[sample.scene] || sample.scene || '—',
      signed(diffOf('avgSentenceChars'), 1),
      signed(diffOf('sentenceVariance'), 1),
      formatMetricDiff('questionRatio', diffOf('questionRatio')),
      formatMetricDiff('particleDensity', diffOf('particleDensity')),
      formatMetricDiff('emojiRatio', diffOf('emojiRatio')),
      coverage,
      String(Math.round(byKey.get('aiTone').gen || 0)),
      `${sample.lines.ref}/${sample.lines.gen}`,
      sample.score.toFixed(0),
    ];
  });
}

function metricTableRows(report) {
  return report.metrics.map((row) => [
    row.label,
    row.skipped ? '—' : formatMetricValue(row.key, row.ref),
    row.skipped ? '—' : formatMetricValue(row.key, row.gen),
    row.skipped ? '—' : formatMetricDiff(row.key, row.diff),
    row.skipped ? '跳过' : row.score.toFixed(2),
    row.weight.toFixed(2),
  ]);
}

function printReport(report, options) {
  console.log(`\n风格测评：${report.sampleCount} 条样例`);
  console.log('\n逐条差异（差值为「生成 - 参考」，行数列为「参考/生成」，单条得分仅供诊断）');
  printTable(
    ['样例', '场景', '句长差', '方差差', '问句差', '语气词差', 'emoji 差', '短语覆盖', 'AI 味', '行数', '得分'],
    sampleTableRows(report),
    ['left', 'left', 'right', 'right', 'right', 'right', 'right', 'right', 'right', 'right', 'right'],
  );
  console.log('\n指标汇总（语料级：所有样例的句子合并统计）');
  printTable(
    ['指标', '参考', '生成', '差值', '子分', '权重'],
    metricTableRows(report),
    ['left', 'right', 'right', 'right', 'right', 'right'],
  );
  if (report.metrics.some((row) => row.skipped)) {
    const skipped = report.metrics.filter((row) => row.skipped).map((row) => row.note).filter(Boolean);
    console.log(`跳过项不参与总分，剩余权重已重新归一（有效权重 ${report.weightSum.toFixed(2)}）。`);
    for (const note of skipped) console.log(`  · ${note}`);
  }
  const passed = report.score >= options.minScore;
  console.log(`\n总分：${report.score.toFixed(1)} / 100（阈值 ${options.minScore}）${passed ? '通过' : '未达标'}`);
}

function metricToJson(row) {
  return {
    metric: row.key,
    reference: row.ref == null ? null : round(row.ref, 4),
    generated: row.gen == null ? null : round(row.gen, 4),
    diff: row.diff == null ? null : round(row.diff, 4),
    score: round(row.score, 4),
    weight: row.weight,
    skipped: row.skipped,
  };
}

/** --json 只放指标与 id；参考/生成正文、短语、具体命中词一律不写。 */
function toJsonReport(report, { minScore = DEFAULT_MIN_SCORE, passed = false } = {}) {
  return {
    version: 1,
    sampleCount: report.sampleCount,
    score: report.score,
    minScore,
    passed: Boolean(passed),
    weightSum: report.weightSum,
    metrics: report.metrics.map(metricToJson),
    samples: report.samples.map((sample) => ({
      id: sample.id,
      scene: sample.scene,
      score: sample.score,
      lines: { reference: sample.lines.ref, generated: sample.lines.gen },
      metrics: sample.rows.map(metricToJson),
    })),
  };
}

function writeJson(file, data) {
  const target = path.resolve(file);
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, JSON.stringify(data, null, 2) + '\n', 'utf8');
  return target;
}

/* ------------------------------------------------------------------ */
/* CLI                                                                 */
/* ------------------------------------------------------------------ */

function usage() {
  return [
    '用法：node scripts/eval-style.js <--check | --run> [选项]',
    '  --check              只校验测评集格式与脱敏规则，不调模型、不读配置',
    '  --run                逐条调用当前模型生成，再与 reference 比对',
    '  --samples <path>     样例文件（默认 tests/fixtures/style-eval/samples.json）',
    '  --json <path>        把指标明细写成 JSON（只有指标与 id，没有正文）',
    '  --min-score <n>      总分阈值 0~100，低于则 exit 1（默认 60）',
    '  --limit <n>          只跑前 n 条（--run 调试用）',
    '  --help, -h           显示这段说明',
  ].join('\n');
}

function parseArgs(argv) {
  const options = {
    check: false, run: false, samples: '', json: '',
    minScore: DEFAULT_MIN_SCORE, limit: 0, help: false,
  };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--help' || arg === '-h') { options.help = true; continue; }
    if (arg === '--check') { options.check = true; continue; }
    if (arg === '--run') { options.run = true; continue; }
    const eq = arg.indexOf('=');
    const name = eq >= 0 ? arg.slice(0, eq) : arg;
    const inline = eq >= 0 ? arg.slice(eq + 1) : null;
    const takeValue = () => {
      if (inline != null) return inline;
      index += 1;
      return index < argv.length ? argv[index] : '';
    };
    if (name === '--samples') { options.samples = String(takeValue() || '').trim(); continue; }
    if (name === '--json') { options.json = String(takeValue() || '').trim(); continue; }
    if (name === '--min-score') {
      const value = Number(takeValue());
      if (!Number.isFinite(value) || value < 0 || value > 100) {
        throw new Error('--min-score 必须是 0~100 的数字');
      }
      options.minScore = value;
      continue;
    }
    if (name === '--limit') {
      const value = Number(takeValue());
      if (!Number.isInteger(value) || value < 1) throw new Error('--limit 必须是正整数');
      options.limit = value;
      continue;
    }
    throw new Error(`不认识的参数：${arg}`);
  }
  return options;
}

function relativeToRoot(file) {
  const root = path.resolve(__dirname, '..');
  const rel = path.relative(root, path.resolve(file));
  return rel && !rel.startsWith('..') ? rel.split(path.sep).join('/') : path.resolve(file);
}

function printCheck(result, file) {
  console.log(`风格测评集：${relativeToRoot(file)}`);
  if (!result.ok) {
    console.error(`\n✗ 共 ${result.errors.length} 项问题：`);
    for (const line of result.errors.slice(0, 30)) console.error('  - ' + line);
    if (result.errors.length > 30) console.error(`  …其余 ${result.errors.length - 30} 项省略`);
    return;
  }
  const { total, scenes } = result.stats;
  console.log(`样例数：${total}（work ${scenes.work} / friend ${scenes.friend} / family ${scenes.family}）`);
  console.log('格式检查：通过');
  console.log('脱敏检查：通过（复用 privacy-scan 的 wxid / 群号 / 手机号 / 邮箱规则）');
  console.log('未调用模型，未写入任何文件。');
}

async function runEval(samples, samplesFile, options) {
  // 延迟加载：--check 不碰配置，也不构造任何运行时对象。
  const { loadConfig, projectRoot } = require('../src/infra/config');
  const { createLogger } = require('../src/infra/logger');
  const { DeepSeekClient } = require('../src/llm/client');
  const timeLib = require('../src/infra/time');

  const root = projectRoot();
  const config = loadConfig({ root, readOnly: true });
  if (config.configError) {
    console.error('提示：config.json 读取失败，本次按默认配置运行：' + config.configError);
  }
  const llm = new DeepSeekClient({
    config,
    logger: createLogger({ level: 'warn', tag: 'eval-style' }),
  });
  if (llm.isMock) {
    console.error('没有可用的模型配置（缺少 Key 或被设为 mock），--run 无法执行。');
    return 1;
  }

  const styleProfile = styleProfileLib.load(config);
  if (!styleProfile) {
    console.error('提示：没有 data/style-profile.json，本次不会注入风格画像；'
      + '先跑 node scripts/learn-style.js，否则测的只是基础输出。');
  }
  const taboo = readTaboo(config);
  if (taboo === null) {
    console.error('提示：画像里没有 taboo 列表，禁忌词一项将跳过。');
  }

  // 测评固定这两个开关：否则默认 owner scope 会跳过注入，测不到分身效果。
  const evalConfig = {
    ...config,
    persona: { ...config.persona, styleScope: 'all' },
    features: { ...config.features, styleProfile: true },
  };
  const timeZone = timeLib.resolveTimeZone(evalConfig);
  const now = new Date();
  const list = options.limit ? samples.slice(0, options.limit) : samples;
  const pairs = [];

  console.log(`风格测评：${list.length} 条样例，模型 ${llm.provider}/${llm.model}`);
  console.log(`样例：${relativeToRoot(samplesFile)}`);
  for (let index = 0; index < list.length; index += 1) {
    const sample = list[index];
    const input = buildPromptInputs(sample, {
      config: evalConfig, styleProfile, now, timeZone,
    });
    let generated;
    try {
      // 非流式调用：和线上 stream 共用同一份 payload / 提示词构造，只是不等分片。
      // 不经过 pipeline 的复读重写和发送前过滤，测的是模型在给定提示词下的表达。
      generated = await llm.complete({
        system: input.system,
        turns: input.turns,
        purpose: 'chat',
      });
    } catch (err) {
      console.error(`✗ 第 ${index + 1} 条（${sample.id}）生成失败：${errorMessage(err, '模型调用失败')}`);
      return 1;
    }
    if (!generated || !generated.trim()) {
      console.error(`✗ 第 ${index + 1} 条（${sample.id}）生成结果为空`);
      return 1;
    }
    pairs.push({
      id: sample.id,
      scene: sample.scene,
      reference: sample.reference,
      generated,
    });
    if ((index + 1) % 5 === 0 || index === list.length - 1) {
      console.log(`已生成 ${index + 1}/${list.length}`);
    }
  }

  const report = scoreReport(pairs, { taboo });
  printReport(report, options);

  if (options.json) {
    const json = toJsonReport(report, {
      minScore: options.minScore,
      passed: report.score >= options.minScore,
    });
    const target = writeJson(options.json, json);
    console.log(`明细已写入：${target}（只有指标与 id）`);
  }

  return report.score >= options.minScore ? 0 : 1;
}

async function main(argv = process.argv.slice(2)) {
  let options;
  try {
    options = parseArgs(argv);
  } catch (err) {
    console.error(err.message);
    console.error(usage());
    return 2;
  }
  if (options.help) {
    console.log(usage());
    return 0;
  }
  if (options.check === options.run) {
    console.error('要么 --check，要么 --run。');
    console.error(usage());
    return 2;
  }

  const samplesFile = path.resolve(options.samples || DEFAULT_SAMPLES);
  let raw;
  try {
    raw = JSON.parse(fs.readFileSync(samplesFile, 'utf8').replace(/^\uFEFF/, ''));
  } catch (err) {
    console.error('读取样例失败：' + errorMessage(err, '文件不存在或不是合法 JSON'));
    return 1;
  }

  const validation = validateSamples(raw);
  if (options.check) {
    printCheck(validation, samplesFile);
    return validation.ok ? 0 : 1;
  }
  if (!validation.ok) {
    printCheck(validation, samplesFile);
    return 1;
  }
  if (options.limit && options.limit < validation.samples.length) {
    console.log(`提示：--limit ${options.limit}，只跑前 ${options.limit} 条，分数不代表整套。`);
  }
  return runEval(validation.samples, samplesFile, options);
}

if (require.main === module) {
  main()
    .then((code) => { process.exitCode = code; })
    .catch((err) => {
      console.error('风格测评失败：' + errorMessage(err, '未知错误'));
      process.exitCode = 1;
    });
}

module.exports = {
  DEFAULT_SAMPLES,
  MIN_SAMPLES,
  DEFAULT_MIN_SCORE,
  SCENES,
  WEIGHTS,
  TOLERANCE,
  AI_TONE_PATTERNS,
  parseArgs,
  splitSentences,
  topPhrasesOf,
  findAiTone,
  measurePair,
  scoreReport,
  validateSamples,
  tabooWordsFromProfile,
  buildPromptInputs,
  printReport,
  toJsonReport,
};
