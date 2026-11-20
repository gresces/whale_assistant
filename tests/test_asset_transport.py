"""Shareable attachment contract (pdf/docx/xlsx/txt/image). Mock-only.

Run: python -B tests/test_asset_transport.py

Never imports WeChatGUI, UIA, win32gui or a live HTTP client. The bridge
mixin methods are AST-extracted (same helper as the sticker tests) so the
receipt and no-retry contract is exercised without a WeChat window.
"""
import os
from pathlib import Path
import sys
import tempfile
import threading
import unittest
import zipfile
from types import SimpleNamespace
from unittest.mock import Mock, patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
sys.path.insert(0, str(ROOT / 'bridge'))
import sticker_transport as transport
from bridge_ast import init, load_function


def bridge_functions(names, environment):
    """Load mixin methods into a fake env; mirrors tests/test_sticker_transport.py."""
    init(environment)
    for name in names:
        load_function(name, environment)
    return environment

PDF = b'%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<<>>\n%%EOF\n'


def write_docx(path):
    with zipfile.ZipFile(path, 'w') as archive:
        archive.writestr('[Content_Types].xml', '<Types/>')
        archive.writestr('word/document.xml', '<w:document/>')


def write_xlsx(path):
    with zipfile.ZipFile(path, 'w') as archive:
        archive.writestr('[Content_Types].xml', '<Types/>')
        archive.writestr('xl/workbook.xml', '<workbook/>')


class FakeDB:
    account = 'test-account'
    wxid = 'self-user'

    def __init__(self):
        self.new = []
        self.queries = []

    def get_messages(self, username, limit):
        self.queries.append(username)
        return [{'sort_seq': 10, 'type': '文件', 'sender_username': 'self-user'}]

    def get_new_messages(self, username, since_seq, limit):
        self.queries.append(username)
        return self.new


class FakeDesktop:
    def __init__(self):
        self.in_foreground = True
        self.has_focus = True
        self.focus = Mock()
        self.input_value = ''
        self.clipboard_valid = True

    def foreground(self, hwnd):
        return self.in_foreground and hwnd == 100

    def focused(self, control, hwnd):
        return self.has_focus

    def value(self, control):
        return self.input_value

    def clipboard_matches(self, path):
        return self.clipboard_valid


class PathSafetyTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = os.path.join(self.temp.name, 'data', 'share', 'files')
        os.makedirs(self.root)

    def make(self, name, data):
        path = os.path.join(self.root, name)
        Path(path).write_bytes(data)
        return path

    def assert_rejected(self, path, fragment, **kwargs):
        with self.assertRaises(transport.AttachmentError) as caught:
            transport.validate_attachment(path, self.root, **kwargs)
        self.assertIn(fragment, str(caught.exception))
        self.assertNotIn(self.temp.name, str(caught.exception), '错误信息不能回显绝对路径')
        return str(caught.exception)

    def test_relative_path_and_parent_reference_rejected(self):
        path = self.make('note.txt', 'hello'.encode('utf-8'))
        self.assert_rejected('note.txt', '绝对路径')
        self.assert_rejected(os.path.join(self.root, 'sub', '..', 'note.txt'), '上级目录')
        self.assert_rejected(os.path.join(self.root, '..', 'note.txt'), '上级目录')
        self.assertTrue(os.path.isabs(path))

    def test_outside_root_and_root_itself_rejected(self):
        outside = os.path.join(self.temp.name, 'outside.txt')
        Path(outside).write_bytes(b'outside')
        self.assert_rejected(outside, 'data/share/files')
        self.assert_rejected(self.root, 'data/share/files')

    def test_missing_and_non_regular_targets_rejected(self):
        self.assert_rejected(os.path.join(self.root, 'missing.txt'), '不存在')
        directory = os.path.join(self.root, 'folder.txt')
        os.makedirs(directory)
        self.assert_rejected(directory, '普通文件')

    def test_symlink_or_reparse_point_rejected(self):
        outside = os.path.join(self.temp.name, 'outside.txt')
        Path(outside).write_bytes(b'outside')
        link = os.path.join(self.root, 'link.txt')
        try:
            os.symlink(outside, link)
        except (OSError, NotImplementedError, AttributeError):
            link = ''
        if link:
            with self.assertRaises(transport.AttachmentError) as caught:
                transport.validate_attachment(link, self.root)
            self.assertTrue('链接' in str(caught.exception) or 'data/share/files' in str(caught.exception))
        # Windows 上无需管理员也能建目录 junction；目标留在 root 内，
        # 专门验证「真实路径没逃逸但链路上有重解析点」也必须拒绝。
        real = os.path.join(self.root, 'real')
        os.makedirs(real)
        Path(os.path.join(real, 'note.txt')).write_bytes(b'inside')
        junction = os.path.join(self.root, 'junction')
        created = False
        try:
            import subprocess
            result = subprocess.run(['cmd', '/c', 'mklink', '/J', junction, real],
                                    capture_output=True, text=True)
            created = result.returncode == 0 and os.path.isdir(junction)
        except Exception:
            created = False
        if not created:
            if not link:
                self.skipTest('本机不能创建符号链接或 junction')
            return
        self.addCleanup(lambda: os.path.isdir(junction) and os.rmdir(junction))
        with self.assertRaises(transport.AttachmentError) as caught:
            transport.validate_attachment(os.path.join(junction, 'note.txt'), self.root)
        self.assertIn('链接', str(caught.exception))

    def test_empty_file_rejected(self):
        self.assert_rejected(self.make('empty.txt', b''), '空文件')


class SizeAndTypeTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = os.path.join(self.temp.name, 'files')
        os.makedirs(self.root)

    def make(self, name, data):
        path = os.path.join(self.root, name)
        Path(path).write_bytes(data)
        return path

    def test_default_limit_is_twenty_mib(self):
        self.assertEqual(transport.FILE_MAX_BYTES, 20 * 1024 * 1024)

    def test_size_limit_boundary_accepts_equal_and_rejects_over(self):
        allowed = self.make('ok.pdf', PDF)
        self.assertEqual(transport.validate_attachment(allowed, self.root, max_bytes=len(PDF))[0],
                         os.path.realpath(allowed))
        with self.assertRaises(transport.AttachmentError) as caught:
            transport.validate_attachment(allowed, self.root, max_bytes=len(PDF) - 1)
        self.assertIn('MiB', str(caught.exception))

    def test_default_limit_rejects_real_oversize_file(self):
        big = os.path.join(self.root, 'big.pdf')
        with open(big, 'wb') as stream:
            stream.write(PDF)
            stream.seek(transport.FILE_MAX_BYTES)
            stream.write(b'X')
        with self.assertRaises(transport.AttachmentError) as caught:
            transport.validate_attachment(big, self.root)
        self.assertIn('20 MiB', str(caught.exception))

    def test_pdf_magic_must_match_extension(self):
        self.assertEqual(transport.validate_attachment(self.make('ok.pdf', PDF), self.root)[2], 'PDF')
        with self.assertRaises(transport.AttachmentError):
            transport.validate_attachment(self.make('fake.pdf', b'not a pdf'), self.root)

    def test_docx_and_xlsx_need_matching_ooxml_parts(self):
        docx = os.path.join(self.root, 'report.docx')
        write_docx(docx)
        self.assertEqual(transport.validate_attachment(docx, self.root)[2], 'DOCX')
        xlsx = os.path.join(self.root, 'table.xlsx')
        write_xlsx(xlsx)
        self.assertEqual(transport.validate_attachment(xlsx, self.root)[2], 'XLSX')
        wrong = os.path.join(self.root, 'wrong.xlsx')
        write_docx(wrong)
        with self.assertRaises(transport.AttachmentError):
            transport.validate_attachment(wrong, self.root)
        broken = self.make('broken.docx', b'PK\x03\x04broken')
        with self.assertRaises(transport.AttachmentError):
            transport.validate_attachment(broken, self.root)

    def test_text_accepts_utf8_and_gbk_but_not_binary(self):
        self.assertEqual(transport.validate_attachment(self.make('utf8.txt', '你好'.encode('utf-8')), self.root)[2], 'TXT')
        self.assertEqual(transport.validate_attachment(self.make('gbk.txt', '你好'.encode('gbk')), self.root)[2], 'TXT')
        with self.assertRaises(transport.AttachmentError) as caught:
            transport.validate_attachment(self.make('binary.txt', b'\x00\x01\x02'), self.root)
        self.assertIn('二进制', str(caught.exception))

    def test_unsupported_extension_rejected(self):
        path = self.make('run.exe', b'MZ')
        with self.assertRaises(transport.AttachmentError) as caught:
            transport.validate_attachment(path, self.root)
        self.assertIn('不支持', str(caught.exception))

    def test_image_uses_the_existing_decoder(self):
        path = self.make('photo.png', b'fake-image')
        with patch.object(transport, '_verify_image', side_effect=transport.StickerImageError('图片内容与扩展名不匹配。')):
            with self.assertRaises(transport.StickerImageError):
                transport.validate_attachment(path, self.root)
        with patch.object(transport, '_verify_image', return_value=None):
            self.assertEqual(transport.validate_attachment(path, self.root)[2], 'IMAGE/PNG')


class TransportResultTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = os.path.join(self.temp.name, 'files')
        os.makedirs(self.root)
        self.path = os.path.join(self.root, 'note.txt')
        self.content = '季度报表说明\n'.encode('utf-8')
        Path(self.path).write_bytes(self.content)
        self.db = FakeDB()
        self.desktop = FakeDesktop()
        self.control = SimpleNamespace(ControlTypeName='EditControl', AutomationId='root.chat_input_field', Name='Peer')
        self.uia = SimpleNamespace(hwnd=100, _win=SimpleNamespace(NativeWindowHandle=100),
                                   current_chat=lambda: 'Peer', _chat_input=lambda: self.control)
        self.keys = []
        self.on_paste = lambda: None
        self.on_enter = lambda: None
        self.allow = True

        def key(code, **kwargs):
            self.keys.append(code)
            if code == 0x56:
                self.desktop.input_value = '\ufffc'
                self.on_paste()
            if code == 0x0D:
                self.on_enter()

        self.gui = SimpleNamespace(main_hwnd=100, _input=SimpleNamespace(key=key),
                                   copy_files_to_clipboard=Mock(return_value=True))

    def send(self, **overrides):
        arguments = dict(gui=self.gui, uia=self.uia, hwnd=100, aliases={'Peer'}, path=self.path,
                         root=self.root, db=self.db, username='peer', gate=lambda: self.allow,
                         account_valid=lambda: True, activity_allowed=lambda: True,
                         desktop=self.desktop, kind='file', sleep=lambda delay: None,
                         confirm_timeout=0)
        arguments.update(overrides)
        return transport.guarded_send(**arguments)

    def confirm_with(self, content):
        self.on_enter = lambda: setattr(self.db, 'new', [
            {'sort_seq': 11, 'type': '文件', 'content': content, 'sender_username': 'self-user'}])

    def test_confirmed_by_exact_file_name_or_md5(self):
        marker = transport._file_marker(self.path)
        for content in ('<msg><appmsg><title>note.txt</title></appmsg></msg>',
                        '<msg><appmsg><md5>%s</md5></appmsg></msg>' % marker['md5']):
            with self.subTest(content=content):
                self.db.new = []
                self.desktop.input_value = ''
                self.keys = []
                self.confirm_with(content)
                self.assertEqual(self.send(), {'status': 'confirmed', 'error': ''})
                self.assertEqual(self.keys, [0x56, 0x0D])

    def test_unconfirmed_never_presses_enter_and_never_retries(self):
        outcome = self.send()
        self.assertEqual(outcome['status'], 'unconfirmed')
        self.assertTrue(outcome['draft_may_remain'])
        self.assertEqual(self.keys, [0x56, 0x0D])
        self.assertEqual(self.keys.count(0x56), 1)
        self.assertEqual(self.keys.count(0x0D), 1)
        self.gui.copy_files_to_clipboard.assert_called_once()
        self.assertEqual(self.gui.copy_files_to_clipboard.call_args.args[0],
                         [os.path.realpath(self.path)])

    def test_no_exact_attachment_draft_never_presses_enter(self):
        self.on_paste = lambda: setattr(self.desktop, 'input_value', '')
        outcome = self.send()
        self.assertEqual(outcome['status'], 'unconfirmed')
        self.assertEqual(self.keys, [0x56])

    def test_invalid_file_fails_before_clipboard_or_keyboard(self):
        bad = os.path.join(self.temp.name, 'outside.txt')
        Path(bad).write_bytes(b'outside')
        outcome = self.send(path=bad)
        self.assertEqual(outcome['status'], 'failed')
        self.assertIn('data/share/files', outcome['error'])
        self.assertNotIn(self.temp.name, outcome['error'])
        self.gui.copy_files_to_clipboard.assert_not_called()
        self.assertEqual(self.keys, [])

    def test_gate_off_cancels_without_touching_the_file(self):
        self.allow = False
        with patch.object(transport, 'validate_attachment', side_effect=AssertionError('file touched while off')):
            self.assertEqual(self.send()['status'], 'cancelled')
        self.gui.copy_files_to_clipboard.assert_not_called()
        self.assertEqual(self.keys, [])

    def test_image_kind_default_keeps_the_sticker_root_contract(self):
        # 缺省 kind 仍然是图片：文本文件不能借 send_attachment 绕过图片校验。
        outcome = self.send(kind='image', root=self.root)
        self.assertEqual(outcome['status'], 'failed')
        self.assertEqual(self.keys, [])

    def test_confirmation_still_requires_an_outgoing_file_message(self):
        self.on_enter = lambda: setattr(self.db, 'new', [
            {'sort_seq': 11, 'type': '文字', 'content': 'note.txt', 'sender_username': 'self-user'}])
        self.assertEqual(self.send()['status'], 'unconfirmed')


