# -*- coding: utf-8 -*-
"""图片 AES 密钥扫描的超时护栏（外部兼容层，不改 vendor/wechatauto）。

现象（2026-11-15 16:15 本机实测）
    发完一张图片后 Bot 就不回话了，聊天里连「收到 私聊」都没有；桥接日志停在
    「图片缓存尚未就绪，等待重试（1/3）」，两分钟里一个字也没有，重启桥接才恢复。

根因
    微信图片是 V2 加密，密钥只能从 Weixin.exe 进程内存里反推。随包 wechatauto 的
    ``MediaDownloader._scan_aes_key(monitor=True)`` 在单次扫描没找到时会**每 2 秒
    重扫一次、最多等 120 秒**；这段代码跑在「同一会话串行」的消息 worker 里，
    于是这个会话后面的消息全部排队等它 —— 用户看到的就是「卡死」。

做法
    给 MediaDownloader 实例打一层超时护栏：
      1. 永远单次扫描（``monitor=False``），不再挂 120 秒等待；
      2. 扫描本身也放进守护线程，超过 ``timeout`` 就放弃这张图，先把会话放出来；
         ``timeout`` 读 ``image.keyScanSeconds``（默认 20 秒，夹在 5~60）；
      3. 失败后冷却 ``image.keyCooldownSeconds``（默认 60 秒），避免每条图片都重扫。
    另外把探测成功的密钥缓存到 ``data/image-key.json``：下次启动直接构造
    ``MediaDownloader(image_key=...)``；解密失败一次就丢弃缓存，回到自动探测。
    扫不到密钥时仍然按原逻辑返回 None（调用方按「这条不回话」处理），只多写一句
    能看懂的日志。

只读 vendor：替换的是**实例属性**，不改上游源码，升级 wechatauto 也不需要重打补丁。
"""

from __future__ import annotations

import json
import os
import threading
import time

import config as bridge_config

# 单次内存扫描的等待上限：实测正常机器 1~3 秒扫完。默认 20 秒是给慢机器/杀软
# 留的余量，可用 image.keyScanSeconds 覆盖（夹在 5~60 秒）。
SCAN_TIMEOUT = 20.0
# 失败后的冷却：同一个账号一分钟内不会重复扫内存（一张图失败不代表下张也不行，
# 但连续重扫只会继续卡会话，所以给用户「点开一张图再发」的恢复时间）。
RETRY_COOLDOWN = 60.0

# 探测成功后的密钥缓存（桥接自己的，不写 vendor 的 workdir/image_keys.json）。
KEY_CACHE_NAME = 'image-key.json'
KEY_CACHE_REL = ('data', KEY_CACHE_NAME)
_HEX = set('0123456789abcdefABCDEF')
# 多条图片消息会并发识别，落盘/删除都串行化，避免同一个 tmp 文件互相覆盖。
_KEY_CACHE_LOCK = threading.Lock()


def configured_limits(config=None):
    """从 image.keyScanSeconds / image.keyCooldownSeconds 读扫描上限与冷却。

    坏值回默认，绝不抛异常；配置文件缺失时静默用默认值。
    """
    if not isinstance(config, dict):
        try:
            config = bridge_config.read_config()
        except Exception:
            config = {}
    section = config.get('image') if isinstance(config, dict) else None
    if not isinstance(section, dict):
        section = {}

    def number(name, default, low, high):
        try:
            value = float(section.get(name, default))
        except (TypeError, ValueError):
            value = float(default)
        return min(float(high), max(float(low), value))

    return (number('keyScanSeconds', SCAN_TIMEOUT, 5.0, 60.0),
            number('keyCooldownSeconds', RETRY_COOLDOWN, 0.0, 3600.0))


