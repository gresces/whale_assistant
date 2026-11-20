'use strict';

/**
 * 分身的可见性判定与联系人策略存储。
 *
 * 安全边界只有一条：**先剔除，再排序**。任何 `private`、任何未授权的域，
 * 在进入提示词候选集之前就必须消失；不靠预算裁剪兜底，也不靠模型自觉。
 *
 * 这个文件不依赖 pipeline / services / modules，pipeline 和 HTTP 路由都能直接用。
 */
const fs = require('node:fs');
const path = require('node:path');

const SCOPES = Object.freeze(['public', 'friend', 'work', 'family', 'private']);
const SCOPE_SET = new Set(SCOPES);
const ON_DUTY_MODES = Object.freeze(['off', 'draft', 'auto']);
const DISCLOSE_MODES = Object.freeze(['never', 'onAsk', 'always']);
const FILE_NAME = 'contact-policy.json';

const DEFAULT_DEFAULTS = Object.freeze({
  scopes: Object.freeze(['public']),
  denyScopes: Object.freeze([]),
  onDuty: 'off',
  canReceiveAssets: false,
  discloseAI: 'onAsk',
});

const DEFAULT_DOC = Object.freeze({
  version: 1,
  defaults: DEFAULT_DEFAULTS,
  contacts: Object.freeze({}),
});

/** 域列表：去重、只保留内置域、未知值直接丢掉。 */
function normalizeScopes(value) {
  const list = Array.isArray(value)
    ? value
    : typeof value === 'string'
      ? String(value).split(/[\s,，]+/)
      : [];
  const out = [];
  for (const raw of list) {
    const scope = String(raw == null ? '' : raw).trim().toLowerCase();
    if (!SCOPE_SET.has(scope) || out.includes(scope)) continue;
    out.push(scope);
  }
  return out;
}

function oneOf(value, list, fallback) {
  const v = String(value == null ? '' : value).trim();
  return list.includes(v) ? v : fallback;
}

function timestamp(value, fallback = 0) {
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? Math.trunc(n) : fallback;
}

function policyFile(config) {
  try {
    return path.join(config.paths.dataDir, FILE_NAME);
  } catch {
    return '';
  }
}

/** 首次访问时创建默认文件；已有文件绝不覆盖。 */
function ensurePolicyFile(config) {
  const file = policyFile(config);
  if (!file) return '';
  try {
    if (!fs.existsSync(file)) {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      const tmp = `${file}.${process.pid}.${Date.now()}.tmp`;
      fs.writeFileSync(tmp, JSON.stringify(DEFAULT_DOC, null, 2) + '\n', 'utf8');
      fs.renameSync(tmp, file);
    }
  } catch { /* 数据目录只读时按内存默认值继续跑 */ }
  return file;
}

function normalizeDefaults(raw) {
  const body = raw && typeof raw === 'object' ? raw : {};
  const scopes = normalizeScopes(body.scopes);
  return {
    // 默认策略本身也要是「可用的」：空域等于谁都看不见，不替用户瞎推断。
    scopes: scopes.length ? scopes : ['public'],
    denyScopes: normalizeScopes(body.denyScopes),
    // defaults 里的 onDuty 只对已登记对象生效；未登记对象永远 off（见 policyFor）。
    onDuty: oneOf(body.onDuty, ON_DUTY_MODES, 'off'),
    canReceiveAssets: body.canReceiveAssets === true,
    discloseAI: oneOf(body.discloseAI, DISCLOSE_MODES, 'onAsk'),
  };
}

function normalizeContact(uid, raw, now = Date.now()) {
  const body = raw && typeof raw === 'object' ? raw : {};
  const scopes = normalizeScopes(body.scopes);
  const denyScopes = normalizeScopes(body.denyScopes);
  const tabooTopics = [];
  for (const item of (Array.isArray(body.tabooTopics) ? body.tabooTopics : [])) {
    const topic = String(item == null ? '' : item).trim().slice(0, 20);
    if (topic && !tabooTopics.includes(topic)) tabooTopics.push(topic);
    if (tabooTopics.length >= 10) break;
  }
  return {
    uid: String(body.uid || uid || ''),
    name: String(body.name || '').trim().slice(0, 60),
    scopes: scopes.length ? scopes : [],
    denyScopes,
    onDuty: oneOf(body.onDuty, ON_DUTY_MODES, 'off'),
    canReceiveAssets: body.canReceiveAssets === true,
    discloseAI: oneOf(body.discloseAI, DISCLOSE_MODES, 'onAsk'),
    // L2 关系层覆盖（契约 §11.4）：只对这个对象生效，不写回 self-model.voice。
    addressAs: String(body.addressAs || '').trim().slice(0, 40),
    toneOverride: String(body.toneOverride || '').trim().slice(0, 120),
    tabooTopics,
    note: String(body.note || '').trim().slice(0, 200),
    updatedAt: timestamp(body.updatedAt, now),
  };
}