class BridgeJobCompatTest(unittest.TestCase):
    """Old jobs (no kind/title) must keep calling send_sticker only."""

    def setUp(self):
        self.cfg = {'features': {'stickers': True}, 'wechat': {}}
        self.http = SimpleNamespace(get=Mock(), post=Mock())
        self.env = dict(STICKER_TEST_ON=True, ACCOUNT_WANTED='', CONFIG_PATH='unused-fake-config',
                        read_enabled_config=lambda path: self.cfg,
                        LOCAL_HTTP=self.http, sticker_base='http://fake.invalid',
                        sticker_headers={'x-bot-token': 'test-only'},
                        sticker_result=transport.result,
                        log=Mock(), log_throttled=Mock(),
                        time=SimpleNamespace(time=lambda: 1, sleep=lambda delay: None))
        bridge_functions({'sticker_config_now', 'sticker_gate', 'claim_and_send_sticker'}, self.env)
        self.item = {'id': 'job-1', 'generation': 'g1', 'path': 'fake.pdf', 'chat': 'Peer',
                     'chatUsername': 'peer', 'status': 'sending'}
        self.http.get.return_value = SimpleNamespace(status_code=200, json=lambda: {
            'ok': True, 'enabled': True, 'generation': 'g1',
            'jobs': [{'id': 'job-1', 'status': 'sending', 'generation': 'g1'}]})
        self.http.post.return_value = SimpleNamespace(status_code=200, json=lambda: {'ok': True, 'item': self.item})
        self.env['send_sticker'] = Mock(return_value=transport.result('confirmed'))
        self.env['send_attachment'] = Mock(return_value=transport.result('confirmed'))

    def test_missing_kind_and_title_still_uses_send_sticker(self):
        self.env['claim_and_send_sticker'](self.item)
        self.env['send_sticker'].assert_called_once_with('fake.pdf', 'Peer', 'peer', 'g1', 'job-1')
        self.env['send_attachment'].assert_not_called()
        ack = [call for call in self.http.post.call_args_list if call.args[0].endswith('/ack')][0]
        self.assertEqual(set(ack.kwargs['json']), {'id', 'status', 'error'})

    def test_file_job_forwards_kind_and_title_to_send_attachment(self):
        self.item.update(kind='file', title='季度报表')
        self.env['claim_and_send_sticker'](self.item)
        self.env['send_sticker'].assert_not_called()
        self.env['send_attachment'].assert_called_once()
        args = self.env['send_attachment'].call_args.args
        self.assertEqual(args[0], 'fake.pdf')
        self.assertEqual(args[1], 'Peer')
        self.assertEqual(args[2], 'peer')
        self.assertEqual(args[3]['kind'], 'file')
        self.assertEqual(args[3]['title'], '季度报表')
        self.assertEqual(args[3]['generation'], 'g1')
        self.assertEqual(args[3]['job_id'], 'job-1')

    def test_claimed_kind_mismatch_sends_nothing(self):
        self.item.update(kind='file')
        self.http.post.return_value = SimpleNamespace(
            status_code=200, json=lambda: {'ok': True, 'item': dict(self.item, kind='image')})
        self.env['claim_and_send_sticker'](self.item)
        self.env['send_sticker'].assert_not_called()
        self.env['send_attachment'].assert_not_called()


class BridgeAttachmentLogTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = os.path.join(self.temp.name, 'data', 'share', 'files')
        os.makedirs(self.root)
        self.path = os.path.join(self.root, 'note.txt')
        Path(self.path).write_bytes('日志里不要出现正文'.encode('utf-8'))
        self.http = SimpleNamespace(get=Mock(), post=Mock())
        self.http.get.return_value = SimpleNamespace(status_code=200, json=lambda: {
            'ok': True, 'enabled': True, 'generation': 'g1',
            'jobs': [{'id': 'job-1', 'status': 'sending', 'generation': 'g1'}]})
        self.log = Mock()
        self.env = dict(STICKER_TEST_ON=True, CONFIG_PATH='unused-fake-config',
                        ACCOUNT_WANTED='',
                        read_enabled_config=lambda path: {'features': {'stickers': True}, 'wechat': {}},
                        LOCAL_HTTP=self.http, sticker_base='http://fake.invalid',
                        sticker_headers={'x-bot-token': 'test-only'},
                        sticker_result=transport.result, log=self.log,
                        log_throttled=Mock(), safe_id=lambda job: '#' + str(job),
                        ROOT=self.temp.name, os=os,
                        send_lock=threading.Lock(), gui_lock=threading.Lock(),
                        db=FakeDB(),
                        sticker_activity_allowed=Mock(return_value=True),
                        sticker_quarantine_allows=Mock(return_value=True),
                        send_policy_allows=Mock(return_value=True), sticker_draft_quarantine={},
                        sticker_account_evidence_allowed=Mock(return_value=True),
                        account_send_allowed=Mock(return_value=True),
                        ACCOUNT={'verified': True, 'hwnd': 100},
                        wechat_window_alive=Mock(return_value=True),
                        WindowsDesktop=Mock(return_value=FakeDesktop()),
                        gui=SimpleNamespace(main_hwnd=100, use_window=Mock(return_value=True),
                                            _uia=SimpleNamespace(current_chat=lambda: '文件传输助手',
                                                                 open_chat=Mock(return_value=True))),
                        resolve_sticker_target=Mock(return_value=('filehelper', {'文件传输助手'})),
                        sticker_alias_match=transport.alias_match,
                        snapshot_win_state=Mock(return_value={'hwnd': 100}),
                        restore_win_state=Mock(),
                        guarded_sticker_send=Mock(return_value=transport.result('confirmed')),
                        _compat_mod=lambda: SimpleNamespace(pinned_window=lambda: 100),
                        SEND_STATE={'active': False, 'start': 0},
                        time=SimpleNamespace(time=lambda: 1, sleep=lambda delay: None))
        bridge_functions({'sticker_config_now', 'sticker_gate', 'send_sticker',
                          'send_attachment', '_attachment_root', '_attachment_note'}, self.env)

    def run_file(self):
        return self.env['send_attachment'](
            self.path, '文件传输助手', 'filehelper',
            {'kind': 'file', 'title': '季度报表', 'generation': 'g1', 'job_id': 'job-1'})

    def test_log_records_only_id_size_type_and_result(self):
        self.assertEqual(self.run_file()['status'], 'confirmed')
        self.assertEqual(self.env['guarded_sticker_send'].call_args.kwargs['kind'], 'file')
        text = ' '.join(' '.join(str(part) for part in call.args) for call in self.log.call_args_list)
        self.assertIn('#job-1', text)
        self.assertIn('kind=file', text)
        self.assertIn('type=TXT', text)
        self.assertIn('result=confirmed', text)
        self.assertIn('size=%d' % os.path.getsize(self.path), text)
        self.assertNotIn(self.temp.name, text)
        self.assertNotIn('note.txt', text)
        self.assertNotIn('日志里不要出现正文', text)

    def test_validation_failure_is_readable_and_path_free(self):
        outside = os.path.join(self.temp.name, 'outside.txt')
        Path(outside).write_bytes(b'outside')
        outcome = self.env['send_attachment'](
            outside, '文件传输助手', 'filehelper',
            {'kind': 'file', 'generation': 'g1', 'job_id': 'job-1'})
        self.assertEqual(outcome['status'], 'failed')
        self.assertIn('data/share/files', outcome['error'])
        self.assertNotIn(self.temp.name, outcome['error'])
        text = ' '.join(' '.join(str(part) for part in call.args) for call in self.log.call_args_list)
        self.assertIn('result=failed', text)
        self.assertNotIn(self.temp.name, text)
        self.env['guarded_sticker_send'].assert_not_called()

    def test_missing_kind_defaults_to_image_semantics(self):
        self.env['send_attachment'](
            self.path, '文件传输助手', 'filehelper',
            {'generation': 'g1', 'job_id': 'job-1'})
        call = self.env['guarded_sticker_send'].call_args
        self.assertEqual(call.kwargs['kind'], 'image')
        self.assertEqual(call.args[5], os.path.join(self.temp.name, 'data/stickers'))

    def test_old_sticker_job_keeps_image_kind_and_receipt(self):
        self.assertEqual(self.env['send_sticker'](self.path, '文件传输助手', 'filehelper', 'g1', 'job-1')['status'],
                         'confirmed')
        self.env['guarded_sticker_send'].assert_called_once()
        self.assertEqual(self.env['guarded_sticker_send'].call_args.kwargs['kind'], 'image')


if __name__ == '__main__':
    unittest.main(verbosity=2)
