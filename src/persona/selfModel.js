'use strict';

/**
 * 数字人分身的「自我模型」与本地数据文件。
 *
 * 纯逻辑 + 文件 IO，不依赖 pipeline / services / modules：
 * pipeline 用它拼提示词，HTTP 路由用它读写文件，两边看到的是同一份数据。
 *
 * 四份数据都在 data/ 下，属于用户数据：
 *   data/self-model.json          L1 共享层
 *   data/share/manifest.json      可分享资产
 *   data/on-duty/pending.json     待确认队列
 *   data/contact-policy.json      对象权限（读写由 security/access.js 负责）
 * 首次访问只创建缺失的文件，绝不覆盖已有内容。
 */
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const retrieval = require('../memory/retrieval');
const personCard = require('../memory/personCard');
const textBudget = require('../infra/textBudget');
const time = require('../infra/time');
const { isVisible, normalizeScopes } = require('../security/access');

const MODEL_FILE = 'self-model.json';
const SHARE_DIR = 'share';
const SHARE_MANIFEST = 'manifest.json';
const PENDING_FILE = path.join('on-duty', 'pending.json');

const MAX_MODEL_BYTES = 512 * 1024;
const MAX_ASSET_BYTES = 20 * 1024 * 1024;
const MAX_ASSETS = 500;
const MAX_PENDING = 200;
const MAX_KNOWLEDGE = 500;
const MAX_STANCES = 200;
const MAX_TEMPLATES = 100;
const KNOWLEDGE_TEXT_MAX = 800;
const STANCE_TEXT_MAX = 300;
const TEMPLATE_TEXT_MAX = 500;
const ID_RE = /^[a-z0-9][a-z0-9._-]{0,63}$/;
const DEFAULT_BUDGET = 900;
const BOUNDARY = '（以下只来自当前对话和授权范围，不得向其他对象转述。）';
const ON_DUTY_REASONS = new Set(['disabled', 'outside_hours', 'contact_off', 'active']);

const DEFAULT_MODEL = Object.freeze({
  version: 1,
  updatedAt: 0,
  identity: Object.freeze({
    name: '', aliases: [], age: 0, city: '', job: '', company: '', timezone: '',
  }),
  voice: Object.freeze({
    tone: '', sentenceStyle: '', catchphrases: [], emojiHabits: '', tabooWords: [],
  }),
  knowledge: Object.freeze([]),
  stances: Object.freeze([]),
  templates: Object.freeze([]),
});

const IDENTITY_KEYS = ['name', 'aliases', 'age', 'city', 'job', 'company', 'timezone'];
const VOICE_KEYS = ['tone', 'sentenceStyle', 'catchphrases', 'emojiHabits', 'tabooWords'];

/* ------------------------------ 敏感内容 ------------------------------ */

// 只拦「真的把凭据写进来了」的形状：单独的「密码」两个字不算。
const SECRET_PATTERNS = [
  /\b(?:sk|oc_sk|ak|ghp|xox[baprs]|brz)[-_][A-Za-z0-9_-]{8,}\b/,
  /(?:密码|口令|验证码|密钥|api[\s_-]?key|access[\s_-]?token|secret)\s*(?:是|为|[:：=])\s*\S{2,}/i,
  /(?<!\d)(?:\d[ -]?){15,19}(?!\d)/,
  /\b\d{17}[\dXx]\b/,
];

function secretHit(value) {
  const text = String(value == null ? '' : value);
  if (!text) return '';
  for (const re of SECRET_PATTERNS) {
    const hit = text.match(re);
    if (hit) return re.source.slice(0, 40);
  }
  return '';
}

function walkStrings(value, visit, trail = '') {
  if (typeof value === 'string') {
    visit(value, trail);
    return;
  }
  if (Array.isArray(value)) {
    value.forEach((item, index) => walkStrings(item, visit, `${trail}[${index}]`));
    return;
  }
  if (value && typeof value === 'object') {
    for (const [key, item] of Object.entries(value)) {
      walkStrings(item, visit, trail ? `${trail}.${key}` : key);
    }
  }
}

/* ------------------------------ 路径与缓存 ------------------------------ */

function dataDir(config) {
  return (config && config.paths && config.paths.dataDir) || path.join(process.cwd(), 'data');
}

function modelFile(config) { return path.join(dataDir(config), MODEL_FILE); }
function shareDir(config) { return path.join(dataDir(config), SHARE_DIR); }
function shareFilesDir(config) { return path.join(shareDir(config), 'files'); }
function shareManifestFile(config) { return path.join(shareDir(config), SHARE_MANIFEST); }
function pendingFile(config) { return path.join(dataDir(config), PENDING_FILE); }

const FILE_CACHE = new Map();

function stampOf(file) {
  try {
    const st = fs.statSync(file);
    return `${st.mtimeMs}:${st.size}`;
  } catch {
    return '';
  }
}

function readCached(file, fallback) {
  const stamp = stampOf(file);
  const hit = FILE_CACHE.get(file);
  if (hit && hit.stamp === stamp) return hit.value;
  let raw;
  try {
    raw = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  } catch {
    return fallback;
  }
  FILE_CACHE.set(file, { stamp, value: raw });
  return raw;
}

function writeJsonAtomic(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.${process.pid}.${Date.now()}.${crypto.randomBytes(2).toString('hex')}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(value, null, 2) + '\n', 'utf8');
  fs.renameSync(tmp, file);
  FILE_CACHE.set(file, { stamp: stampOf(file), value });
}

