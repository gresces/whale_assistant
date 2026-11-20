# -*- coding: utf-8 -*-
"""图片多档（full/best/thumb）与界面兜底重试：失败不能变成无界重试。"""

import os
from pathlib import Path
import sys
import tempfile
import threading
import types
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
sys.path.insert(0, str(ROOT / 'bridge'))

import image_ui_fallback  # noqa: E402
from bridge_ast import load_function  # noqa: E402

KEY = '0123456789abcdef'


def nested(name, env):
    return load_function(name, env)


class DescribeImageTierTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.image = self.root / 'image.jpg'
        self.image.write_bytes(b'\xff\xd8\xff' + b'0' * 64)

    def env(self, media):
        sleeps = []
        post = Mock(return_value=types.SimpleNamespace(json=lambda: {'ok': True, 'text': '一张图'}))
        env = dict(
            os=os,
            time=types.SimpleNamespace(sleep=sleeps.append),
            bot_url='http://localhost/webhook/wechat',
            log=Mock(), log_throttled=Mock(),
            _get_media=lambda: media,
            LOCAL_HTTP=types.SimpleNamespace(post=post),
        )
        return env, post, sleeps

    def recorder(self, pick):
        calls = []

        def download(user, local_id, tier=None, shard=None):
            calls.append(tier)
            return pick(tier)

        return calls, download

    def test_without_key_only_full_and_best_are_tried(self):
        calls, download = self.recorder(lambda tier: None)
        media = types.SimpleNamespace(download_image=download)
        env, post, sleeps = self.env(media)
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}), '')
        self.assertEqual(calls, ['full', 'best'] * 4)
        self.assertNotIn('thumb', calls)
        self.assertEqual(sleeps, [0.5, 1, 2])
        post.assert_not_called()

    def test_thumb_is_tried_once_when_key_exists_and_vendor_supports_it(self):
        calls, download = self.recorder(lambda tier: None)
        media = types.SimpleNamespace(download_image=download, _image_key=KEY,
                                      IMAGE_TIERS=('original', 'mid', 'thumb'))
        env, post, sleeps = self.env(media)
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}), '')
        self.assertEqual(calls.count('thumb'), 1, '缩略图只补一次，不跟着每轮重试')
        self.assertEqual(calls, ['full', 'best', 'thumb', 'full', 'best', 'full', 'best', 'full', 'best'])
        self.assertEqual(len(calls), 9, '重试次数必须有界')
        post.assert_not_called()

    def test_thumb_success_is_used_after_full_and_best_fail(self):
        calls, download = self.recorder(lambda tier: str(self.image) if tier == 'thumb' else None)
        media = types.SimpleNamespace(download_image=download, _image_key=KEY,
                                      IMAGE_TIERS=('original', 'mid', 'thumb'))
        env, post, _sleeps = self.env(media)
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}),
                         '一张图')
        self.assertEqual(calls, ['full', 'best', 'thumb'])
        post.assert_called_once()

    def test_thumb_skipped_when_vendor_does_not_support_it(self):
        calls, download = self.recorder(lambda tier: None)
        media = types.SimpleNamespace(download_image=download, _image_key=KEY,
                                      IMAGE_TIERS=('original', 'mid'))
        env, post, _sleeps = self.env(media)
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}), '')
        self.assertEqual(calls, ['full', 'best'] * 4)
        post.assert_not_called()

    def test_missing_key_stops_retries_and_skips_thumb(self):
        def boom(user, local_id, tier=None, shard=None):
            raise RuntimeError('无法获取图片 AES 密钥：请保持微信登录')

        media = types.SimpleNamespace(download_image=boom, _image_key=KEY,
                                      IMAGE_TIERS=('original', 'mid', 'thumb'))
        env, post, sleeps = self.env(media)
        ui = Mock(return_value=b'')
        env['image_ui_fallback'] = ui
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}), '')
        self.assertEqual(sleeps, [], '密钥问题不再等缓存')
        ui.assert_called_once()
        post.assert_not_called()
        # full + best 各一次；没有 thumb，也没有 4 轮重试。
        tiers = [args[0].split(':')[3] for args, _kw in env['log_throttled'].call_args_list
                 if str(args[0]).startswith('img-dl:')]
        self.assertEqual(tiers, ['full', 'best'])

    def test_repeated_failures_do_not_explode_into_unbounded_retries(self):
        calls, download = self.recorder(lambda tier: (_ for _ in ()).throw(OSError('disk')))
        media = types.SimpleNamespace(download_image=download, _image_key=KEY,
                                      IMAGE_TIERS=('original', 'mid', 'thumb'))
        env, post, sleeps = self.env(media)
        ui = Mock(return_value=b'')
        env['image_ui_fallback'] = ui
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}), '')
        self.assertEqual(len(calls), 9)
        self.assertEqual(sleeps, [0.5, 1, 2])
        ui.assert_called_once()
        post.assert_not_called()

    def test_save_as_disabled_skips_c_and_keeps_capture_fallback(self):
        media = types.SimpleNamespace(download_image=Mock(return_value=None))
        env, post, _sleeps = self.env(media)
        env['CURRENT_CONFIG'] = {'image': {'uiSaveFallback': False}}
        saver = Mock(return_value=b'\xff\xd8\xffsaved')
        capture = Mock(return_value=b'\xff\xd8\xffshot')
        env['image_save_fallback'] = saver
        env['image_ui_fallback'] = capture
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}),
                         '一张图')
        saver.assert_not_called()
        capture.assert_called_once()
        post.assert_called_once()

    def test_save_as_success_skips_capture_fallback(self):
        media = types.SimpleNamespace(download_image=Mock(return_value=None))
        env, post, _sleeps = self.env(media)
        env['image_save_fallback'] = Mock(return_value=b'\xff\xd8\xffsaved')
        env['image_ui_fallback'] = Mock(return_value=b'\xff\xd8\xffshot')
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}),
                         '一张图')
        env['image_save_fallback'].assert_called_once()
        env['image_ui_fallback'].assert_not_called()
        post.assert_called_once()

    def test_save_as_failure_still_uses_capture_fallback_once(self):
        media = types.SimpleNamespace(download_image=Mock(return_value=None))
        env, post, _sleeps = self.env(media)
        saver = Mock(return_value=b'')
        capture = Mock(return_value=b'\xff\xd8\xffshot')
        env['image_save_fallback'] = saver
        env['image_ui_fallback'] = capture
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}),
                         '一张图')
        saver.assert_called_once()
        capture.assert_called_once()
        post.assert_called_once()

    def test_save_as_runs_at_most_once_even_on_wxgf_failure(self):
        broken = self.root / 'broken.dat'
        broken.write_bytes(b'wxgf' + b'0' * 32)
        media = types.SimpleNamespace(download_image=Mock(return_value=str(broken)),
                                      _wxgf_to_jpg=lambda raw: None)
        env, post, _sleeps = self.env(media)
        saver = Mock(return_value=b'')
        capture = Mock(return_value=b'\xff\xd8\xffshot')
        env['image_save_fallback'] = saver
        env['image_ui_fallback'] = capture
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}),
                         '一张图')
        saver.assert_called_once()
        capture.assert_called_once()
        post.assert_called_once()

    def test_save_as_exception_is_throttled_and_capture_still_runs(self):
        media = types.SimpleNamespace(download_image=Mock(return_value=None))
        env, post, _sleeps = self.env(media)
        env['image_save_fallback'] = Mock(side_effect=RuntimeError('boom'))
        capture = Mock(return_value=b'\xff\xd8\xffshot')
        env['image_ui_fallback'] = capture
        self.assertEqual(nested('describe_image', env)({'username': 'peer', 'local_id': 4}),
                         '一张图')
        capture.assert_called_once()
        logged = [str(arg) for args, _kwargs in env['log_throttled'].call_args_list
                  for arg in args]
        self.assertTrue(any('另存为兜底失败 reason=RuntimeError' in text for text in logged))


