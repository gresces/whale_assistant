# -*- coding: utf-8 -*-
"""发言范围边界 + 接收端顺序：黑名单/白名单必须在媒体处理之前生效。"""

import json
import os
from pathlib import Path
import sys
import tempfile
import types
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))
sys.path.insert(0, str(ROOT / 'bridge'))

import scope  # noqa: E402
from bridge_ast import load_function  # noqa: E402


def group_config(**sections):
    base = {'group': {}, 'wechat': {}, 'care': {}}
    for key, value in sections.items():
        base[key] = value
    return base


class ScopeRuleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def config_file(self, config):
        path = self.root / 'config.json'
        path.write_text(json.dumps(config, ensure_ascii=False), encoding='utf-8')
        return str(path)

    def evaluate(self, config, **fields):
        return scope.evaluate(config, config_path='', **fields)

    def test_star_mutes_every_chat_and_keeps_group_reason(self):
        cfg = group_config(group={'mutedGroups': ['*']})
        self.assertEqual(self.evaluate(cfg, username='wxid_a'), 'muted_chat')
        self.assertEqual(self.evaluate(cfg, username='123@chatroom', is_group=True),
                         'muted_group')

    def test_muted_group_id_match_is_case_insensitive_and_trimmed(self):
        cfg = group_config(group={'mutedGroups': ['  ABC123@chatroom ']})
        self.assertEqual(self.evaluate(cfg, username='abc123@chatroom', is_group=True),
                         'muted_group')
        self.assertIsNone(self.evaluate(cfg, username='other@chatroom', is_group=True,
                                        display_name='别的群'))

    def test_muted_group_name_match_tolerates_punctuation_emoji_and_group_suffix(self):
        cfg = group_config(group={'mutedGroups': ['🎉 测试群！']})
        for name in ('测试', '测试群', '测试那个群', '🎉测试！'):
            with self.subTest(name=name):
                self.assertEqual(self.evaluate(cfg, username='123@chatroom', is_group=True,
                                               display_name=name), 'muted_group')

    def test_muted_name_does_not_apply_to_private_chat(self):
        # 内核只在群聊里做名称匹配：私聊的显示名撞上群名不能误伤。
        cfg = group_config(group={'mutedGroups': ['张三']})
        self.assertIsNone(self.evaluate(cfg, username='wxid_zhang', display_name='张三'))

    def test_empty_whitelist_denies_everything(self):
        cfg = group_config(wechat={'whitelistOnly': True}, care={'whitelist': []})
        self.assertEqual(self.evaluate(cfg, username='wxid_a'), 'not_whitelisted')

    def test_whitelist_accepts_ids_only_case_insensitively(self):
        cfg = group_config(wechat={'whitelistOnly': True},
                           care={'whitelist': ['WXID_A', ' 123@chatroom ']})
        self.assertIsNone(self.evaluate(cfg, username='wxid_a'))
        self.assertIsNone(self.evaluate(cfg, username='123@chatroom', is_group=True))
        self.assertEqual(self.evaluate(cfg, username='wxid_b'), 'not_whitelisted')
        # 白名单只认会话 id，不认显示名。
        self.assertEqual(self.evaluate(cfg, username='wxid_c', display_name='WXID_A'),
                         'not_whitelisted')

    def test_whitelist_accepts_legacy_string_form(self):
        cfg = group_config(wechat={'whitelistOnly': True}, care={'whitelist': 'a, B;c'})
        for username in ('a', 'b', 'c'):
            with self.subTest(username=username):
                self.assertIsNone(self.evaluate(cfg, username=username))

    def test_ignore_senders_matches_id_or_display_name(self):
        cfg = group_config(wechat={'ignoreSenders': ['微信团队', 'wxid_bot']})
        self.assertEqual(self.evaluate(cfg, username='peer', sender_name='微信团队'),
                         'ignored_sender')
        self.assertEqual(self.evaluate(cfg, username='room@chatroom', is_group=True,
                                       sender_username='WXID_BOT'), 'ignored_sender')
        self.assertIsNone(self.evaluate(cfg, username='peer', sender_name='张三',
                                        sender_username='wxid_zhang'))

    def test_ignore_senders_not_triggered_by_empty_sender(self):
        cfg = group_config(wechat={'ignoreSenders': ['']})
        self.assertIsNone(self.evaluate(cfg, username='peer'))

    def test_missing_config_fails_open(self):
        missing = str(self.root / 'nope.json')
        self.assertEqual(scope.load_config(missing), {})
        self.assertIsNone(scope.evaluate(None, config_path=missing, username='peer'))

    def test_corrupt_config_fails_open(self):
        path = self.root / 'config.json'
        path.write_text('{not json', encoding='utf-8')
        self.assertEqual(scope.load_config(str(path)), {})
        self.assertIsNone(scope.evaluate(None, config_path=str(path), username='peer'))

    def test_mtime_cache_reads_disk_once_then_refreshes(self):
        path = self.config_file(group_config(group={'mutedGroups': ['old']}))
        with patch('json.load', wraps=json.load) as loader:
            first = scope.load_config(path)
            second = scope.load_config(path)
        self.assertIs(first, second)
        loader.assert_called_once()

        path_obj = Path(path)
        stat = path_obj.stat()
        path_obj.write_text(json.dumps(group_config(group={'mutedGroups': ['new']})),
                            encoding='utf-8')
        os.utime(path, (stat.st_atime, stat.st_mtime + 10))
        third = scope.load_config(path)
        self.assertEqual(third['group']['mutedGroups'], ['new'])