/** 只创建缺失文件；已有内容一个字节都不动（data/ 是用户数据）。 */
function ensureJsonFile(file, value) {
  try {
    if (fs.existsSync(file)) return;
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const tmp = `${file}.${process.pid}.${Date.now()}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify(value, null, 2) + '\n', 'utf8');
    fs.renameSync(tmp, file);
  } catch { /* 目录只读时按默认值继续 */ }
}

function shortId(bytes = 2) {
  return crypto.randomBytes(bytes).toString('hex');
}

function dayStamp(ms) {
  const d = new Date(Number(ms) || Date.now());
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}`;
}

/* ------------------------------ 规范化 ------------------------------ */

function clip(value, max) {
  const s = String(value == null ? '' : value).replace(/\u0000/g, '').trim();
  return s.length > max ? s.slice(0, max) : s;
}

function clamp01(value, fallback) {
  const n = Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.min(1, Math.max(0, n));
}

function timestamp(value, fallback = 0) {
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? Math.trunc(n) : fallback;
}

function stringList(value, { maxItems = 20, maxLength = 40 } = {}) {
  const list = Array.isArray(value) ? value : [];
  const out = [];
  for (const item of list) {
    const s = clip(item, maxLength);
    if (!s || out.includes(s)) continue;
    out.push(s);
    if (out.length >= maxItems) break;
  }
  return out;
}

function normalizeSource(value) {
  if (typeof value === 'string') return clip(value, 40);
  if (!value || typeof value !== 'object') return 'manual';
  const out = {};
  if (value.uid) out.uid = clip(value.uid, 100);
  if (value.chatId) out.chatId = clip(value.chatId, 160);
  if (value.messageId) out.messageId = clip(value.messageId, 120);
  if (value.at) out.at = timestamp(value.at, 0);
  return Object.keys(out).length ? out : 'manual';
}

function normalizeKnowledge(raw, warnings, index) {
  if (!raw || typeof raw !== 'object') {
    warnings.push(`knowledge[${index}] 不是对象，已忽略`);
    return null;
  }
  const text = clip(raw.text, KNOWLEDGE_TEXT_MAX);
  if (!text) {
    warnings.push(`knowledge[${index}] 没有正文，已忽略`);
    return null;
  }
  if (String(raw.text).length > KNOWLEDGE_TEXT_MAX) {
    warnings.push(`knowledge[${index}] 正文超过 ${KNOWLEDGE_TEXT_MAX} 字，已截断`);
  }
  const id = raw.id == null || raw.id === '' ? `k-${dayStamp(raw.createdAt)}-${shortId()}` : String(raw.id).trim();
  if (!ID_RE.test(id)) {
    warnings.push(`knowledge[${index}] 的 id 不合法，已拒绝`);
    return null;
  }
  let scopes = normalizeScopes(raw.scopes);
  if (!scopes.length) {
    scopes = ['private'];
    warnings.push(`knowledge[${index}] 没有有效域，按 private（仅主人）保存`);
  }
  return {
    id,
    title: clip(raw.title, 60) || text.slice(0, 20),
    text,
    scopes,
    tags: stringList(raw.tags, { maxItems: 12, maxLength: 16 }),
    importance: clamp01(raw.importance, 0.5),
    confidence: clamp01(raw.confidence, 0.6),
    source: normalizeSource(raw.source),
    createdAt: timestamp(raw.createdAt, Date.now()),
    updatedAt: timestamp(raw.updatedAt, timestamp(raw.createdAt, Date.now())),
  };
}

function normalizeStance(raw, warnings, index) {
  if (!raw || typeof raw !== 'object') {
    warnings.push(`stances[${index}] 不是对象，已忽略`);
    return null;
  }
  const position = clip(raw.position, STANCE_TEXT_MAX);
  const topic = clip(raw.topic, 40);
  if (!topic || !position) {
    warnings.push(`stances[${index}] 缺少话题或立场，已忽略`);
    return null;
  }
  if (String(raw.position).length > STANCE_TEXT_MAX) {
    warnings.push(`stances[${index}] 立场超过 ${STANCE_TEXT_MAX} 字，已截断`);
  }
  let scopes = normalizeScopes(raw.scopes);
  if (!scopes.length) {
    scopes = ['private'];
    warnings.push(`stances[${index}] 没有有效域，按 private（仅主人）保存`);
  }
  return { topic, position, scopes };
}

function normalizeTemplate(raw, warnings, index) {
  if (!raw || typeof raw !== 'object') {
    warnings.push(`templates[${index}] 不是对象，已忽略`);
    return null;
  }
  const text = clip(raw.text, TEMPLATE_TEXT_MAX);
  if (!text) {
    warnings.push(`templates[${index}] 没有正文，已忽略`);
    return null;
  }
  if (String(raw.text).length > TEMPLATE_TEXT_MAX) {
    warnings.push(`templates[${index}] 正文超过 ${TEMPLATE_TEXT_MAX} 字，已截断`);
  }
  const id = raw.id == null || raw.id === '' ? `t-${dayStamp(raw.createdAt)}-${shortId()}` : String(raw.id).trim();
  if (!ID_RE.test(id)) {
    warnings.push(`templates[${index}] 的 id 不合法，已拒绝`);
    return null;
  }
  let scopes = normalizeScopes(raw.scopes);
  if (!scopes.length) {
    scopes = ['private'];
    warnings.push(`templates[${index}] 没有有效域，按 private（仅主人）保存`);
  }
  return {
    id,
    title: clip(raw.title, 60) || text.slice(0, 20),
    text,
    scopes,
  };
}

/**
 * 字段白名单 + 长度 + id + 敏感内容校验。
 * @returns {{model:object, warnings:string[], rejected:string[]}}
 */
function validate(input) {
  const warnings = [];
  const rejected = [];
  const raw = input && typeof input === 'object' && !Array.isArray(input) ? input : {};
  if (!input || typeof input !== 'object' || Array.isArray(input)) {
    warnings.push('自我模型必须是对象，已按空模型处理');
  }

  // 敏感内容先查：只要命中，整次写入拒绝，避免半截数据落盘。
  walkStrings(raw, (value, trail) => {
    const hit = secretHit(value);
    if (hit) rejected.push(`${trail || '字段'} 含敏感内容（Key / 密码 / 验证码 / 完整卡号），已拒绝`);
  });

  const identityIn = raw.identity && typeof raw.identity === 'object' ? raw.identity : {};
  const identity = {
    name: clip(identityIn.name, 24),
    aliases: stringList(identityIn.aliases, { maxItems: 20, maxLength: 40 }),
    age: Math.max(0, Math.min(120, Math.trunc(Number(identityIn.age) || 0))),
    city: clip(identityIn.city, 40),
    job: clip(identityIn.job, 60),
    company: clip(identityIn.company, 60),
    timezone: clip(identityIn.timezone, 64),
  };
  if (identity.timezone && !time.isValidTimeZone(identity.timezone)) {
    warnings.push('identity.timezone 不是合法时区，已清空');
    identity.timezone = '';
  }

  const voiceIn = raw.voice && typeof raw.voice === 'object' ? raw.voice : {};
  const voice = {
    tone: clip(voiceIn.tone, 120),
    sentenceStyle: clip(voiceIn.sentenceStyle, 120),
    catchphrases: stringList(voiceIn.catchphrases, { maxItems: 20, maxLength: 40 }),
    emojiHabits: clip(voiceIn.emojiHabits, 120),
    tabooWords: stringList(voiceIn.tabooWords, { maxItems: 20, maxLength: 20 }),
  };

  const knowledge = (Array.isArray(raw.knowledge) ? raw.knowledge : [])
    .map((item, index) => normalizeKnowledge(item, warnings, index))
    .filter(Boolean);
  const stances = (Array.isArray(raw.stances) ? raw.stances : [])
    .map((item, index) => normalizeStance(item, warnings, index))
    .filter(Boolean);
  const templates = (Array.isArray(raw.templates) ? raw.templates : [])
    .map((item, index) => normalizeTemplate(item, warnings, index))
    .filter(Boolean);

  // knowledge 按 importance + updatedAt 保留；stances / templates 没有这两个字段，保留最新的。
  if (knowledge.length > MAX_KNOWLEDGE) {
    warnings.push(`knowledge 超过 ${MAX_KNOWLEDGE} 条，按 importance + updatedAt 淘汰了 ${knowledge.length - MAX_KNOWLEDGE} 条`);
    knowledge.sort((a, b) => (b.importance - a.importance) || (b.updatedAt - a.updatedAt));
    knowledge.length = MAX_KNOWLEDGE;
  }
  if (stances.length > MAX_STANCES) {
    warnings.push(`stances 超过 ${MAX_STANCES} 条，淘汰了最旧的 ${stances.length - MAX_STANCES} 条`);
    stances.splice(0, stances.length - MAX_STANCES);
  }
  if (templates.length > MAX_TEMPLATES) {
    warnings.push(`templates 超过 ${MAX_TEMPLATES} 条，淘汰了最旧的 ${templates.length - MAX_TEMPLATES} 条`);
    templates.splice(0, templates.length - MAX_TEMPLATES);
  }

  // 未知字段只计数，不保留正文。
  const allowed = new Set(['version', 'updatedAt', 'identity', 'voice', 'knowledge', 'stances', 'templates']);
  const unknown = Object.keys(raw).filter((key) => !allowed.has(key));
  if (unknown.length) warnings.push(`已丢弃 ${unknown.length} 个未知字段`);

  const model = {
    version: 1,
    updatedAt: timestamp(raw.updatedAt, 0),
    identity,
    voice,
    knowledge,
    stances,
    templates,
  };
  return { model, warnings, rejected };
}

/* ------------------------------ 读写 ------------------------------ */

function readRaw(config) {
  const file = modelFile(config);
  ensureJsonFile(file, DEFAULT_MODEL);
  return readCached(file, null);
}

function defaultModel() {
  return JSON.parse(JSON.stringify(DEFAULT_MODEL));
}

function load(config) {
  const raw = readRaw(config);
  if (!raw) return defaultModel();
  return validate(raw).model;
}

/** 原子写；命中敏感内容整次拒绝。 */
function save(config, input) {
  const checked = validate(input);
  if (checked.rejected.length) {
    throw Object.assign(new Error('自我模型没有保存：' + checked.rejected.join('；')), {
      statusCode: 400,
      code: 'SELF_MODEL_REJECTED',
    });
  }
  const model = checked.model;
  model.updatedAt = Date.now();
  const body = JSON.stringify(model, null, 2) + '\n';
  if (Buffer.byteLength(body, 'utf8') > MAX_MODEL_BYTES) {
    throw Object.assign(new Error(`自我模型超过 ${Math.round(MAX_MODEL_BYTES / 1024)} KiB 上限，没有保存`), {
      statusCode: 413,
      code: 'SELF_MODEL_TOO_LARGE',
    });
  }
  writeJsonAtomic(modelFile(config), model);
  return { model, warnings: checked.warnings };
}

/* ------------------------------ L1 写入（仅主人） ------------------------------ */

/**
 * 把一条「主人自己的事实」写进 L1。
 *
 * 调用方必须先确认来源：只有主人自己说的话 / 主人手动录入才能调这里。
 * `scopes` 默认要传当前对象的域（最保守），绝不默认 public。
 */
function rememberKnowledge(config, entry = {}) {
  if (!entry || typeof entry !== 'object') throw new TypeError('entry 必须是对象');
  const current = load(config);
  const now = entry.updatedAt || entry.createdAt || Date.now();
  let scopes = normalizeScopes(entry.scopes);
  if (!scopes.length) scopes = ['private'];
  const text = clip(entry.text, KNOWLEDGE_TEXT_MAX);
  if (!text) return { model: current, entry: null, created: false };
  const title = clip(entry.title, 60) || text.slice(0, 20);
  const source = normalizeSource(entry.source);
  const existing = current.knowledge.find((item) => item.title === title
    && personCard.similarity(item.text, text) >= 0.9);
  if (existing) {
    existing.text = text;
    existing.scopes = scopes;
    existing.updatedAt = now;
    if (source && (typeof source === 'object' || !existing.source)) existing.source = source;
    const saved = save(config, current);
    return { model: saved.model, entry: existing, created: false, warnings: saved.warnings };
  }
  const item = {
    id: entry.id && ID_RE.test(String(entry.id))
      ? String(entry.id)
      : `k-${dayStamp(now)}-${shortId()}`,
    title,
    text,
    scopes,
    tags: stringList(entry.tags, { maxItems: 12, maxLength: 16 }),
    importance: clamp01(entry.importance, 0.6),
    confidence: clamp01(entry.confidence, 0.85),
    source,
    createdAt: timestamp(entry.createdAt, now),
    updatedAt: now,
  };
  current.knowledge.push(item);
  const saved = save(config, current);
  return { model: saved.model, entry: item, created: true, warnings: saved.warnings };
}

/**
 * L2 → L1 的显式提升。只接受 about=self 的条目；
 * 对象的披露（about=other）永远留在 L2，不允许被这里搬过去。
 */
function promoteToL1(config, { entry, scopes, source } = {}) {
  if (!entry || typeof entry !== 'object') throw new TypeError('缺少要提升的 L2 条目');
  if (entry.about !== 'self') {
    throw Object.assign(new Error('只有主人自己的条目（about=self）才能提升到共享层'), {
      statusCode: 403,
      code: 'L2_NOT_SELF',
    });
  }
  return rememberKnowledge(config, {
    title: entry.title || entry.key || entry.text,
    text: entry.text,
    scopes,
    source: source || entry.source,
    importance: entry.importance,
    confidence: entry.confidence,
    tags: entry.tags,
  });
}

/* ------------------------------ 静态提示词段 ------------------------------ */

function identityLines(model) {
  const identity = (model && model.identity) || {};
  const name = String(identity.name || '').trim();
  const lines = ['【在岗身份】'];
  if (name) {
    lines.push(`- 你现在以「${name}」的身份说话，不是助手，也不是客服；不要自称助手、机器人、程序。`);
  } else {
    lines.push('- 你现在以主人的身份说话，不是助手，也不是客服；不要自称助手、机器人、程序。');
  }
  const facts = [];
  if (identity.job) facts.push(`${identity.job}${identity.company ? `（${identity.company}）` : ''}`);
  if (identity.city) facts.push(`在${identity.city}`);
  if (facts.length) lines.push(`- 你的情况：${facts.join('，')}${identity.age ? `，${identity.age} 岁` : ''}。`);
  else if (identity.age) lines.push(`- 年龄：${identity.age} 岁。`);
  if (identity.aliases && identity.aliases.length) {
    lines.push(`- 有人叫你 ${identity.aliases.join('、')}，那也是在叫你。`);
  }
  lines.push('- 用第一人称说话；别人问起你的身份，按上面这些说，不编造没写过的经历。');
  return lines;
}

function voiceLines(model, overrides = null) {
  const voice = (model && model.voice) || {};
  const ov = overrides && typeof overrides === 'object' ? overrides : {};
  const lines = ['【说话语气】'];
  const tone = clip(ov.toneOverride, 120) || String(voice.tone || '').trim();
  if (tone) lines.push(`- 语气：${tone}`);
  if (voice.sentenceStyle) lines.push(`- 句子：${voice.sentenceStyle}`);
  if (voice.catchphrases && voice.catchphrases.length) {
    lines.push(`- 常用口头禅：${voice.catchphrases.join('、')}（自然时用，不要每句都带）。`);
  }
  if (voice.emojiHabits) lines.push(`- 表情习惯：${voice.emojiHabits}`);
  if (voice.tabooWords && voice.tabooWords.length) {
    lines.push(`- 绝对不用这些词：${voice.tabooWords.join('、')}。`);
  }
  if (ov.addressAs) lines.push(`- 对这个人：称呼「${clip(ov.addressAs, 40)}」，不要换别的称呼。`);
  const tabooTopics = stringList(ov.tabooTopics, { maxItems: 10, maxLength: 20 });
  if (tabooTopics.length) lines.push(`- 对这个人不要主动聊：${tabooTopics.join('、')}。`);
  return lines;
}

/** 契约 §4.2 的强制规则；discloseAI 只改第 4 条，不引入额外状态机。 */
function ruleLines(model, policy) {
  const disclose = policy && policy.discloseAI ? policy.discloseAI : 'onAsk';
  const disclosure = disclose === 'never'
    ? '被直接问「你是不是 AI / 是不是代回」时：承认「是本人，只是现在不方便长聊」，并把这一轮标记为需要主人接管。'
    : disclose === 'always'
      ? '被直接问「你是不是 AI / 是不是代回」时如实说明；这一轮的首条回复要主动说明「这条是分身代回的」。'
      : '被直接问「你是不是 AI / 是不是代回」时如实说明「这条是分身代回的，主人看到会回你」。';
  return [
    '【在岗规则（必须遵守）】',
    '1. 只能使用本轮给出的【授权信息】和本会话记忆回答；没有依据的事实不编造，说「我确认下」或「这个我不太清楚」。',
    '2. 不替主人承诺时间、价格、合同、金额、责任；这类内容一律回「我确认后回复你」，并进待确认队列。',
    '3. 不透露任何其他会话、其他对象、其他权限域的信息；被问到就说「这个不方便说」。',
    `4. 不主动透露自己是程序。${disclosure}`,
    '5. 不发未经允许的文件、图片、链接；只允许使用【可发送资产】里的条目，并在回复最末尾写一个 [[asset:id]] 标记，一条回复最多一个。',
  ];
}

/* ------------------------------ 在岗判定 ------------------------------ */

function minutesOfClock(text) {
  const m = /^(\d{1,2}):(\d{2})$/.exec(String(text || '').trim());
  if (!m) return null;
  const hour = Number(m[1]);
  const minute = Number(m[2]);
  if (hour > 23 || minute > 59) return null;
  return hour * 60 + minute;
}

function workingHours(config) {
  const raw = (config && config.onDuty && config.onDuty.workingHours) || {};
  const start = minutesOfClock(raw.start);
  const end = minutesOfClock(raw.end);
  const days = Array.isArray(raw.days)
    ? [...new Set(raw.days.map((d) => Number(d)).filter((d) => Number.isInteger(d) && d >= 0 && d <= 6))]
    : [];
  return {
    start: start == null ? 9 * 60 : start,
    end: end == null ? 19 * 60 : end,
    days: days.length ? days : [1, 2, 3, 4, 5],
  };
}

/**
 * 登录态判定：总开关 + 时段 + 对象策略。群聊按群 ID 传 policy 即可。
 * @returns {{active:boolean, reason:string, mode:string, minutes:number, day:number}}
 */
function onDutyStatus(config, policy, now = Date.now()) {
  const at = Number(now) || Date.now();
  const tz = time.resolveTimeZone(config);
  const wall = time.zonedDate(at, tz);
  const minutes = wall.getHours() * 60 + wall.getMinutes();
  const day = wall.getDay();
  const mode = (policy && policy.onDuty) || 'off';
  if (!(config && config.onDuty && config.onDuty.enabled === true)) {
    return { active: false, reason: 'disabled', mode, minutes, day };
  }
  if (mode === 'off') return { active: false, reason: 'contact_off', mode, minutes, day };
  const hours = workingHours(config);
  if (!hours.days.includes(day)) return { active: false, reason: 'outside_hours', mode, minutes, day };
  const inWindow = hours.start <= hours.end
    ? minutes >= hours.start && minutes < hours.end
    : minutes >= hours.start || minutes < hours.end;
  if (!inWindow) return { active: false, reason: 'outside_hours', mode, minutes, day };
  return { active: true, reason: 'active', mode, minutes, day };
}

function sensitiveTopics(config) {
  const list = (config && config.onDuty && config.onDuty.sensitiveTopics) || [];
  return (Array.isArray(list) ? list : [])
    .map((item) => String(item == null ? '' : item).trim())
    .filter(Boolean)
    .slice(0, 50);
}

/** 命中敏感话题：不信任模型，由程序强制转成 sensitive 草稿。 */
function sensitiveHit(config, text) {
  const body = String(text == null ? '' : text);
  if (!body) return '';
  return sensitiveTopics(config).find((topic) => body.includes(topic)) || '';
}

/* ------------------------------ 可分享资产 ------------------------------ */

function normalizeAsset(raw, warnings, index) {
  if (!raw || typeof raw !== 'object') {
    warnings.push(`assets[${index}] 不是对象，已忽略`);
    return null;
  }
  const id = String(raw.id || '').trim();
  if (!ID_RE.test(id)) {
    warnings.push(`assets[${index}] 的 id 不合法，已忽略`);
    return null;
  }
  const rel = String(raw.path || '').replace(/\\/g, '/');
  if (!rel || path.isAbsolute(rel) || rel.split('/').includes('..') || rel.split('/').includes('.')) {
    warnings.push(`assets[${index}] 的路径不合法，已忽略`);
    return null;
  }
  const size = Number(raw.size);
  if (!Number.isFinite(size) || size <= 0 || size > MAX_ASSET_BYTES) {
    warnings.push(`assets[${index}] 的大小不合法，已忽略`);
    return null;
  }
  let scopes = normalizeScopes(raw.scopes);
  if (!scopes.length) {
    scopes = ['private'];
    warnings.push(`assets[${index}] 没有有效域，按 private（仅主人）保存`);
  }
  return {
    id,
    title: clip(raw.title, 60) || id,
    path: rel,
    kind: raw.kind === 'file' ? 'file' : 'image',
    mime: clip(raw.mime, 80) || 'application/octet-stream',
    size: Math.trunc(size),
    scopes,
    keywords: stringList(raw.keywords, { maxItems: 12, maxLength: 16 }),
    note: clip(raw.note, 200),
    sha256: /^[a-f0-9]{64}$/i.test(String(raw.sha256 || '')) ? String(raw.sha256).toLowerCase() : '',
    createdAt: timestamp(raw.createdAt, Date.now()),
  };
}

function loadManifestRaw(config) {
  const file = shareManifestFile(config);
  ensureJsonFile(file, { version: 1, assets: [] });
  return readCached(file, { version: 1, assets: [] });
}

function loadAssets(config) {
  const raw = loadManifestRaw(config);
  const warnings = [];
  const assets = (Array.isArray(raw && raw.assets) ? raw.assets : [])
    .map((item, index) => normalizeAsset(item, warnings, index))
    .filter(Boolean);
  return assets;
}

/** 资产文件必须落在 data/share/files/ 内，拒绝符号链接、`..` 和绝对路径。 */
function assetPath(config, asset) {
  const base = path.resolve(shareDir(config));
  const root = path.resolve(shareFilesDir(config));
  const rel = String((asset && asset.path) || '').replace(/\\/g, '/');
  if (!rel || path.isAbsolute(rel) || rel.split('/').some((part) => part === '..' || part === '.' || part === '')) {
    return '';
  }
  // 清单里的 path 是相对 data/share/ 的（files/<id>.<ext>），不能相对 files/ 再拼一次。
  const file = path.resolve(base, rel);
  if (file === root || !file.startsWith(root + path.sep)) return '';
  try {
    const realRoot = fs.realpathSync.native ? fs.realpathSync.native(root) : fs.realpathSync(root);
    const realFile = fs.realpathSync.native ? fs.realpathSync.native(file) : fs.realpathSync(file);
    if (realFile !== realRoot && !realFile.startsWith(realRoot + path.sep)) return '';
    // 逐层 lstat：目录联接 / 符号链接一律拒绝（Windows junction 有时不标记 symlink）。
    let cur = root;
    for (const part of path.relative(root, file).split(path.sep)) {
      cur = path.join(cur, part);
      if (fs.lstatSync(cur).isSymbolicLink()) return '';
    }
    const st = fs.statSync(file);
    if (!st.isFile() || st.size <= 0 || st.size > MAX_ASSET_BYTES) return '';
    return file;
  } catch {
    return '';
  }
}

function sha256File(file) {
  try {
    return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  } catch {
    return '';
  }
}

/**
 * 把回复里的 `[[asset:...]]` 解析成一个候选。标记全部剥掉，只采用第一个。
 */
function parseAssetMarks(input) {
  const text = String(input == null ? '' : input);
  const markers = [];
  const kept = [];
  let from = 0;
  const re = /\[\[\s*asset\s*[:：]\s*([^\]\r\n]{1,80}?)\s*\]\]/gi;
  let m;
  while ((m = re.exec(text))) {
    markers.push(m[1].trim());
    kept.push(text.slice(from, m.index));
    from = m.index + m[0].length;
  }
  kept.push(text.slice(from));
  return {
    text: kept.join('').trim(),
    marker: markers[0] || '',
    count: markers.length,
    markers,
  };
}

