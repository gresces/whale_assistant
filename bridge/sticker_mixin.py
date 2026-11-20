# -*- coding: utf-8 -*-
"""Bridge runtime mixin: StickerMixin."""
import engine as G


class StickerMixin:

    def sticker_quarantine_allows(self, username, shown='', gui_locked=False):
        requested = str(username or '').strip()
        matches = [(key, entry) for key, entry in self.sticker_draft_quarantine.items() if key == requested or (not requested and (str(shown or '').strip() == key or str(shown or '').strip() in entry['aliases']))]
        if not matches:
            return True

        def inspect():
            for key, entry in matches:
                value = None
                try:
                    compat = self._compat_mod()
                    if self.ACCOUNT.get('verified') and int(self.ACCOUNT.get('hwnd') or 0) == entry['hwnd'] and (compat is not None) and (int(compat.pinned_window() or 0) == entry['hwnd']) and self.wechat_window_alive():
                        value = G.quarantined_input_value(self.gui, entry['hwnd'], entry['aliases'])
                except Exception:
                    pass
                if isinstance(value, str) and (not G.sticker_input_is_blank(value)):
                    entry['draft_observed'] = True
                if G.sticker_input_is_blank(value) and entry['draft_observed']:
                    self.sticker_draft_quarantine.pop(key, None)
                else:
                    return False
            return True
        if gui_locked:
            allowed = inspect()
        else:
            with self.gui_lock:
                allowed = inspect()
        if not allowed:
            G.log_throttled('uncertain-attachment-draft', '发送暂停：上次附件操作可能留有草稿；请手动清空目标输入框后重试。无法读取清空证据时，请手动清空后重启桥接。', ttl=60)
        return allowed

    def sticker_config_now(self):
        if not G.STICKER_TEST_ON:
            return None
        current = G.read_enabled_config(G.CONFIG_PATH)
        if current is None:
            G.STICKER_TEST_ON = False
            return None
        if str((current.get('wechat') or {}).get('account') or '').strip() != G.ACCOUNT_WANTED:
            return None
        return current

    def sticker_gate(self, generation, job_id=''):
        if not generation or self.sticker_config_now() is None:
            return False
        try:
            response = G.LOCAL_HTTP.get(self.sticker_base + '/api/stickers/gate', params={'id': job_id}, headers=self.sticker_headers, timeout=3)
            status = response.json() if response.status_code == 200 else None
            return bool(isinstance(status, dict) and status.get('ok') is True and (status.get('enabled') is True) and (status.get('generation') == generation) and (not job_id or any((isinstance(job, dict) and job.get('id') == job_id and (job.get('status') == 'sending') and (job.get('generation', generation) == generation) for job in status.get('jobs') or []))) and (self.sticker_config_now() is not None))
        except Exception:
            return False

    def sticker_activity_allowed(self, username=''):
        try:
            return not (self.foreground_is_fullscreen() or (username and (self.chat_paused(username) or not self.group_is_alive(username))))
        except Exception:
            return False

    def _attachment_root(self, kind):
        """Approved directory per kind. Files never follow sticker category dirs."""
        if str(kind or '').strip().lower() == 'file':
            return G.os.path.join(G.ROOT, 'data', 'share', 'files')
        current = self.sticker_config_now()
        directory = ((current or {}).get('sticker') or {}).get('dir') or 'data/stickers'
        return directory if G.os.path.isabs(directory) else G.os.path.join(G.ROOT, directory)

    def _attachment_note(self, job_id, kind, result, media_type='-', size=0):
        """只记 id/大小/类型/结果；绝不把路径或文件正文写进日志。"""
        try:
            G.log('[attachment] job=%s kind=%s size=%d type=%s result=%s'
                  % (G.safe_id(job_id), kind, max(0, int(size or 0)), media_type or '-', result))
        except Exception:
            pass

    def send_attachment(self, path, chat_display_name, chat_username='', meta=None):
        """一次性、无导航的附件粘贴；只能向已在前台打开且聚焦的精确会话发送。

        meta: {kind: 'image'|'file', generation, job_id, title, gate?}。缺 kind
        时按图片处理，保持旧表情包任务的行为与回执语义（不自动重试；草稿
        状态不确定时不再发送）。
        """
        meta = meta if isinstance(meta, dict) else {}
        kind = 'file' if str(meta.get('kind') or '').strip().lower() == 'file' else 'image'
        noun = 'attachment' if kind == 'file' else 'sticker'
        generation = str(meta.get('generation') or '')
        job_id = str(meta.get('job_id') or '')
        # meta['title'] 只为兼容内核任务字段而接收：微信文件气泡显示真实文件名，
        # 改名会破坏路径白名单，所以标题不参与发送内容。
        custom_gate = meta.get('gate') if callable(meta.get('gate')) else None

        def gate_now():
            if custom_gate is not None:
                try:
                    return bool(custom_gate())
                except Exception:
                    return False
            return self.sticker_gate(generation, job_id)

        if not job_id or not gate_now():
            return G.sticker_result('cancelled',
                                    'attachments are disabled or generation expired'
                                    if kind == 'file' else
                                    'stickers are disabled or generation expired')
        with self.send_lock, G._SendStateScope(len(str(path or ''))):
            if not gate_now():
                return G.sticker_result('cancelled',
                                        'attachments are disabled or generation expired'
                                        if kind == 'file' else
                                        'stickers are disabled or generation expired')
            media_type, media_size = '-', 0
            if kind == 'file':
                # 先做只读校验：路径逃逸/链接/超限/类型不符要立刻给出可读原因，
                # 不必等到账号与窗口闸都通过。传输前还会再校验一次指纹。
                try:
                    _real, _fingerprint, media_type, media_size = G.validate_attachment(
                        path, self._attachment_root('file'))
                except G.AttachmentError as exc:
                    self._attachment_note(job_id, kind, 'failed', 'invalid', 0)
                    return G.sticker_result('failed', str(exc))
                except Exception:
                    self._attachment_note(job_id, kind, 'failed', 'invalid', 0)
                    return G.sticker_result('failed', '附件检查失败：文件不存在、类型不支持或超过大小上限。')
            else:
                try:
                    media_type = (G.os.path.splitext(str(path or ''))[1] or '').lstrip('.').upper() or '-'
                    media_size = int(G.os.path.getsize(path))
                except Exception:
                    media_type, media_size = '-', 0
            if not self.sticker_quarantine_allows(chat_username, chat_display_name):
                self._attachment_note(job_id, kind, 'failed', media_type, media_size)
                return G.sticker_result('failed', 'an uncertain attachment draft must be cleared manually before further sends')
            if not self.sticker_activity_allowed(chat_username):
                self._attachment_note(job_id, kind, 'failed', media_type, media_size)
                return G.sticker_result('failed', 'activity gate rejected the %s' % noun)
            if not self.account_send_allowed('附件' if kind == 'file' else '表情包') or not self.ACCOUNT.get('verified') or (not self.wechat_window_alive()) or (not self.sticker_account_evidence_allowed()):
                self._attachment_note(job_id, kind, 'failed', media_type, media_size)
                return G.sticker_result('failed', 'target account window could not be verified')
            hwnd = int(self.ACCOUNT.get('hwnd') or 0)
            snap = None
            try:
                desktop = G.WindowsDesktop()
                if not desktop.foreground(hwnd):
                    snap = self.snapshot_win_state()
                    from moments_window import activate_verified_window

                    def activation_allowed():
                        return (bool(self.ACCOUNT.get('verified'))
                                and int(self.ACCOUNT.get('hwnd') or 0) == hwnd
                                and self.sticker_config_now() is not None
                                and self.sticker_activity_allowed(chat_username))

                    evidence = {}
                    ready = activate_verified_window(
                        self, hwnd, int(self.ACCOUNT.get('pid') or 0), activation_allowed,
                        pause=G.time.sleep, probe=evidence.update)
                    if not ready or not desktop.foreground(hwnd):
                        G.log_throttled('sticker-window-not-ready',
                                        '[sticker] stage=foreground result=failed reason=window_not_ready'
                                        ' attempts=%d visible=%d iconic=%d same_process=%d'
                                        % (evidence.get('attempts', 0), evidence.get('visible', False),
                                           evidence.get('iconic', False), evidence.get('sameProcess', False)),
                                        ttl=60)
                        self._attachment_note(job_id, kind, 'failed', media_type, media_size)
                        return G.sticker_result('failed', 'could not bring the WeChat window to the foreground')
                    G.log_throttled('sticker-foreground', '表情包：微信不在前台，先切过去（发完会把窗口还原）', ttl=120)
            except Exception:
                self._attachment_note(job_id, kind, 'failed', media_type, media_size)
                return G.sticker_result('failed', 'foreground safety checks are unavailable')
            try:
                with self.gui_lock:
                    current_gui = self.gui
                    if current_gui is None:
                        return G.sticker_result('failed', 'window driver is not ready')
                    if not gate_now():
                        return G.sticker_result('cancelled',
                                                'attachments are disabled or generation expired'
                                                if kind == 'file' else
                                                'stickers are disabled or generation expired')
                    try:
                        canonical, aliases = G.resolve_sticker_target(self.db, chat_display_name, chat_username)
                        if not self.send_policy_allows(canonical, chat_display_name):
                            return G.sticker_result('cancelled', 'chat is blocked by the send policy')
                        if not self.sticker_quarantine_allows(canonical, chat_display_name, gui_locked=True):
                            return G.sticker_result('failed', 'an uncertain attachment draft must be cleared manually before further sends')
                        if not self.sticker_activity_allowed(canonical):
                            return G.sticker_result('failed', 'target or activity gate rejected the sticker')
                        if not gate_now() or not desktop.foreground(hwnd):
                            return G.sticker_result('cancelled', 'send gates changed before binding')
                        if not current_gui.use_window(hwnd) or int(current_gui.main_hwnd) != hwnd:
                            return G.sticker_result('failed', 'unable to bind the verified account window')
                        uia = getattr(current_gui, '_uia', None)
                        if uia is None:
                            # 冷启动/驱动重建后缓存未初始化不等于不可用，先走正式获取入口。
                            uia = current_gui._get_uia()
                        if uia is None:
                            return G.sticker_result('failed', 'UIA target proof is unavailable; no OCR fallback')
                        search_keys = [chat_display_name] + [a for a in aliases if a and a != chat_display_name]
                        try:
                            if not G.sticker_alias_match(uia.current_chat(), aliases):
                                uia.open_chat(search_keys)
                                for _ in range(8):
                                    G.time.sleep(0.25)
                                    if G.sticker_alias_match(uia.current_chat(), aliases):
                                        break
                        except Exception:
                            return G.sticker_result('failed', 'exact target is not open in the pinned window')
                        if not G.sticker_alias_match(uia.current_chat(), aliases):
                            return G.sticker_result('failed', 'exact target is not open in the pinned window')
                        current = self.sticker_config_now()
                        if current is None:
                            return G.sticker_result('cancelled', 'stickers are disabled')
                        if kind == 'file':
                            directory = self._attachment_root('file')
                        else:
                            directory = (current.get('sticker') or {}).get('dir') or 'data/stickers'
                            directory = directory if G.os.path.isabs(directory) else G.os.path.join(G.ROOT, directory)

                        def pinned_account_valid():
                            try:
                                compat = self._compat_mod()
                                return bool(self.ACCOUNT.get('verified') and int(self.ACCOUNT.get('hwnd') or 0) == hwnd and self.sticker_account_evidence_allowed() and self.wechat_window_alive() and (compat is not None) and (int(compat.pinned_window() or 0) == hwnd))
                            except Exception:
                                return False

                        def transaction_gate():
                            if not gate_now():
                                return False
                            if not self.send_policy_allows(canonical, chat_display_name):
                                return False
                            latest = self.sticker_config_now()
                            if latest is None:
                                return False
                            if kind == 'file':
                                # 资产目录固定，不随表情包分类目录切换；只核对窗口没换号。
                                return int((latest.get('wechat') or {}).get('windowHwnd') or 0) in (0, hwnd)
                            latest_dir = (latest.get('sticker') or {}).get('dir') or 'data/stickers'
                            latest_dir = latest_dir if G.os.path.isabs(latest_dir) else G.os.path.join(G.ROOT, latest_dir)
                            wanted_hwnd = int((latest.get('wechat') or {}).get('windowHwnd') or 0)
                            return G.os.path.normcase(G.os.path.abspath(latest_dir)) == G.os.path.normcase(G.os.path.abspath(directory)) and wanted_hwnd in (0, hwnd)
                        G.SEND_STATE['start'] = G.time.time()
                        try:
                            outcome = G.guarded_sticker_send(current_gui, uia, hwnd, aliases, path, directory, self.db, canonical, gate=transaction_gate, account_valid=pinned_account_valid, activity_allowed=lambda: self.sticker_activity_allowed(canonical), desktop=desktop, kind=kind)
                            if not isinstance(outcome, dict) or outcome.get('status') not in {'confirmed', 'unconfirmed', 'failed', 'cancelled'}:
                                raise ValueError('unknown attachment result')
                        except Exception:
                            outcome = G.sticker_result('unconfirmed', 'transport action is uncertain; a draft may remain; do not retry', draft_may_remain=True)
                        if outcome.get('draft_may_remain') or outcome.get('status') == 'unconfirmed':
                            self.sticker_draft_quarantine[canonical] = {'hwnd': hwnd, 'aliases': aliases, 'draft_observed': bool(outcome.get('draft_observed'))}
                        self._attachment_note(job_id, kind, outcome.get('status') or 'unconfirmed', media_type, media_size)
                        return outcome
                    except Exception:
                        return G.sticker_result('failed', '%s safety checks are unavailable' % noun)
            finally:
                if snap:
                    try:
                        # 用户切走之后，仍去还原会再次抢焦点。
                        if desktop.foreground(hwnd):
                            self.restore_win_state(snap)
                    except Exception:
                        pass

    def send_sticker(self, path, chat_display_name, chat_username='', generation='', job_id=''):
        """薄封装：旧表情包任务字段（无 kind）继续走图片语义。"""
        return self.send_attachment(path, chat_display_name, chat_username,
                                    {'kind': 'image', 'generation': generation, 'job_id': job_id})

    def claim_and_send_sticker(self, item, expected_chat='', expected_username=''):
        """Atomic claim precedes every send, including normal webhook stickers.

        Never send from an unclaimed payload, never retry a claim or attachment,
        and never interpret a truthy result dictionary as confirmed delivery.
        """
        if not isinstance(item, dict) or not self.sticker_gate(item.get('generation')):
            return G.sticker_result('cancelled', 'stickers are disabled or generation expired')
        job_id = str(item.get('id') or '')
        if not job_id:
            return G.sticker_result('failed', 'sticker job id is missing')
        try:
            response = G.LOCAL_HTTP.post(self.sticker_base + '/api/stickers/claim', json={'id': job_id}, headers=self.sticker_headers, timeout=3)
            claimed = response.json() if response.status_code == 200 else None
            claimed = claimed.get('item') if isinstance(claimed, dict) and claimed.get('ok') is True else None
        except Exception:
            claimed = None
        if not isinstance(claimed, dict):
            return G.sticker_result('failed', 'sticker job was not claimed; nothing was sent')
        requested_kind = 'file' if str(item.get('kind') or '').strip().lower() == 'file' else 'image'
        claimed_kind = 'file' if str(claimed.get('kind') or '').strip().lower() == 'file' else 'image'
        if claimed.get('id') != job_id or claimed.get('generation') != item.get('generation') or claimed.get('status') != 'sending' or claimed_kind != requested_kind or (expected_chat and (not expected_username) and (claimed.get('chat') != expected_chat)) or (expected_username and claimed.get('chatUsername') != expected_username):
            outcome = G.sticker_result('failed', 'claimed job identity did not match the request')
        elif claimed_kind == 'file':
            outcome = self.send_attachment(claimed.get('path'), claimed.get('chat') or '', claimed.get('chatUsername') or '',
                                           {'kind': 'file', 'title': claimed.get('title') or '',
                                            'generation': claimed.get('generation'), 'job_id': job_id})
        else:
            outcome = self.send_sticker(claimed.get('path'), claimed.get('chat') or '', claimed.get('chatUsername') or '', claimed.get('generation'), job_id)
        if self.sticker_gate(item.get('generation')):
            payload = {'id': job_id, 'status': outcome['status'], 'error': outcome['error']}
            for attempt in range(3):
                try:
                    resp = G.LOCAL_HTTP.post(self.sticker_base + '/api/stickers/ack', json=payload, headers=self.sticker_headers, timeout=8)
                    if getattr(resp, 'status_code', 0) in (200, 404, 409):
                        break
                except Exception as exc:
                    if attempt == 2:
                        G.log_throttled('sticker-ack-fail', '表情包结果没能写回服务端（不影响已经发出的消息）：' + repr(exc), ttl=300)
                G.time.sleep(0.4)
            if self.sticker_config_now() is not None:
                G.log('表情包结果：', outcome['status'], outcome['error'])
        return outcome

    def cancel_reply_sticker(self, item, expected_username):
        """Release a reserved reply without claiming it or touching WeChat."""
        if not isinstance(item, dict) or not expected_username or self.sticker_config_now() is None:
            return False
        if not item.get('id') or not item.get('generation') or item.get('chatUsername') != expected_username:
            return False
        payload = {'id': item['id'], 'generation': item['generation'], 'chatUsername': expected_username}
        try:
            response = G.LOCAL_HTTP.post(self.sticker_base + '/api/stickers/cancel',
                                         json=payload, headers=self.sticker_headers, timeout=3)
            body = response.json() if response.status_code == 200 else None
            cancelled = bool(isinstance(body, dict) and body.get('ok') is True
                             and isinstance(body.get('item'), dict)
                             and body['item'].get('status') == 'cancelled'
                             and body['item'].get('id') == item['id']
                             and body['item'].get('generation') == item['generation']
                             and body['item'].get('chatUsername') == expected_username)
            G.log('[sticker] stage=cancel result=%s reason=reply_delivery_stopped' % ('cancelled' if cancelled else 'unconfirmed'))
            return cancelled
        except Exception as exc:
            G.log('[sticker] stage=cancel result=unconfirmed reason=cancel_request_failed type=%s' % type(exc).__name__)
            return False

    def sticker_test_loop(self):
        """常驻但默认休眠：关闭时不访问任何表情包接口、文件或窗口，也不打印日志。"""
        while True:
            try:
                if G.STICKER_TEST_ON and self.sticker_config_now() is not None:
                    response = G.LOCAL_HTTP.get(self.sticker_base + '/api/stickers/pending', headers=self.sticker_headers, timeout=3)
                    body = response.json() if response.status_code == 200 else None
                    items = body.get('items') if isinstance(body, dict) and body.get('ok') is True else []
                    for item in items if isinstance(items, list) else []:
                        if not G.STICKER_TEST_ON or self.sticker_config_now() is None:
                            break
                        self.claim_and_send_sticker(item)
            except Exception:
                pass
            G.time.sleep(5)
