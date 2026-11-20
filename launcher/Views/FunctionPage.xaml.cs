using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WhaleLauncher.Views
{
    /// <summary>
    /// 功能页只做页内分段，四个旧页面按需实例化；业务逻辑仍留在各自页面里。
    /// 页签上的状态摘要由各页通过 ITabStatus 提供，宿主只负责显示，不复制业务判断。
    /// </summary>
    public partial class FunctionPage : UserControl, IRefreshable
    {
        private readonly RuntimeSupervisor _sup;
        private readonly Dictionary<string, UserControl> _pages = new Dictionary<string, UserControl>();
        private readonly Dictionary<string, Button> _tabs = new Dictionary<string, Button>();
        private readonly Dictionary<string, TextBlock> _badges = new Dictionary<string, TextBlock>();
        private string _active;

        private static readonly (string Key, string Label)[] Entries =
        {
            ("sticker", "表情包"),
            ("moments", "朋友圈"),
            ("reminder", "主动提醒"),
            ("voice", "语音"),
        };

        public FunctionPage(RuntimeSupervisor sup)
        {
            InitializeComponent();
            _sup = sup;
            foreach (var entry in Entries)
            {
                var key = entry.Key;
                var tab = SegmentedTabs.Make(entry.Label, out var badge, (_, __) => Show(key));
                _tabs[key] = tab;
                _badges[key] = badge;
                TabsHost.Children.Add(tab);

                // 先建页面但不放进可视树：页签摘要需要各页的数据结构，
                // 页面自己的 Loaded 逻辑仍然只在真正切过去时才跑。
                var page = Create(key);
                _pages[key] = page;
                if (page is ITabStatus status)
                {
                    status.TabStatusChanged += () => Dispatcher.BeginInvoke(new Action(() =>
                        badge.Text = status.TabStatus));
                    badge.Text = status.TabStatus;
                }
            }
            Show("sticker");
            Loaded += async (_, __) =>
            {
                foreach (var page in _pages.Values)
                {
                    if (page is ITabStatus status) await status.RefreshTabStatusAsync();
                }
            };
        }

        private UserControl Create(string key)
        {
            UserControl page = key switch
            {
                "moments" => new MomentsPage(_sup),
                "reminder" => new ReminderPage(_sup),
                "voice" => new VoiceSendPage(_sup),
                _ => (UserControl)new StickerPage(_sup),
            };
            if (page is IEmbeddedPage embedded) embedded.Embedded = true;
            return page;
        }

        private void Show(string key)
        {
            if (_active == key) return;
            if (!_pages.TryGetValue(key, out var page)) return;

            _active = key;
            Host.Content = page;
            foreach (var kv in _tabs) SegmentedTabs.Mark(kv.Value, kv.Key == key);
            if (page is IRefreshable refresh) _ = refresh.RefreshAsync();
        }

        /// <summary>截图工具用：切到指定功能页签。</summary>
        public void PreviewSection(string key)
        {
            if (_pages.ContainsKey(key)) Show(key);
        }

        public async Task RefreshAsync()
        {
            if (Host.Content is IRefreshable refresh) await refresh.RefreshAsync();
        }
    }
}