function matchAsset(assets, marker) {
  const needle = String(marker || '').trim();
  if (!needle) return [];
  const lower = needle.toLowerCase();
  const byId = assets.filter((item) => item.id === needle);
  if (byId.length) return byId;
  const exact = assets.filter((item) => item.title === needle
    || (item.keywords || []).some((word) => word === needle));
  if (exact.length) return exact;
  return assets.filter((item) => item.title.toLowerCase().includes(lower)
    || (item.keywords || []).some((word) => word.toLowerCase().includes(lower)));
}

/**
 * 解析并校验一条资产标记。
 * 权限不通过时返回 ok=false，调用方负责记「权限拒绝」并从可见文本里剥掉标记。
 */
function resolveAsset(config, marker, policy) {
  const assets = loadAssets(config);
  const hits = matchAsset(assets, marker);
  if (hits.length !== 1) {
    return { ok: false, reason: hits.length ? 'ambiguous' : 'not_found', asset: null, file: '' };
  }
  const asset = hits[0];
  if (!policy || policy.canReceiveAssets !== true || !isVisible(asset.scopes, policy)) {
    return { ok: false, reason: 'denied', asset, file: '' };
  }
  const file = assetPath(config, asset);
  if (!file) return { ok: false, reason: 'unsafe', asset, file: '' };
  if (asset.sha256) {
    const digest = sha256File(file);
    if (digest !== asset.sha256) return { ok: false, reason: 'hash_mismatch', asset, file: '' };
  }
  return { ok: true, reason: 'ok', asset, file };
}