class PreviewFailureReasonTests(unittest.TestCase):
    def test_read_image_reports_alignment_reason_for_retry(self):
        last = {}
        result = types.SimpleNamespace(returncode=4, stdout=b'')
        image_ui_fallback.read_image_via_ui({'hwnd': 1}, lambda *a: None,
                                            run=lambda *a, **k: result, last=last)
        self.assertEqual(last.get('reason'), 'message_not_aligned')

    def test_read_image_timeout_is_reported(self):
        import subprocess

        last = {}

        def run(*args, **kwargs):
            raise subprocess.TimeoutExpired(args[0], 1)

        image_ui_fallback.read_image_via_ui({'hwnd': 1}, lambda *a: None,
                                            run=run, last=last)
        self.assertEqual(last.get('reason'), 'timeout')


class ImageUiFallbackRetryTests(unittest.TestCase):
    """message_not_aligned / 前台被抢：拉前台 + 滚到底，只重试一次。"""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)

    def env(self, foreground=None):
        send_lock, gui_lock = threading.Lock(), threading.Lock()
        fg = {'hwnd': 100 if foreground is None else foreground}
        win = types.SimpleNamespace(IsWindow=lambda hwnd: True,
                                    GetForegroundWindow=lambda: fg['hwnd'])
        process = types.SimpleNamespace(GetWindowThreadProcessId=lambda hwnd: (1, 42))
        db = types.SimpleNamespace(account='fixture', wxid='self',
                                   get_messages=lambda *a, **k: [
                                       {'type': '图片', 'local_id': 4, 'shard': 's',
                                        'content': ''},
                                   ])
        env = {
            'ACCOUNT': {'verified': True, 'hwnd': 100, 'pid': 42},
            'db': db,
            'display_name': lambda username: '测试会话',
            'configured_account_dir': lambda: 'fixture',
            'send_lock': send_lock,
            'gui_lock': gui_lock,
            'log': Mock(),
        }
        return env, win, process, fg

    def capture(self, env, read, reveal=None, foreground=None):
        env_data, win, process, fg = self.env(foreground=foreground)
        env_data.update(env)

        def do_reveal(request, log):
            return reveal(request, log, fg) if reveal else True

        with patch.dict(sys.modules, {'win32gui': win, 'win32process': process}), \
                patch('image_ui_fallback.read_image_via_ui', side_effect=read) as reader, \
                patch('image_ui_fallback.reveal_target', side_effect=do_reveal) as revealer:
            result = nested('image_ui_fallback', env_data)({'username': 'peer', 'local_id': 4,
                                                            'shard': 's'})
        return result, reader, revealer, fg

    def test_alignment_failure_reveals_and_retries_once(self):
        calls = []
        image = b'\xff\xd8\xffok'

        def read(request, log, timeout=12, run=None, last=None):
            calls.append(1)
            if len(calls) == 1:
                if last is not None:
                    last['reason'] = 'message_not_aligned'
                return b''
            return image

        result, reader, revealer, _fg = self.capture({}, read)
        self.assertEqual(result, image)
        self.assertEqual(reader.call_count, 2)
        revealer.assert_called_once()

    def test_alignment_failure_retries_only_once(self):
        def read(request, log, timeout=12, run=None, last=None):
            if last is not None:
                last['reason'] = 'account_or_foreground_changed'
            return b''

        result, reader, revealer, _fg = self.capture({}, read)
        self.assertEqual(result, b'')
        self.assertEqual(reader.call_count, 2, '只补一次，不循环')
        revealer.assert_called_once()

    def test_background_window_is_revealed_before_capture(self):
        image = b'\xff\xd8\xffok'

        def reveal(request, log, fg):
            fg['hwnd'] = request['hwnd']
            return True

        def read(request, log, timeout=12, run=None, last=None):
            return image

        result, reader, revealer, _fg = self.capture({}, read, reveal=reveal,
                                                     foreground=0)
        self.assertEqual(result, image)
        self.assertEqual(reader.call_count, 1)
        revealer.assert_called_once()


if __name__ == '__main__':
    unittest.main()
