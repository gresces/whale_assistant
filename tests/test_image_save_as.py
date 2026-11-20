# -*- coding: utf-8 -*-
"""C 兜底（微信「右键 → 另存为…」）的单元测试：不碰真实微信。"""

import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import Mock

try:
    from PIL import Image
except ImportError:
    raise unittest.SkipTest('另存为兜底测试需要 Pillow')

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'bridge'))

from image_save_as import (  # noqa: E402
    SaveUnavailable, build_save_request, is_save_as_item, read_saved_image,
    save_image_as, save_image_via_ui, save_target_path, save_timeout,
)


class FakeClock:
    """真实流程里 pause 会推进时间；用假时钟让超时用例瞬间跑完。"""

    def __init__(self):
        self.now = 0.0

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.now += max(0.01, float(seconds))


class ImageSaveAsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.main, self.pid = 100, 42
        self.foreground = self.main
        self.rect = types.SimpleNamespace(left=0, top=100, right=600, bottom=300)
        self.row = types.SimpleNamespace(
            BoundingRectangle=self.rect, IsOffscreen=False,
            ClassName='mmui::ChatImageItemView', Name='图片')
        self.win = types.SimpleNamespace(
            IsWindow=lambda hwnd: hwnd == self.main,
            IsWindowVisible=lambda hwnd: hwnd == self.main,
            IsIconic=lambda hwnd: False,
            GetForegroundWindow=lambda: self.foreground,
            GetAncestor=lambda hwnd, mode: self.main,
            WindowFromPoint=Mock(return_value=self.main),
        )
        self.process = types.SimpleNamespace(
            GetWindowThreadProcessId=lambda hwnd: (1, self.pid))
        self.uia = types.SimpleNamespace(
            current_chat=Mock(return_value='测试会话'),
            _message_list=lambda: types.SimpleNamespace(BoundingRectangle=self.rect),
        )
        self.locator = types.SimpleNamespace(
            _visible_rows=lambda messages: [('image', '图片', self.row)],
            _align_index=Mock(return_value=0),
            _bubble_click_xs=lambda rect, own: [170, 430],
        )
        self.events = []
        self.inp = types.SimpleNamespace(
            send_input_click=lambda x, y, right=False: self.events.append(
                ('click', int(x), int(y), bool(right))),
            key=lambda vk, ctrl=False, shift=False: self._key(vk, ctrl),
            type_unicode=lambda text: self.events.append(('type', text)),
        )
        self.menu = Mock(return_value=types.SimpleNamespace(
            BoundingRectangle=types.SimpleNamespace(left=200, top=320, right=340, bottom=352),
            IsOffscreen=False, ClassName='mmui::XMenuView', Name='另存为...'))
        self.edit = types.SimpleNamespace(SetFocus=Mock())
        self.dialog = Mock(return_value=types.SimpleNamespace(
            filename_edit=lambda: self.edit))
        self.target = str(self.root / 'saved.jpg')
        self.on_enter = lambda: None
        self.request = {
            'hwnd': self.main, 'pid': self.pid, 'chat': '测试会话', 'target': 0,
            'sequence': [{'kind': 'image', 'content': '', 'local_id': 0}],
            'is_self': False, 'target_path': self.target, 'budget': 5,
        }
        self.clock = FakeClock()

    def _key(self, vk, ctrl):
        self.events.append(('key', vk, bool(ctrl)))
        if vk == 0x0D:
            self.on_enter()

    def save(self):
        return save_image_as(
            self.request, win=self.win, process=self.process, uia=self.uia,
            locator=self.locator, inp=self.inp, menu=self.menu, dialog=self.dialog,
            pause=self.clock.sleep, clock=self.clock,
        )

    def save_valid_jpeg(self):
        Image.new('RGB', (24, 12), 'green').save(self.target, format='JPEG')

    def test_menu_text_matches_chinese_and_english_only_for_save_as(self):
        for text in ('另存为...', '另存为…', '另存为', '另存为(&A)', 'Save as...',
                     'Save as…', 'Save As'):
            self.assertTrue(is_save_as_item(text), text)
        for text in ('复制', '另存为图片', 'Save as image', '保存', '', None):
            self.assertFalse(is_save_as_item(text), text)

    def test_alignment_failure_returns_message_not_aligned_without_touching_ui(self):
        self.locator._align_index.return_value = None
        with self.assertRaisesRegex(SaveUnavailable, 'message_not_aligned'):
            self.save()
        self.assertFalse(any(event[0] == 'click' for event in self.events))
        self.dialog.assert_not_called()
        self.assertNotIn(('key', 0x1B, False), self.events)

    def test_wrong_conversation_or_background_window_never_clicks(self):
        self.uia.current_chat.return_value = '另一个会话'
        with self.assertRaisesRegex(SaveUnavailable, 'message_not_aligned'):
            self.save()
        self.uia.current_chat.return_value = '测试会话'
        self.foreground = 999
        with self.assertRaisesRegex(SaveUnavailable, 'no_window'):
            self.save()
        self.menu.assert_not_called()

    def test_target_path_is_typed_after_select_all(self):
        self.on_enter = self.save_valid_jpeg
        data = self.save()
        self.assertTrue(data.startswith(b'\xff\xd8\xff'))
        self.assertEqual([event for event in self.events if event[0] == 'type'],
                         [('type', self.target)])
        select_all = self.events.index(('key', 0x41, True))
        typed = self.events.index(('type', self.target))
        self.assertLess(select_all, typed, '必须先 Ctrl+A，再输入完整路径')
        self.assertIn(('key', 0x0D, False), self.events)
        self.assertNotIn(('key', 0x1B, False), self.events, '成功时不该按 Esc')

    def test_success_returns_saved_image_bytes_and_clicks_save_as_item(self):
        self.on_enter = self.save_valid_jpeg
        data = self.save()
        with Image.open(io.BytesIO(data)) as image:
            self.assertEqual(image.size, (24, 12))
        clicks = [event for event in self.events if event[0] == 'click']
        self.assertTrue(clicks[0][3], '第一次是右键图片')
        self.assertFalse(clicks[-1][3], '点菜单项是左键')
        self.assertEqual((clicks[-1][1], clicks[-1][2]), (270, 336))

    def test_menu_missing_closes_with_escape_at_most_twice(self):
        self.menu.return_value = None
        with self.assertRaisesRegex(SaveUnavailable, 'menu_missing'):
            self.save()
        escapes = [event for event in self.events if event == ('key', 0x1B, False)]
        self.assertEqual(len(escapes), 2)
        self.dialog.assert_not_called()

    def test_save_timeout_presses_escape_twice_and_reports_timeout(self):
        self.on_enter = lambda: None  # 对话框关了但文件始终不出现
        with self.assertRaisesRegex(SaveUnavailable, 'timeout'):
            self.save()
        self.assertLessEqual(self.clock.now, 5.5)
        escapes = [event for event in self.events if event == ('key', 0x1B, False)]
        self.assertEqual(len(escapes), 2)

    def test_invalid_image_is_deleted_and_reported(self):
        def write_junk():
            with open(self.target, 'wb') as handle:
                handle.write(b'not an image at all')

        self.on_enter = write_junk
        with self.assertRaisesRegex(SaveUnavailable, 'invalid_image'):
            self.save()
        self.assertFalse(os.path.exists(self.target))

    def test_empty_file_is_deleted_as_invalid_instead_of_timeout(self):
        def touch_empty():
            with open(self.target, 'wb'):
                pass

        self.on_enter = touch_empty
        with self.assertRaisesRegex(SaveUnavailable, 'invalid_image'):
            self.save()
        self.assertFalse(os.path.exists(self.target))

    def test_dialog_missing_is_reported_after_menu_click(self):
        self.dialog.return_value = None
        with self.assertRaisesRegex(SaveUnavailable, 'dialog_missing'):
            self.save()
        escapes = [event for event in self.events if event == ('key', 0x1B, False)]
        self.assertEqual(len(escapes), 2)

    def test_read_saved_image_rejects_oversized_and_removes_file(self):
        path = self.root / 'huge.jpg'
        path.write_bytes(b'\xff\xd8\xff' + b'0' * 10)
        with self.assertRaisesRegex(SaveUnavailable, 'invalid_image'):
            read_saved_image(str(path))
        self.assertFalse(path.exists())

    def test_timeout_config_is_clamped(self):
        self.assertEqual(save_timeout({'image': {'uiSaveTimeoutSeconds': 1}}), 5.0)
        self.assertEqual(save_timeout({'image': {'uiSaveTimeoutSeconds': 999}}), 60.0)
        self.assertEqual(save_timeout({'image': {'uiSaveTimeoutSeconds': 'bad'}}), 15.0)
        self.assertEqual(save_timeout({}), 15.0)

    def test_target_path_has_no_chat_name_and_is_unique(self):
        msg = {'username': 'private-peer-name', 'local_id': 7, 'shard': 's1'}
        first = save_target_path(str(self.root), msg, now=1700000000.123)
        second = save_target_path(str(self.root), msg, now=1700000000.456)
        self.assertNotIn('private-peer-name', first)
        self.assertNotEqual(first, second)
        self.assertTrue(first.endswith('.jpg'))
        self.assertIn(os.path.join('data', 'media-cache'), first)

    def test_build_save_request_keeps_shard_identity(self):
        photo = {'type': '图片', 'local_id': 7, 'shard': 's1', 'sender_username': 'peer'}
        db = types.SimpleNamespace(wxid='self', get_messages=lambda *a, **k: [photo])
        request = build_save_request(
            db, {'username': 'peer', 'local_id': 7, 'shard': 's1'},
            '测试会话', self.main, self.pid, self.target)
        self.assertEqual(request['target_path'], self.target)
        self.assertEqual(request['target'], 0)
        self.assertIsNone(build_save_request(
            db, {'username': 'peer', 'local_id': 7, 'shard': 'other'},
            '测试会话', self.main, self.pid, self.target))


class SaveParentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.target = str(Path(self.temp.name) / 'cache' / 'out.jpg')

    def test_worker_failure_maps_to_reason_without_leaking_paths(self):
        log = Mock()
        last = {}
        result = types.SimpleNamespace(returncode=6, stdout=b'')
        data = save_image_via_ui(
            {'hwnd': 1, 'pid': 2, 'chat': '私密会话', 'target_path': self.target},
            log, run=lambda *a, **k: result, last=last, reveal=lambda request, log: True)
        self.assertEqual(data, b'')
        self.assertEqual(last['reason'], 'timeout')
        self.assertIn('reason=timeout', log.call_args.args[0])
        self.assertNotIn('私密会话', str(log.mock_calls))
        self.assertNotIn(self.target, str(log.mock_calls))

    def test_worker_timeout_is_reported_as_timeout(self):
        log = Mock()
        last = {}

        def run(*args, **kwargs):
            raise subprocess.TimeoutExpired(args[0], kwargs['timeout'])

        self.assertEqual(save_image_via_ui(
            {'hwnd': 1, 'pid': 2, 'chat': '会话', 'target_path': self.target},
            log, run=run, last=last, reveal=lambda request, log: True), b'')
        self.assertEqual(last['reason'], 'timeout')

    def test_success_returns_worker_bytes(self):
        raw = b'\xff\xd8\xff' + b'0' * 32
        result = types.SimpleNamespace(returncode=0, stdout=raw)
        data = save_image_via_ui(
            {'hwnd': 1, 'pid': 2, 'chat': '会话', 'target_path': self.target},
            Mock(), run=lambda *a, **k: result, reveal=lambda request, log: True)
        self.assertEqual(data, raw)
        self.assertTrue(os.path.isdir(os.path.dirname(self.target)))


if __name__ == '__main__':
    unittest.main()