function assetExt(source) {
  const ext = path.extname(String(source || '')).toLowerCase();
  return /^\.[a-z0-9]{1,8}$/.test(ext) ? ext : '';
}

function mimeOf(ext) {
  const map = {
    '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.gif': 'image/gif',
    '.webp': 'image/webp', '.pdf': 'application/pdf', '.txt': 'text/plain',
  };
  return map[ext] || 'application/octet-stream';
}

/**
 * 导入资产：复制进 data/share/files/，记录 sha256，不引用原始路径。
 */
function importAsset(config, input = {}) {
  const sourcePath = String(input.sourcePath || '').trim();
  if (!sourcePath) throw Object.assign(new Error('请选择要导入的文件'), { statusCode: 400 });
  let stat;
  try {
    stat = fs.lstatSync(sourcePath);
  } catch {
    throw Object.assign(new Error('找不到这个文件'), { statusCode: 400 });
  }
  if (stat.isSymbolicLink() || !stat.isFile()) {
    throw Object.assign(new Error('只能导入普通文件，不能是链接或目录'), { statusCode: 400 });
  }
  if (stat.size <= 0 || stat.size > MAX_ASSET_BYTES) {
    throw Object.assign(new Error(`单个资产不能超过 ${Math.round(MAX_ASSET_BYTES / 1024 / 1024)} MiB`), { statusCode: 400 });
  }
  const assets = loadAssets(config);
  if (assets.length >= MAX_ASSETS) {
    throw Object.assign(new Error(`资产总数不能超过 ${MAX_ASSETS} 个`), { statusCode: 429 });
  }
  const title = clip(input.title, 60) || path.basename(sourcePath, path.extname(sourcePath)).slice(0, 60);
  let scopes = normalizeScopes(input.scopes);
  if (!scopes.length) scopes = ['private'];
  const now = Date.now();
  const ext = assetExt(sourcePath);
  const id = `a-${dayStamp(now)}-${shortId()}`;
  const rel = `files/${id}${ext}`;
  const dir = shareFilesDir(config);
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${id}${ext}`);
  const buffer = fs.readFileSync(sourcePath);
  const tmp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, buffer);
  fs.renameSync(tmp, file);
  const asset = {
    id,
    title,
    path: rel,
    kind: ['.png', '.jpg', '.jpeg', '.gif', '.webp'].includes(ext) ? 'image' : 'file',
    mime: mimeOf(ext),
    size: buffer.length,
    scopes,
    keywords: stringList(input.keywords, { maxItems: 12, maxLength: 16 }),
    note: clip(input.note, 200),
    sha256: crypto.createHash('sha256').update(buffer).digest('hex'),
    createdAt: now,
  };
  const next = { version: 1, assets: [...assets, asset] };
  writeJsonAtomic(shareManifestFile(config), next);
  return asset;
}

/** 删除资产同时删文件；路径不安全时只删记录，不碰盘。 */
function removeAsset(config, id) {
  const wanted = String(id || '').trim();
  const assets = loadAssets(config);
  const asset = assets.find((item) => item.id === wanted);
  if (!asset) return { removed: false, asset: null };
  const file = assetPath(config, asset);
  if (file) {
    try { fs.unlinkSync(file); } catch { /* 文件已不在也算删除成功 */ }
  }
  writeJsonAtomic(shareManifestFile(config), {
    version: 1,
    assets: assets.filter((item) => item.id !== wanted),
  });
  return { removed: true, asset };
}

/* ------------------------------ 待确认队列 ------------------------------ */

function normalizePendingItem(raw, warnings, index) {
  if (!raw || typeof raw !== 'object') {
    warnings.push(`pending[${index}] 不是对象，已忽略`);
    return null;
  }
  const id = String(raw.id || '').trim();
  if (!/^p-[0-9a-z-]{4,80}$/i.test(id)) {
    warnings.push(`pending[${index}] 的 id 不合法，已忽略`);
    return null;
  }
  const text = clip(raw.text, 4000);
  if (!text) {
    warnings.push(`pending[${index}] 没有正文，已忽略`);
    return null;
  }
  return {
    id,
    key: clip(raw.key, 240),
    uid: clip(raw.uid, 120),
    name: clip(raw.name, 60),
    text,
    reason: raw.reason === 'sensitive' ? 'sensitive' : 'draft',
    assets: (Array.isArray(raw.assets) ? raw.assets : []).slice(0, 5).map((item) => ({
      id: clip(item && item.id, 64),
      title: clip(item && item.title, 60),
    })).filter((item) => item.id),
    at: timestamp(raw.at, Date.now()),
    status: ['pending', 'queued', 'sent', 'failed', 'uncertain', 'discarded'].includes(raw.status) ? raw.status : 'pending',
    error: clip(raw.error, 200),
    reminderId: clip(raw.reminderId, 80),
    finishedAt: timestamp(raw.finishedAt, 0),
  };
}

function loadPending(config) {
  const file = pendingFile(config);
  ensureJsonFile(file, { version: 1, items: [] });
  const raw = readCached(file, { version: 1, items: [] });
  const warnings = [];
  const items = (Array.isArray(raw && raw.items) ? raw.items : [])
    .map((item, index) => normalizePendingItem(item, warnings, index))
    .filter(Boolean);
  return { version: 1, items, warnings };
}

function savePending(config, doc) {
  const next = {
    version: 1,
    items: (Array.isArray(doc && doc.items) ? doc.items : [])
      .map((item, index) => normalizePendingItem(item, [], index))
      .filter(Boolean),
  };
  writeJsonAtomic(pendingFile(config), next);
  return next;
}

/** 超上限时先丢 discarded，再丢最旧的终态项，最后才丢未处理项。 */
function trimPending(items) {
  if (items.length <= MAX_PENDING) return items;
  const out = items.slice().sort((a, b) => a.at - b.at);
  let overflow = out.length - MAX_PENDING;
  const dropMatching = (predicate) => {
    for (let i = 0; i < out.length && overflow > 0;) {
      if (predicate(out[i])) {
        out.splice(i, 1);
        overflow -= 1;
      } else i += 1;
    }
  };
  dropMatching((item) => item.status === 'discarded');
  const terminal = new Set(['sent', 'failed', 'uncertain']);
  dropMatching((item) => terminal.has(item.status));
  if (overflow > 0) out.splice(0, overflow);
  return out;
}

function addPending(config, input = {}) {
  const doc = loadPending(config);
  const text = clip(input.text, 4000);
  if (!text) throw Object.assign(new Error('没有要待确认的内容'), { statusCode: 400 });
  const now = Date.now();
  const item = {
    id: `p-${now}-${shortId()}`,
    key: clip(input.key, 240),
    uid: clip(input.uid, 120),
    name: clip(input.name, 60),
    text,
    reason: input.reason === 'sensitive' ? 'sensitive' : 'draft',
    assets: (Array.isArray(input.assets) ? input.assets : []).slice(0, 5).map((asset) => ({
      id: clip(asset && asset.id, 64),
      title: clip(asset && asset.title, 60),
    })).filter((asset) => asset.id),
    at: now,
    status: 'pending',
    error: '',
    reminderId: '',
    finishedAt: 0,
  };
  doc.items = trimPending([...doc.items, item]);
  savePending(config, doc);
  return item;
}

function updatePending(config, id, patch = {}) {
  const doc = loadPending(config);
  const item = doc.items.find((row) => row.id === String(id || ''));
  if (!item) return null;
  Object.assign(item, patch);
  savePending(config, doc);
  return item;
}

/** 清掉已发送/已丢弃的项的正文，避免待确认文件无限保存草稿。 */
function prunePending(config) {
  const doc = loadPending(config);
  let changed = false;
  for (const item of doc.items) {
    if (item.status === 'sent' || item.status === 'discarded') {
      if (item.text !== '（已处理）') { item.text = '（已处理）'; changed = true; }
    }
  }
  if (changed) savePending(config, doc);
  return doc.items;
}

/* ------------------------------ 动态尾部 ------------------------------ */

function hitScore(query, text, weight = 1) {
  const rel = query && text ? retrieval.overlap(query, text) : 0;
  return rel * weight;
}

/** 索引用：标题 + 首句；详情只对命中度最高的两条展开。 */
function firstSentence(text) {
  const body = String(text == null ? '' : text).trim();
  if (!body) return '';
  const hit = body.match(/^.*?[。！？!?\n]/);
  return (hit ? hit[0] : body).slice(0, 80).trim();
}

function labelLine(kind, text) {
  return `${kind === 'relation' ? '[关系]' : '[共享]'} ${text}`;
}

/**
 * 生成本轮动态尾部；总预算默认 900 token。
 *
 * 顺序：
 *   1. private / 不可见项在进入候选集之前剔除（安全优先，先于预算）。
 *   2. L2（当前对象）优先于 L1；L1 再按相关度 + importance 排序。
 *   3. 预算不够时按 assets → templates → stances → knowledge 反向丢。
 */
function buildBlock(model, policy, opts = {}) {
  if (!model) return '';
  const budget = Math.max(120, Number(opts.budgetTokens) || DEFAULT_BUDGET);
  const query = String(opts.query || '');
  const assets = Array.isArray(opts.assets) ? opts.assets : [];
  const l2 = opts.l2 && typeof opts.l2 === 'object' ? opts.l2 : null;
  const overrides = (l2 && l2.overrides) || null;
  const visibleKnowledge = (model.knowledge || []).filter((item) => isVisible(item.scopes, policy));
  const visibleStances = (model.stances || []).filter((item) => isVisible(item.scopes, policy));
  const visibleTemplates = (model.templates || []).filter((item) => isVisible(item.scopes, policy));

  // L2 只来自当前对象；调用方不传就什么都不读（绝不跨对象猜）。
  const prefs = (l2 && Array.isArray(l2.preferences) ? l2.preferences : [])
    .filter((item) => item && item.text)
    .map((item) => ({ ...item, _rel: hitScore(query, item.text) }));
  const relatedL2 = prefs.filter((item) => item._rel >= 0.2)
    .sort((a, b) => b._rel - a._rel || (b.mentions || 0) - (a.mentions || 0));
  const relatedL1 = visibleKnowledge
    .map((item) => ({
      ...item,
      _score: hitScore(query, `${item.title || ''} ${firstSentence(item.text)}`, 0.7) + item.importance * 0.3,
    }))
    .filter((item) => item._score >= 0.15)
    .sort((a, b) => b._score - a._score);
  const expanded = new Set(relatedL1.slice(0, 2).map((item) => item.id));
  const longL2 = prefs.filter((item) => !relatedL2.includes(item))
    .sort((a, b) => (b.mentions || 0) - (a.mentions || 0) || (b.confidence || 0) - (a.confidence || 0));
  const restL1 = visibleKnowledge
    .filter((item) => !relatedL1.some((hit) => hit.id === item.id))
    .sort((a, b) => b.importance - a.importance || b.updatedAt - a.updatedAt);
  const stanceLines = visibleStances
    .map((item) => ({ ...item, _rel: hitScore(query, `${item.topic} ${item.position}`) }))
    .sort((a, b) => b._rel - a._rel);
  const templateLines = visibleTemplates
    .map((item) => ({ ...item, _rel: hitScore(query, `${item.title} ${item.text}`) }))
    .sort((a, b) => b._rel - a._rel);
  const assetLines = (policy && policy.canReceiveAssets === true)
    ? assets.filter((asset) => isVisible(asset.scopes, policy))
    : [];
  const promiseLines = [];
  // 未完成承诺只取当前对象的 L2 未完成话题；待确认草稿属于主人侧，不给模型看，
  // 否则下一轮它会把还没发出去的话当成已经说过了。
  for (const thread of (l2 && Array.isArray(l2.threads) ? l2.threads : [])) {
    const text = clip(thread, 120);
    if (text) promiseLines.push(labelLine('relation', text));
  }

  const infoOrder = [];
  for (const item of relatedL2) infoOrder.push(labelLine('relation', item.text));
  for (const item of relatedL1) {
    infoOrder.push(labelLine('shared', `${item.title}：${expanded.has(item.id) ? item.text : firstSentence(item.text)}`));
  }
  for (const item of longL2) infoOrder.push(labelLine('relation', item.text));
  for (const item of restL1) infoOrder.push(labelLine('shared', `${item.title}：${firstSentence(item.text)}`));

  let knowledgeLines = infoOrder.slice();
  let stanceOut = stanceLines.map((item) => labelLine('shared', `${item.topic}：${item.position}`));
  let templateOut = templateLines.map((item) => labelLine('shared', `${item.title}：${item.text}`));
  let assetOut = assetLines.map((asset) => labelLine('shared', `${asset.title}（id=${asset.id}，${asset.kind}）${asset.note ? `；${asset.note}` : ''}`));
  let promiseOut = promiseLines.slice();

  const compose = () => {
    const parts = [BOUNDARY];
    if (knowledgeLines.length) parts.push(['【授权信息】', ...knowledgeLines].join('\n'));
    if (stanceOut.length) parts.push(['【立场】', ...stanceOut].join('\n'));
    if (templateOut.length) parts.push(['【可用话术】', ...templateOut].join('\n'));
    if (assetOut.length) parts.push(['【可发送资产】', ...assetOut].join('\n'));
    if (promiseOut.length) parts.push(['【未完成承诺】', ...promiseOut].join('\n'));
    // 语气覆盖属于每个对象的 L2，写在末尾；不挤占信息项预算的大头。
    if (overrides) {
      const ovLines = voiceLines(model, overrides).slice(1).filter((line) => line.startsWith('- 对这个人'));
      if (ovLines.length) parts.push(['【对这个人】', ...ovLines].join('\n'));
    }
    return parts.join('\n\n');
  };

  let text = compose();
  // 反向丢：assets → templates → stances → knowledge。
  while (textBudget.estimateTokens(text) > budget) {
    if (assetOut.length) { assetOut = []; text = compose(); continue; }
    if (templateOut.length) { templateOut = []; text = compose(); continue; }
    if (stanceOut.length) { stanceOut = []; text = compose(); continue; }
    if (knowledgeLines.length) { knowledgeLines = knowledgeLines.slice(0, -1); text = compose(); continue; }
    if (promiseOut.length) { promiseOut = promiseOut.slice(0, -1); text = compose(); continue; }
    // 连边界声明都超预算（不可能，除非调用方给了极小的 budget）：直接硬截。
    text = Array.from(text).slice(0, Math.max(24, budget * 2)).join('');
    break;
  }
  return text;
}

/* ------------------------------ 首次访问 ------------------------------ */

function ensureDefaultFiles(config) {
  ensureJsonFile(modelFile(config), DEFAULT_MODEL);
  ensureJsonFile(shareManifestFile(config), { version: 1, assets: [] });
  ensureJsonFile(pendingFile(config), { version: 1, items: [] });
  // 资产目录也一起建出来：桥接的通用附件通道只认 data/share/files 下的绝对路径。
  try { fs.mkdirSync(shareFilesDir(config), { recursive: true }); } catch { /* 只读目录按空资产继续 */ }
  // 联系人策略归 security/access.js；重复创建是幂等的。
  require('../security/access').ensurePolicyFile(config);
  return true;
}

/* ------------------------------ 主人发言 → L1 候选 ------------------------------ */

/**
 * 只从主人自己的话里抽「以后还用得上」的事实。
 *
 * 这不是通用抽取器：只认明确的记忆指令和第一人称在做的事，
 * 避免把「我在吃饭」这种一次性状态也写进共享层。
 */
function masterFactFromText(text) {
  const body = clip(text, 400);
  if (!body) return [];
  const out = [];
  const manual = /^(?:记住|记一下|帮我记住|帮我记一下|记下|别忘了)[：:\s]*(.+)$/.exec(body);
  if (manual) {
    const value = clip(manual[1], KNOWLEDGE_TEXT_MAX);
    if (value) out.push({ title: value.slice(0, 20), text: value });
  }
  const doing = /^我(?:在|正在)(?:做|搞|负责|参与|跟)(.{2,60})/.exec(body);
  if (doing) {
    const value = clip(body, KNOWLEDGE_TEXT_MAX);
    out.push({ title: `在做${clip(doing[1], 18)}`, text: value });
  }
  return out;
}

module.exports = {
  MODEL_FILE,
  SHARE_DIR,
  PENDING_FILE,
  MAX_MODEL_BYTES,
  MAX_ASSET_BYTES,
  MAX_ASSETS,
  MAX_PENDING,
  MAX_KNOWLEDGE,
  MAX_STANCES,
  MAX_TEMPLATES,
  DEFAULT_MODEL,
  BOUNDARY,
  ON_DUTY_REASONS,
  modelFile,
  shareDir,
  shareFilesDir,
  shareManifestFile,
  pendingFile,
  readRaw,
  load,
  validate,
  save,
  rememberKnowledge,
  promoteToL1,
  identityLines,
  voiceLines,
  ruleLines,
  onDutyStatus,
  workingHours,
  sensitiveTopics,
  sensitiveHit,
  loadAssets,
  assetPath,
  parseAssetMarks,
  resolveAsset,
  importAsset,
  removeAsset,
  loadPending,
  savePending,
  addPending,
  updatePending,
  prunePending,
  buildBlock,
  ensureDefaultFiles,
  masterFactFromText,
  secretHit,
};