def key_cache_path(root=None):
    base = str(root or os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    return os.path.join(base, *KEY_CACHE_REL)


def load_cached_image_key(root=None):
    """读上次探测成功的密钥；缺失/损坏/格式不对都安静返回 None。"""
    try:
        with open(key_cache_path(root), 'r', encoding='utf-8') as f:
            data = json.load(f)
    except (OSError, ValueError):
        return None
    key = data.get('key') if isinstance(data, dict) else None
    if not isinstance(key, str):
        return None
    if len(key) != 16 or any(ch not in _HEX for ch in key):
        return None
    return key


def save_cached_image_key(key, root=None, wechat_version=''):
    """写 {key, at, wechatVersion?}；权限尽量收紧，写失败也不影响识图。"""
    key = str(key or '').strip()
    if len(key) != 16 or any(ch not in _HEX for ch in key):
        return False
    path = key_cache_path(root)
    payload = {'key': key, 'at': int(time.time())}
    if wechat_version:
        payload['wechatVersion'] = str(wechat_version)
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        temp = path + '.%d.tmp' % threading.get_ident()
        with open(temp, 'w', encoding='utf-8') as f:
            json.dump(payload, f, ensure_ascii=False, separators=(',', ':'))
        try:
            os.chmod(temp, 0o600)
        except OSError:
            pass
        with _KEY_CACHE_LOCK:
            os.replace(temp, path)
        try:
            os.chmod(path, 0o600)
        except OSError:
            pass
        return True
    except OSError:
        return False


def drop_cached_image_key(root=None):
    """丢弃缓存；文件本来就不存在也返回 False，调用方据此决定是否打日志。"""
    try:
        with _KEY_CACHE_LOCK:
            os.remove(key_cache_path(root))
        return True
    except OSError:
        return False


def install_key_cache(downloader, root=None, log=None, wechat_version=''):
    """把成功拿到的密钥落到 data/image-key.json；解密失败一次就丢弃。

    挂的是实例方法（不碰 vendor 源码）：
      - ``_resolve_aes_key`` / ``detect_image_key`` 成功 → 记缓存；
      - ``decrypt_image`` 抛异常 → 清掉注入的密钥和缓存文件，下次回到自动探测。
    """
    if getattr(downloader, '_key_cache_installed', False):
        return False
    say = log or (lambda *args, **kwargs: None)
    state = {'saved': load_cached_image_key(root)}

    def remember(key):
        if isinstance(key, (tuple, list)):
            key = key[0] if key else None
        key = str(key or '').strip()
        if not key or key == state['saved']:
            return
        if save_cached_image_key(key, root=root, wechat_version=wechat_version):
            state['saved'] = key

    def forget(reason):
        dropped = drop_cached_image_key(root)
        had = bool(state['saved']) or dropped
        state['saved'] = None
        try:
            downloader._image_key = None
            downloader._img_key = None
        except Exception:
            pass
        if had:
            say('图片密钥缓存已丢弃（%s），下次回到自动探测' % reason)

    original_resolve = getattr(downloader, '_resolve_aes_key', None)
    if callable(original_resolve):
        def resolve(probe=None):
            key = original_resolve(probe)
            if key:
                remember(key)
            return key
        downloader._resolve_aes_key = resolve

    original_detect = getattr(downloader, 'detect_image_key', None)
    if callable(original_detect):
        def detect(refresh=False):
            result = original_detect(refresh=refresh)
            if result:
                remember(result)
            return result
        downloader.detect_image_key = detect

    original_decrypt = getattr(downloader, 'decrypt_image', None)
    if callable(original_decrypt):
        def decrypt(*args, **kwargs):
            try:
                return original_decrypt(*args, **kwargs)
            except Exception:
                forget('解密失败')
                raise
        downloader.decrypt_image = decrypt

    downloader._key_cache_installed = True
    downloader._whale_key_cache = bool(state['saved'])
    return True


def install_key_scan_guard(downloader, log=None, timeout=None, cooldown=None,
                           config=None):
    """给 ``MediaDownloader`` 实例装上图片密钥扫描护栏。返回是否装上。

    ``timeout`` / ``cooldown`` 不传时读 config 的 image.keyScanSeconds /
    image.keyCooldownSeconds；显式传值仍以调用方为准（测试用）。
    """
    if getattr(downloader, '_key_scan_guarded', False):
        return False
    original = getattr(downloader, '_scan_aes_key', None)
    if not callable(original):
        return False
    if timeout is None or cooldown is None:
        cfg_timeout, cfg_cooldown = configured_limits(config)
        timeout = cfg_timeout if timeout is None else timeout
        cooldown = cfg_cooldown if cooldown is None else cooldown
    say = log or (lambda *args, **kwargs: None)
    state = {'blocked_until': 0.0, 'running': False}
    guard = threading.Lock()

    def scan(monitor=False, monitor_timeout=120.0, probe=None):
        with guard:
            if state['running']:
                # 上一次扫描还挂在后台：这次直接用「没取到」，别叠加上去。
                return None
            if time.monotonic() < state['blocked_until']:
                return None
            state['running'] = True
        try:
            box = {}

            def work():
                try:
                    # 关键：强制单次扫描。monitor=True 会再等 120 秒，正是卡死的来源。
                    box['key'] = original(monitor=False, monitor_timeout=monitor_timeout, probe=probe)
                except BaseException as exc:  # noqa: BLE001 - 原样交回调用方处理
                    box['err'] = exc

            thread = threading.Thread(target=work, name='image-key-scan', daemon=True)
            thread.start()
            thread.join(max(0.1, float(timeout)))
            if thread.is_alive():
                state['blocked_until'] = time.monotonic() + max(0.0, float(cooldown))
                say('图片密钥扫描超过 %.0f 秒还没结束，这张图先跳过（%.0f 秒内不再重扫）；'
                    '在微信里点开任意一张图片后再发图，能提高取到密钥的概率'
                    % (timeout, cooldown))
                return None
            if 'err' in box:
                raise box['err']
            key = box.get('key')
            if not key:
                state['blocked_until'] = time.monotonic() + max(0.0, float(cooldown))
                say('图片密钥没能从微信进程里取到，这张图先跳过（%.0f 秒内不再重扫）；'
                    '在微信里点开任意一张图片后再发图试试' % cooldown)
            return key
        finally:
            with guard:
                state['running'] = False

    downloader._scan_aes_key = scan
    downloader._key_scan_guarded = True
    return True
