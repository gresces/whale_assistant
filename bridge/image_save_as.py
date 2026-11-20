# -*- coding: utf-8 -*-
"""C 兜底：让微信自己「右键 → 另存为…」把原图写到磁盘。

为什么需要：磁盘解密失败（多半是没取到图片密钥）时，``image_ui_fallback``
只能截微信的预览窗，拿到的往往是屏幕上那一幅、甚至还是缩略图。微信的
「另存为…」会把它自己解出来的原图重新编码后写盘，质量明显更好，所以这一步
放在截图兜底前面；只在取图失败时触发，每次 ``describe_image`` 最多一次。

和 ``image_ui_fallback`` 一样，所有界面动作都跑在限时子进程里：UIA / 微信
卡住时由父进程把子进程终止，不把桥接的消息线程钉住。日志只写原因类别，
不写路径、文件名或会话名。
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import subprocess
import sys
import time
from contextlib import redirect_stdout

import config as bridge_config
from image_ui_fallback import MAX_REQUEST_BYTES, build_preview_request, reveal_target

SAVE_TIMEOUT = 15.0
MIN_SAVE_TIMEOUT = 5.0
MAX_SAVE_TIMEOUT = 60.0
MAX_IMAGE_BYTES = 8 * 1024 * 1024
# 父进程的硬时限比子进程内部预算多几秒：内部超时会先按 Esc 收掉菜单/对话框，
# 只有 UIA 真的挂死时才轮到父进程杀子进程。
SAVE_GRACE = 6.0
MENU_WAIT = 3.0
DIALOG_WAIT = 4.0

# 子进程退出码和原因的对应关系（1 固定是 internal_error）。
FAILURES = (
    '', 'internal_error', 'no_window', 'message_not_aligned', 'menu_missing',
    'dialog_missing', 'timeout', 'save_failed', 'invalid_image',
)

VK_A = 0x41
VK_ESCAPE = 0x1B
VK_RETURN = 0x0D

# 保存对话框里不能写值的 Edit：文件格式选择器里也有一颗 Edit，右上角还有搜索栏。
_BAD_EDIT_WORDS = ('类型', '格式', '搜索', 'type', 'format', 'search')


class SaveUnavailable(RuntimeError):
    """限时子进程里的失败信号；参数是 ``FAILURES`` 里的原因。"""


def is_save_as_item(text):
    """右键菜单文案是不是「另存为…」这一项（中英、带不带省略号都算）。

    只认整项，不做包含匹配：微信菜单里还有「另存为图片」之类容易误点的邻近项，
    宁可返回 ``menu_missing`` 也不要右键后点错东西。
    """
    value = str(text or '').strip().lower().replace('&', '')
    value = ''.join(value.split())
    if '(' in value:
        value = value.split('(', 1)[0]
    while value.endswith(('…', '.', '。')):
        value = value[:-1]
    return value in ('另存为', 'saveas')


def save_timeout(config=None):
    """``image.uiSaveTimeoutSeconds``，夹在 5~60 秒；坏值回默认 15。"""
    if not isinstance(config, dict):
        try:
            config = bridge_config.read_config()
        except Exception:
            config = {}
    section = config.get('image') if isinstance(config, dict) else None
    if not isinstance(section, dict):
        section = {}
    try:
        value = float(section.get('uiSaveTimeoutSeconds', SAVE_TIMEOUT))
    except (TypeError, ValueError):
        value = SAVE_TIMEOUT
    return min(MAX_SAVE_TIMEOUT, max(MIN_SAVE_TIMEOUT, value))


def save_target_path(root, msg, now=None):
    """给这条消息生成一个不会撞名、也不会带聊天名的目标路径。

    名字里只有消息身份的 sha1 前 12 位 + 毫秒时间戳：同一张图重试不会覆盖
    上一次的产物，也不会把会话名/文件名写进日志或缓存目录。
    """
    username = str(msg.get('username') or '')
    identity = '%s|%s|%s' % (username, msg.get('local_id'), msg.get('shard') or '')
    digest = hashlib.sha1(identity.encode('utf-8')).hexdigest()[:12]
    moment = time.time() if now is None else float(now)
    stamp = time.strftime('%Y%m%d-%H%M%S', time.localtime(moment))
    stamp += '-%03d' % int((moment % 1) * 1000)
    return os.path.join(str(root), 'data', 'media-cache',
                        'wechat-save-%s-%s.jpg' % (digest, stamp))


def build_save_request(db, msg, chat_name, hwnd, pid, target_path):
    """复用 ``build_preview_request`` 的 local_id + shard 对齐信息，再带上目标路径。

    找不到唯一一条消息就返回 ``None``，调用方按 ``message_not_aligned`` 处理。
    """
    request = build_preview_request(db, msg, chat_name, hwnd, pid)
    if request is None:
        return None
    request['target_path'] = str(target_path)
    return request


# ---------------------------------------------------------------------------
# Windows「另存为」对话框（UIA）。只做找控件和确认按钮，不改 vendor 源码。
# ---------------------------------------------------------------------------

def _find_by_name(ctrl, pred, max_depth=14, _depth=0):
    if _depth > max_depth:
        return None
    try:
        children = ctrl.GetChildren()
    except Exception:
        return None
    for child in children:
        try:
            if pred((child.Name or '').strip()):
                return child
        except Exception:
            continue
        got = _find_by_name(child, pred, max_depth, _depth + 1)
        if got is not None:
            return got
    return None


def _find_button(ctrl, pred, max_depth=14, _depth=0):
    """只认真正的按钮：「保存类型」下拉框的 UIA Name 也叫「保存」，点到它没用。"""
    if _depth > max_depth:
        return None
    try:
        children = ctrl.GetChildren()
    except Exception:
        return None
    for child in children:
        try:
            is_button = 'Button' in (child.ControlTypeName or '')
            name = (child.Name or '').strip()
        except Exception:
            continue
        if is_button and pred(name):
            return child
        got = _find_button(child, pred, max_depth, _depth + 1)
        if got is not None:
            return got
    return None


def _is_save_button(name):
    value = ''.join(str(name or '').lower().split())
    return value.startswith('保存') or value.startswith('save')


def _first_edit(root, max_depth=14):
    found = []

    def walk(ctrl, depth=0):
        if depth > max_depth:
            return
        try:
            children = list(ctrl.GetChildren())
        except Exception:
            return
        for child in children:
            try:
                is_edit = 'Edit' in (child.ControlTypeName or '')
            except Exception:
                is_edit = False
            if is_edit:
                found.append(child)
            walk(child, depth + 1)

    walk(root)
    for edit in found:
        try:
            blob = (edit.Name or '') + ' ' + (edit.ClassName or '')
        except Exception:
            blob = ''
        if any(word in blob for word in _BAD_EDIT_WORDS):
            continue
        return edit
    return None


def _file_name_edit(dialog):
    """文件名输入框：优先 Name 含「文件名」，命中容器后再往里找真正的 Edit。"""
    hit = _find_by_name(dialog, lambda name: '文件名' in name or 'filename' in name.lower())
    if hit is not None:
        try:
            if 'Edit' in (hit.ControlTypeName or ''):
                return hit
        except Exception:
            pass
        inner = _first_edit(hit)
        if inner is not None:
            return inner
    return _first_edit(dialog)


class SaveDialog:
    """通用保存对话框的薄视图；只暴露流程需要的「找文件名框」。"""

    def __init__(self, control):
        self.control = control

    def filename_edit(self):
        return _file_name_edit(self.control)


def find_save_dialog(pid):
    """找微信弹出的 Windows 通用保存对话框（``#32770`` + 有「保存/Save」按钮）。

    必须核对进程号：桌面上同时开着的别的程序也可能有保存框，往那里写路径
    等于把缓存写到随机位置。
    """
    import uiautomation as auto

    try:
        tops = auto.GetRootControl().GetChildren()
    except Exception:
        return None
    for window in tops:
        try:
            if (window.ClassName or '') != '#32770':
                continue
            if pid and getattr(window, 'ProcessId', 0) not in (0, pid):
                continue
            if _find_button(window, _is_save_button) is None:
                continue
            return SaveDialog(window)
        except Exception:
            continue
    return None


def find_menu_item(root):
    """在主窗子树里找右键菜单的「另存为…」项（菜单物化在 ``mmui::XMenu`` 下）。"""
    found = []

    def walk(ctrl, depth=0):
        if found or depth > 10:
            return
        try:
            children = ctrl.GetChildren()
        except Exception:
            return
        for child in children:
            try:
                name = child.Name or ''
                cls = child.ClassName or ''
                ctype = child.ControlTypeName or ''
            except Exception:
                continue
            if is_save_as_item(name) and (cls == 'mmui::XMenuView' or ctype == 'MenuItemControl'):
                found.append(child)
                return
            walk(child, depth + 1)

    try:
        walk(root)
    except Exception:
        pass
    return found[0] if found else None


# ---------------------------------------------------------------------------
# 子进程流程
# ---------------------------------------------------------------------------

def _size(path):
    try:
        return os.path.getsize(path) if os.path.isfile(path) else 0
    except OSError:
        return 0


def _discard(path):
    try:
        os.remove(path)
    except OSError:
        pass


def read_saved_image(path):
    """校验微信写出的文件并返回字节；不合格删除后抛 ``invalid_image``。

    只接受 JPEG / PNG / GIF 头（``describe_image`` 只认这三种）；其它能被 PIL
    打开的格式（BMP/WebP）转成 JPEG，避免后面「图片格式不认识」白跑一次。
    """
    size = _size(path)
    if size <= 0 or size > MAX_IMAGE_BYTES:
        _discard(path)
        raise SaveUnavailable('invalid_image')
    try:
        from PIL import Image

        with Image.open(path) as image:
            image.load()
            picture_format = (image.format or '').upper()
            if picture_format in ('JPEG', 'PNG', 'GIF'):
                with open(path, 'rb') as source:
                    return source.read()
            converted = image.convert('RGB')
            out = io.BytesIO()
            converted.save(out, format='JPEG', quality=90)
            data = out.getvalue()
    except SaveUnavailable:
        raise
    except Exception:
        _discard(path)
        raise SaveUnavailable('invalid_image')
    if not data or len(data) > MAX_IMAGE_BYTES:
        _discard(path)
        raise SaveUnavailable('invalid_image')
    return data


def _wait(getter, until, pause, clock, interval=0.12):
    while True:
        try:
            got = getter()
        except Exception:
            got = None
        if got is not None:
            return got
        if clock() >= until:
            return None
        pause(interval)


def _wait_file(path, deadline, pause, clock, interval=0.25):
    """等文件出现且大小稳定（两次采样一致才算写完）。

    返回字节数；到时限还不存在返回 ``None``（已存在但是 0 字节也照样返回，
    交给 ``read_saved_image`` 按非法图片删除，而不是报成「超时没出现」）。
    """
    last = -1
    while True:
        exists = os.path.isfile(path)
        size = _size(path)
        if exists and size and size == last:
            return size
        last = size
        if clock() >= deadline:
            return size if exists else None
        pause(interval)


def _right_click(inp, x, y):
    sender = getattr(inp, 'send_input_click', None)
    if callable(sender):
        sender(int(x), int(y), right=True)
    else:
        inp.real_click(int(x), int(y), right=True)


def _left_click(inp, x, y):
    sender = getattr(inp, 'send_input_click', None)
    if callable(sender):
        sender(int(x), int(y))
    else:
        inp.real_click(int(x), int(y))


def _dismiss(inp, pause):
    """按 Esc 收掉菜单/对话框，最多两次；关不掉也不抛异常。"""
    for _ in range(2):
        try:
            inp.key(VK_ESCAPE)
        except Exception:
            pass
        try:
            pause(0.15)
        except Exception:
            pass


def save_image_as(request, *, win, process, uia, locator, inp, menu=None, dialog=None,
                  pause=time.sleep, clock=time.monotonic, budget=None):
    """在当前会话里右键目标图片 →「另存为…」→ 填路径 → 回车，返回图片字节。

    不做任何静默重试：调用方（describe_image）已经保证整条 C 兜底每次最多一次，
    这里再重试只会让用户的窗口被反复抢焦点。
    """
    main = int(request.get('hwnd') or 0)
    pid = int(request.get('pid') or 0)
    target_path = str(request.get('target_path') or '')
    if not main or not pid or not target_path:
        raise SaveUnavailable('no_window')
    if os.path.exists(target_path):
        # 目标名带 hash + 时间戳，正常不会撞名；撞了也不能覆盖已有文件。
        raise SaveUnavailable('save_failed')
    if (not win.IsWindow(main) or not win.IsWindowVisible(main) or win.IsIconic(main)
            or process.GetWindowThreadProcessId(main)[1] != pid
            or win.GetForegroundWindow() != main):
        raise SaveUnavailable('no_window')
    chat = request.get('chat') or ''
    if (uia.current_chat() or '').strip() != chat:
        raise SaveUnavailable('message_not_aligned')
    messages = uia._message_list()
    rows = locator._visible_rows(messages) if messages is not None else []
    index = locator._align_index(rows, request.get('sequence') or [], request.get('target'))
    if index is None or not (0 <= index < len(rows)):
        raise SaveUnavailable('message_not_aligned')
    target = rows[index][2]
    if getattr(target, 'IsOffscreen', False):
        raise SaveUnavailable('message_not_aligned')

    root = getattr(uia, '_win', None)
    find_menu = menu or (lambda: find_menu_item(root))
    find_dialog = dialog or (lambda: find_save_dialog(pid))
    try:
        wait_budget = float(budget if budget is not None else
                            request.get('budget') or SAVE_TIMEOUT)
    except (TypeError, ValueError):
        wait_budget = SAVE_TIMEOUT
    wait_budget = min(MAX_SAVE_TIMEOUT, max(MIN_SAVE_TIMEOUT, wait_budget))
    deadline = clock() + wait_budget

    rect = target.BoundingRectangle
    bounds = messages.BoundingRectangle
    y = (rect.top + rect.bottom) // 2
    midpoint = (rect.left + rect.right) / 2.0
    is_self = bool(request.get('is_self'))
    xs = [x for x in locator._bubble_click_xs(rect, is_self)
          if rect.left <= x < rect.right and (x > midpoint) == is_self][:2]
    if not xs or not (bounds.left <= xs[0] < bounds.right
                      and bounds.top <= y < bounds.bottom):
        raise SaveUnavailable('message_not_aligned')

    opened = False
    ok = False
    try:
        item = None
        for x in xs:
            if clock() >= deadline:
                raise SaveUnavailable('timeout')
            # 右键之前重新核对一次：用户可能在等图的时候切了会话/窗口。
            if (win.GetForegroundWindow() != main
                    or (uia.current_chat() or '').strip() != chat
                    or process.GetWindowThreadProcessId(main)[1] != pid):
                raise SaveUnavailable('no_window')
            if win.GetAncestor(win.WindowFromPoint((x, y)), 2) != main:
                raise SaveUnavailable('message_not_aligned')
            _right_click(inp, x, y)
            opened = True
            item = _wait(find_menu, min(deadline, clock() + MENU_WAIT), pause, clock)
            if item is not None:
                break
        if item is None:
            raise SaveUnavailable('menu_missing')
        if getattr(item, 'IsOffscreen', False):
            raise SaveUnavailable('menu_missing')
        item_rect = item.BoundingRectangle
        _left_click(inp, (item_rect.left + item_rect.right) // 2,
                    (item_rect.top + item_rect.bottom) // 2)

        dlg = _wait(find_dialog, min(deadline, clock() + DIALOG_WAIT), pause, clock)
        if dlg is None:
            raise SaveUnavailable('dialog_missing')
        edit = dlg.filename_edit()
        if edit is None:
            raise SaveUnavailable('dialog_missing')
        edit.SetFocus()
        pause(0.2)
        # 先全选再输入：文件名框默认预填「微信图片_时间戳.jpg」，直接输入会变成追加。
        inp.key(VK_A, ctrl=True)
        pause(0.1)
        inp.type_unicode(target_path)
        pause(0.1)
        inp.key(VK_RETURN)
        size = _wait_file(target_path, deadline, pause, clock)
        if size is None:
            raise SaveUnavailable('timeout')
        data = read_saved_image(target_path)
        ok = True
        return data
    finally:
        # 只有真的拉出过菜单/对话框才按 Esc，失败不留下半开窗口。
        if opened and not ok:
            _dismiss(inp, pause)


def save_image_via_ui(request, log, *, timeout=SAVE_TIMEOUT, run=None, last=None,
                      reveal=None):
    """父进程等固定时限；子进程负责右键、另存为、填路径和校验。

    ``last`` 可选：失败时写入 ``{'reason': ...}``，调用方据此写节流日志。
    """
    try:
        timeout = min(MAX_SAVE_TIMEOUT, max(MIN_SAVE_TIMEOUT, float(timeout)))
    except (TypeError, ValueError):
        timeout = SAVE_TIMEOUT

    target_path = str(request.get('target_path') or '')
    if not target_path:
        log('图片另存为兜底跳过 reason=no_target')
        _note_reason(last, 'save_failed')
        return b''
    try:
        os.makedirs(os.path.dirname(target_path), exist_ok=True)
    except OSError:
        log('图片另存为兜底跳过 reason=save_failed')
        _note_reason(last, 'save_failed')
        return b''

    # 先把微信拉到前台并滚到底：目标行不在可视区时右键点不到。
    try:
        (reveal or reveal_target)(
            {'hwnd': request.get('hwnd'), 'pid': request.get('pid'), 'chat': request.get('chat')},
            log)
    except Exception as exc:
        log('图片另存为兜底：拉前台/滚动失败 type=%s' % type(exc).__name__)

    try:
        payload = json.dumps(dict(request, budget=timeout), ensure_ascii=False).encode('utf-8')
        if len(payload) > MAX_REQUEST_BYTES:
            log('图片另存为兜底跳过 reason=request_too_large')
            _note_reason(last, 'save_failed')
            return b''
        result = (run or subprocess.run)(
            [sys.executable, '-X', 'utf8', '-B', os.path.abspath(__file__), '--save-as'],
            input=payload, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            timeout=timeout + SAVE_GRACE, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0),
        )
    except subprocess.TimeoutExpired:
        log('另存为兜底失败 reason=timeout（%.0f 秒）' % timeout)
        _note_reason(last, 'timeout')
        return b''
    except Exception as exc:
        log('另存为兜底失败 reason=save_failed type=%s' % type(exc).__name__)
        _note_reason(last, 'save_failed')
        return b''
    if result.returncode:
        code = result.returncode
        reason = FAILURES[code] if 0 < code < len(FAILURES) else 'save_failed'
        log('另存为兜底失败 reason=%s' % reason)
        _note_reason(last, reason)
        return b''
    data = result.stdout or b''
    if (not data.startswith(b'\xff\xd8\xff') and not data.startswith(b'\x89PNG\r\n\x1a\n')
            and not data.startswith(b'GIF')) or len(data) > MAX_IMAGE_BYTES:
        log('另存为兜底失败 reason=invalid_image')
        _note_reason(last, 'invalid_image')
        return b''
    log('图片另存为兜底已取图（%d KB）' % (len(data) // 1024))
    return data


def _note_reason(last, reason):
    if last is not None:
        last['reason'] = reason


# ---------------------------------------------------------------------------
# 子进程入口
# ---------------------------------------------------------------------------

def _worker():
    output = sys.stdout.buffer
    try:
        with redirect_stdout(sys.stderr):
            payload = sys.stdin.buffer.read(MAX_REQUEST_BYTES + 1)
            if len(payload) > MAX_REQUEST_BYTES:
                return FAILURES.index('save_failed')
            request = json.loads(payload.decode('utf-8'))
            import win32gui as win
            import win32process as process

            hwnd = int(request.get('hwnd') or 0)
            pid = int(request.get('pid') or 0)
            if (not hwnd or not win.IsWindow(hwnd)
                    or process.GetWindowThreadProcessId(hwnd)[1] != pid):
                return FAILURES.index('no_window')
            import uiautomation as auto
            from wechatauto import MediaDownloader
            from wechatauto.guia import WinInput
            from wechatauto.uia_driver import WeChatUIA

            uia = WeChatUIA(hwnd=hwnd, search_timeout=0.5)
            root = auto.ControlFromHandle(hwnd)
            if root is None or root.ProcessId != pid or root.NativeWindowHandle != hwnd:
                return FAILURES.index('no_window')
            # 直接用已存在的控件树，避免重新搜窗/激活/再开数据库连接。
            uia._win = root
            data = save_image_as(
                request, win=win, process=process, uia=uia,
                locator=MediaDownloader(None), inp=WinInput(),
            )
        output.write(data)
        output.flush()
        return 0
    except SaveUnavailable as exc:
        reason = str(exc.args[0]) if exc.args else 'save_failed'
        return FAILURES.index(reason) if reason in FAILURES else FAILURES.index('save_failed')
    except Exception:
        return FAILURES.index('internal_error')


if __name__ == '__main__' and sys.argv[1:] == ['--save-as']:
    sys.exit(_worker())