class ScopeReceiverOrderTests(unittest.TestCase):
    """接收端必须在 describe_image/emoji/voice 之前命中发言范围。"""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def env(self, config, **stubs):
        path = self.root / 'config.json'
        path.write_text(json.dumps(config, ensure_ascii=False), encoding='utf-8')
        clock = types.SimpleNamespace(now=1_700_000_000.0)
        clock.time = lambda: clock.now
        env = dict(
            me='self', time=clock, _THROTTLE_LOG={}, log=Mock(), log_error=Mock(),
            display_name=lambda chat: '测试群' if chat.endswith('@chatroom') else '好友',
            db=types.SimpleNamespace(get_nickname=lambda sender: '张三'),
            LOCAL_HTTP=types.SimpleNamespace(post=Mock()),
            token='', bot_url='http://localhost/webhook/wechat',
            CONN_STATE={'ok': True}, ONLY_CHATS=[], CONFIG_PATH=str(path),
            describe_image=Mock(return_value=''), describe_emoji=Mock(return_value=''),
            transcribe_voice=Mock(return_value=''),
            # 查重表是模块级全局状态，跨测试会互相污染；这里只测发言范围，固定放行。
            duplicate_guard=lambda *args, **kwargs: False,
        )
        env.update(stubs)
        env['log_throttled'] = self.helper('log_throttled', env)
        env['fix_garbled'] = self.helper('fix_garbled', env)
        env['on_message_inner'] = self.helper('on_message_inner', env)
        return env

    @staticmethod
    def helper(name, env):
        return load_function(name, env)

    @staticmethod
    def message(**changes):
        msg = dict(username='peer', sender_username='friend', type='图片', content='',
                   local_id=7, shard='shard1', create_time=200.)
        msg.update(changes)
        return msg

    def logs(self, env):
        return ' '.join(str(call.args) for call in env['log'].call_args_list)

    def test_muted_chat_skips_media_and_webhook(self):
        env = self.env(group_config(group={'mutedGroups': ['peer']}))
        env['on_message_inner'](self.message(username='peer'), None)
        env['describe_image'].assert_not_called()
        env['describe_emoji'].assert_not_called()
        env['transcribe_voice'].assert_not_called()
        env['LOCAL_HTTP'].post.assert_not_called()
        line = self.logs(env)
        self.assertIn('消息跳过', line)
        self.assertIn('黑名单', line)
        self.assertIn('会话=#', line)

    def test_muted_group_skips_by_display_name_before_media(self):
        env = self.env(group_config(group={'mutedGroups': ['测试']}))
        env['on_message_inner'](self.message(username='123@chatroom'), None)
        env['describe_image'].assert_not_called()
        env['LOCAL_HTTP'].post.assert_not_called()

    def test_not_whitelisted_skips_emoji_before_decode(self):
        env = self.env(group_config(wechat={'whitelistOnly': True},
                                    care={'whitelist': ['someone-else']}))
        env['on_message_inner'](self.message(type='表情', content='<emoji md5="x"/>'), None)
        env['describe_emoji'].assert_not_called()
        env['LOCAL_HTTP'].post.assert_not_called()
        self.assertIn('不在白名单', self.logs(env))

    def test_ignored_sender_skips_voice_before_transcribe(self):
        env = self.env(group_config(wechat={'ignoreSenders': ['张三']}))
        env['on_message_inner'](self.message(type='语音'), None)
        env['transcribe_voice'].assert_not_called()
        env['LOCAL_HTTP'].post.assert_not_called()
        self.assertIn('忽略发送者', self.logs(env))

    def test_empty_config_still_reaches_media_normal_path(self):
        env = self.env(group_config())
        env['on_message_inner'](self.message(username='peer'), None)
        env['describe_image'].assert_called_once()
        # 取不到图会走占位文案并交给内核，说明没有被误拦。
        self.assertEqual(env['LOCAL_HTTP'].post.call_count, 1, self.logs(env))


if __name__ == '__main__':
    unittest.main()
