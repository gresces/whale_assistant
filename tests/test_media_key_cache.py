# -*- coding: utf-8 -*-
"""图片密钥：扫描上限可配 + data/image-key.json 缓存（命中/失效/丢弃）。"""

import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import types
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
sys.path.insert(0, str(ROOT / 'bridge'))

import media_key_compat as mk  # noqa: E402
from bridge_ast import load_function  # noqa: E402

KEY = '0123456789abcdef'


class ConfiguredLimitsTests(unittest.TestCase):
    def test_defaults(self):
        self.assertEqual(mk.configured_limits({}), (20.0, 60.0))

    def test_config_overrides(self):
        self.assertEqual(
            mk.configured_limits({'image': {'keyScanSeconds': 12, 'keyCooldownSeconds': 30}}),
            (12.0, 30.0))

    def test_values_are_clamped_and_bad_values_fall_back(self):
        self.assertEqual(mk.configured_limits({'image': {'keyScanSeconds': 1}})[0], 5.0)
        self.assertEqual(mk.configured_limits({'image': {'keyScanSeconds': 999}})[0], 60.0)
        self.assertEqual(mk.configured_limits({'image': {'keyScanSeconds': 'oops'}})[0], 20.0)
        self.assertEqual(mk.configured_limits({'image': {'keyCooldownSeconds': -5}})[1], 0.0)

    def test_scan_guard_uses_configured_timeout(self):
        calls = []

        def slow_scan(monitor=False, monitor_timeout=120.0, probe=None):
            calls.append(monitor)
            time.sleep(0.5)
            return 'late-key'

        downloader = types.SimpleNamespace(_scan_aes_key=slow_scan)
        with patch.object(mk, 'configured_limits', return_value=(0.1, 0.0)) as limits:
            self.assertTrue(mk.install_key_scan_guard(
                downloader, log=lambda *a: None,
                config={'image': {'keyScanSeconds': 20}}))
            started = time.monotonic()
            self.assertIsNone(downloader._scan_aes_key(monitor=True))
            self.assertLess(time.monotonic() - started, 0.4)
        limits.assert_called_once()
        self.assertEqual(calls, [False], '仍然强制单次扫描，不挂 monitor')


class KeyCacheFileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def read_payload(self):
        return json.loads((self.root / 'data' / 'image-key.json').read_text(encoding='utf-8'))

    def test_missing_or_corrupt_cache_is_ignored(self):
        self.assertIsNone(mk.load_cached_image_key(str(self.root)))
        target = self.root / 'data'
        target.mkdir()
        (target / 'image-key.json').write_text('{broken', encoding='utf-8')
        self.assertIsNone(mk.load_cached_image_key(str(self.root)))

    def test_invalid_key_shape_is_ignored(self):
        for key in ('', 'abc', 'z' * 16, 123):
            with self.subTest(key=key):
                self.assertFalse(mk.save_cached_image_key(key, root=str(self.root)))
        (self.root / 'data').mkdir(exist_ok=True)
        (self.root / 'data' / 'image-key.json').write_text(
            json.dumps({'key': 'xyz'}), encoding='utf-8')
        self.assertIsNone(mk.load_cached_image_key(str(self.root)))

    def test_roundtrip_keeps_only_expected_fields(self):
        self.assertTrue(mk.save_cached_image_key(KEY, root=str(self.root),
                                                 wechat_version='4.1.2.0'))
        payload = self.read_payload()
        self.assertEqual(payload['key'], KEY)
        self.assertIsInstance(payload['at'], int)
        self.assertEqual(payload['wechatVersion'], '4.1.2.0')
        self.assertEqual(set(payload), {'key', 'at', 'wechatVersion'})
        self.assertEqual(mk.load_cached_image_key(str(self.root)), KEY)

        mk.save_cached_image_key(KEY, root=str(self.root))
        self.assertEqual(set(self.read_payload()), {'key', 'at'})
        self.assertEqual(list((self.root / 'data').glob('*.tmp')), [])

    def test_drop_removes_file_and_missing_is_false(self):
        self.assertFalse(mk.drop_cached_image_key(str(self.root)))
        mk.save_cached_image_key(KEY, root=str(self.root))
        self.assertTrue(mk.drop_cached_image_key(str(self.root)))
        self.assertIsNone(mk.load_cached_image_key(str(self.root)))


class InstallKeyCacheTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def downloader(self, key=None):
        dl = types.SimpleNamespace()

        def resolve(probe=None):
            return key

        def detect(refresh=False):
            return (key, 0x88) if key else None

        def decrypt(*args, **kwargs):
            return (key or '').encode()

        dl._image_key = None
        dl._resolve_aes_key = resolve
        dl.detect_image_key = detect
        dl.decrypt_image = decrypt
        return dl

    def test_resolved_key_is_persisted_once(self):
        dl = self.downloader(KEY)
        self.assertTrue(mk.install_key_cache(dl, root=str(self.root)))
        self.assertEqual(dl._resolve_aes_key(), KEY)
        self.assertEqual(mk.load_cached_image_key(str(self.root)), KEY)

        with patch.object(mk, 'save_cached_image_key', wraps=mk.save_cached_image_key) as save:
            self.assertEqual(dl._resolve_aes_key(), KEY)
            save.assert_not_called()   # 同一把密钥不反复写盘

    def test_detect_image_key_result_is_persisted(self):
        dl = self.downloader(KEY)
        mk.install_key_cache(dl, root=str(self.root))
        self.assertEqual(dl.detect_image_key(), (KEY, 0x88))
        self.assertEqual(mk.load_cached_image_key(str(self.root)), KEY)

    def test_decrypt_failure_drops_cache_and_injected_key(self):
        mk.save_cached_image_key(KEY, root=str(self.root))
        logs = []

        def boom(*args, **kwargs):
            raise RuntimeError('解密失败')

        dl = types.SimpleNamespace(_image_key=KEY, decrypt_image=boom,
                                   _resolve_aes_key=lambda probe=None: KEY)
        self.assertTrue(mk.install_key_cache(dl, root=str(self.root),
                                             log=lambda *a: logs.append(a)))
        with self.assertRaises(RuntimeError):
            dl.decrypt_image('x.dat')
        self.assertIsNone(dl._image_key, '失败后回到自动探测')
        self.assertIsNone(mk.load_cached_image_key(str(self.root)))
        self.assertTrue(any('丢弃' in str(item) for item in logs))

    def test_decrypt_failure_without_cache_stays_quiet(self):
        logs = []
        dl = types.SimpleNamespace(_image_key=None,
                                   decrypt_image=lambda *a, **k: (_ for _ in ()).throw(ValueError('x')))
        mk.install_key_cache(dl, root=str(self.root), log=lambda *a: logs.append(a))
        with self.assertRaises(ValueError):
            dl.decrypt_image('x.dat')
        self.assertEqual(logs, [])

    def test_reinstall_is_idempotent(self):
        dl = self.downloader(KEY)
        self.assertTrue(mk.install_key_cache(dl, root=str(self.root)))
        self.assertFalse(mk.install_key_cache(dl, root=str(self.root)))

    def test_cached_key_can_be_injected_at_construction(self):
        # 桥接启动时先尝试 data/image-key.json；这里只验证取值路径是安静的。
        mk.save_cached_image_key(KEY, root=str(self.root))
        self.assertEqual(mk.load_cached_image_key(str(self.root)), KEY)
        self.assertEqual(mk.key_cache_path(str(self.root)),
                         str(self.root / 'data' / 'image-key.json'))

    def test_get_media_passes_cached_key_to_downloader(self):
        mk.save_cached_image_key(KEY, root=str(self.root))
        created = {}

        class FakeMedia:
            def __init__(self, db, **kwargs):
                created.update(kwargs)
                self._image_key = kwargs.get('image_key')
                self._scan_aes_key = lambda *a, **k: None
                self.decrypt_image = lambda *a, **k: b''

        env = dict(
            os=os, ROOT=str(self.root), log=Mock(), CURRENT_CONFIG={}, DB_ROOT_NAMES=[],
            MediaDownloader=FakeMedia,
            db=types.SimpleNamespace(version='', account_dir=str(self.root)),
            _media_box={'downloader': None}, _media_lock=threading.Lock(),
        )
        downloader = load_function('_get_media', env)()
        self.assertEqual(created.get('image_key'), KEY)
        self.assertTrue(downloader._whale_key_cache)
        self.assertTrue(downloader._key_cache_installed)
        self.assertTrue(downloader._key_scan_guarded, '扫描护栏要一起装上')


if __name__ == '__main__':
    unittest.main()
