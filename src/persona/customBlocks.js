'use strict';

/**
 * 提示词画布的自定义段落：校验、稳定 id、提示词行。
 *
 * 段落正文最终是系统提示词的一部分，和 customPrompt 同级；这里只做纯逻辑，
 * 持久化仍走 persona/store.js，接口仍在 modules/persona。
 */
const crypto = require('node:crypto');
const { secretHit } = require('./selfModel');

const MAX_BLOCKS = 10;
const TITLE_MAX = 40;
const TEXT_MAX = 2000;
// 与可分享包导出的 customPrompt 上限保持一致，不再另立一套数字。
const CUSTOM_PROMPT_MAX = 24000;
const CUSTOM_ID_RE = /^custom-[0-9a-f]{8}$/;

/** 标题是单行短名，换行/制表符会破坏画布卡片和提示词标题行。 */
function cleanTitle(value) {
  return String(value == null ? '' : value).replace(/[\r\n\t]+/g, ' ').trim();
}

/** 正文保留内部换行（用户就是按段写的），只修掉首尾空白。 */
function cleanText(value) {
  return String(value == null ? '' : value).trim();
}

function validId(value) {
  return CUSTOM_ID_RE.test(String(value || ''));
}

/** 读旧配置 / 外部数据时用：能修的修，修不了的丢掉，绝不让坏段落进提示词。 */
function normalizeBlocks(raw) {
  if (!Array.isArray(raw)) return [];
  const seen = new Set();
  const out = [];
  for (const item of raw) {
    if (!item || typeof item !== 'object') continue;
    const id = String(item.id || '').trim();
    if (!validId(id) || seen.has(id)) continue;
    const title = cleanTitle(item.title).slice(0, TITLE_MAX);
    const text = cleanText(item.text).slice(0, TEXT_MAX);
    // 标题和正文都空的段落没有任何意义，UI 也不该显示它。
    if (!title && !text) continue;
    seen.add(id);
    out.push({ id, title, text });
    if (out.length >= MAX_BLOCKS) break;
  }
  return out;
}

/** 新建段落前校验；返回 { error } 或 { title, text }。 */
function validateNewBlock(input = {}) {
  const title = cleanTitle(input.title);
  const text = cleanText(input.text);
  if (!title) return { error: '段落标题不能为空（1~40 字）' };
  if (title.length > TITLE_MAX) {
    return { error: `段落标题最多 ${TITLE_MAX} 字，当前 ${title.length} 字` };
  }
  if (text.length > TEXT_MAX) {
    return { error: `段落正文最多 ${TEXT_MAX} 字，当前 ${text.length} 字` };
  }
  return { title, text };
}

/**
 * 写入前的敏感内容检测：沿用自我模型的密钥 / 凭据形状识别，
 * 命中就整次拒绝（不截断、不落盘）。
 */
function secretReason(...values) {
  for (const value of values) {
    if (secretHit(value)) return '检测到疑似密钥、验证码或身份证号，已拒绝保存';
  }
  return '';
}

function newId(existing = []) {
  const used = new Set((Array.isArray(existing) ? existing : []).map((item) => item && item.id));
  for (let i = 0; i < 50; i += 1) {
    const id = 'custom-' + crypto.randomBytes(4).toString('hex');
    if (!used.has(id)) return id;
  }
  // 理论上到不了这里（32 位空间 + 最多 10 段）；真到了也不能返回重复 id。
  throw new Error('生成自定义段落 id 失败');
}

/**
 * 提示词里的落点：每段「标题 + 正文」各占一段，空正文整段跳过。
 * 老配置 customBlocks 缺失 / 为空时返回空数组，提示词逐字节不变。
 */
function promptLines(raw) {
  const lines = [];
  for (const block of normalizeBlocks(raw)) {
    if (!block.text) continue;
    if (block.title) lines.push(`【${block.title}】`);
    lines.push(block.text);
  }
  return lines;
}

module.exports = {
  MAX_BLOCKS,
  TITLE_MAX,
  TEXT_MAX,
  CUSTOM_PROMPT_MAX,
  CUSTOM_ID_RE,
  cleanTitle,
  cleanText,
  validId,
  normalizeBlocks,
  validateNewBlock,
  secretReason,
  newId,
  promptLines,
};
