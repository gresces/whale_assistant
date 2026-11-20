'use strict';

/**
 * 权限域可见性矩阵（契约 §1 / §11.3）。
 *
 * 这里锁死的是安全边界：private 对任何对象都不可见、denyScopes 是减法、
 * 未登记对象只拿到 public、群聊按群 ID 判定。全部用占位 id，不出现真实 wxid / 群号。
 */
const test = require('node:test');
const assert = require('node:assert/strict');

const access = require('../src/security/access');

const doc = access.normalizeDoc({
  defaults: { scopes: ['public'], denyScopes: [], onDuty: 'off' },
  contacts: {
    'u-work': { uid: 'u-work', name: '同事', scopes: ['work'], onDuty: 'auto' },
    'u-work-deny': { uid: 'u-work-deny', scopes: ['work', 'friend'], denyScopes: ['friend'] },
    'u-all': { uid: 'u-all', scopes: ['public', 'friend', 'work', 'family'] },
    'group-1@chatroom': { uid: 'group-1@chatroom', scopes: ['work'], onDuty: 'draft' },
  },
});

const policy = (uid) => access.policyFor(doc, uid);

test('normalizeScopes：去重、忽略未知域和非字符串', () => {
  assert.deepEqual(access.normalizeScopes(['work', 'work', 'private', 'nope', 1, '']), ['work', 'private']);
  assert.deepEqual(access.normalizeScopes('work, friend work'), ['work', 'friend']);
  assert.deepEqual(access.normalizeScopes(null), []);
});

test('未登记对象：只有 public，onDuty 强制 off', () => {
  const p = policy('u-unknown');
  assert.equal(p.known, false);
  assert.deepEqual(p.scopes, ['public']);
  assert.equal(p.onDuty, 'off');
  assert.equal(p.canReceiveAssets, false);
});

test('public 对所有已登记对象可见（denyScopes 除外）', () => {
  assert.equal(access.isVisible(['public'], policy('u-work')), true);
  assert.equal(access.isVisible(['public'], policy('u-all')), true);
  assert.equal(access.isVisible(['public'], policy('u-unknown')), true);
  const denied = access.policyFor(access.normalizeDoc({
    contacts: { 'u-deny': { uid: 'u-deny', scopes: ['work'], denyScopes: ['public'] } },
  }), 'u-deny');
  assert.equal(access.isVisible(['public'], denied), false);
});

test('private 永远不可见，即使对象 scopes 里误配了 private', () => {
  const ownerish = access.policyFor(access.normalizeDoc({
    contacts: { 'u-ownerish': { uid: 'u-ownerish', scopes: ['private', 'work'] } },
  }), 'u-ownerish');
  assert.equal(access.isVisible(['private'], ownerish), false);
  assert.equal(access.isVisible(['private', 'work'], ownerish), false);
  assert.equal(access.isVisible(['private'], policy('u-all')), false);
});

test('域交集判定 + 多域条目', () => {
  assert.equal(access.isVisible(['work'], policy('u-work')), true);
  assert.equal(access.isVisible(['family'], policy('u-work')), false);
  assert.equal(access.isVisible(['family', 'work'], policy('u-work')), true);
  assert.equal(access.isVisible(['friend'], policy('u-work')), false);
  assert.equal(access.isVisible(['friend'], policy('u-work-deny')), false, 'denyScopes 是减法');
  assert.equal(access.isVisible(['work'], policy('u-work-deny')), true);
});

test('没有可识别域的信息项按不可见处理', () => {
  assert.equal(access.isVisible([], policy('u-all')), false);
  assert.equal(access.isVisible(['unknown-scope'], policy('u-all')), false);
  assert.equal(access.isVisible(null, policy('u-all')), false);
});

test('群聊按群 ID 判定，不按群成员身份提权', () => {
  const group = policy('group-1@chatroom');
  assert.equal(group.known, true);
  assert.deepEqual(group.scopes, ['work']);
  assert.equal(access.isVisible(['work'], group), true);
  assert.equal(access.isVisible(['family'], group), false);
  // 群成员 id 在策略里不存在 → 仍是未登记对象
  assert.equal(policy('member-of-group').known, false);
});

test('visibleTo 先剔除再返回，不改变原数组', () => {
  const items = [
    { id: 'a', scopes: ['work'] },
    { id: 'b', scopes: ['private'] },
    { id: 'c', scopes: ['public'] },
    { id: 'd', scopes: ['family'] },
  ];
  const out = access.visibleTo(items, policy('u-work'));
  assert.deepEqual(out.map((x) => x.id), ['a', 'c']);
  assert.equal(items.length, 4);
});

test('audienceSize 只统计真正能看见资产的对象', () => {
  assert.equal(access.audienceSize(doc, ['work']), 4);
  assert.equal(access.audienceSize(doc, ['private']), 0);
});
