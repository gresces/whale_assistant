using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace WhaleLauncher.Views
{
    /// <summary>
    /// 聊天 = 人设 + 模型接入。两段内容原样嵌回旧页，只保留一个页头，
    /// 不再把模型/Key 藏在设置里绕一圈。
    /// </summary>
    public partial class ChatPage : UserControl, IRefreshable
    {
        private readonly RuntimeSupervisor _sup;
        private readonly Dictionary<string, UserControl> _pages = new Dictionary<string, UserControl>();
        private readonly Button[] _tabs;
        private string _active;

        public ChatPage(RuntimeSupervisor sup)
        {
            InitializeComponent();
            _sup = sup;

            var persona = SegmentedTabs.Make("人设与回复", (_, __) => Show("persona"));
            var model = SegmentedTabs.Make("模型与 Key", (_, __) => Show("model"));
            _tabs = new[] { persona, model };
            TabsHost.Children.Add(persona);
            TabsHost.Children.Add(model);
            Show("persona");
        }

        private void Show(string key)
        {
            if (_active == key) return;
            if (!_pages.TryGetValue(key, out var page))
            {
                page = key == "model"
                    ? new SettingsPage(_sup, SettingsPage.ViewMode.ModelOnly)
                    : new PersonaPage(_sup);
                if (page is IEmbeddedPage embedded) embedded.Embedded = true;
                _pages[key] = page;
            }

            _active = key;
            Host.Content = page;
            for (var i = 0; i < _tabs.Length; i++)
                SegmentedTabs.Mark(_tabs[i], (i == 0 && key == "persona") || (i == 1 && key == "model"));
            if (page is IRefreshable refresh) _ = refresh.RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            try
            {
                var s = await _sup.ProbeAsync();
                if (!s.NodeUp) ChatModelHint.Text = "内核未启动";
                else if (s.NeedsKey && !s.HasKey) ChatModelHint.Text = "模型 " + s.Model + " · 未填 Key";
                else ChatModelHint.Text = "模型 " + s.Model + " · 已就绪";
            }
            catch
            {
                ChatModelHint.Text = "模型状态未知";
            }

            if (Host.Content is IRefreshable refresh) await refresh.RefreshAsync();
        }
    }
}
