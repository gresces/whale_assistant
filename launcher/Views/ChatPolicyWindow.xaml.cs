using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WhaleLauncher.Views
{
    public partial class ChatPolicyWindow : System.Windows.Controls.UserControl
    {
        private readonly RuntimeSupervisor _sup;
        private readonly ContactCheckList _muted;
        private readonly ContactCheckList _whitelist;
        private readonly TaskCompletionSource<bool> _loaded =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private Button _tabMuted;
        private Button _tabWhitelist;
        private TextBlock _mutedBadge;
        private TextBlock _whitelistBadge;
        private bool _preview;

        /// <summary>截图用的加载完成信号；运行时不依赖它。</summary>
        public Task PreviewLoaded => _loaded.Task;

        public ChatPolicyWindow(RuntimeSupervisor sup)
        {
            InitializeComponent();
            _sup = sup;
            _muted = new ContactCheckList(sup, "group.mutedGroups", true);
            _whitelist = new ContactCheckList(sup, "care.whitelist", false);
            MutedHost.Content = _muted;
            WhitelistHost.Content = _whitelist;
            _muted.Status += SetStatus;
            _whitelist.Status += SetStatus;
            _muted.Changed += RefreshSummary;
            _whitelist.Changed += RefreshSummary;

            BuildTabs();
            UpdateWhitelistPurpose();
            Loaded += async (_, __) => await LoadAsync();
        }

        private void BuildTabs()
        {
            _tabMuted = SegmentedTabs.Make("黑名单", out _mutedBadge, (_, __) => ShowSection(false));
            _tabWhitelist = SegmentedTabs.Make("白名单", out _whitelistBadge, (_, __) => ShowSection(true));
            _tabMuted.Margin = new Thickness(0, 0, 6, 0);
            TabHost.Children.Add(_tabMuted);
            TabHost.Children.Add(_tabWhitelist);
            ShowSection(false);
        }

        private void ShowSection(bool whitelist)
        {
            if (MutedPanel == null) return;
            MutedPanel.Visibility = whitelist ? Visibility.Collapsed : Visibility.Visible;
            WhitelistPanel.Visibility = whitelist ? Visibility.Visible : Visibility.Collapsed;
            SegmentedTabs.Mark(_tabMuted, !whitelist);
            SegmentedTabs.Mark(_tabWhitelist, whitelist);
            RefreshSummary();
        }

        private async Task LoadAsync()
        {
            if (_preview)
            {
                _loaded.TrySetResult(true);
                return;
            }

            SetEditable(false);
            try
            {
                var res = await _sup.ReadConfigurationAsync("/api/config");
                if (!res.Ok)
                {
                    SetStatus("读取失败：" + Api.Message(res));
                    return;
                }

                var config = res.Prop("config");
                _muted.SetValue(Property(Property(config, "group"), "mutedGroups"));
                _whitelist.SetValue(Property(Property(config, "care"), "whitelist"));
                WhitelistOnly.IsChecked =
                    Property(Property(config, "wechat"), "whitelistOnly").ValueKind == JsonValueKind.True;
                UpdateWhitelistPurpose();
                SetEditable(true);

                await _muted.ReloadAsync();
                await _whitelist.ReloadAsync();
                RefreshSummary();
            }
            finally
            {
                // 读失败时保持禁用：此时名单是空的，放开编辑会让下一次保存覆盖掉磁盘上的真实配置。
                _loaded.TrySetResult(true);
            }
        }

        private async void OnWhitelistOnly(object sender, RoutedEventArgs e)
        {
            var enabled = WhitelistOnly.IsChecked == true;
            UpdateWhitelistPurpose();
            RefreshSummary();
            if (_preview) return;

            SetEditable(false);
            try
            {
                var res = await _sup.PatchConfigurationAsync(new Dictionary<string, object>
                {
                    ["wechat.whitelistOnly"] = enabled
                });
                if (!res.Ok)
                {
                    WhitelistOnly.IsChecked = !enabled;
                    UpdateWhitelistPurpose();
                    RefreshSummary();
                }
                SetStatus(res.Ok
                    ? "已保存 · " + DateTime.Now.ToString("HH:mm:ss")
                    : "保存失败：" + Api.Message(res));
            }
            finally { SetEditable(true); }
        }

        private void SetEditable(bool enabled)
        {
            WhitelistOnly.IsEnabled = enabled;
            MutedHost.IsEnabled = enabled;
            WhitelistHost.IsEnabled = enabled;
        }

        /// <summary>
        /// 顶栏摘要按优先级算：黑名单全选 → 全部不处理；仅白名单 → 可发言数；
        /// 否则所有会话都回复，白名单只决定主动搭话对象。
        /// </summary>
        private void RefreshSummary()
        {
            if (SummaryText == null || _muted == null || _whitelist == null) return;

            var black = _muted.SelectedCount;
            var white = _whitelist.SelectedCount;
            if (_muted.AllMuted)
                SummaryText.Text = "当前：全部会话都不处理";
            else if (WhitelistOnly.IsChecked == true)
                SummaryText.Text = white == 0
                    ? "当前：仅白名单 · 白名单为空，全部不处理"
                    : "当前：仅白名单 · " + white + " 个会话可发言 · 黑名单 " + black + " 个";
            else
                SummaryText.Text =
                    "当前：所有会话都回复 · 主动搭话 " + white + " 个 · 黑名单 " + black + " 个";

            if (_mutedBadge != null) _mutedBadge.Text = _muted.AllMuted ? "全部" : black.ToString();
            if (_whitelistBadge != null) _whitelistBadge.Text = white.ToString();
        }

        private void UpdateWhitelistPurpose()
        {
            var only = WhitelistOnly.IsChecked == true;
            WhitelistPurpose.Text = only
                ? "只有这些会话会被处理；空白名单等于全部不处理。"
                : "只用于挑选主动搭话对象，普通回复不受影响。";
            WhitelistTag.Visibility = only ? Visibility.Collapsed : Visibility.Visible;
            _whitelist?.SetScopeHint(only);
        }

        private void SetStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || SaveState == null) return;
            var failed = message.Contains("失败");
            ErrorLine.Text = failed ? message : "";
            ErrorLine.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
            SaveState.Text = failed ? "未保存" : message;
            SaveState.ToolTip = message;
            SaveState.SetResourceReference(TextBlock.ForegroundProperty, failed ? "Bad" : "Dim");
        }

        /// <summary>
        /// 截图预览：不启动内核、不读真实配置，直接用示例名字画两张状态图。
        /// 参数 blacklist=true 时停在黑名单段（拍影响预览）。
        /// </summary>
        public void PreviewSeed(bool whitelistOnly, bool blacklist)
        {
            _preview = true;
            _muted.PreviewSeed(new[]
            {
                new ContactInfo { Wxid = "demo-owner-room", Name = "业主群", IsGroup = true },
                new ContactInfo { Wxid = "demo-daigou", Name = "代购小号", IsGroup = false },
            }, false);
            _whitelist.PreviewSeed(new[]
            {
                new ContactInfo { Wxid = "demo-ayuan", Name = "阿螈", IsGroup = false },
                new ContactInfo { Wxid = "demo-xiaoyu", Name = "小雨", IsGroup = false },
                new ContactInfo { Wxid = "demo-pond", Name = "深海鱼塘", IsGroup = true },
            }, false);

            WhitelistOnly.IsChecked = whitelistOnly;
            UpdateWhitelistPurpose();
            ShowSection(!blacklist);
            SetStatus("已保存 · 14:32");
            RefreshSummary();
            _loaded.TrySetResult(true);
        }

        private static JsonElement Property(JsonElement obj, string key)
            => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value : default;
    }
}
