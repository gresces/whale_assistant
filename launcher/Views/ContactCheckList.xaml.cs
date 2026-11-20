using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WhaleLauncher.Views
{
    /// <summary>联系人候选项；人设页的主要联系人搜索也复用它。</summary>
    public sealed class ContactItem : ContactInfo
    {
        public string Sub => Wxid + (IsGroup ? " · 群聊" : " · 私聊");
        public Visibility GroupBadgeVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>已选名单里的一行。</summary>
    public sealed class PolicyContactRow : ContactInfo
    {
        public string Kind => IsGroup ? "群聊" : "私聊";
        public string RemoveName => "移除 " + Display;
    }

    /// <summary>添加会话时的一个候选项；IsPicked 只属于这一次添加。</summary>
    public sealed class PickRow : ContactInfo
    {
        private bool _isPicked;

        public bool IsPicked
        {
            get => _isPicked;
            set => Set(ref _isPicked, value);
        }

        public string Kind => IsGroup ? "群聊" : "私聊";
    }

    /// <summary>
    /// 一份发言名单的「已选 + 添加」控件。
    ///
    /// 名单是结果集，不是联系人全集：这里只列已经选中的会话，添加时再从联系人目录里多选。
    /// 勾选/移除/添加都直接 PATCH config，没有「确定 / 保存」第二道关；失败就回滚到上次成功的名单。
    /// 搜索框只在名单超过 8 个时出现，平时不占一行表单。
    /// </summary>
    public partial class ContactCheckList : UserControl
    {
        private const int SearchThreshold = 8;

        private readonly RuntimeSupervisor _sup;
        private readonly string _configPath;
        private readonly bool _allowAllMute;
        private readonly ConfigSaver _saver;

        private readonly List<string> _chosen = new List<string>();
        private readonly Dictionary<string, ContactInfo> _known = new Dictionary<string, ContactInfo>();
        private readonly HashSet<string> _pending = new HashSet<string>();
        private List<ContactInfo> _contacts = new List<ContactInfo>();
        private List<string> _savedIds = new List<string>();

        private bool _savedAllMute;
        private bool _ready;
        private bool _loading;
        private bool _applying;
        private bool _preview;
        private bool _contactsFailed;
        private bool _onlyMode;
        private bool _rendering;

        /// <summary>保存结果或失败原因，交给宿主决定显示在顶部还是行内。</summary>
        public event Action<string> Status;

        /// <summary>名单数量 / 「全部」状态变了，宿主刷新状态摘要。</summary>
        public event Action Changed;

        public int SelectedCount => _chosen.Count;

        public bool AllMuted => _allowAllMute && ChkAll.IsChecked == true;

        public ContactCheckList(RuntimeSupervisor sup, string configPath, bool allowAllMute)
        {
            InitializeComponent();
            _sup = sup;
            _configPath = configPath;
            _allowAllMute = allowAllMute;
            _saver = new ConfigSaver(sup);

            // 只有黑名单认 *（全部会话）；白名单里的 * 是旧配置残留。
            ChkAll.Visibility = allowAllMute ? Visibility.Visible : Visibility.Collapsed;
            Loaded += (_, __) => _ready = true;
            Render();
        }

        /// <summary>从 config 回填（不会触发保存）。</summary>
        public void SetValue(JsonElement v)
        {
            _chosen.Clear();
            var allMute = false;
            if (v.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in v.EnumerateArray())
                {
                    if (x.ValueKind != JsonValueKind.String) continue;
                    var id = (x.GetString() ?? "").Trim();
                    if (id.Length == 0) continue;
                    if (id == "*")
                    {
                        if (_allowAllMute) allMute = true;
                        continue;
                    }
                    if (!_chosen.Contains(id)) _chosen.Add(id);
                }
            }
            _savedIds = new List<string>(_chosen);
            _savedAllMute = allMute;

            _applying = true;
            try { ChkAll.IsChecked = allMute; }
            finally { _applying = false; }
            Render();
        }

        /// <summary>截图预览用：直接给名字和分组，不读接口、不写配置。</summary>
        public void PreviewSeed(IEnumerable<ContactInfo> items, bool allMute)
        {
            _preview = true;
            _chosen.Clear();
            _known.Clear();
            foreach (var info in items)
            {
                if (info == null || string.IsNullOrEmpty(info.Wxid)) continue;
                if (!_chosen.Contains(info.Wxid)) _chosen.Add(info.Wxid);
                _known[info.Wxid] = info;
            }
            _savedIds = new List<string>(_chosen);
            _savedAllMute = allMute;
            _applying = true;
            try { ChkAll.IsChecked = allMute; }
            finally { _applying = false; }
            Render();
        }

        /// <summary>仅白名单模式开关会影响空白名单该怎么提示。</summary>
        public void SetScopeHint(bool onlyMode)
        {
            if (_onlyMode == onlyMode) return;
            _onlyMode = onlyMode;
            Render();
        }

        /// <summary>拉联系人列表；失败时保留已选条目，只在添加面板里说明原因。</summary>
        public async Task ReloadAsync(bool force = false)
        {
            if (_preview || _sup == null) return;
            if (_loading) return;
            _loading = true;
            try
            {
                if (!_sup.NodeRunning && !await _sup.KernelReachableAsync())
                {
                    _contactsFailed = true;
                    return;
                }

                var snap = await _sup.Contacts.LoadAsync(force);
                _contactsFailed = !snap.Ok;
                if (snap.Ok)
                {
                    _contacts = snap.Items;
                    foreach (var id in _chosen)
                    {
                        var info = _contacts.FirstOrDefault(x => x.Wxid == id);
                        if (info != null) _known[id] = info;
                    }
                    if (_contacts.Count == 0) _contactsFailed = false;
                }
            }
            finally
            {
                _loading = false;
                Render();
            }
        }

        private async void OnTogglePicker(object sender, RoutedEventArgs e)
        {
            if (!_ready || AllMuted) return;
            BtnAdd.IsEnabled = false;
            if (_contacts.Count == 0 || _contactsFailed)
            {
                BtnAdd.Content = "读取中…";
                await ReloadAsync(force: true);
                BtnAdd.Content = "添加会话";
            }

            _pending.Clear();
            PickerSearch.Text = "";
            PickerPanel.Visibility = Visibility.Visible;
            SearchBox.IsEnabled = false;
            RenderPicker();
            BtnAdd.IsEnabled = false;
            PickerSearch.Focus();
        }

        private void OnClosePicker(object sender, RoutedEventArgs e)
        {
            _pending.Clear();
            PickerPanel.Visibility = Visibility.Collapsed;
            Render();
            BtnAdd.Focus();
        }

        private void OnPickerSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || PickerPanel.Visibility != Visibility.Visible) return;
            RenderPicker();
        }

        private void OnPickToggled(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || !(cb.DataContext is PickRow row)) return;
            if (cb.IsChecked == true) _pending.Add(row.Wxid);
            else _pending.Remove(row.Wxid);
            BtnAddPicked.IsEnabled = _pending.Count > 0;
        }

        private async void OnAddPicked(object sender, RoutedEventArgs e)
        {
            if (_pending.Count == 0) return;
            foreach (var id in _pending)
            {
                if (_chosen.Contains(id)) continue;
                _chosen.Add(id);
                var info = _contacts.FirstOrDefault(x => x.Wxid == id);
                if (info != null) _known[id] = info;
            }
            _pending.Clear();
            PickerPanel.Visibility = Visibility.Collapsed;
            Render();
            await SaveAsync();
            BtnAdd.Focus();
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded || _rendering) return;
            RenderRows(AllMuted);
        }

        private async void OnRemove(object sender, RoutedEventArgs e)
        {
            if (!_ready || AllMuted) return;
            if (!(sender is FrameworkElement element) || !(element.DataContext is PolicyContactRow row)) return;
            _chosen.Remove(row.Wxid);
            Render();
            await SaveAsync();
        }

        private async void OnAllMuteChanged(object sender, RoutedEventArgs e)
        {
            if (!_ready || _applying) return;
            Render();
            await SaveAsync();
        }

        /// <summary>搜索框、添加按钮、列表一起跟随「全部会话」和名单数量。</summary>
        private void Render()
        {
            if (_rendering) return;
            _rendering = true;
            try
            {
                var all = AllMuted;
                if (all && PickerPanel.Visibility == Visibility.Visible)
                {
                    _pending.Clear();
                    PickerPanel.Visibility = Visibility.Collapsed;
                }
                BtnAdd.IsEnabled = !all && PickerPanel.Visibility != Visibility.Visible;
                SearchBox.IsEnabled = !all && PickerPanel.Visibility != Visibility.Visible;
                SearchBox.Visibility = !all && _chosen.Count > SearchThreshold
                    ? Visibility.Visible : Visibility.Collapsed;
                FilterHost.Visibility = SearchBox.Visibility;
                RenderRows(all);
                Changed?.Invoke();
            }
            finally
            {
                _rendering = false;
            }
        }

        private void RenderRows(bool all)
        {
            if (all)
            {
                ShowState("全部会话都已不处理（*）。取消上面的勾选，可恢复按名单处理。");
                return;
            }

            var q = SearchBox.Visibility == Visibility.Visible ? (SearchBox.Text ?? "").Trim() : "";
            var rows = _chosen
                .Select(BuildRow)
                .Where(x => q.Length == 0 || ContactDirectory.Matches(x, q))
                .ToList();
            List.ItemsSource = rows;

            if (_chosen.Count == 0)
            {
                ShowState(IsBlacklist
                    ? "没有黑名单会话。需要彻底忽略某个群时再加。"
                    : (_onlyMode
                        ? "白名单是空的。开启仅白名单后，所有会话都不会被处理。"
                        : "白名单是空的。加入会话后，只对它们主动搭话。"));
            }
            else if (rows.Count == 0)
            {
                ShowState("没有匹配「" + q + "」的会话。");
            }
            else
            {
                List.Visibility = Visibility.Visible;
                StateText.Visibility = Visibility.Collapsed;
            }
        }

        private PolicyContactRow BuildRow(string id)
        {
            _known.TryGetValue(id, out var info);
            return new PolicyContactRow
            {
                Wxid = id,
                Name = info != null && !string.IsNullOrEmpty(info.Display)
                    ? info.Display
                    : (id == "filehelper" ? "文件传输助手" : id),
                Remark = info?.Remark ?? "",
                IsGroup = info?.IsGroup
                    ?? id.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)
            };
        }

        private bool IsBlacklist => _allowAllMute;

        private void RenderPicker()
        {
            var q = (PickerSearch.Text ?? "").Trim();
            var rows = _contacts
                .Where(x => ContactDirectory.Matches(x, q))
                .Where(x => !_chosen.Contains(x.Wxid))
                .Select(x => new PickRow
                {
                    Wxid = x.Wxid,
                    Name = x.Name,
                    Remark = x.Remark,
                    IsGroup = x.IsGroup,
                    IsPicked = _pending.Contains(x.Wxid)
                })
                .ToList();
            PickerList.ItemsSource = rows;

            var message = _contactsFailed
                ? "联系人接口还没就绪，先启动内核再添加。"
                : (_contacts.Count == 0
                    ? "联系人列表是空的，启动核心并同步后再试。"
                    : (rows.Count == 0 ? "没有匹配「" + q + "」的联系人。" : ""));
            PickerState.Text = message;
            PickerState.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            PickerList.Visibility = !_contactsFailed && rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnAddPicked.IsEnabled = _pending.Count > 0;
        }

        private async Task SaveAsync()
        {
            if (_preview) return;
            var res = await _saver.SaveAsync(BuildPatch);
            if (!res.Ok)
            {
                Revert();
                Status?.Invoke("保存失败：" + Api.Message(res) + "，已恢复上次保存的名单。");
                return;
            }

            _savedIds = new List<string>(_chosen);
            _savedAllMute = AllMuted;
            Status?.Invoke("已保存 · " + DateTime.Now.ToString("HH:mm:ss"));
        }

        private IDictionary<string, object> BuildPatch()
            => new Dictionary<string, object>
            {
                [_configPath] = AllMuted ? new List<string> { "*" } : new List<string>(_chosen)
            };

        private void Revert()
        {
            _chosen.Clear();
            _chosen.AddRange(_savedIds);
            _applying = true;
            try { ChkAll.IsChecked = _savedAllMute; }
            finally { _applying = false; }
            Render();
        }

        private void ShowState(string text)
        {
            List.Visibility = Visibility.Collapsed;
            StateText.Text = text ?? "";
            StateText.Visibility = Visibility.Visible;
        }
    }
}
