'use strict';

/**
 * 提示词画布的段落映射与只读预览。
 *
 * blocks 只写给编辑区定位；system / tail 一律由 prompt.buildPromptParts 用
 * 合成消息算出来，接口不自己拼提示词。这里不做任何 IO，GET 也没有副作用。
 */
const { DEFAULT_CONFIG } = require('../infra/config');
const { estimateBudgetTokens } = require('../memory/tokens');
const { buildPromptParts } = require('./prompt');
const customBlocks = require('./customBlocks');

const P = DEFAULT_CONFIG.persona;

/** 百分比按整数展示：0.06 → 6% */
function percent(value) {
  const n = Number(value);
  if (!Number.isFinite(n) || n <= 0) return 0;
  return Math.min(100, Math.round(n * 100));
}

/** 说话方式的一句话人话，跟着当前选择的私聊 / 群聊走。 */
function speechDerived(config, chatType) {
  const chat = (config && config.chat) || {};
  const isGroup = chatType === 'group';
  const max = Math.max(1, Number(isGroup ? chat.groupMaxChars : chat.privateMaxChars) || (isGroup ? 70 : 100));
  const bubbles = Math.max(1, Number(chat.maxBubbles) || 2);
  const emoji = percent(chat.emojiChance);
  return `${isGroup ? '群聊' : '私聊'}最多 ${max} 字 · 最多 ${bubbles} 条 · 颜文字 ${emoji}%`;
}

function textBlock(id, label, text, defaultText, maxLength) {
  return {
    id,
    label,
    kind: 'text',
    editable: true,
    text: String(text == null ? '' : text),
    defaultText: String(defaultText == null ? '' : defaultText),
    maxLength,
  };
}

/** 固定顺序：identity → personality → background → speech → behavior → custom → custom-* → memory */
function buildBlockList(config, { chatType = 'private' } = {}) {
  const persona = (config && config.persona) || {};
  const chat = (config && config.chat) || {};
  const blocks = [
    {
      id: 'identity',
      label: '身份',
      kind: 'form',
      fields: ['name', 'age', 'job', 'appearance', 'ownerName'],
      value: {
        name: persona.name || '',
        age: persona.age,
        job: persona.job || '',
        appearance: persona.appearance || '',
        ownerName: persona.ownerName || '',
      },
    },
    textBlock('personality', '性格', persona.personality, P.personality, 500),
    textBlock('background', '经历与背景', persona.background, P.background, 500),
    {
      id: 'speech',
      label: '说话方式',
      kind: 'chips',
      fields: ['privateMaxChars', 'groupMaxChars', 'maxBubbles', 'emojiChance'],
      value: {
        privateMaxChars: Number(chat.privateMaxChars) || 100,
        groupMaxChars: Number(chat.groupMaxChars) || 70,
        maxBubbles: Number(chat.maxBubbles) || 2,
        emojiChance: Number(chat.emojiChance) || 0,
      },
      derived: speechDerived(config, chatType),
    },
    {
      id: 'behavior',
      label: '回复行为',
      kind: 'toggle',
      fields: ['masterRoast', 'proactive', 'opinions', 'newBehavior'],
      value: {
        masterRoast: Boolean(persona.masterRoast),
        proactive: persona.proactive !== false,
        opinions: persona.opinions !== false,
        newBehavior: persona.newBehavior !== false,
      },
    },
    textBlock('custom', '补充要求', persona.customPrompt, '', customBlocks.CUSTOM_PROMPT_MAX),
  ];

  for (const block of customBlocks.normalizeBlocks(persona.customBlocks)) {
    blocks.push({
      ...textBlock(block.id, block.title, block.text, '', customBlocks.TEXT_MAX),
      custom: true,
      title: block.title,
    });
  }

  blocks.push({ id: 'memory', label: '身份关系与记忆', kind: 'link' });
  return blocks;
}

/** 群聊 / 私聊合成消息：无表情包、无联网、无事件，和真实聊天共用同一个拼装函数。 */
function syntheticMessage(chatType, isMaster) {
  const isGroup = chatType === 'group';
  return {
    chatType: isGroup ? 'group' : 'private',
    isMaster: isMaster !== false,
    senderName: isMaster !== false ? '主人' : (isGroup ? '群友' : '朋友'),
    chatTitle: isGroup ? '一个微信群' : '',
  };
}

/**
 * @returns {{chatType:string,isMaster:boolean,blocks:Array,system:string,tail:string,tokens:number,tokenSource:string}}
 */
function buildPreview(config, options = {}) {
  const chatType = options.chatType === 'group' ? 'group' : 'private';
  const isMaster = options.isMaster !== false;
  const msg = syntheticMessage(chatType, isMaster);
  const parts = buildPromptParts({ config, msg });
  return {
    chatType,
    isMaster,
    blocks: buildBlockList(config, { chatType }),
    system: parts.system,
    tail: parts.tail,
    // 与 tokenCheck 的兜底口径同一个估算函数，不另造一套算法。
    tokens: estimateBudgetTokens(parts.system),
    tokenSource: 'estimate',
  };
}

module.exports = {
  buildBlockList,
  buildPreview,
  syntheticMessage,
  speechDerived,
};