function normalizeDoc(raw) {
  const body = raw && typeof raw === 'object' ? raw : {};
  const contacts = {};
  const src = body.contacts && typeof body.contacts === 'object' && !Array.isArray(body.contacts)
    ? body.contacts
    : {};
  for (const [uid, value] of Object.entries(src)) {
    const id = String(uid || '').trim();
    if (!id || id.length > 120) continue;
    contacts[id] = normalizeContact(id, value);
  }
  return { version: 1, defaults: normalizeDefaults(body.defaults), contacts };
}

/* ------------------------------ 读缓存 ------------------------------ */

// 每个消息都读一遍策略文件太浪费；用 mtime + size 做失效，写回时手动更新缓存。
const CACHE = new Map();

function fileStamp(file) {
  try {
    const st = fs.statSync(file);
    return `${st.mtimeMs}:${st.size}`;
  } catch {
    return '';
  }
}

function loadDoc(config) {
  const file = ensurePolicyFile(config);
  if (!file) return normalizeDoc(DEFAULT_DOC);
  const stamp = fileStamp(file);
  const hit = CACHE.get(file);
  if (hit && hit.stamp === stamp) return hit.doc;
  let raw = null;
  try {
    raw = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  } catch {
    // 文件坏了也不能覆盖（data/ 是用户数据），按默认策略继续跑。
    return normalizeDoc(DEFAULT_DOC);
  }
  const doc = normalizeDoc(raw);
  CACHE.set(file, { stamp, doc });
  return doc;
}

function saveDoc(config, raw) {
  const file = ensurePolicyFile(config);
  if (!file) throw Object.assign(new Error('找不到数据目录，策略没有保存'), { statusCode: 500 });
  const doc = normalizeDoc(raw);
  const tmp = `${file}.${process.pid}.${Date.now()}.tmp`;
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(tmp, JSON.stringify(doc, null, 2) + '\n', 'utf8');
  fs.renameSync(tmp, file);
  CACHE.set(file, { stamp: fileStamp(file), doc });
  return doc;
}

/* ------------------------------ 策略视图 ------------------------------ */

/**
 * 一个对象的最终策略。
 *
 * - 未登记对象：只有 `public`，`onDuty` 强制 `off`（不允许对陌生人自动分身）。
 * - 登记对象：使用自己的域；`private` 留在 scopes 里也无意义，可见判定会再拦一次。
 */
function policyFor(doc, uid) {
  const body = doc && typeof doc === 'object' ? doc : DEFAULT_DOC;
  const defaults = body.defaults && typeof body.defaults === 'object'
    ? normalizeDefaults(body.defaults)
    : normalizeDefaults(DEFAULT_DEFAULTS);
  const key = String(uid == null ? '' : uid);
  const contact = key && body.contacts && typeof body.contacts === 'object'
    ? body.contacts[key]
    : null;
  if (!contact || typeof contact !== 'object') {
    return {
      uid: key,
      known: false,
      name: '',
      // 未登记对象即使 defaults.scopes 被放宽，也只给 public。
      scopes: ['public'],
      denyScopes: defaults.denyScopes.slice(),
      onDuty: 'off',
      canReceiveAssets: defaults.canReceiveAssets === true,
      discloseAI: defaults.discloseAI,
      addressAs: '',
      toneOverride: '',
      tabooTopics: [],
      note: '',
      updatedAt: 0,
      defaults: defaults,
    };
  }
  const normalized = normalizeContact(key, contact);
  return { ...normalized, known: true, defaults };
}

function isVisible(itemScopes, policy) {
  const scopes = normalizeScopes(itemScopes);
  // 没有可识别域的信息项按不可见处理：宁可少注入，也不默认公开。
  if (!scopes.length) return false;
  // private 永远不注入给任何对象（包括误配到 scopes 里的情况）。
  if (scopes.includes('private')) return false;
  // 主人本人始终可见全部；pipeline 不会把主人会话当作在岗对象，这里只做防御。
  if (policy && policy.owner === true) return true;
  const contact = normalizeScopes(policy && policy.scopes);
  const deny = normalizeScopes(policy && policy.denyScopes);
  if (!contact.length) return false;
  // denyScopes 是减法，先于授权判断：命中任何一个被拒域就整体不可见。
  if (scopes.some((scope) => deny.includes(scope))) return false;
  // public 的语义是「谁都能知道」，不要求对象的 scopes 里再写一遍 public。
  if (scopes.includes('public')) return true;
  return scopes.some((scope) => contact.includes(scope));
}

function visibleTo(items, policy) {
  return (Array.isArray(items) ? items : []).filter((item) => isVisible(item && item.scopes, policy));
}

/** 给启动器展示的「能看见这条资产的对象数」 */
function audienceSize(doc, itemScopes) {
  const body = normalizeDoc(doc);
  let count = 0;
  for (const uid of Object.keys(body.contacts)) {
    const policy = policyFor(body, uid);
    if (isVisible(itemScopes, policy)) count += 1;
  }
  return count;
}

module.exports = {
  SCOPES,
  DEFAULT_DOC,
  DEFAULT_DEFAULTS,
  ON_DUTY_MODES,
  DISCLOSE_MODES,
  normalizeScopes,
  normalizeDoc,
  normalizeContact,
  normalizeDefaults,
  policyFor,
  isVisible,
  visibleTo,
  audienceSize,
  policyFile,
  ensurePolicyFile,
  loadDoc,
  saveDoc,
};
