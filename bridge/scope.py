# -*- coding: utf-8 -*-
"""发言范围：在桥接接收端先挡掉不在范围的会话/发送者。

内核 ``src/core/wechatSendPolicy.js`` 只在生成完回复后拦一次。桥接如果照常
下载图片、跑视觉识别、再发 webhook 被内核拒掉，既浪费电也把日志刷成一串
「识别失败」。这里把同一份规则提前到接收端：命中就直接跳过，不碰媒体。

配置来源固定是 ``G.CONFIG_PATH``（``bridge.config.CONFIG_PATH``）。桥接每条
消息都会过这里，所以按文件 mtime 缓存；配置缺失或损坏时 **fail-open**（照常
处理），不能因为一个坏 JSON 让整个桥接停摆。
"""

from __future__ import annotations

import json
import os
import re
import threading
import unicodedata

import config as bridge_config

# 规则命中后的原因码；日志里换成中文短语，原因码留在节流 key 里方便排查。
REASONS = {
    'muted_group': '黑名单（群聊禁言）',
    'muted_chat': '黑名单（会话禁言）',
    'not_whitelisted': '不在白名单',
    'ignored_sender': '忽略发送者',
}

_CACHE = {'path': None, 'stamp': None, 'config': {}}
_LOCK = threading.Lock()


def reason_label(reason):
    """原因码 → 日志里的中文短语；未知原因原样返回。"""
    return REASONS.get(reason, str(reason or ''))


def default_config_path():
    """和 engine 的 G.CONFIG_PATH 是同一个值。"""
    return str(getattr(bridge_config, 'CONFIG_PATH', '') or '')


def _stat_stamp(path):
    try:
        st = os.stat(path)
    except OSError:
        return None
    # mtime + 大小：既满足「按 mtime 缓存」，也能挡住同秒内被替换的文件。
    return (st.st_mtime_ns, st.st_size)


def load_config(path=None):
    """读 config.json；同一路径同一 mtime 只读一次盘，坏文件按空配置处理。"""
    target = str(path if path is not None else default_config_path())
    stamp = _stat_stamp(target) if target else None
    with _LOCK:
        if _CACHE['path'] == target and _CACHE['stamp'] == stamp:
            return _CACHE['config']
    cfg = {}
    if stamp is not None:
        try:
            with open(target, 'r', encoding='utf-8-sig') as f:
                raw = json.load(f)
            if isinstance(raw, dict):
                cfg = raw
        except Exception:
            # fail-open：配置坏了只是「没有黑名单」，不能让桥接整个停摆。
            cfg = {}
    with _LOCK:
        _CACHE['path'] = target
        _CACHE['stamp'] = stamp
        _CACHE['config'] = cfg
    return cfg


def _section(config, name):
    value = (config or {}).get(name) if isinstance(config, dict) else None
    return value if isinstance(value, dict) else {}


def _entries(value):
    """名单字段：数组或老配置里的「换行/逗号/空白分隔字符串」都认。"""
    if isinstance(value, str):
        # 与内核 configuredWhitelist 一致：换行 / 逗号 / 分号 / 空白都算分隔符。
        items = re.split(r'[\n,;，；\s]+', value)
    elif isinstance(value, (list, tuple, set)):
        items = list(value)
    else:
        items = []
    return [str(item or '').strip() for item in items if str(item or '').strip()]


def _norm_name(value):
    """去空白、emoji、标点后比较；emoji 属于 S*，ZWJ 属于 C*。"""
    text = unicodedata.normalize('NFKC', str(value or '')).casefold()
    return ''.join(
        ch for ch in text
        if not ch.isspace() and unicodedata.category(ch)[0] not in ('P', 'S', 'Z', 'C', 'M')
    )


def name_matches(entry, name):
    """与内核 ``text.nameMatches`` 等价：归一化后相等，群名允许少个「群」字。"""
    left = _norm_name(entry)
    right = _norm_name(name)
    if not left or not right:
        return False
    if left == right:
        return True

    # 内核还兼容「那个群 / 这个群」；归一化不会删汉字，这里补上。
    def trim_group_full(text):
        for suffix in ('那个群', '这个群', '群'):
            if text.endswith(suffix):
                return text[:-len(suffix)]
        return text

    target = trim_group_full(left)
    return bool(target) and target == trim_group_full(right)


def _id_key(value):
    return str(value or '').strip().casefold()


def evaluate(config=None, *, username='', display_name='', is_group=False,
             sender_username='', sender_name='', config_path=None):
    """返回 ``None`` 表示可处理，否则返回 muted_group / muted_chat /
    not_whitelisted / ignored_sender。

    规则与 ``src/core/wechatSendPolicy.js`` 对齐：
    1. ``group.mutedGroups``：``*`` 命中全部；与会话 id 不区分大小写相等；
       群聊还与会话显示名做名称匹配（兼容只写了群名、没写 id 的旧配置）。
    2. ``wechat.whitelistOnly === true``：``care.whitelist`` 里没有这个会话 id
       就拒绝；空白名单 = 全部拒绝。
    3. ``wechat.ignoreSenders``：发送者 id 或显示名命中就拒绝，私聊群聊都算。
    """
    cfg = load_config(config_path) if config is None else config
    if not isinstance(cfg, dict):
        cfg = {}
    is_group = bool(is_group)
    key = _id_key(username)

    for entry in _entries(_section(cfg, 'group').get('mutedGroups')):
        if entry == '*':
            return 'muted_group' if is_group else 'muted_chat'
        if key and key == _id_key(entry):
            return 'muted_group' if is_group else 'muted_chat'
        if is_group and display_name and name_matches(entry, display_name):
            return 'muted_group'

    wechat = _section(cfg, 'wechat')
    if wechat.get('whitelistOnly') is True:
        allowed = {_id_key(entry) for entry in _entries(_section(cfg, 'care').get('whitelist'))}
        if not key or key not in allowed:
            return 'not_whitelisted'

    ignored = {_id_key(entry) for entry in _entries(wechat.get('ignoreSenders'))}
    ignored.discard('')
    senders = {_id_key(sender_username), _id_key(sender_name)}
    senders.discard('')
    if ignored and senders & ignored:
        return 'ignored_sender'
    return None


def message_denial(msg, *, display_name='', sender_name='', is_group=None,
                   config=None, config_path=None):
    """消息字典入口：桥接接收端用这个，避免每处都手写字段提取。"""
    msg = msg if isinstance(msg, dict) else {}
    username = str(msg.get('username') or '')
    if is_group is None:
        is_group = username.endswith('@chatroom')
    return evaluate(
        config,
        username=username,
        display_name=display_name,
        is_group=is_group,
        sender_username=msg.get('sender_username') or '',
        sender_name=sender_name or msg.get('sender_name') or '',
        config_path=config_path,
    )
