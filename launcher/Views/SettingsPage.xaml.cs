using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WhaleLauncher.Views
{
    /// <summary>一个可编辑的配置项。启动器是唯一界面，所以这里就是「全部设置」的清单。</summary>
    public sealed class FieldSpec
    {
        public string Path;
        public string Label;
        public string Kind = "text";      // bool / num / text / secret / select / multiline / slider / sliderInput
        public string Hint = "";
        public double Min = double.NaN;
        public double Max = double.NaN;
        // sliderInput 专用：右边输入框自己的范围（可以比滑块宽，比如滑块 2000–20000、输入框 1000–不设上限）
        public double InputMin = double.NaN;
        public double InputMax = double.NaN;
        public string[] Options;
        public bool IsInt = true;
        public string Unit = "";          // 拖动条右侧读数后缀，比如「 条/分钟」
    }

    public sealed class SectionSpec
    {
        public string Title;
        public string Note = "";
        public List<FieldSpec> Fields = new List<FieldSpec>();
    }

    public sealed class ProviderItem
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public override string ToString() => Label ?? Key ?? "";
    }

    public partial class SettingsPage : UserControl, IRefreshable
    {
        /// <summary>设置搜索里的一行：结果只读当前配置，不复制表单值。</summary>
        private sealed class SearchEntry
        {
            public string Section;
            public string Label;
            public string Hint = "";
            public string[] Keywords = Array.Empty<string>();
            public Func<string> Summary;
            public FrameworkElement Target;
            public Action Reveal;
        }
        public enum ViewMode
        {
            Full,
            ModelOnly,
        }

        private readonly RuntimeSupervisor _sup;
        private readonly ConfigSaver _saver;
        private readonly ViewMode _view;
        private readonly Action _checkUpdate;
        private readonly Func<string, UserControl> _pageFactory;
        private readonly Dictionary<string, UserControl> _subPages = new Dictionary<string, UserControl>();
        private Button _subPageReturnTarget;
        private readonly PageOperationLifetime _pageLifetime = new PageOperationLifetime();
        private readonly List<Action> _refreshBudgetHints = new List<Action>();
        private long _configurationRevision;
        private readonly Dictionary<string, Action<JsonElement>> _loaders = new Dictionary<string, Action<JsonElement>>();
        private readonly Dictionary<string, Func<Task<bool>>> _apiFieldSaves = new Dictionary<string, Func<Task<bool>>>();
        private readonly HashSet<string> _dirtyFields = new HashSet<string>();
        private readonly Dictionary<string, Action<string>> _secretMaskLoaders = new Dictionary<string, Action<string>>();
        private static string MaskApiKey(string value)
            => string.IsNullOrEmpty(value) ? "" : value.Length <= 4 ? "****" : value.Substring(0, 2) + "********" + value.Substring(value.Length - 2);
        private Action<JsonElement, Dictionary<string, JsonElement>> _loadModel;
        private Action<JsonElement> _loadVision;
        private TextBlock _adminSpellStatus;
        private Dictionary<string, JsonElement> _providers;
        private bool _loading;
        private bool _configLoaded;      // 已经成功全量回填过一次（M30：周期刷新据此判断要不要补）
        private bool _loadedOnline;
        private bool _loadingConfig;     // 全量回填在途，防止载入和定时器双跑
        private DateTime _lastLoad = DateTime.MinValue;

        // ── 设置搜索 / 分组摘要 ─────────────────────────────────
        private readonly List<SearchEntry> _searchEntries = new List<SearchEntry>();
        private readonly List<(TextBlock Badge, Func<string> Summary)> _sectionSummaries =
            new List<(TextBlock, Func<string>)>();
        private readonly Dictionary<string, FrameworkElement> _fieldRows =
            new Dictionary<string, FrameworkElement>();
        private JsonElement _configRoot;
        private readonly Dictionary<string, JsonElement> _configOverrides =
            new Dictionary<string, JsonElement>();
        private StackPanel _searchResultsHost;
        private readonly List<FrameworkElement> _lastSearchHits = new List<FrameworkElement>();
        private readonly TaskCompletionSource<bool> _previewLoaded =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PreviewLoaded => _previewLoaded.Task;
        private Expander _advancedExpander;
        private Action<int> _activateAdvancedTab;
        private List<(string Path, JsonElement Value)> _undoReset;
        private DispatcherTimer _undoTimer;

        // 高级 → 高级与其他的出厂值。只用于状态摘要里的「N 项已改」，
        // 真正写回仍由内核的 /api/config/reset 决定，别拿这份替代恢复逻辑。
        private static readonly Dictionary<string, object> AdvancedDefaults = new Dictionary<string, object>
        {
            ["memory.dialogueBudget"] = 5500,
            ["env.timezone"] = "",
            ["server.host"] = "127.0.0.1",
            ["server.port"] = 8787,
            ["log.level"] = "info",
            ["storage.mediaKeepDays"] = 7,
            ["storage.mediaMaxMB"] = 500,
        };

        private static readonly string[][] SynonymGroups =
        {
            new[] { "通知", "推送", "提醒", "提示" },
            new[] { "深色", "暗色", "夜间", "跟随系统" },
            new[] { "代理", "网络", "联网" },
            new[] { "语音", "识别", "转写", "asr" },
            new[] { "记忆", "上下文", "回忆" },
            new[] { "预算", "token", "字数" },
        };
        public SettingsPage(RuntimeSupervisor sup, ViewMode view = ViewMode.Full,
            Action checkUpdate = null, Func<string, UserControl> pageFactory = null)
        {
            InitializeComponent();
            _sup = sup;
            _view = view;
            _checkUpdate = checkUpdate;
            _pageFactory = pageFactory;
            _saver = new ConfigSaver(sup);
            if (view == ViewMode.ModelOnly)
            {
                PageHeader.Visibility = Visibility.Collapsed;
                PageRoot.Margin = new Thickness(0);
            }
            BuildOnce();
            Loaded += async (_, __) =>
            {
                _pageLifetime.Activate();
                var generation = _pageLifetime.Capture();
                MainScroll.ScrollToTop();
                // 页面重新挂载时不要被上一轮 5 秒节流挡住：首次打开常常刚好碰上
                // ContentControl 换页导致上一轮 generation 失效，否则表单会空白几秒。
                if (!_configLoaded) _lastLoad = DateTime.MinValue;
                await RefreshAsync();
                if (_pageLifetime.IsCurrent(generation))
                    foreach (var refresh in _refreshBudgetHints) refresh();
            };
            Unloaded += (_, __) => _pageLifetime.Deactivate();
        }

        // ── 全部设置清单（启动器是唯一界面）────────────────────────
        private static List<SectionSpec> Specs() => new List<SectionSpec>
        {
            new SectionSpec { Title = "群聊与回复", Fields =
            {
                new FieldSpec { Path = "group.requireAt", Label = "必须 @ 她才回", Kind = "bool", Hint = "关掉 = 群里不 @ 也会接，但按下面的插话概率抽，不是每条都回" },
                new FieldSpec { Path = "group.freeChat", Label = "允许她自己插话", Kind = "bool", Hint = "开着时没点名的消息也按概率抽一句；@ 她的不受影响" },
                new FieldSpec { Path = "group.freeChatChance", Label = "插话概率", Kind = "num", Min = 0, Max = 1, IsInt = false, Hint = "没点名的消息按这个概率接一句；0.06 = 百分之六，约 16 条回 1 条；填 1 = 每条都回，仍受冷却和每分钟上限约束" },
                new FieldSpec { Path = "group.cooldownSec", Label = "两次回复冷却(秒)", Kind = "num", Min = 0, Max = 3600 },
                new FieldSpec { Path = "group.maxRepliesPerMinute", Label = "每分钟最多回几条", Kind = "slider", Min = 5, Max = 30, Unit = " 条/分钟", Hint = "推荐 10–20 条" },
                new FieldSpec { Path = "group.masterAlwaysReply", Label = "主要联系人在群里说话永远回", Kind = "bool" },
                new FieldSpec { Path = "group.engagedMinutes", Label = "接话后保持参与(分钟)", Kind = "num", Min = 1, Max = 1440 },
                new FieldSpec { Path = "group.silentMinutes", Label = "冷场多久算结束(分钟)", Kind = "num", Min = 1, Max = 10080 },
                new FieldSpec { Path = "group.muteDefaultMinutes", Label = "免打扰默认时长(分钟)", Kind = "num", Min = 1, Max = 1440 },
                new FieldSpec { Path = "chat.messageSettleMs", Label = "连续消息等待(毫秒)", Kind = "num", Min = 0, Max = 10000, Hint = "私信和群聊共用：最后一条消息后安静这么久再统一处理；默认 1500" },
                new FieldSpec { Path = "chat.groupMaxBatchWaitMs", Label = "群聊最多等待(毫秒)", Kind = "num", Min = 0, Max = 60000, Hint = "群聊从批次第一条开始计时，到点强制处理；不能被后续消息无限重置；默认 5000" },
            }},
            new SectionSpec { Title = "微信与发送", Fields =
            {
                new FieldSpec { Path = "wechat.dbDir", Label = "微信文件保存位置", Kind = "text", Hint = "留空 = 自动找；填到 xwechat_files 这一层" },
                new FieldSpec { Path = "wechat.staleAfterSec", Label = "多久没消息算去忙(秒)", Kind = "num", Min = 60, Max = 86400 },
                new FieldSpec { Path = "wechat.ignoreSenders", Label = "这些会话永不回复", Kind = "multiline", Hint = "一行一个（微信团队、订阅号…）" },
                new FieldSpec { Path = "wechat.pipelineWorkers", Label = "同时处理几个会话", Kind = "num", Min = 1, Max = 8 },
                new FieldSpec { Path = "wechat.pipelineQueueLimit", Label = "单个会话最多积压几条", Kind = "num", Min = 1, Max = 32, Hint = "超出就丢掉最早的一条；默认 8" },
            }},
            new SectionSpec { Title = "联网与检索", Fields =
            {
                // 本地搜索强制开启，不再提供“联网搜索”开关；API 搜索在“API 接入”页签里。
                new FieldSpec { Path = "websearch.city", Label = "搜索城市", Kind = "text" },
                new FieldSpec { Path = "websearch.timeoutMs", Label = "搜索超时(毫秒)", Kind = "num", Min = 1000, Max = 30000 },
                new FieldSpec { Path = "links.enabled", Label = "解析聊天里的链接", Kind = "bool" },
                new FieldSpec { Path = "links.timeoutMs", Label = "链接超时(毫秒)", Kind = "num", Min = 1000, Max = 30000 },
                new FieldSpec { Path = "links.cacheMinutes", Label = "链接缓存(分钟)", Kind = "num", Min = 0, Max = 1440 },
            }},
            new SectionSpec { Title = "对话与记忆", Note = "实现与维护选项，不属于角色性格。按需展开调整；原有配置保持，保存后按各项说明生效。", Fields =
            {
                new FieldSpec { Path = "features.envContext", Label = "提供环境信息", Kind = "bool", Hint = "向模型提供本机环境、时区与已配置地点；关闭后不提供环境块" },
                new FieldSpec { Path = "features.relations", Label = "加载群聊关系", Kind = "bool", Hint = "按需提供人物关系资料，不改变角色性格" },
                new FieldSpec { Path = "features.groupAddressGuard", Label = "群聊发言对象检查", Kind = "bool", Hint = "避免把群友之间的话认成对机器人说的；建议保持开启" },
                new FieldSpec { Path = "features.fightQuoteCheck", Label = "反击原话核对", Kind = "bool", Hint = "反击前核对原话依据，不等于开启顶嘴人格" },
                new FieldSpec { Path = "features.staticSystem", Label = "稳定提示词前缀", Kind = "bool", Hint = "只调整本轮状态的位置以便服务商缓存，不改变人格与设置含义" },
                new FieldSpec { Path = "features.memorySnapshot", Label = "自动记忆快照", Kind = "bool", Hint = "启动和定时创建备份；关闭不删除已有备份" },
                new FieldSpec { Path = "features.mediaCleanup", Label = "启动时清理媒体缓存", Kind = "bool", Hint = "按缓存保留策略清理；下次启动生效" },
            }},
            new SectionSpec { Title = "高级与其他", Note = "改完要重启的项都在这里（对话预算和时区例外，改完立刻生效）。", Fields =
            {
                new FieldSpec { Path = "memory.dialogueBudget", Label = "对话预算(token)", Kind = "sliderInput", Min = 2000, Max = 20000, InputMin = 1000, Hint = "每轮带多少对话内容（不含人设）：默认 5,500。记忆和原文按比例分，预算越大带得越多；滑块 2,000–20,000，下面四个胶囊是一键跳档，右边输入框可以填到 1,000 以上。" },
                new FieldSpec { Path = "env.timezone", Label = "时区", Kind = "text", Hint = "IANA 时区，如 Asia/Shanghai；留空 = 系统时区。日记时间、主动搭话时间段、提醒、朋友圈夜间判断和聊天时间戳都按它算。" },
                new FieldSpec { Path = "server.host", Label = "监听地址", Kind = "text", Hint = "只建议 127.0.0.1；同 WiFi 访问只对桥接和接口有效，没有网页界面（改完重启）" },
                new FieldSpec { Path = "server.port", Label = "本机端口", Kind = "selectnum", Options = new[] { "8787", "9191", "1919", "7878" }, Hint = "默认 8787；只在这四个里选，改完重启内核生效" },
                new FieldSpec { Path = "log.level", Label = "日志级别", Kind = "select", Options = new[] { "debug", "info", "error" }, Hint = "界面 / 控制台用哪一档；日志文件始终记 debug 全量（data/whale.log、data/wechat-bridge-4x.log），改完重启生效" },
                new FieldSpec { Path = "storage.mediaKeepDays", Label = "媒体缓存保留几天", Kind = "num", Min = 0, Max = 90 },
                new FieldSpec { Path = "storage.mediaMaxMB", Label = "媒体缓存上限(MB)", Kind = "num", Min = 0, Max = 10000 },
            }},
        };

        private void BuildOnce()
        {
            SectionsHost.Children.Clear();
            _searchEntries.Clear();
            _sectionSummaries.Clear();

            if (_view == ViewMode.ModelOnly)
            {
                AddSection(BuildModelSection());
                return;
            }

            // 搜索结果卡固定在内容区最上面；搜索框常驻页头。输入即筛，Esc / 清空恢复。
            _searchResultsHost = new StackPanel { Visibility = Visibility.Collapsed };
            SectionsHost.Children.Add(_searchResultsHost);

            AddSection(BuildAppearanceSection());
            AddSection(BuildAdvancedSection());
            AddSection(BuildEntriesSection());
            AddSection(BuildAboutSection());
        }

        /// <summary>卡片直接按顺序堆进单列滚动区；4px 节奏，卡片之间 12px。</summary>
        private void AddSection(Border card)
        {
            card.Margin = new Thickness(0, 0, 0, 12);
            SectionsHost.Children.Add(card);
        }

        /// <summary>管理员魔咒属于低频项，收进「高级」折叠，不再单独占一张卡。</summary>
        private StackPanel BuildAdminSpellBlock()
        {
            var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            body.Children.Add(new TextBlock
            {
                Text = "管理员魔咒",
                Style = (Style)FindResource("H2"),
            });
            var adminNote = new TextBlock
            {
                Text = "主要联系人私聊设置，可在同一私聊发管理命令。",
                FontSize = 14,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8),
            };
            adminNote.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
            body.Children.Add(adminNote);
            _adminSpellStatus = new TextBlock { Style = (Style)FindResource("Small") };
            body.Children.Add(_adminSpellStatus);
            var password = new PasswordBox();
            body.Children.Add(Row("管理员口令", password, "设置后只显示是否已设置。", false, 150));
            body.Children.Add(BuildField(new FieldSpec { Path = "admin.minutes", Label = "有效期（分钟）", Kind = "num", Min = 1, Max = 1440 }));
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var save = new Button { Content = "保存魔咒", Style = (Style)FindResource("Ghost") };
            var clear = new Button { Content = "清除魔咒", Style = (Style)FindResource("Ghost") };
            save.Click += async (_, __) =>
            {
                var spell = password.Password.Trim();
                if (spell.Length == 0) return;
                if (await PatchAsync("admin.spell", spell))
                {
                    password.Clear();
                    _adminSpellStatus.Text = "已设置";
                }
            };
            clear.Click += async (_, __) =>
            {
                if (await PatchAsync("admin.spell", ""))
                {
                    password.Clear();
                    _adminSpellStatus.Text = "未设置";
                }
            };
            actions.Children.Add(save);
            actions.Children.Add(clear);
            body.Children.Add(actions);
            return body;
        }

        /// <summary>
        /// 高级设置只在底部留一个折叠入口，默认收起；里面沿用原来的分组页签，
        /// 点哪一组才展开哪一组，避免一进设置页就铺满低频项。
        /// </summary>
        private Border BuildAdvancedSection()
        {
            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                Padding = new Thickness(16),
            };
            var expander = new Expander
            {
                Header = "高级设置",
                IsExpanded = false,
            };
            expander.SetResourceReference(Control.ForegroundProperty, "Text");
            _advancedExpander = expander;
            var content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            expander.Content = content;
            card.Child = expander;

            var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
            content.Children.Add(strip);

            var specs = Specs();
            var tabs = new Button[specs.Count];
            var panels = new StackPanel[specs.Count];
            var active = -1;

            // 搜索点中某一组时用 Activate：和页签点击不同，这里不能把已展开的组再收起来。
            void Activate(int idx)
            {
                for (var i = 0; i < panels.Length; i++)
                {
                    panels[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
                    MarkTab(tabs[i], i == idx);
                }
                active = idx;
            }

            void Select(int idx)
            {
                if (active == idx)
                {
                    panels[idx].Visibility = Visibility.Collapsed;
                    MarkTab(tabs[idx], false);
                    active = -1;
                    return;
                }
                Activate(idx);
            }

            for (var i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                var tab = MakeTab(spec.Title, out var badge);
                var panel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
                if (!string.IsNullOrEmpty(spec.Note))
                    panel.Children.Add(new TextBlock
                    {
                        Text = spec.Note,
                        Style = (Style)FindResource("Small"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 8),
                    });
                var idx = i;
                foreach (var f in spec.Fields)
                {
                    var row = BuildField(f);
                    _fieldRows[f.Path] = row;
                    panel.Children.Add(row);
                    RegisterEntry(spec.Title, f.Label, f.Hint, FieldKeywords(f), row,
                        () => DescribeField(f),
                        () => { expander.IsExpanded = true; Activate(idx); });
                }
                // 高级与其他 → 记忆：旧版（3.x）记忆的一键入账，放在这一组最后。
                if (spec.Title == "高级与其他") panel.Children.Add(BuildMemoryImportBlock());

                // 这一组「恢复默认」：只写回这个页签里的字段，默认值由内核给
                var foot = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                var resetTab = new Button { Content = "这一组恢复默认", Style = (Style)FindResource("Ghost") };
                resetTab.Click += async (s, e) => await ResetFieldsAsync(spec.Title, spec.Fields.Select(f => f.Path));
                foot.Children.Add(resetTab);
                panel.Children.Add(foot);
                tabs[i] = tab;
                panels[i] = panel;
                strip.Children.Add(tab);
                content.Children.Add(panel);
                tab.Click += (s, e) => Select(idx);

                var sectionTitle = spec.Title;
                _sectionSummaries.Add((badge, () => SummaryForSection(sectionTitle)));
            }

            var divider = new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(0, 14, 0, 0) };
            content.Children.Add(divider);
            content.Children.Add(BuildAdminSpellBlock());
            _activateAdvancedTab = Activate;

            return card;
        }

        // ── 外观：深浅色 + 背景图（只影响启动器）────────────────
        private Border BuildAppearanceSection()
        {
            var card = Card("外观", "只影响启动器界面。", out var body);

            // 三态主题：浅色 / 深色 / 跟随系统。默认跟随系统，用户手动选过后不再跟随。
            var themeBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var lightTheme = SegmentedTabs.Make("浅色", (_, __) => Appearance.SetTheme("light"));
            var darkTheme = SegmentedTabs.Make("深色", (_, __) => Appearance.SetTheme("dark"));
            var systemTheme = SegmentedTabs.Make("跟随系统", (_, __) => Appearance.SetTheme("system"));
            systemTheme.ToolTip = "随 Windows 的应用模式实时切换；手动选浅色 / 深色后固定，不再跟随";
            themeBar.Children.Add(lightTheme);
            themeBar.Children.Add(darkTheme);
            themeBar.Children.Add(systemTheme);
            var themeRow = Row("主题", themeBar, "", false);
            body.Children.Add(themeRow);
            RegisterEntry("外观", "主题", "深色 暗色 夜间 跟随系统 系统",
                new[] { "主题", "深色", "暗色", "夜间", "跟随系统", "外观" }, themeRow,
                () => Appearance.ThemeDescription, null);

            var preview = new Border
            {
                Width = 216,
                Height = 116,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8)
            };
            preview.SetResourceReference(Border.BackgroundProperty, "Surface3");
            preview.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var img = new Image { Stretch = Stretch.UniformToFill };
            var empty = new TextBlock
            {
                Text = "未设置背景图",
                Style = (Style)FindResource("Small"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var previewGrid = new Grid();
            previewGrid.Children.Add(img);
            previewGrid.Children.Add(empty);
            preview.Child = previewGrid;

            var pathText = new TextBlock
            {
                Style = (Style)FindResource("Small"),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 560,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var pick = new Button { Content = "选择图片…", Style = (Style)FindResource("Ghost"), ToolTip = "支持 JPG / PNG / BMP" };
            var clear = new Button { Content = "清除背景", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
            actions.Children.Add(pick);
            actions.Children.Add(clear);

            var hint = new TextBlock
            {
                Style = (Style)FindResource("Small"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var panel = new StackPanel();
            panel.Children.Add(preview);
            panel.Children.Add(pathText);
            panel.Children.Add(actions);
            panel.Children.Add(hint);
            var backdropRow = Row("背景图片", panel, "", true);
            body.Children.Add(backdropRow);
            RegisterEntry("外观", "背景图片", "背景图 壁纸 图片 填充 模糊 透明度",
                new[] { "背景", "图片", "壁纸" }, backdropRow,
                () => Appearance.HasBackdrop ? Path.GetFileName(Appearance.Backdrop) : "纯色背景", null);

            // 外观细节：默认只看主题和背景图入口，调色/模糊等低频项收起。
            var detailsExpander = new Expander
            {
                Header = "外观细节",
                IsExpanded = false,
                Margin = new Thickness(0, 12, 0, 0),
            };
            detailsExpander.SetResourceReference(Control.ForegroundProperty, "Text");
            var details = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            detailsExpander.Content = details;
            body.Children.Add(detailsExpander);

            var fit = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            fit.Items.Add("等比裁剪（铺满）");
            fit.Items.Add("拉伸铺满");
            fit.Items.Add("等比完整（留边）");
            fit.SelectedIndex = Appearance.BackdropFit switch { "stretch" => 1, "uniform" => 2, _ => 0 };
            fit.SelectionChanged += (_, __) =>
            {
                if (fit.SelectedIndex < 0) return;
                Appearance.SetBackdropFit(fit.SelectedIndex switch { 1 => "stretch", 2 => "uniform", _ => "fill" });
            };
            var fitRow = Row("背景填充", fit, "", true);
            details.Children.Add(fitRow);
            RegisterEntry("外观", "背景填充", "背景 铺满 拉伸 留边",
                new[] { "背景", "填充", "铺满" }, fitRow,
                () => Appearance.BackdropFit switch { "stretch" => "拉伸铺满", "uniform" => "等比完整", _ => "等比裁剪" },
                () => detailsExpander.IsExpanded = true);

            // 模糊 / 透明度滑块：值直接写进 launcher-ui.json，拖一下就能看到效果。
            var blurValue = ValueLabel();
            var blurSlider = new Slider
            {
                Style = (Style)FindResource("BudgetSlider"),
                Minimum = 0,
                Maximum = 60,
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            blurSlider.Value = Appearance.BackdropBlur;
            blurValue.Text = Math.Round(Appearance.BackdropBlur) + " px";
            blurSlider.ValueChanged += (_, e) =>
            {
                var v = Math.Round(e.NewValue);
                blurValue.Text = v + " px";
                Appearance.SetBackdropBlur(v);
            };
            var blurRow = Row("背景模糊", SliderWithValue(blurSlider, blurValue), "", true);
            details.Children.Add(blurRow);
            RegisterEntry("外观", "背景模糊", "背景 模糊 高斯",
                new[] { "背景", "模糊" }, blurRow,
                () => Math.Round(Appearance.BackdropBlur) + " px",
                () => detailsExpander.IsExpanded = true);

            var opacityValue = ValueLabel();
            var opacitySlider = new Slider
            {
                Style = (Style)FindResource("BudgetSlider"),
                Minimum = 0,
                Maximum = 100,
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            opacitySlider.Value = Math.Round((1 - Appearance.BackdropOpacity) * 100);
            opacityValue.Text = Math.Round(opacitySlider.Value) + "%";
            opacitySlider.ValueChanged += (_, e) =>
            {
                var v = Math.Round(e.NewValue);
                opacityValue.Text = v + "%";
                Appearance.SetBackdropOpacity(1 - v / 100.0);
            };
            var opacityRow = Row("背景透明度", SliderWithValue(opacitySlider, opacityValue), "", true);
            details.Children.Add(opacityRow);
            RegisterEntry("外观", "背景透明度", "背景 透明度 透明",
                new[] { "背景", "透明度" }, opacityRow,
                () => Math.Round((1 - Appearance.BackdropOpacity) * 100) + "%",
                () => detailsExpander.IsExpanded = true);

            var surfaceValue = ValueLabel();
            var surfaceSlider = new Slider
            {
                Style = (Style)FindResource("BudgetSlider"),
                Minimum = 0,
                Maximum = 100,
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            surfaceSlider.Value = Math.Round(Appearance.SurfaceOpacity * 100);
            surfaceValue.Text = Math.Round(surfaceSlider.Value) + "%";
            surfaceSlider.ValueChanged += (_, e) =>
            {
                var v = Math.Round(e.NewValue);
                surfaceValue.Text = v + "%";
                Appearance.SetSurfaceOpacity(v / 100.0);
            };
            var surfaceRow = Row("面板不透明度", SliderWithValue(surfaceSlider, surfaceValue), "", true);
            details.Children.Add(surfaceRow);
            RegisterEntry("外观", "面板不透明度", "面板 卡片 不透明度 玻璃",
                new[] { "面板", "不透明度", "卡片" }, surfaceRow,
                () => Math.Round(Appearance.SurfaceOpacity * 100) + "%",
                () => detailsExpander.IsExpanded = true);

            // ── 配色：莫奈取色 + 手动覆盖（顶栏固定，不参与）──
            var monetCheck = new CheckBox
            {
                Content = "从背景图自动取色（莫奈）",
                IsChecked = Appearance.MonetEnabled,
                VerticalAlignment = VerticalAlignment.Center,
            };
            monetCheck.Click += (_, __) => Appearance.SetMonet(monetCheck.IsChecked == true);
            var remonet = new Button { Content = "重新取色", Style = (Style)FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
            remonet.Click += (_, __) => Appearance.RefreshMonet();
            var monetPanel = new WrapPanel();
            monetPanel.Children.Add(monetCheck);
            monetPanel.Children.Add(remonet);
            var monetRow = Row("莫奈取色", monetPanel, "", true);
            details.Children.Add(monetRow);
            RegisterEntry("外观", "莫奈取色", "背景 自动取色 主题色",
                new[] { "莫奈", "取色", "配色" }, monetRow,
                () => Appearance.MonetEnabled ? "开" : "关",
                () => detailsExpander.IsExpanded = true);

            var accentRow = Row("主题色", BuildColorRow("accent", "主题色"), "", true);
            var tintRow = Row("遮罩色", BuildColorRow("tint", "遮罩色"), "", true);
            var textColorRow = Row("文字色", BuildColorRow("text", "文字色"), "", true);
            details.Children.Add(accentRow);
            details.Children.Add(tintRow);
            details.Children.Add(textColorRow);
            RegisterEntry("外观", "主题色", "强调色 品牌色 配色",
                new[] { "主题色", "强调色", "配色" }, accentRow,
                () => Appearance.AccentOverride.Length > 0 ? Appearance.ToHex(Appearance.EffectiveColor("accent")) : "自动",
                () => detailsExpander.IsExpanded = true);
            RegisterEntry("外观", "遮罩色", "背景蒙版 遮罩 配色",
                new[] { "遮罩色", "蒙版", "配色" }, tintRow,
                () => Appearance.TintOverride.Length > 0 ? Appearance.ToHex(Appearance.EffectiveColor("tint")) : "自动",
                () => detailsExpander.IsExpanded = true);
            RegisterEntry("外观", "文字色", "文字 颜色 配色",
                new[] { "文字色", "字体颜色", "配色" }, textColorRow,
                () => Appearance.TextOverride.Length > 0 ? Appearance.ToHex(Appearance.EffectiveColor("text")) : "自动",
                () => detailsExpander.IsExpanded = true);

            var haloCheck = new CheckBox
            {
                Content = "文字描边（觉得发光可以关掉）",
                IsChecked = Appearance.TextHaloEnabled,
                VerticalAlignment = VerticalAlignment.Center,
            };
            haloCheck.Click += (_, __) => Appearance.SetTextHalo(haloCheck.IsChecked == true);
            var haloRow = Row("文字描边", haloCheck, "", true);
            details.Children.Add(haloRow);
            RegisterEntry("外观", "文字描边", "发光 描边 文字",
                new[] { "文字", "描边", "发光" }, haloRow,
                () => Appearance.TextHaloEnabled ? "开" : "关",
                () => detailsExpander.IsExpanded = true);

            var resetPalette = new Button { Content = "恢复自动配色", Style = (Style)FindResource("Ghost") };
            resetPalette.Click += (_, __) => Appearance.ResetPalette();
            var resetPaletteRow = Row("配色重置", resetPalette, "", true);
            details.Children.Add(resetPaletteRow);
            RegisterEntry("外观", "配色重置", "恢复自动配色 主题色 遮罩色",
                new[] { "配色", "重置", "恢复默认" }, resetPaletteRow,
                () => "自动", () => detailsExpander.IsExpanded = true);

            var hintIsError = false;
            void PaintTheme()
            {
                SegmentedTabs.Mark(lightTheme, Appearance.Theme == "light");
                SegmentedTabs.Mark(darkTheme, Appearance.Theme == "dark");
                SegmentedTabs.Mark(systemTheme, Appearance.Theme == "system");
            }
            void Refresh()
            {
                PaintTheme();
                monetCheck.IsChecked = Appearance.MonetEnabled;
                haloCheck.IsChecked = Appearance.TextHaloEnabled;
                monetCheck.IsEnabled = Appearance.HasBackdrop;
                remonet.IsEnabled = Appearance.HasBackdrop;
                var bmp = Appearance.LoadBackdrop();
                var hasBackdrop = bmp != null;
                img.Source = bmp;
                img.Opacity = Appearance.BackdropOpacity;
                if (bmp == null)
                {
                    img.Effect = null;
                }
                else
                {
                    // 复用同一个 BlurEffect：拖滑块时 Changed 会高频触发，别每次 new。
                    if (img.Effect is not BlurEffect blur)
                    {
                        blur = new BlurEffect { KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
                        img.Effect = blur;
                    }
                    blur.Radius = Appearance.BackdropBlur * 0.35;
                }
                img.Visibility = hasBackdrop ? Visibility.Visible : Visibility.Collapsed;
                empty.Visibility = hasBackdrop ? Visibility.Collapsed : Visibility.Visible;
                preview.Visibility = hasBackdrop ? Visibility.Visible : Visibility.Collapsed;
                pathText.Visibility = hasBackdrop ? Visibility.Visible : Visibility.Collapsed;
                clear.Visibility = hasBackdrop ? Visibility.Visible : Visibility.Collapsed;
                pick.Content = hasBackdrop ? "更换图片…" : "选择图片…";
                hint.Visibility = (hasBackdrop || hintIsError) && !string.IsNullOrEmpty(hint.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
                clear.IsEnabled = hasBackdrop;
                blurSlider.IsEnabled = Appearance.HasBackdrop;
                opacitySlider.IsEnabled = Appearance.HasBackdrop;
                surfaceSlider.IsEnabled = Appearance.HasBackdrop;
                pathText.Text = string.IsNullOrEmpty(Appearance.Backdrop)
                    ? "当前使用纯色背景。"
                    : "当前：" + Appearance.Backdrop;
                RefreshSectionSummaries();
                RefreshSearchIfActive();
            }

            pick.Click += (_, __) =>
            {
                var file = AppFilePicker.Open("选择背景图片", ".jpg;.jpeg;.png;.bmp");
                if (file == null) return;
                if (Appearance.TrySetBackdrop(file, out var err))
                {
                    hintIsError = false;
                    hint.Text = "已应用。";
                    hint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                }
                else
                {
                    hintIsError = true;
                    hint.Text = err;
                    hint.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                }
                Refresh();
            };

            clear.Click += (_, __) =>
            {
                hintIsError = false;
                Appearance.ClearBackdrop();
                hint.Text = "";
                Refresh();
            };

            Appearance.Changed += Refresh;
            Refresh();
            return card;
        }

        // 官网和赞助入口收进设置 → 关于；链接写死，断网时浏览器自行提示。
        private const string OfficialSite = "https://bot.zyyc.cc/";
        private const string SponsorPage = "https://afdian.com/a/Banan1ce";

        private Border BuildEntriesSection()
        {
            var card = Card("诊断工具", "检查运行状态、日志与本机数据。", out var body);

            var diagnostic = new Grid();
            diagnostic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            diagnostic.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var diagnosticText = new StackPanel();
            diagnosticText.Children.Add(new TextBlock { Text = "诊断", Style = (Style)FindResource("H2") });
            diagnostic.Children.Add(diagnosticText);
            var diagnosticButton = new Button { Content = "打开诊断", Style = (Style)FindResource("Ghost"), VerticalAlignment = VerticalAlignment.Center };
            diagnosticButton.Click += (_, __) =>
            {
                _subPageReturnTarget = diagnosticButton;
                OpenSubPage("诊断", "诊断");
            };
            Grid.SetColumn(diagnosticButton, 1);
            diagnostic.Children.Add(diagnosticButton);
            body.Children.Add(diagnostic);
            RegisterEntry("诊断工具", "打开诊断", "运行状态、日志、语音识别与通知排查",
                new[] { "诊断", "日志", "运行状态", "自检", "语音", "识别", "通知", "推送", "内核", "桥接" },
                diagnostic, () => "运行状态 · 日志 · 本机数据", null);

            return card;
        }

        private Border BuildAboutSection()
        {
            var card = Card("关于", "版本、更新与项目链接。", out var body);

            var version = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            version.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            version.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var versionText = new StackPanel();
            versionText.Children.Add(new TextBlock { Text = "助手", Style = (Style)FindResource("H2") });
            versionText.Children.Add(new TextBlock
            {
                Text = "v" + UpdateManifestService.LocalVersion(_sup.BaseDir),
                Style = (Style)FindResource("Small"),
                Margin = new Thickness(0, 2, 0, 0),
            });
            version.Children.Add(versionText);
            if (_checkUpdate != null)
            {
                var update = new Button { Content = "检查更新", Style = (Style)FindResource("Ghost"), VerticalAlignment = VerticalAlignment.Center };
                update.Click += (_, __) => _checkUpdate();
                Grid.SetColumn(update, 1);
                version.Children.Add(update);
            }
            body.Children.Add(version);
            RegisterEntry("关于", "版本与更新", "检查更新 升级 版本号",
                new[] { "版本", "更新", "升级" }, version,
                () => "v" + UpdateManifestService.LocalVersion(_sup.BaseDir), null);

            var links = new WrapPanel();
            var website = new Button { Content = "官网", Style = (Style)FindResource("Ghost") };
            website.Click += (_, __) => OpenExternal(OfficialSite, "打开官网失败");
            var sponsor = new Button { Content = "赞助", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            sponsor.Click += (_, __) => OpenExternal(SponsorPage, "打开赞助页面失败");
            links.Children.Add(website);
            links.Children.Add(sponsor);
            body.Children.Add(links);
            RegisterEntry("关于", "官网", "项目主页 链接",
                new[] { "官网", "主页", "网站" }, links, () => "bot.zyyc.cc", null);
            RegisterEntry("关于", "赞助", "支持 项目 链接",
                new[] { "赞助", "打赏", "支持" }, links, () => "afdian.com", null);

            return card;
        }

        private void OpenExternal(string url, string failureTitle)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppDialog.Show(Window.GetWindow(this),
                    failureTitle + "：\n" + ex.Message + "\n\n可以手动在浏览器打开：" + url, failureTitle);
            }
        }

        private void OpenSubPage(string key, string title)
        {
            if (_pageFactory == null) return;
            if (!_subPages.TryGetValue(key, out var page))
            {
                page = _pageFactory(key);
                if (page is IEmbeddedPage embedded) embedded.Embedded = true;
                _subPages[key] = page;
            }
            SubPageContent.Content = page;
            SettingsView.Visibility = Visibility.Collapsed;
            SubPageView.Visibility = Visibility.Visible;
        }

        private void OnBackFromSubPage(object sender, RoutedEventArgs e)
        {
            SubPageView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Visible;
            var target = _subPageReturnTarget;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (target == null || !target.IsVisible) return;
                target.BringIntoView();
                target.Focus();
            }));
        }

        // ── 设置搜索 ────────────────────────────────────────────
        private void RegisterEntry(string section, string label, string hint, string[] keywords,
            FrameworkElement target, Func<string> summary, Action reveal)
        {
            if (target == null) return;
            _searchEntries.Add(new SearchEntry
            {
                Section = section,
                Label = label,
                Hint = hint ?? "",
                Keywords = keywords ?? Array.Empty<string>(),
                Target = target,
                Summary = summary ?? (() => ""),
                Reveal = reveal,
            });
        }

        private static string[] FieldKeywords(FieldSpec f)
        {
            // 同一组的关键词让「网络」「记忆」这类用户词也能覆盖组内字段。
            var section = f.Path.StartsWith("group.", StringComparison.Ordinal) ? "群聊 群 回复 @"
                : f.Path.StartsWith("wechat.", StringComparison.Ordinal) ? "微信 发送 通知 推送 消息"
                : f.Path.StartsWith("websearch.", StringComparison.Ordinal) || f.Path.StartsWith("links.", StringComparison.Ordinal)
                    ? "网络 联网 代理 搜索 链接"
                : f.Path.StartsWith("features.", StringComparison.Ordinal) ? "功能 记忆 上下文"
                : "高级 其他 重启";
            var extra = f.Path switch
            {
                "memory.dialogueBudget" => " 预算 token 记忆 上下文 对话",
                "env.timezone" => " 时区 时间",
                "server.host" or "server.port" => " 端口 网络 代理",
                "log.level" => " 日志 调试 排错",
                _ => "",
            };
            return (section + extra).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        private string DescribeField(FieldSpec f)
        {
            var value = GetConfigValue(f.Path);
            if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null) return "—";
            switch (f.Kind)
            {
                case "bool":
                    return value.ValueKind == JsonValueKind.True ? "开" : "关";
                case "num":
                case "slider":
                case "sliderInput":
                    if (value.ValueKind != JsonValueKind.Number) return "—";
                    var number = value.GetDouble();
                    var text = f.IsInt || f.Kind != "num"
                        ? Math.Round(number).ToString("N0")
                        : number.ToString("0.###");
                    return text + (f.Unit ?? "");
                case "multiline":
                    return value.ValueKind == JsonValueKind.Array
                        ? value.GetArrayLength() + " 项"
                        : (string.IsNullOrWhiteSpace(value.ToString()) ? "未设置" : "已填写");
                default:
                    if (value.ValueKind == JsonValueKind.Array) return value.GetArrayLength() + " 项";
                    if (value.ValueKind != JsonValueKind.String) return value.ToString();
                    var str = value.GetString() ?? "";
                    return str.Length == 0 ? "未设置" : str;
            }
        }

        private string SummaryForSection(string section)
        {
            switch (section)
            {
                case "群聊与回复":
                    return GetConfigBool("group.requireAt", true) ? "@ 才回" : "全部回复";
                case "微信与发送":
                {
                    var bg = GetConfigBool("wechat.bgSend.enabled", GetConfigBool("wechat.backgroundSend", false));
                    if (!bg) return "仅前台";
                    return GetConfigBool("wechat.bgSend.fallbackToForeground", true) ? "自动发送" : "后台严格";
                }
                case "联网与检索":
                    return GetConfigBool("links.enabled", true) ? "本地搜索 · 开" : "本地搜索 · 开 · 链接关";
                case "对话与记忆":
                    return "预算 " + GetConfigLong("memory.dialogueBudget", 5500).ToString("N0");
                case "高级与其他":
                {
                    var changed = 0;
                    foreach (var pair in AdvancedDefaults)
                        if (!ValueEqualsDefault(GetConfigValue(pair.Key), pair.Value)) changed++;
                    return changed == 0 ? "默认值" : changed + " 项已改";
                }
                default:
                    return "";
            }
        }

        private static bool ValueEqualsDefault(JsonElement value, object fallback)
        {
            if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
                return true;   // 配置里没有这一项 = 用默认值
            switch (fallback)
            {
                case string s:
                    return value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), s, StringComparison.Ordinal);
                case int n:
                    return value.ValueKind == JsonValueKind.Number && Math.Abs(value.GetDouble() - n) < 0.001;
                case double d:
                    return value.ValueKind == JsonValueKind.Number && Math.Abs(value.GetDouble() - d) < 0.001;
                case bool b:
                    return (value.ValueKind == JsonValueKind.True) == b;
                default:
                    return false;
            }
        }

        private JsonElement GetConfigValue(string dotted)
        {
            if (_configOverrides.TryGetValue(dotted, out var overridden)) return overridden;
            var cur = _configRoot;
            foreach (var part in dotted.Split('.'))
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next)) return default;
                cur = next;
            }
            return cur;
        }

        private bool GetConfigBool(string dotted, bool fallback = false)
        {
            var value = GetConfigValue(dotted);
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String) return string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            return fallback;
        }

        private long GetConfigLong(string dotted, long fallback)
        {
            var value = GetConfigValue(dotted);
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : fallback;
        }

        private void RefreshSectionSummaries()
        {
            foreach (var (badge, summary) in _sectionSummaries)
            {
                if (badge == null) continue;
                try { badge.Text = summary(); }
                catch { badge.Text = ""; }
            }
        }

        /// <summary>总开关关闭时，把依赖它的子项置灰（并在行上说明原因）。</summary>
        private void RefreshFieldState()
        {
            void Set(string path, bool enabled, string reason)
            {
                if (!_fieldRows.TryGetValue(path, out var row)) return;
                row.IsEnabled = enabled;
                row.ToolTip = enabled ? null : reason;
                ToolTipService.SetShowOnDisabled(row, !enabled);
            }

            var requireAt = GetConfigBool("group.requireAt", true);
            var freeChat = GetConfigBool("group.freeChat", false);
            var links = GetConfigBool("links.enabled", true);
            Set("group.masterAlwaysReply", requireAt, "已设为回复全部消息，不需要这个例外");
            Set("group.freeChat", requireAt, "只回 @ 时不会对未点名的消息插话");
            Set("group.freeChatChance", requireAt && freeChat,
                requireAt ? "先打开「允许她自己插话」" : "只回 @ 时插话概率不生效");
            Set("links.timeoutMs", links, "先打开「解析聊天里的链接」");
            Set("links.cacheMinutes", links, "先打开「解析聊天里的链接」");
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchPlaceholder != null)
                SearchPlaceholder.Visibility = string.IsNullOrEmpty(SettingsSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            RebuildSearchResults(scrollTop: true);
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            SettingsSearch.Clear();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void RefreshSearchIfActive()
        {
            if (SettingsSearch != null && !string.IsNullOrEmpty(SettingsSearch.Text)) RebuildSearchResults();
        }

        private void RebuildSearchResults(bool scrollTop = false)
        {
            if (_searchResultsHost == null) return;
            _searchResultsHost.Children.Clear();
            _lastSearchHits.Clear();
            var query = (SettingsSearch?.Text ?? "").Trim();
            if (query.Length == 0)
            {
                _searchResultsHost.Visibility = Visibility.Collapsed;
                return;
            }

            var groups = ExpandSearch(query);
            var hits = _searchEntries
                .Where(entry => groups.All(group => group.Any(term => Matches(entry, term))))
                .OrderBy(entry => HitScore(entry, groups))
                .ThenBy(entry => entry.Section, StringComparer.Ordinal)
                .ThenBy(entry => entry.Label, StringComparer.Ordinal)
                .Take(40)
                .ToList();

            var card = Card("搜索", hits.Count == 0 ? "" : hits.Count + " 项", out var body);
            if (hits.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "没有匹配的设置项",
                    Style = (Style)FindResource("Small"),
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
            else
            {
                foreach (var hit in hits)
                {
                    var row = BuildSearchHit(hit);
                    _lastSearchHits.Add(row);
                    body.Children.Add(row);
                }
            }
            _searchResultsHost.Children.Add(card);
            _searchResultsHost.Visibility = Visibility.Visible;
            if (scrollTop) MainScroll.ScrollToTop();
        }

        private static List<string[]> ExpandSearch(string query)
        {
            var parts = query.Split(new[] { ' ', '\t', '，', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var groups = new List<string[]>();
            foreach (var part in parts)
            {
                var synonyms = SynonymGroups.FirstOrDefault(group =>
                    group.Any(term => part.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
                groups.Add(synonyms == null ? new[] { part } : synonyms.Concat(new[] { part }).Distinct().ToArray());
            }
            return groups;
        }

        private static bool Matches(SearchEntry entry, string term)
        {
            if (term.Length == 0) return true;
            return entry.Label.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Hint.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Section.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Keywords.Any(keyword => keyword.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int HitScore(SearchEntry entry, List<string[]> groups)
        {
            var first = groups.Count > 0 ? groups[0][0] : "";
            if (entry.Label.Equals(first, StringComparison.OrdinalIgnoreCase)) return 0;
            if (entry.Label.StartsWith(first, StringComparison.OrdinalIgnoreCase)) return 1;
            if (entry.Label.IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 3;
        }

        private FrameworkElement BuildSearchHit(SearchEntry entry)
        {
            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
            };
            row.MouseEnter += (_, __) =>
            {
                if (!ReferenceEquals(_highlightTarget, row))
                    row.Background = (Brush)FindResource("Hover");
            };
            row.MouseLeave += (_, __) =>
            {
                if (!ReferenceEquals(_highlightTarget, row))
                    row.Background = Brushes.Transparent;
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            var section = new TextBlock
            {
                Text = entry.Section,
                Style = (Style)FindResource("Small"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var dot1 = new TextBlock { Text = "·", Style = (Style)FindResource("Small"), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock
            {
                Text = entry.Label,
                Foreground = (Brush)FindResource("Text"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var dot2 = new TextBlock { Text = "·", Style = (Style)FindResource("Small"), VerticalAlignment = VerticalAlignment.Center };
            var summary = new TextBlock
            {
                Text = SafeSummary(entry),
                Style = (Style)FindResource("Small"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(dot1, 1);
            Grid.SetColumn(label, 2);
            Grid.SetColumn(dot2, 3);
            Grid.SetColumn(summary, 4);
            grid.Children.Add(section);
            grid.Children.Add(dot1);
            grid.Children.Add(label);
            grid.Children.Add(dot2);
            grid.Children.Add(summary);
            row.Child = grid;
            row.MouseLeftButtonUp += (_, __) => ActivateSearchEntry(entry);
            return row;
        }

        private static string SafeSummary(SearchEntry entry)
        {
            try { return entry.Summary() ?? ""; }
            catch { return "—"; }
        }

        private void ActivateSearchEntry(SearchEntry entry, FrameworkElement source = null)
        {
            try { entry.Reveal?.Invoke(); } catch { }
            // 等折叠面板 / 页签真正展开后再算滚动位置。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                UpdateLayout();
                var target = entry.Target;
                if (target == null) return;
                try
                {
                    var point = target.TranslatePoint(new Point(0, 0), SectionsHost);
                    MainScroll.ScrollToVerticalOffset(Math.Max(0, MainScroll.VerticalOffset + point.Y - 16));
                }
                catch
                {
                    target.BringIntoView();
                }
                HighlightTarget(target);
            }));
        }

        private FrameworkElement _highlightTarget;

        /// <summary>背景色渐变 1.5 秒；不改控件自身样式，也不影响主题画刷。</summary>
        private void HighlightTarget(FrameworkElement target)
        {
            Brush brush;
            Action clear;
            if (target is Panel panel)
            {
                var accent = (SolidColorBrush)FindResource("AccentSoft");
                brush = new SolidColorBrush(accent.Color);
                panel.Background = brush;
                clear = () => { if (ReferenceEquals(panel.Background, brush)) panel.ClearValue(Panel.BackgroundProperty); };
            }
            else if (target is Border border)
            {
                var accent = (SolidColorBrush)FindResource("AccentSoft");
                brush = new SolidColorBrush(accent.Color);
                border.Background = brush;
                clear = () => { if (ReferenceEquals(border.Background, brush)) border.ClearValue(Border.BackgroundProperty); };
            }
            else if (target is Control control)
            {
                var accent = (SolidColorBrush)FindResource("AccentSoft");
                brush = new SolidColorBrush(accent.Color);
                control.Background = brush;
                clear = () => { if (ReferenceEquals(control.Background, brush)) control.ClearValue(Control.BackgroundProperty); };
            }
            else return;

            _highlightTarget = target;
            var animation = new ColorAnimation
            {
                To = Colors.Transparent,
                BeginTime = TimeSpan.FromMilliseconds(900),
                Duration = TimeSpan.FromMilliseconds(600),
                FillBehavior = FillBehavior.Stop,
            };
            animation.Completed += (_, __) =>
            {
                clear();
                if (ReferenceEquals(_highlightTarget, target)) _highlightTarget = null;
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
        }

        /// <summary>截图工具用：输入查询并高亮第一条命中（不滚动，保证结果列表也在画面里）。</summary>
        public void PreviewSearch(string query)
        {
            SettingsSearch.Text = query;
            UpdateLayout();
            if (_lastSearchHits.Count > 0) HighlightTarget(_lastSearchHits[0]);
        }

        /// <summary>截图工具用：advanced = 展开高级并停在第一组；search:xxx = 搜索并高亮首条。</summary>
        public void PreviewSection(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (key.StartsWith("search:", StringComparison.Ordinal))
            {
                PreviewSearch(key.Substring("search:".Length));
                return;
            }
            if (key == "advanced")
            {
                if (_advancedExpander != null) _advancedExpander.IsExpanded = true;
                _activateAdvancedTab?.Invoke(0);
                MainScroll.ScrollToTop();
                return;
            }
            if (key == "search-budget")
            {
                PreviewSearch("预算");
                return;
            }
            if (key == "diagnostics") OpenSubPage("诊断", "诊断");
        }

        // ── 卡片与行 ────────────────────────────────────────────
        private Border Card(string title, string note, out StackPanel body)
        {
            var border = new Border
            {
                Style = (Style)FindResource("Card"),
                Padding = new Thickness(18, 16, 18, 16),
                Margin = new Thickness(0, 0, 0, 14)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("H2") });
            if (!string.IsNullOrEmpty(note))
            {
                var noteText = new TextBlock
                {
                    Text = note,
                    FontSize = 14,
                    LineHeight = 20,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                noteText.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
                stack.Children.Add(noteText);
            }
            body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            stack.Children.Add(body);
            border.Child = stack;
            return border;
        }

        private TextBlock ValueLabel() => new TextBlock
        {
            Style = (Style)FindResource("Small"),
            MinWidth = 58,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };

        /// <summary>色块 + 当前色值 + 调色 / 自动。点「自动」清掉手选色，回到莫奈或主题默认。</summary>
        private FrameworkElement BuildColorRow(string kind, string label)
        {
            var swatch = new Border
            {
                Width = 38,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var hex = new TextBlock
            {
                Style = (Style)FindResource("Small"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 76,
                Margin = new Thickness(8, 0, 0, 0),
            };
            var pick = new Button { Content = "调色…", Style = (Style)FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
            var auto = new Button { Content = "自动", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(swatch);
            row.Children.Add(hex);
            row.Children.Add(pick);
            row.Children.Add(auto);

            bool IsCustom() => kind switch
            {
                "accent" => Appearance.AccentOverride.Length > 0,
                "tint" => Appearance.TintOverride.Length > 0,
                _ => Appearance.TextOverride.Length > 0,
            };

            void RefreshSwatch()
            {
                var c = Appearance.EffectiveColor(kind);
                swatch.Background = new SolidColorBrush(c);
                hex.Text = Appearance.ToHex(c);
                auto.IsEnabled = IsCustom();
            }

            pick.Click += (_, __) =>
            {
                var dlg = new ColorPickerWindow(label, Appearance.EffectiveColor(kind), (Style)FindResource("BudgetSlider"));
                if (AppDialog.ShowContent("调色 · " + label, dlg, 430, "确定", "取消"))
                    Appearance.SetPaletteColor(kind, Appearance.ToHex(dlg.SelectedColor));
            };
            auto.Click += (_, __) => Appearance.ClearPaletteColor(kind);
            Appearance.Changed += RefreshSwatch;
            RefreshSwatch();
            return row;
        }

        private static StackPanel SliderWithValue(Slider slider, TextBlock value)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(slider);
            panel.Children.Add(value);
            return panel;
        }

        private Grid Row(string label, FrameworkElement control, string hint, bool top = false, double labelWidth = 150)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("Label"),
                VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = top ? new Thickness(0, 2, 0, 0) : new Thickness(0),
                TextWrapping = TextWrapping.Wrap
            });
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            right.Children.Add(control);
            if (!string.IsNullOrEmpty(hint))
                right.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Small"), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            return grid;
        }

        /// <summary>
        /// 高级 → 记忆：识别并转换 3.x 旧记忆（data\store.json / memory.json / 旧备份包）。
        /// 走内核的 /api/memory/import，已导入过的签名会跳过，所以反复点也不会重复。
        /// </summary>
        private FrameworkElement BuildMemoryImportBlock()
        {
            var box = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var divider = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 12) };
            divider.SetResourceReference(Border.BackgroundProperty, "Line");
            box.Children.Add(divider);
            box.Children.Add(new TextBlock { Text = "旧版记忆（3.x）", Style = (Style)FindResource("Label") });

            var hint = new TextBlock
            {
                Text = "3.x 的 data\\store.json（含 .journal）/ memory.json / 旧备份包会转成现在的格式；"
                     + "同名聊天按时间合并，已经转过的不会重复导入。启动时也会自动检查一次。",
                Style = (Style)FindResource("Small"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8),
            };
            box.Children.Add(hint);

            var btn = new Button
            {
                Content = "一键识别转换记忆",
                Style = (Style)FindResource("Ghost"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var result = new TextBlock
            {
                Style = (Style)FindResource("Small"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            };
            box.Children.Add(btn);
            box.Children.Add(result);

            btn.Click += async (s, e) =>
            {
                if (!_sup.NodeRunning)
                {
                    result.Text = "内核没在运行，先启动内核再试。";
                    return;
                }
                btn.IsEnabled = false;
                var original = btn.Content;
                btn.Content = "正在检查…";
                try
                {
                    var status = await Api.Get(_sup.Url, "/api/memory/import/status");
                    if (!status.Ok)
                    {
                        result.Text = "检查失败：" + Api.Message(status);
                        return;
                    }
                    if (!status.Bool("pending"))
                    {
                        result.Text = "没有发现待转换的旧版记忆（data 里没有 store.json / memory.json，或已经转过了）。";
                        return;
                    }
                    var plan = status.Prop("plan");
                    var imported = NestedLong(plan, "imported");
                    var merged = NestedLong(plan, "merged");
                    var messages = NestedLong(plan, "messages");
                    var notes = NestedLong(plan, "notes");
                    if (!AppDialog.Confirm(
                        Window.GetWindow(this),
                        "发现旧版记忆：\n\n"
                        + "· 新增 " + imported + " 个聊天\n"
                        + "· 合并 " + merged + " 个已有聊天\n"
                        + "· 补进 " + messages + " 条消息 / " + notes + " 条偏好\n\n"
                        + "开始转换吗？原文件会保留，转换前也会自动备份当前记忆。",
                        "转换旧版记忆", okText: "开始转换"))
                    {
                        result.Text = "已取消，什么都没改。";
                        return;
                    }

                    btn.Content = "正在转换…";
                    var res = await Api.Post(_sup.Url, "/api/memory/import", "{}");
                    if (!res.Ok)
                    {
                        result.Text = "转换失败：" + Api.Message(res);
                        return;
                    }
                    if (res.Bool("idle"))
                    {
                        result.Text = "没有需要转换的旧记忆。";
                        return;
                    }
                    result.Text = "完成：新增 " + res.Long("imported") + " 个聊天、合并 " + res.Long("merged")
                        + " 个，补进 " + res.Long("messages") + " 条消息 / " + res.Long("notes") + " 条偏好。";
                    AppDialog.Show(
                        Window.GetWindow(this),
                        "记忆已导入并转换格式。\n\n" + result.Text + "\n\n原文件保留在 data\\memory\\legacy\\。",
                        "记忆已导入");
                    await Api.Post(_sup.Url, "/api/memory/import/ack", "{}");
                }
                catch (Exception ex)
                {
                    result.Text = "出错：" + ex.Message;
                }
                finally
                {
                    btn.Content = original;
                    btn.IsEnabled = true;
                }
            };
            return box;
        }

        private static long NestedLong(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            return 0;
        }

        // ── 单个字段 ────────────────────────────────────────────
        /// <param name="compact">两列并排时用的紧凑标签列</param>
        /// <param name="width">单独指定标签列宽（不传就按 compact 取默认值）</param>
        private FrameworkElement BuildField(FieldSpec f, bool compact = false, double width = double.NaN)
        {
            // 标签列别太宽：210 的时候标签和输入框中间空出半个屏幕，短标签尤其难看
            var labelWidth = !double.IsNaN(width) ? width : (compact ? 110.0 : 150.0);
            switch (f.Kind)
            {
                case "bool":
                {
                    var chk = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
                    chk.Click += async (s, e) =>
                    {
                        if (_loading) return;
                        await PatchAsync(f.Path, ((CheckBox)s).IsChecked == true);
                    };
                    _loaders[f.Path] = v => { if (chk.IsKeyboardFocusWithin) return; _loading = true; chk.IsChecked = v.ValueKind == JsonValueKind.True; _loading = false; };
                    return Row(f.Label, chk, f.Hint, false, labelWidth);
                }
                case "select":
                {
                    var cmb = new ComboBox { Height = 26, ItemsSource = f.Options, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
                    cmb.SelectionChanged += async (s, e) =>
                    {
                        if (_loading || cmb.SelectedItem == null) return;
                        await PatchAsync(f.Path, (string)cmb.SelectedItem);
                    };
                    _loaders[f.Path] = v =>
                    {
                        if (cmb.IsKeyboardFocusWithin) return;   // 正在编辑就不覆盖（M30）
                        _loading = true;
                        cmb.SelectedItem = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        _loading = false;
                    };
                    return Row(f.Label, cmb, f.Hint, false, labelWidth);
                }
                case "selectnum":
                {
                    // 端口这类「只能从固定几项里挑」的数字：下拉显示，写回仍是数字
                    var cmb = new ComboBox { Height = 26, ItemsSource = f.Options, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
                    cmb.SelectionChanged += async (s, e) =>
                    {
                        if (_loading || cmb.SelectedItem == null) return;
                        if (int.TryParse((string)cmb.SelectedItem, out var port))
                            await PatchAsync(f.Path, port);
                    };
                    _loaders[f.Path] = v =>
                    {
                        if (cmb.IsKeyboardFocusWithin) return;
                        _loading = true;
                        var cur = v.ValueKind == JsonValueKind.Number ? v.GetInt32().ToString()
                            : (v.ValueKind == JsonValueKind.String ? v.GetString() : null);
                        if (cur != null && (f.Options ?? new string[0]).Contains(cur)) cmb.SelectedItem = cur;
                        _loading = false;
                    };
                    return Row(f.Label, cmb, f.Hint, false, labelWidth);
                }
                case "secret":
                {
                    var pwd = new PasswordBox();
                    var savedKey = new TextBlock { Style = (Style)FindResource("Small") };
                    var secretPanel = new StackPanel();
                    secretPanel.Children.Add(pwd);
                    secretPanel.Children.Add(savedKey);
                    var secret = new ConfigurationSecretState();
                    var clearing = false;
                    var generation = _pageLifetime.Capture();
                    var saves = new ConfigurationSaveState<ConfigurationSecretSnapshot>(
                        () => { generation = _pageLifetime.Capture(); return secret.Capture(pwd.Password); },
                        build => PatchResultAsync(f.Path, () => build().Value.Trim(), true),
                        (snapshot, result) =>
                        {
                            if (!_pageLifetime.IsCurrent(generation) || !result.Ok || !secret.IsCurrent(snapshot, pwd.Password)) return;
                            clearing = true;
                            try { savedKey.Text = MaskApiKey(snapshot.Value); pwd.Password = ""; }
                            finally { clearing = false; }
                        });
                    pwd.PasswordChanged += (s, e) =>
                    {
                        secret.Changed();
                        if (_loading || clearing) return;
                        _configurationRevision++;
                        saves.MarkDesired();
                    };
                    pwd.LostFocus += async (s, e) =>
                    {
                        if (_loading || pwd.Password.Length == 0) return;
                        await saves.SaveAsync();
                    };
                    _apiFieldSaves[f.Path] = async () => pwd.Password.Length == 0 || (await saves.SaveAsync()).Ok;
                    _secretMaskLoaders[f.Path] = value => { if (pwd.Password.Length == 0) savedKey.Text = value; };
                    return Row(f.Label, secretPanel, f.Hint, false, labelWidth);
                }
                case "multiline":
                {
                    var box = new TextBox { AcceptsReturn = true, MinHeight = 56, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    box.TextChanged += (s, e) => { if (!_loading) { _dirtyFields.Add(f.Path); _configurationRevision++; } };
                    box.LostFocus += async (s, e) =>
                    {
                        if (_loading) return;
                        var lines = (box.Text ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
                        await PatchAsync(f.Path, lines);
                    };
                    _loaders[f.Path] = v =>
                    {
                        if (box.IsKeyboardFocusWithin || _dirtyFields.Contains(f.Path)) return;
                        _loading = true;
                        box.Text = v.ValueKind == JsonValueKind.Array
                            ? string.Join("\n", v.EnumerateArray().Select(x => x.ToString()))
                            : (v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "");
                        _loading = false;
                    };
                    return Row(f.Label, box, f.Hint, false, labelWidth);
                }
                case "num":
                {
                    TextBox box;
                    if (f.IsInt)
                    {
                        var min = double.IsNaN(f.Min) ? int.MinValue : (int)Math.Ceiling(f.Min);
                        var max = double.IsNaN(f.Max) ? int.MaxValue : (int)Math.Floor(f.Max);
                        box = new NumberField
                        {
                            Width = 120,
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Minimum = min,
                            Maximum = max,
                            EmptyUsesFallback = false,
                            Label = f.Label
                        };
                    }
                    else
                    {
                        box = new TextBox { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
                    }
                    box.LostFocus += async (s, e) =>
                    {
                        if (_loading) return;
                        if (box is NumberField number)
                        {
                            if (!number.TryRead(out var n))
                            {
                                StatusText.Text = number.ErrorText;
                                return;
                            }
                            number.Text = n.ToString();
                            await PatchAsync(f.Path, n);
                            return;
                        }
                        var text = (box.Text ?? "").Trim();
                        if (!double.TryParse(text, out var v) || !double.IsFinite(v))
                        {
                            StatusText.Text = f.Label + "：要填数字";
                            return;
                        }
                        // 以前越界会静默改成边界值，用户以为自己的输入生效了；现在直接说清楚。
                        if (!double.IsNaN(f.Min) && v < f.Min) { StatusText.Text = f.Label + " 不能小于 " + f.Min; return; }
                        if (!double.IsNaN(f.Max) && v > f.Max) { StatusText.Text = f.Label + " 不能大于 " + f.Max; return; }
                        await PatchAsync(f.Path, v);
                    };
                    box.TextChanged += (s, e) => { if (!_loading) { _dirtyFields.Add(f.Path); _configurationRevision++; } };
                    _apiFieldSaves[f.Path] = async () =>
                    {
                        if (!_dirtyFields.Contains(f.Path)) return true;
                        if (box is NumberField nfield)
                        {
                            if (!nfield.TryRead(out var value)) { StatusText.Text = nfield.ErrorText; return false; }
                            return await PatchAsync(f.Path, value);
                        }
                        if (!double.TryParse(box.Text, out var number) || !double.IsFinite(number)
                            || (!double.IsNaN(f.Min) && number < f.Min) || (!double.IsNaN(f.Max) && number > f.Max))
                        { StatusText.Text = f.Label + "：数值无效"; return false; }
                        return await PatchAsync(f.Path, number);
                    };
                    _loaders[f.Path] = v =>
                    {
                        if (box.IsKeyboardFocusWithin || _dirtyFields.Contains(f.Path)) return;
                        _loading = true;
                        box.Text = v.ValueKind == JsonValueKind.Number
                            ? (f.IsInt ? Math.Round(v.GetDouble()).ToString() : v.GetDouble().ToString("0.###"))
                            : "";
                        _loading = false;
                    };
                    return Row(f.Label, box, f.Hint, false, labelWidth);
                }
                case "slider":
                {
                    // 拖动条：拖动时只更新右侧读数，停手 450ms 才写回（避免拖一次发几十个 PATCH）
                    var slider = new Slider
                    {
                        Minimum = double.IsNaN(f.Min) ? 0 : f.Min,
                        Maximum = double.IsNaN(f.Max) ? 100 : f.Max,
                        TickFrequency = 1,
                        IsSnapToTickEnabled = true,
                        Width = 220,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var readout = new TextBlock
                    {
                        Style = (Style)FindResource("Small"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(12, 0, 0, 0)
                    };
                    var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                    var sliderPending = false;
                    var sliderRevision = 0;
                    debounce.Tick += async (s, e) =>
                    {
                        debounce.Stop();
                        if (_loading) return;
                        var revision = sliderRevision;
                        if (await PatchAsync(f.Path, Math.Round(slider.Value)) && revision == sliderRevision)
                            sliderPending = false;
                    };
                    void ShowValue() => readout.Text = Math.Round(slider.Value) + (f.Unit ?? "");
                    slider.ValueChanged += (s, e) =>
                    {
                        ShowValue();
                        if (_loading) return;
                        sliderPending = true;
                        sliderRevision++;
                        _configurationRevision++;
                        debounce.Stop();
                        debounce.Start();
                    };
                    _loaders[f.Path] = v =>
                    {
                        if (sliderPending) return;
                        _loading = true;
                        if (v.ValueKind == JsonValueKind.Number)
                        {
                            var n = v.GetDouble();
                            if (!double.IsNaN(f.Min)) n = Math.Max(f.Min, n);
                            if (!double.IsNaN(f.Max)) n = Math.Min(f.Max, n);
                            slider.Value = Math.Round(n);
                        }
                        _loading = false;
                        ShowValue();
                    };
                    var panel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
                    panel.Children.Add(slider);
                    panel.Children.Add(readout);
                    return Row(f.Label, panel, f.Hint, false, labelWidth);
                }
                case "sliderInput":
                {
                    // 滑块管「常用范围」，右边输入框管「精确值 / 超出滑块范围的值」；
                    // 下面那行说明由 /api/budget/estimate 实时算（只读估算，不联网、不花钱）。
                    // 滑轨按真实比例画 4 个档位刻度（2,000 / 5,500 / 10,000 / 20,000 并不等距），
                    // 下面再给 4 个一键跳档的胶囊；拖到档位 ±5% 以内时对应胶囊高亮。
                    const double sliderWidth = 420;
                    var tiers = new[]
                    {
                        (Name: "极简", Value: 2000d), (Name: "默认", Value: 5500d),
                        (Name: "标准", Value: 10000d), (Name: "重度", Value: 20000d),
                    };
                    var slider = new Slider
                    {
                        Minimum = double.IsNaN(f.Min) ? 0 : f.Min,
                        Maximum = double.IsNaN(f.Max) ? 100 : f.Max,
                        TickFrequency = 100,
                        IsSnapToTickEnabled = true,
                        Width = sliderWidth,
                        VerticalAlignment = VerticalAlignment.Center,
                        Style = (Style)FindResource("BudgetSlider"),
                    };
                    var tickLayer = new Canvas { Width = sliderWidth, Height = 24, IsHitTestVisible = false };
                    foreach (var tier in tiers)
                    {
                        var ratio = (tier.Value - slider.Minimum) / Math.Max(1, slider.Maximum - slider.Minimum);
                        var tick = new System.Windows.Shapes.Rectangle
                        {
                            Width = 2,
                            Height = 10,
                            Fill = (Brush)FindResource("Muted"),
                            Opacity = 0.45,
                        };
                        Canvas.SetLeft(tick, 7 + ratio * (sliderWidth - 14) - 1);
                        Canvas.SetTop(tick, 8);
                        tickLayer.Children.Add(tick);
                    }
                    var trackGrid = new Grid { Width = sliderWidth, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                    trackGrid.Children.Add(slider);
                    trackGrid.Children.Add(tickLayer);
                    var pillMap = new List<(Border Box, TextBlock Label, double Value)>();
                    var box = new TextBox
                    {
                        Width = 96,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(12, 0, 0, 0),
                        TextAlignment = TextAlignment.Center
                    };
                    var unit = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(f.Unit) ? " token" : f.Unit,
                        Style = (Style)FindResource("Small"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0)
                    };
                    var line = new TextBlock
                    {
                        Style = (Style)FindResource("Small"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0),
                        Text = f.Hint,
                    };
                    var bar = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left };
                    bar.Children.Add(trackGrid);
                    bar.Children.Add(box);
                    bar.Children.Add(unit);

                    // 计时器要先声明：下面的胶囊和 Show/Schedule 都会捕获它们
                    var saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                    var hintTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                    var syncing = false;
                    var hintOperation = new PageOperation(_pageLifetime);
                    var saveGeneration = _pageLifetime.Capture();
                    ConfigurationSaveState<double> saves = null;
                    saves = new ConfigurationSaveState<double>(() => { saveGeneration = _pageLifetime.Capture(); return Current(); },
                        build => PatchResultAsync(f.Path, () => build()),
                        (snapshot, result) =>
                        {
                            if (result.Ok && saves.HasPendingChanges && _pageLifetime.IsCurrent(saveGeneration))
                                StatusText.Text = f.Path + " 正在保存新的修改…";
                        });
                    _refreshBudgetHints.Add(() =>
                    {
                        hintOperation.Invalidate();
                        hintTimer.Stop();
                        hintTimer.Start();
                    });

                    // 四档快捷按钮：点一下跳到该档，滑块照旧可以拖任意值
                    var pills = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
                    foreach (var tier in tiers)
                    {
                        var caption = new TextBlock
                        {
                            Text = tier.Name + " " + tier.Value.ToString("N0"),
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        var pill = new Border
                        {
                            CornerRadius = new CornerRadius(4),
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(10, 0, 10, 0),
                            Height = 26,
                            Margin = new Thickness(0, 0, 6, 0),
                            Cursor = System.Windows.Input.Cursors.Hand,
                            Background = Brushes.Transparent,
                            BorderBrush = (Brush)FindResource("Line"),
                            Child = caption,
                        };
                        var jumpTo = tier.Value;
                        pill.MouseLeftButtonUp += (s, e) =>
                        {
                            if (_loading) return;
                            Show(jumpTo);
                            Schedule(true);
                        };
                        pills.Children.Add(pill);
                        pillMap.Add((pill, caption, jumpTo));
                    }

                    var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                    stack.Children.Add(bar);
                    stack.Children.Add(pills);
                    stack.Children.Add(line);

                    double Clamp(double v)
                    {
                        if (!double.IsNaN(f.InputMin)) v = Math.Max(f.InputMin, v);
                        if (!double.IsNaN(f.InputMax)) v = Math.Min(f.InputMax, v);
                        return Math.Round(v);
                    }

                    double Current()
                    {
                        var text = (box.Text ?? "").Trim();
                        return double.TryParse(text, out var v) ? Clamp(v) : Math.Round(slider.Value);
                    }

                    void PaintPills(double v)
                    {
                        foreach (var pill in pillMap)
                        {
                            // ±5% 以内算「就是这一档」，拖到别处就四个都不亮，避免错觉
                            var active = Math.Abs(v - pill.Value) <= Math.Max(150, pill.Value * 0.05);
                            if (active)
                            {
                                pill.Box.SetResourceReference(Border.BackgroundProperty, "AccentDim");
                                pill.Box.SetResourceReference(Border.BorderBrushProperty, "Accent");
                                pill.Label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
                            }
                            else
                            {
                                pill.Box.Background = Brushes.Transparent;
                                pill.Box.SetResourceReference(Border.BorderBrushProperty, "Line");
                                pill.Label.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
                            }
                        }
                    }

                    void Show(double v)
                    {
                        syncing = true;
                        slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, v));
                        if (!box.IsKeyboardFocusWithin) box.Text = Math.Round(v).ToString();
                        syncing = false;
                        PaintPills(Math.Round(v));
                    }

                    async void RefreshHint(double v)
                    {
                        var ticket = hintOperation.Begin();
                        try
                        {
                            var res = await Api.Get(_sup.Url, "/api/budget/estimate?dialogueBudget=" + Math.Round(v));
                            if (!hintOperation.IsCurrent(ticket)) return;
                            if (!res.Ok)
                            {
                                line.Text = f.Hint;
                                return;
                            }
                            var sys = res.Long("systemTokens");
                            var maxIn = res.Long("maxInputTokens");
                            var hard = res.Long("hardTotalTokens");
                            var room = Math.Max(0, hard - sys);
                            var over = res.Bool("overHardLimit");
                            // 只分三类：人设 / 记忆 / 对话（记忆 = 人物卡+摘要，对话 = 原文+余量）
                            var mem = (long)Num(res.Root, "split", "memory");
                            var dlg = (long)Num(res.Root, "split", "dialogue");
                            var first = Num(res.Prop("cost"), "idle", "first");
                            var steady = Num(res.Prop("cost"), "idle", "cached");
                            var hitRate = Num(res.Prop("cache"), "hitRate");
                            line.Text =
                                $"人设 {sys:N0} · 记忆 {mem:N0} · 对话 {dlg:N0} → 单轮最多 ≈ {maxIn:N0}（上限 {hard:N0}）\n"
                                + $"稳定期约 ¥{steady:0.0000} / 次（缓存命中 ≈{hitRate * 100:0}%，首轮 ¥{first:0.0000}）"
                                + (over ? $"\n⚠ 超过 {hard:N0} 上限：这个预算下每轮都会被拒绝，最多只能填 {room:N0}" : "");
                        }
                        catch
                        {
                            if (hintOperation.IsCurrent(ticket)) line.Text = f.Hint;
                        }
                    }

                    void Schedule(bool refreshHint)
                    {
                        if (_loading) return;
                        _configurationRevision++;
                        saves.MarkDesired();
                        hintOperation.Invalidate();
                        saveTimer.Stop();
                        saveTimer.Start();
                        if (!refreshHint) return;
                        hintTimer.Stop();
                        hintTimer.Start();
                    }

                    slider.ValueChanged += (s, e) =>
                    {
                        // 注意：这里要顺手刷胶囊高亮。以前只有 Show() 刷，
                        // 结果是「拖着滑块走，下面四个胶囊一直不亮」。
                        PaintPills(Math.Round(slider.Value));
                        if (syncing || _loading) return;
                        box.Text = Math.Round(slider.Value).ToString();
                        Schedule(true);
                    };

                    box.LostFocus += (s, e) =>
                    {
                        if (syncing || _loading) return;
                        var v = Current();
                        Show(v);
                        Schedule(true);
                    };
                    box.TextChanged += (s, e) =>
                    {
                        if (syncing || _loading || !box.IsKeyboardFocusWithin) return;
                        _configurationRevision++;
                        saves.MarkDesired();
                        hintOperation.Invalidate();
                    };
                    box.KeyDown += (s, e) =>
                    {
                        if (e.Key == System.Windows.Input.Key.Enter) System.Windows.Input.Keyboard.ClearFocus();
                    };

                    saveTimer.Tick += async (s, e) =>
                    {
                        saveTimer.Stop();
                        await saves.SaveAsync();
                    };

                    hintTimer.Tick += (s, e) =>
                    {
                        hintTimer.Stop();
                        RefreshHint(Current());
                    };

                    _loaders[f.Path] = v =>
                    {
                        if (box.IsKeyboardFocusWithin || saves.HasPendingChanges) return;
                        _loading = true;
                        if (v.ValueKind == JsonValueKind.Number)
                        {
                            var n = v.GetDouble();
                            Show(n);
                            saves.Initialize(Math.Round(n));
                        }
                        _loading = false;
                        RefreshHint(Current());
                    };

                    // 三行（滑轨 / 胶囊 / 说明）在上面，标签贴顶才不会落到胶囊那一行
                    return Row(f.Label, stack, "", true, labelWidth);
                }
                default:
                {
                    var box = new TextBox();
                    box.TextChanged += (s, e) => { if (!_loading) { _dirtyFields.Add(f.Path); _configurationRevision++; } };
                    _apiFieldSaves[f.Path] = async () => !_dirtyFields.Contains(f.Path) || await PatchAsync(f.Path, (box.Text ?? "").Trim());
                    box.LostFocus += async (s, e) =>
                    {
                        if (_loading) return;
                        await PatchAsync(f.Path, (box.Text ?? "").Trim());
                    };
                    _loaders[f.Path] = v =>
                    {
                        if (box.IsKeyboardFocusWithin || _dirtyFields.Contains(f.Path)) return;
                        _loading = true;
                        box.Text = v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "")
                            : (v.ValueKind == JsonValueKind.Array ? string.Join(",", v.EnumerateArray().Select(x => x.ToString())) : "");
                        _loading = false;
                    };
                    return Row(f.Label, box, f.Hint, false, labelWidth);
                }
            }
        }

        /// <summary>按路径从 JSON 里取数字（拿不到就 0），给估算回包的嵌套字段用。</summary>
        private static double Num(JsonElement root, params string[] path)
        {
            var cur = root;
            foreach (var p in path)
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out var next)) return 0;
                cur = next;
            }
            return cur.ValueKind == JsonValueKind.Number ? cur.GetDouble() : 0;
        }

        private async Task<bool> PatchAsync(string path, object value)
        {
            var res = await PatchResultAsync(path, () => value);
            return res.Ok;
        }

        private async Task<ApiResult> PatchResultAsync(string path, Func<object> build, bool secret = false)
        {
            _configurationRevision++;
            var revision = _configurationRevision;
            var generation = _pageLifetime.Capture();
            var res = secret ? await _saver.SaveSecretAsync(path, () => (string)build())
                : await _saver.SaveAsync(() => new Dictionary<string, object> { [path] = build() });
            if (res.Ok && revision == _configurationRevision) _dirtyFields.Remove(path);
            if (res.Ok && !secret)
            {
                // 写回一份乐观快照：不必等 5 秒轮询，分组摘要和搜索结果立刻用新值。
                try { _configOverrides[path] = JsonSerializer.SerializeToElement(build()); } catch { }
                RefreshSectionSummaries();
                RefreshFieldState();
                RefreshSearchIfActive();
            }
            if (_pageLifetime.IsCurrent(generation))
                StatusText.Text = res.Ok
                    ? path + " 已保存 " + DateTime.Now.ToString("HH:mm:ss")
                    : path + " 保存失败：" + Api.Message(res);
            return res;
        }

        private void AddApiProbe(StackPanel panel, string provider, string caption)
        {
            var button = new Button { Content = caption, Style = (Style)FindResource("Ghost"),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            var result = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(button);
            panel.Children.Add(result);
            button.Click += async (_, __) =>
            {
                if (!_sup.NodeRunning) { result.Text = "请先启动内核；离线只支持保存配置。"; return; }
                if (!AppDialog.Confirm(Window.GetWindow(this),
                    "将保存当前接口设置并调用服务商，可能产生少量费用。语音测试会上传你选择的音频，不会发送微信消息。",
                    "测试连接", okText: "继续测试")) return;
                button.IsEnabled = false;
                var generation = _pageLifetime.Capture();
                try
                {
                    var prefix = provider == "voice" ? "voice." : "websearch." + provider + ".";
                    foreach (var saveField in _apiFieldSaves.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(x => x.Value).ToArray())
                        if (!await saveField()) { result.Text = "配置未保存，请先修正输入或查看保存失败原因。"; return; }
                    await _saver.FlushAsync();
                    var body = new Dictionary<string, object> { ["provider"] = provider };
                    if (provider == "voice")
                    {
                        var file = AppFilePicker.Open("选择有清晰人声的短音频（最多 2 MiB）", ".wav");
                        if (file == null) { result.Text = "已取消测试。"; return; }
                        if (new FileInfo(file).Length > 2 * 1024 * 1024) { result.Text = "音频超过 2 MiB，请选择更短的 WAV。"; return; }
                        body["audio"] = Convert.ToBase64String(await File.ReadAllBytesAsync(file));
                    }
                    result.Text = "正在调用服务商…";
                    var response = await Api.PostJson(_sup.Url, "/api/settings/test-service", body, 45);
                    if (!_pageLifetime.IsCurrent(generation)) return;
                    result.Text = response.Ok ? response.Str("message") + " " + response.Str("sample") : "测试失败：" + Api.Message(response);
                }
                catch (Exception ex) { if (_pageLifetime.IsCurrent(generation)) result.Text = "测试失败：" + Api.FriendlyError(ex); }
                finally { button.IsEnabled = true; }
            };
        }

        private sealed class ModelSaveSnapshot
        {
            public object Body;
            public ConfigurationSecretSnapshot Secret;
            public long PageGeneration;
        }

        /// <summary>
        /// 「恢复默认」：把这些字段写回默认值。
        /// 默认值由内核按 DEFAULT_CONFIG 给（启动器不自己抄一份，抄的那份迟早和 config.js 对不上）。
        /// 只动这些字段：API Key 不重置，data\ 里的聊天记忆更不会碰。
        /// </summary>
        private async Task ResetFieldsAsync(string title, IEnumerable<string> paths)
        {
            var list = (paths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct()
                .ToList();
            if (list.Count == 0) return;

            if (!AppDialog.Confirm(
                Window.GetWindow(this),
                "把「" + title + "」改回默认值？\n\n只改这 " + list.Count + " 项；API Key 和聊天记忆不会被清掉。",
                "恢复默认", okText: "恢复默认"))
                return;

            // 先留一份改前的值，恢复默认成功后给「撤销」，不用让用户自己去记。
            var before = new List<(string Path, JsonElement Value)>();
            foreach (var path in list)
            {
                var value = GetConfigValue(path);
                if (value.ValueKind != JsonValueKind.Undefined)
                    before.Add((path, value.Clone()));
            }

            var res = await _sup.SaveConfigurationAsync("/api/config/reset", new { paths = list });
            if (!res.Ok)
            {
                StatusText.Text = "恢复默认失败：" + Api.Message(res);
                return;
            }

            var unknown = res.Prop("unknown");
            var missed = unknown.ValueKind == JsonValueKind.Array ? unknown.GetArrayLength() : 0;
            var message = "「" + title + "」已恢复默认（" + list.Count + " 项）"
                + (missed > 0 ? "，其中 " + missed + " 项不是可恢复项" : "");
            StatusText.Text = message;
            if (before.Count > 0) ShowUndo(before);
            _configOverrides.Clear();
            _lastLoad = DateTime.MinValue;   // 绕过节流，立刻把默认值刷回表单
            await LoadConfigAsync();
        }

        private void ShowUndo(List<(string Path, JsonElement Value)> before)
        {
            _undoReset = before;
            UndoText.Text = "恢复默认前的内容还留着 · 可以撤销";
            UndoBar.Visibility = Visibility.Visible;
            if (_undoTimer == null)
            {
                _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
                _undoTimer.Tick += (_, __) => HideUndo();
            }
            _undoTimer.Stop();
            _undoTimer.Start();
        }

        private void HideUndo()
        {
            _undoTimer?.Stop();
            _undoReset = null;
            if (UndoBar != null) UndoBar.Visibility = Visibility.Collapsed;
        }

        private async void OnUndoReset(object sender, RoutedEventArgs e)
        {
            var items = _undoReset;
            if (items == null || items.Count == 0) { HideUndo(); return; }
            HideUndo();
            var patch = new Dictionary<string, object>();
            foreach (var (path, value) in items)
                patch[path] = value;
            var res = await _sup.PatchConfigurationAsync(patch);
            if (!res.Ok)
            {
                StatusText.Text = "撤销失败：" + Api.Message(res);
                return;
            }
            StatusText.Text = "已撤销恢复默认（" + items.Count + " 项）";
            _configOverrides.Clear();
            _lastLoad = DateTime.MinValue;
            await LoadConfigAsync();
        }

        // ── 模型 / 看图（一张卡，两组用页签切换）────────────────────
        /// <summary>模型卡片里的标签都短，标签列收到 110 就够；再宽就是白条。</summary>
        private const double ModelLabel = 110;

        private Button MakeTab(string text, out TextBlock badge)
        {
            badge = new TextBlock { FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(badge);
            var tab = new Button
            {
                Height = 32,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
                Content = stack,
                Style = (Style)FindResource("Ghost"),
            };
            // 页签内容是「文字 + 角标」的 StackPanel，读屏拿不到名字，这里显式给。
            AutomationProperties.SetName(tab, text + "页签");
            return tab;
        }

        private static void MarkTab(Button btn, bool active)
        {
            btn.SetResourceReference(Button.BackgroundProperty, active ? "AccentSoft" : "Transparent");
            btn.SetResourceReference(Button.BorderBrushProperty, active ? "Accent" : "Line");
            btn.SetResourceReference(Button.ForegroundProperty, active ? "Accent" : "Muted");
            btn.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        /// <summary>
        /// 两列并排；right 为空时只占左半列。
        /// 左列定宽而不是各占一半：字段本身只有一百多像素宽，平均分两半会让右列
        /// 孤零零飘在卡片正中间，左边空出一大块（4.0 的采样区就是这样）。
        /// </summary>
        private static FrameworkElement TwoCol(FrameworkElement left, FrameworkElement right)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(left);
            if (right != null)
            {
                Grid.SetColumn(right, 2);
                g.Children.Add(right);
            }
            return g;
        }

        private Border BuildModelSection()
        {
            var card = Card("API 接入", "聊天模型连接。", out var body);

            var visionTab = MakeTab("看图（视觉）", out var visionBadge);
            var voiceTab = MakeTab("语音（识别）", out var voiceBadge);
            var searchTab = MakeTab("搜索", out var searchBadge);
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            strip.Children.Add(visionTab);
            strip.Children.Add(voiceTab);
            strip.Children.Add(searchTab);

            var chatPanel = new StackPanel();
            var visionPanel = new StackPanel { Visibility = Visibility.Collapsed };
            var voicePanel = new StackPanel { Visibility = Visibility.Collapsed };
            var searchPanel = new StackPanel { Visibility = Visibility.Collapsed };
            body.Children.Add(chatPanel);

            // 采样、独立模型与 Key 都属于低频项：默认只看聊天模型，其余收进底部一个折叠。
            var advancedExpander = new Expander
            {
                Header = "高级",
                IsExpanded = false,
                Margin = new Thickness(0, 12, 0, 0),
            };
            advancedExpander.SetResourceReference(Control.ForegroundProperty, "Text");
            var advancedBody = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            advancedExpander.Content = advancedBody;
            body.Children.Add(advancedExpander);
            advancedBody.Children.Add(new TextBlock
            {
                Text = "看图 / 语音 / 搜索默认跟随聊天模型。",
                Style = (Style)FindResource("Small"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            var tabs = new[] { visionTab, voiceTab, searchTab };
            var panels = new[] { visionPanel, voicePanel, searchPanel };
            void SelectTab(int idx)
            {
                for (var i = 0; i < panels.Length; i++)
                {
                    panels[i].Visibility = i == idx ? Visibility.Visible : Visibility.Collapsed;
                    MarkTab(tabs[i], i == idx);
                }
            }
            visionTab.Click += (s, e) => SelectTab(0);
            voiceTab.Click += (s, e) => SelectTab(1);
            searchTab.Click += (s, e) => SelectTab(2);
            SelectTab(0);

            // 两组控件先都建出来：换聊天模型时要同步刷新看图的提示和角标
            var suppressAuto = false;           // 程序化改控件时别触发自动保存
            ConfigurationSaveState<ModelSaveSnapshot> chatSaves = null;
            ConfigurationSaveState<ModelSaveSnapshot> visionSaves = null;
            var chatSecret = new ConfigurationSecretState();
            var visionSecret = new ConfigurationSecretState();
            var cmb = new ComboBox { Height = 28, DisplayMemberPath = "Label", MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            var baseUrl = new TextBox();
            var model = new ComboBox { Height = 28, IsEditable = true, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            var key = new PasswordBox();
            var chatKeyMask = new TextBlock { Style = (Style)FindResource("Small") };
            var chatKeyPanel = new StackPanel();
            chatKeyPanel.Children.Add(key);
            chatKeyPanel.Children.Add(chatKeyMask);
            // 深度思考：和 /api/settings 共用 data/settings.json 的 thinking（保存走 ModelRequest）。
            // 只在这里声明，Click 处理器等 AutoSaveChatAsync 定义好之后再挂（否则编译器会报
            // 「未赋值的局部变量」——那个函数捕获了后面才声明的控件）。
            var thinking = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            var chatResult = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

            // 看图：和聊天页一样的「服务商 + 模型」两个下拉。
            // 服务商第一项是「自动跟随聊天模型」（默认），最后一项是「自定义」：
            // 选了自定义就自己填接口地址和模型名（中转站、自建视觉服务、同服务商换路径都能用），
            // 还可以一键「从聊天复制」把聊天那套地址/模型抄过来。
            var visionProviders = new List<ProviderItem>();
            var visionProvider = new ComboBox { Height = 28, DisplayMemberPath = "Label", MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            // 预设服务商时只能从下拉里选（选项跟聊天模型同一份）；
            // 只有「自定义」才切成可编辑，好让用户填预设里没有的模型名
            var visionModel = new ComboBox { Height = 28, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            var visionUrl = new TextBox { MinWidth = 320 };
            var visionTip = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            var visionBox = new StackPanel();
            visionBox.Children.Add(visionModel);
            visionBox.Children.Add(visionTip);
            var visionKey = new PasswordBox();
            var visionKeyMask = new TextBlock { Style = (Style)FindResource("Small") };
            var visionKeyPanel = new StackPanel();
            visionKeyPanel.Children.Add(visionKey);
            visionKeyPanel.Children.Add(visionKeyMask);
            var visionHint = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            var visionEndpoint = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            var visionResult = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            // 这些行要按「自动 / 手动」切显隐，所以在事件挂上之前先声明、下面再赋值。
            FrameworkElement visionModelRow = null;
            FrameworkElement visionUrlRow = null;
            FrameworkElement visionTimeoutRow = null;
            FrameworkElement visionKeyRow = null;
            Button vsave = null;
            Button vcopy = null;

            JsonElement currentPreset = default;   // 当前服务商预设（判断「聊天模型自己能不能看图」用）

            void FillChatOptions(JsonElement pv)
            {
                currentPreset = pv;
                var chat = new List<string>();
                if (pv.ValueKind == JsonValueKind.Object
                    && pv.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var it in arr.EnumerateArray())
                        if (it.ValueKind == JsonValueKind.String) chat.Add(it.GetString());
                model.ItemsSource = chat;
            }

            // 看图模型和聊天模型用同一份选项：当前服务商的 models + visionModels。
            // 选了哪家服务商，就列哪家的模型；「自动跟随」时不显示这一行。
            void FillVisionModels(string providerKey)
            {
                var list = new List<string>();
                if (_providers != null && !string.IsNullOrEmpty(providerKey)
                    && _providers.TryGetValue(providerKey, out var pv) && pv.ValueKind == JsonValueKind.Object)
                {
                    void Add(JsonElement arr)
                    {
                        if (arr.ValueKind != JsonValueKind.Array) return;
                        foreach (var it in arr.EnumerateArray())
                            if (it.ValueKind == JsonValueKind.String)
                            {
                                var s = it.GetString();
                                if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s)) list.Add(s);
                            }
                    }
                    if (pv.TryGetProperty("models", out var arr)) Add(arr);
                    if (pv.TryGetProperty("visionModels", out var varr)) Add(varr);
                }
                var keep = visionModel.SelectedItem as string;
                visionModel.ItemsSource = list;
                if (!string.IsNullOrWhiteSpace(keep) && list.Contains(keep)) visionModel.SelectedItem = keep;
                else if (list.Count == 0) visionModel.SelectedItem = null;
                else visionModel.SelectedItem = list[0];
            }

            bool VisionIsAuto() => string.IsNullOrEmpty((visionProvider.SelectedItem as ProviderItem)?.Key);
            bool VisionIsCustom() => (visionProvider.SelectedItem as ProviderItem)?.Key == "custom";

            void SyncVisionTip()
            {
                var picked = model.SelectedItem as string ?? model.Text;
                if (VisionIsAuto())
                {
                    var fallbackModel = "";
                    if (currentPreset.ValueKind == JsonValueKind.Object
                        && currentPreset.TryGetProperty("visionModels", out var fv)
                        && fv.ValueKind == JsonValueKind.Array)
                        foreach (var it in fv.EnumerateArray())
                            if (it.ValueKind == JsonValueKind.String) { fallbackModel = it.GetString(); break; }
                    var capable = fallbackModel.Length > 0 && fallbackModel == picked;
                    visionTip.Text = capable
                        ? "自动跟随聊天模型：" + picked
                        : fallbackModel.Length > 0
                            ? "自动：聊天模型不能看图时用本服务商的看图模型 " + fallbackModel + "（复用同一把 Key）"
                            : "自动：聊天服务商没有看图模型时用默认视觉模型（glm-4.6v-flash）";
                    visionBadge.Text = capable ? "跟随中" : "自动";
                }
                else
                {
                    var prov = (visionProvider.SelectedItem as ProviderItem)?.Label ?? "";
                    if (VisionIsCustom())
                    {
                        var url = visionUrl.Text.Trim();
                        var vm = VisionValue();
                        visionTip.Text = url.Length > 0
                            ? "自定义接口：" + url + " · " + vm
                            : "自定义接口：先填接口地址和模型名（点「从聊天复制」也能直接把聊天的地址/模型搬过来）";
                        visionBadge.Text = "自定义";
                    }
                    else
                    {
                        visionTip.Text = "手动指定：" + prov + " · " + (visionModel.SelectedItem as string ?? "");
                        visionBadge.Text = "已指定";
                    }
                }
            }

            // 老设置可能只存了 vision.model 没存 vision.provider：从模型名反推是哪家的。
            string OwnerProviderOf(string visionModelName)
            {
                if (_providers == null || string.IsNullOrWhiteSpace(visionModelName)) return "";
                foreach (var kv in _providers)
                {
                    var p = kv.Value;
                    if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("visionModels", out var arr)) continue;
                    if (arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var it in arr.EnumerateArray())
                        if (it.ValueKind == JsonValueKind.String && it.GetString() == visionModelName) return kv.Key;
                }
                return "";
            }

            // 聊天模型自己能不能识图：只有能，才允许把看图服务商选成「自动跟随」。
            bool ChatCanSeeImages()
            {
                var picked = (model.SelectedItem as string) ?? (model.Text ?? "").Trim();
                if (picked.Length == 0 || currentPreset.ValueKind != JsonValueKind.Object) return false;
                if (!currentPreset.TryGetProperty("visionModels", out var varr) || varr.ValueKind != JsonValueKind.Array) return false;
                foreach (var it in varr.EnumerateArray())
                    if (it.ValueKind == JsonValueKind.String && it.GetString() == picked) return true;
                return false;
            }

            bool ProviderHasVision(string key)
            {
                if (_providers == null || key.Length == 0 || !_providers.TryGetValue(key, out var p)) return false;
                return p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("visionModels", out var varr)
                    && varr.ValueKind == JsonValueKind.Array
                    && varr.GetArrayLength() > 0;
            }

            // 看图服务商列表：聊天模型能识图时第一项才是「自动跟随」；只列真的有看图
            // 模型的服务商，免得选完模型下拉是空的。最后一项「自定义」不依赖预设，常驻。
            void RebuildVisionProviders()
            {
                var keepKey = (visionProvider.SelectedItem as ProviderItem)?.Key;
                // 同样换新实例：复用同一个 List 会被 WPF 的默认视图缓存住，选中的新对象匹配不上
                var list = new List<ProviderItem>();
                if (ChatCanSeeImages())
                    list.Add(new ProviderItem { Key = "", Label = "自动跟随聊天模型" });
                if (_providers != null)
                    foreach (var kv in _providers)
                        if (ProviderHasVision(kv.Key))
                            list.Add(new ProviderItem { Key = kv.Key, Label = JsonRead.Str(kv.Value, "label") + "（" + kv.Key + "）" });
                // 自定义永远在最后：不依赖任何预设，地址和模型都由用户填
                list.Add(new ProviderItem { Key = "custom", Label = "自定义（自己填接口地址和模型）" });
                visionProviders = list;
                visionProvider.ItemsSource = list;
                if (keepKey != null)
                {
                    var hit = list.FirstOrDefault(x => x.Key == keepKey);
                    if (hit != null) visionProvider.SelectedItem = hit;
                }
            }

            // 当前选择失效（比如刚把聊天模型换成不能识图的，自动项消失了）：落到聊天服务商或第一家。
            void EnsureVisionSelection()
            {
                if (visionProvider.SelectedItem is ProviderItem) return;
                var chatKey = (cmb.SelectedItem as ProviderItem)?.Key ?? "";
                var hit = visionProviders.FirstOrDefault(x => x.Key == chatKey) ?? visionProviders.FirstOrDefault();
                visionProvider.SelectedItem = hit;
                if (hit != null) FillVisionModels(hit.Key);
            }

            cmb.SelectionChanged += async (s, e) =>
            {
                if (_loading || suppressAuto || !(cmb.SelectedItem is ProviderItem p)) return;
                if (_providers != null && _providers.TryGetValue(p.Key, out var pv))
                {
                    var wasAuto = VisionIsAuto();
                    suppressAuto = true;
                    try
                    {
                        baseUrl.Text = JsonRead.Str(pv, "baseUrl");
                        FillChatOptions(pv);
                        model.Text = JsonRead.Str(pv, "model");
                        RebuildVisionProviders();   // 新服务商的聊天模型能不能识图，决定「自动跟随」还在不在
                        if (!wasAuto || !VisionIsAuto()) EnsureVisionSelection();
                        ApplyVisionMode();
                    }
                    finally { suppressAuto = false; }
                    await AutoSaveChatAsync();
                    if (wasAuto && !VisionIsAuto()) await AutoSaveVisionAsync();   // 自动失效，落成手动
                }
            };
            model.SelectionChanged += async (s, e) =>
            {
                if (_loading || suppressAuto) return;
                var wasAuto = VisionIsAuto();
                suppressAuto = true;
                try
                {
                    RebuildVisionProviders();
                    if (wasAuto && !VisionIsAuto()) EnsureVisionSelection();
                    ApplyVisionMode();
                }
                finally { suppressAuto = false; }
                SyncVisionTip();
                await AutoSaveChatAsync();
                if (wasAuto && !VisionIsAuto()) await AutoSaveVisionAsync();
            };
            visionProvider.SelectionChanged += async (s, e) =>
            {
                // suppressAuto 为真 = 程序在重建下拉（比如换了聊天模型），不是用户点的：
                // 以前这里会把外层设的 suppressAuto 冲成 false，然后一路自动保存 → 整页重读，
                // 结果「换模型服务后下拉框变空白」。
                if (_loading || suppressAuto || !_configLoaded) return;
                suppressAuto = true;
                try
                {
                    var key = (visionProvider.SelectedItem as ProviderItem)?.Key ?? "";
                    if (key.Length > 0) FillVisionModels(key);
                    if (key == "custom")
                    {
                        // 从「跟随 / 预设」切到自定义：先用聊天那套把地址和模型填好，用户再改。
                        // 这就是「抄一份过来」，不用从零敲。
                        visionModel.IsEditable = true;
                        if (visionUrl.Text.Trim().Length == 0) visionUrl.Text = baseUrl.Text.Trim();
                        if (VisionValue().Length == 0) visionModel.Text = (model.Text ?? "").Trim();
                    }
                    ApplyVisionMode();
                }
                finally { suppressAuto = false; }
                // 自定义还缺地址/模型时先别存：后端会拒绝，界面上提示就够了
                if (VisionIsCustom() && (visionUrl.Text.Trim().Length == 0 || VisionValue().Length == 0)) return;
                await AutoSaveVisionAsync();
            };
            visionModel.SelectionChanged += async (s, e) =>
            {
                if (_loading || suppressAuto) return;
                SyncVisionTip();
                await AutoSaveVisionAsync();
            };
            // 输入/选择完离开控件就自动保存；手点「保存」仍然可用（老习惯不用改）
            baseUrl.LostFocus += async (s, e) => await AutoSaveChatAsync();
            model.LostFocus += async (s, e) => await AutoSaveChatAsync();
            key.LostFocus += async (s, e) => { if (key.Password.Length > 0) await AutoSaveChatAsync(); };
            visionModel.LostFocus += async (s, e) => await AutoSaveVisionAsync();
            visionKey.LostFocus += async (s, e) => { if (visionKey.Password.Length > 0) await AutoSaveVisionAsync(); };

            // ── 聊天面板 ────────────────────────────────────────
            chatPanel.Children.Add(new TextBlock
            {
                Text = "聊天模型",
                Style = (Style)FindResource("H2"),
                Margin = new Thickness(0, 0, 0, 10),
            });
            chatPanel.Children.Add(Row("模型服务", cmb, "", false, ModelLabel));
            chatPanel.Children.Add(Row("接口地址", baseUrl, "", false, ModelLabel));
            chatPanel.Children.Add(Row("模型名", model, "", false, ModelLabel));
            chatPanel.Children.Add(Row("API Key", chatKeyPanel, "留空表示不改。", false, ModelLabel));
            var connectionNote = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1) };
            connectionNote.SetResourceReference(Border.BackgroundProperty, "Hover");
            connectionNote.SetResourceReference(Border.BorderBrushProperty, "Accent");
            connectionNote.Child = new TextBlock { Text = "回复异常时先点「测试连通」。",
                Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap };
            chatPanel.Children.Add(connectionNote);

            var chatDivider = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 12) };
            chatDivider.SetResourceReference(Border.BackgroundProperty, "Line");
            advancedBody.Children.Add(chatDivider);
            advancedBody.Children.Add(new TextBlock { Text = "采样", Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 8) });
            advancedBody.Children.Add(TwoCol(
                BuildField(new FieldSpec { Path = "deepseek.temperature", Label = "温度", Kind = "num", Min = 0, Max = 2, IsInt = false, Hint = "越大越放得开（>1.2 容易跑偏）" }, true),
                BuildField(new FieldSpec { Path = "deepseek.maxTokens", Label = "最多 token", Kind = "num", Min = 16, Max = 32768, Hint = "默认 700" }, true)));
            advancedBody.Children.Add(TwoCol(
                BuildField(new FieldSpec { Path = "deepseek.frequencyPenalty", Label = "频率惩罚", Kind = "num", Min = -2, Max = 2, IsInt = false }, true),
                BuildField(new FieldSpec { Path = "deepseek.presencePenalty", Label = "在场惩罚", Kind = "num", Min = -2, Max = 2, IsInt = false }, true)));
            advancedBody.Children.Add(TwoCol(
                BuildField(new FieldSpec { Path = "deepseek.timeoutMs", Label = "请求超时(ms)", Kind = "num", Min = 1000, Max = 600000 }, true),
                null));
            // 深度思考：和 /api/settings 共用 data/settings.json 的 thinking，走 /api/settings 保存。
            // 以前这里是「语音」页签里的 deepseek.thinking：接口一保存 settings.thinking 就把它盖掉，
            // 在 exe 里改了也不生效。现在它属于聊天设置，和接口写的是同一个值。
            thinking.Click += async (s, e) =>
            {
                if (_loading) return;
                await AutoSaveChatAsync();
            };
            advancedBody.Children.Add(Row(
                "深度思考", thinking,
                "更慢、更贵；思考 token 计入输出预算。",
                false, ModelLabel));

            var chatButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var save = new Button { Content = "保存", Style = (Style)FindResource("Ghost") };
            var test = new Button { Content = "测试连通", Style = (Style)FindResource("Primary") };
            var resetSampling = new Button { Content = "采样恢复默认", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            resetSampling.Click += async (s, e) => await ResetFieldsAsync("采样参数", new[]
            {
                "deepseek.temperature", "deepseek.maxTokens",
                "deepseek.frequencyPenalty", "deepseek.presencePenalty",
                "deepseek.timeoutMs",
            });
            chatButtons.Children.Add(test);
            chatPanel.Children.Add(chatButtons);
            chatPanel.Children.Add(chatResult);

            var advancedButtons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            advancedButtons.Children.Add(save);
            advancedButtons.Children.Add(resetSampling);
            advancedBody.Children.Add(advancedButtons);

            var advancedDivider = new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(0, 12, 0, 12) };
            advancedBody.Children.Add(advancedDivider);
            advancedBody.Children.Add(strip);
            advancedBody.Children.Add(visionPanel);
            advancedBody.Children.Add(voicePanel);
            advancedBody.Children.Add(searchPanel);

            // 看图取值：自动 = 服务商传空（服务端跟随聊天模型）；手动 = 服务商 + 模型。
            string VisionProviderValue() => (visionProvider.SelectedItem as ProviderItem)?.Key ?? "";
            string VisionValue()
            {
                if (VisionProviderValue().Length == 0) return "";
                // 自定义接口的模型是可编辑下拉：用户可能直接敲了一个预设里没有的名字
                var picked = visionModel.SelectedItem as string;
                return string.IsNullOrWhiteSpace(picked) ? (visionModel.Text ?? "").Trim() : picked;
            }

            object ModelRequest()
            {
                var prov = (cmb.SelectedItem as ProviderItem)?.Key ?? "";
                var req = new Dictionary<string, object>
                {
                    ["provider"] = prov,
                    ["baseUrl"] = baseUrl.Text.Trim(),
                    ["model"] = model.Text.Trim(),
                    ["visionProvider"] = VisionProviderValue(),
                    ["visionModel"] = VisionValue(),
                    ["mock"] = false,
                    ["thinking"] = thinking.IsChecked == true
                };
                if (VisionIsCustom()) req["visionBaseUrl"] = visionUrl.Text.Trim();
                if (key.Password.Length > 0) req["apiKey"] = key.Password.Trim();
                return req;
            }

            chatSaves = new ConfigurationSaveState<ModelSaveSnapshot>(
                () => new ModelSaveSnapshot { Body = ModelRequest(), Secret = chatSecret.Capture(key.Password), PageGeneration = _pageLifetime.Capture() },
                build => _saver.SaveSettingsAsync(() => build().Body),
                (snapshot, result) =>
                {
                    if (!_pageLifetime.IsCurrent(snapshot.PageGeneration)) return;
                    if (result.Ok && chatSecret.IsCurrent(snapshot.Secret, key.Password))
                    {
                        suppressAuto = true;
                        try { if (snapshot.Secret.Value.Length > 0) chatKeyMask.Text = MaskApiKey(snapshot.Secret.Value); key.Password = ""; }
                        finally { suppressAuto = false; }
                    }
                    chatResult.Text = result.Ok
                        ? (chatSaves.HasPendingChanges ? "正在保存新的修改…" : "已自动保存。")
                        : "自动保存失败：" + Api.Message(result);
                });
            visionSaves = new ConfigurationSaveState<ModelSaveSnapshot>(
                CaptureVisionSnapshot,
                build => _saver.SaveSettingsAsync(() => build().Body),
                (snapshot, result) =>
                {
                    if (!_pageLifetime.IsCurrent(snapshot.PageGeneration)) return;
                    if (result.Ok && visionSecret.IsCurrent(snapshot.Secret, visionKey.Password))
                    {
                        suppressAuto = true;
                        try { if (snapshot.Secret.Value.Length > 0) visionKeyMask.Text = MaskApiKey(snapshot.Secret.Value); visionKey.Password = ""; }
                        finally { suppressAuto = false; }
                    }
                    if (!result.Ok) visionResult.Text = "保存失败：" + Api.Message(result);
                });
            void Edited(bool vision)
            {
                if (suppressAuto || _loading || !_configLoaded) return;
                _configurationRevision++;
                if (vision) visionSaves.MarkDesired(); else chatSaves.MarkDesired();
                foreach (var refresh in _refreshBudgetHints) refresh();
            }
            baseUrl.TextChanged += (s, e) => Edited(false);
            model.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => Edited(false)));
            key.PasswordChanged += (s, e) => { chatSecret.Changed(); Edited(false); };
            visionUrl.TextChanged += (s, e) => Edited(true);
            visionModel.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => Edited(true)));
            visionKey.PasswordChanged += (s, e) => { visionSecret.Changed(); Edited(true); };

            // 自动保存：聊天/看图这些输入改完（失焦、换了选项、勾了开关）立刻写回，
            // 不用再记得点「保存」。手动按钮保留，老习惯照样能用。
            async Task AutoSaveChatAsync()
            {
                if (suppressAuto || _loading || !_configLoaded) return;
                Edited(false);
                await chatSaves.SaveAsync();
            }

            async Task AutoSaveVisionAsync()
            {
                if (suppressAuto || _loading || !_configLoaded) return;
                await SaveVisionAsync("");
            }

            save.Click += async (s, e) =>
            {
                var generation = _pageLifetime.Capture();
                Edited(false);
                var res = await chatSaves.SaveAsync();
                if (!_pageLifetime.IsCurrent(generation)) return;
                if (!res.Ok) { chatResult.Text = "保存失败：" + Api.Message(res); return; }
                chatResult.Text = "已保存。";
                // 服务端可能顺手改了看图模型（跟随/解除跟随），必须重新拉一次，
                // 否则「看图」页还显示保存前的旧模型。
                _lastLoad = DateTime.MinValue;
                await LoadConfigAsync();
            };
            test.Click += async (s, e) =>
            {
                if (!_sup.NodeRunning) { chatResult.Text = "请先启动内核，再测试连接。"; return; }
                test.IsEnabled = false;
                try
                {
                    chatResult.Text = "正在保存并测试…";
                    var saved = await chatSaves.SaveAsync();
                    if (!saved.Ok) { chatResult.Text = "保存失败：" + Api.Message(saved); return; }
                    var res = await Api.PostJson(_sup.Url, "/api/settings/test", new { }, 60);
                    chatResult.Text = res.Ok ? "模型调用成功。" : "测试没通过：" + Api.Message(res);
                }
                finally { test.IsEnabled = true; }
            };

            // ── 看图面板 ────────────────────────────────────────
            // 和聊天页一样的两个下拉：服务商（第一项是「自动跟随聊天模型」）+ 模型；
            // 最后一项「自定义」会多出一个接口地址输入框，模型也能自己敲。
            var visionProviderRow = Row("服务商", visionProvider, "自动跟随 = 用聊天模型同一家的看图模型和 Key；自定义 = 地址和模型都自己填", false, ModelLabel);
            visionModelRow = Row("模型", visionBox, "", false, ModelLabel);
            visionUrlRow = Row("接口地址", visionUrl, "自定义接口就填这里（例如 https://api.example.com/v1）；和聊天一样时直接复用聊天那把 Key", false, ModelLabel);
            visionTimeoutRow = BuildField(new FieldSpec { Path = "vision.timeoutMs", Label = "超时(毫秒)", Kind = "num", Min = 1000, Max = 300000 }, false, ModelLabel);
            visionKeyRow = Row("看图 Key", visionKeyPanel, "留空表示不改；和聊天同一地址会自动复用聊天 Key；本机 / 内网服务可以留空", false, ModelLabel);
            visionPanel.Children.Add(visionHint);
            visionPanel.Children.Add(visionProviderRow);
            visionPanel.Children.Add(visionUrlRow);
            visionPanel.Children.Add(visionModelRow);
            visionPanel.Children.Add(visionEndpoint);
            visionPanel.Children.Add(visionTimeoutRow);
            visionPanel.Children.Add(visionKeyRow);

            var visionButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            vsave = new Button { Content = "保存看图设置", Style = (Style)FindResource("Ghost") };
            vcopy = new Button { Content = "从聊天复制", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "把聊天正在用的接口地址和能看图的模型抄到看图设置里" };
            var vtest = new Button { Content = "测一下", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            visionButtons.Children.Add(vsave);
            visionButtons.Children.Add(vcopy);
            visionButtons.Children.Add(vtest);
            visionPanel.Children.Add(visionButtons);
            visionPanel.Children.Add(visionResult);

            // 自动跟随：隐藏模型 / 接口地址 / 超时 / Key / 保存，只留「测一下」。
            void ApplyVisionMode()
            {
                if (visionModelRow == null) return;
                var auto = VisionIsAuto();
                var custom = VisionIsCustom();
                visionModel.IsEditable = custom;   // 只有自定义接口才允许自己敲模型名
                visionModelRow.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
                visionUrlRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
                visionEndpoint.Visibility = (!auto && !custom) ? Visibility.Visible : Visibility.Collapsed;
                visionTimeoutRow.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
                visionKeyRow.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
                visionHint.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
                vsave.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
                vcopy.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
                SyncVisionTip();
            }
            ApplyVisionMode();   // 默认：自动跟随（模型 / Key 等都先藏起来）

            // 保存这一页的全部内容：看图服务商 + 模型 + （自定义时的）接口地址 + 看图 Key。
            async Task<bool> SaveVisionAsync(string successText)
            {
                var generation = _pageLifetime.Capture();
                Edited(true);
                var res = await visionSaves.SaveAsync();
                if (!_pageLifetime.IsCurrent(generation)) return res.Ok;
                if (!res.Ok)
                {
                    visionResult.Text = "保存失败：" + Api.Message(res);
                    return false;
                }
                if (successText.Length > 0) visionResult.Text = successText;
                _lastLoad = DateTime.MinValue;
                await LoadConfigAsync();
                return true;
            }

            ModelSaveSnapshot CaptureVisionSnapshot()
            {
                var request = new Dictionary<string, object>
                {
                    ["visionProvider"] = VisionProviderValue(),
                    ["visionModel"] = VisionValue(),
                };
                if (VisionIsCustom()) request["visionBaseUrl"] = visionUrl.Text.Trim();
                if (visionKey.Password.Length > 0) request["visionApiKey"] = visionKey.Password.Trim();
                return new ModelSaveSnapshot { Body = request, Secret = visionSecret.Capture(visionKey.Password), PageGeneration = _pageLifetime.Capture() };
            }

            vsave.Click += async (s, e) => await SaveVisionAsync("看图设置已保存。");
            // 「从聊天复制」：服务端取聊天实际在用的地址 + 能看图的模型写进看图设置，
            // 比在界面上比对两遍下拉更省事（聊天模型不能看图时它会自动挑本家的视觉模型）。
            vcopy.Click += async (s, e) =>
            {
                visionResult.Text = "正在从聊天复制…";
                var generation = _pageLifetime.Capture();
                var res = await _saver.SaveSettingsAsync(() => new { visionCopyFromChat = true });
                if (!_pageLifetime.IsCurrent(generation)) return;
                if (!res.Ok)
                {
                    visionResult.Text = "复制失败：" + Api.Message(res);
                    return;
                }
                _lastLoad = DateTime.MinValue;
                await LoadConfigAsync();
                visionResult.Text = "已把聊天的接口地址和看图模型抄过来，还能继续改。";
            };
            visionUrl.LostFocus += async (s, e) => await AutoSaveVisionAsync();
            vtest.Click += async (s, e) =>
            {
                if (!_sup.NodeRunning) { visionResult.Text = "请先启动内核，再测试连接。"; return; }
                visionResult.Text = "正在测…";
                if (!await SaveVisionAsync("")) return;      // 先落盘，测的就是刚才填的模型/Key
                var res = await Api.Post(_sup.Url, "/api/vision/test", "{}", 60);
                visionResult.Text = res.Ok ? "看图通了，它说：" + res.Str("sample") : "没通：" + Api.Message(res);
            };

            // ── 语音面板（原来单独一页，并到 API 接入里）──────────
            var voiceHint = new TextBlock
            {
                Style = (Style)FindResource("Small"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "接收微信语音并识别成文字；默认跟随聊天服务和 Key，按服务商音频用量计费。发送语音请使用工作台「语音发送」。",
            };

            var voiceBody = new StackPanel { Visibility = Visibility.Collapsed };
            var voiceSwitch = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            voiceSwitch.Click += async (s, e) =>
            {
                if (_loading) return;
                var on = voiceSwitch.IsChecked == true;
                if (!on && !AppDialog.Confirm(Window.GetWindow(this),
                    "关闭语音识别会删除专用语音 Key，聊天 Key 保留。\n\n跟随模式下重新开启即可沿用聊天 Key；手动模式需要重新填写专用 Key。继续吗？",
                    "关闭语音识别", okText: "关闭并删除"))
                {
                    _loading = true;
                    voiceSwitch.IsChecked = true;
                    _loading = false;
                    return;
                }
                if (!await PatchAsync("voice.enabled", on))
                {
                    _loading = true;
                    voiceSwitch.IsChecked = !on;
                    _loading = false;
                    return;
                }
                voiceBody.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (!on)
                {
                    var cleared = await _saver.ClearSecretsAsync(new[] { "voice.apiKey" });
                    StatusText.Text = cleared.Ok
                        ? "语音识别已关闭，Key 已删除。"
                        : "已关闭，但语音 Key 清除失败，请重试：" + Api.Message(cleared);
                }
            };
            _loaders["voice.enabled"] = v =>
            {
                if (voiceSwitch.IsKeyboardFocusWithin) return;
                _loading = true;
                voiceSwitch.IsChecked = v.ValueKind == JsonValueKind.True;
                voiceBody.Visibility = voiceSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                _loading = false;
            };
            voicePanel.Children.Add(Row("启用语音识别", voiceSwitch,
                "关闭后不下载语音、不请求 ASR，并删除专用语音 Key；不会删除聊天 Key。", false, ModelLabel));
            voicePanel.Children.Add(voiceBody);
            voiceBody.Children.Add(voiceHint);

            var voiceProvider = new ComboBox { Height = 28, DisplayMemberPath = "Label", MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            var voiceModeHint = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
            var voiceProviderChoices = new List<ProviderItem>
            {
                new ProviderItem { Key = "follow", Label = "跟随主模型（默认，共用聊天 Key）" },
                new ProviderItem { Key = "auto", Label = "自动（按接口地址判断）" },
                new ProviderItem { Key = "deepseek", Label = "DeepSeek 语音识别" },
                new ProviderItem { Key = "dashscope", Label = "阿里云百炼 qwen3-asr-flash" },
                new ProviderItem { Key = "custom", Label = "自定义接口" },
            };
            voiceProvider.ItemsSource = voiceProviderChoices;
            voiceProvider.SelectedItem = voiceProviderChoices[0];
            voiceBody.Children.Add(Row("语音服务", voiceProvider, "", false, ModelLabel));
            voiceBody.Children.Add(voiceModeHint);
            var voiceManual = new StackPanel();
            voiceBody.Children.Add(voiceManual);
            voiceManual.Children.Add(BuildField(new FieldSpec { Path = "voice.apiKey", Label = "语音接口 Key", Kind = "secret", Hint = "留空表示不改；同源地址可复用服务商 Key" }, false, ModelLabel));
            var voiceBaseRow = BuildField(new FieldSpec { Path = "voice.baseUrl", Label = "接口地址", Kind = "text" }, false, ModelLabel);
            var voiceModelRow = BuildField(new FieldSpec { Path = "voice.model", Label = "语音模型", Kind = "text" }, false, ModelLabel);
            voiceManual.Children.Add(voiceBaseRow);
            voiceManual.Children.Add(voiceModelRow);
            voiceBody.Children.Add(TwoCol(
                BuildField(new FieldSpec { Path = "voice.timeoutMs", Label = "识别超时(ms)", Kind = "num", Min = 1000, Max = 300000 }, true),
                BuildField(new FieldSpec { Path = "voice.maxSeconds", Label = "最长语音(秒)", Kind = "num", Min = 1, Max = 600 }, true)));

            string VoiceProviderKey() => (voiceProvider.SelectedItem as ProviderItem)?.Key ?? "follow";
            void PaintVoiceMode()
            {
                var key = VoiceProviderKey();
                voiceManual.Visibility = key == "follow" ? Visibility.Collapsed : Visibility.Visible;
                voiceModeHint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                // 自动模式无法在这里完美判断地址，交给桥接；界面只给通用说明。
                voiceModeHint.Text = key == "follow"
                    ? "自动沿用聊天配置；已适配 DeepSeek 与千问官方语音识别。其他接口请手动指定语音服务。"
                    : key == "deepseek"
                    ? "DeepSeek：POST /v1/tools/asr，最长 120 秒、8 MiB，自动识别语种，不支持流式。"
                    : key == "dashscope"
                        ? "阿里云百炼：multimodal-generation 接口，模型名默认 qwen3-asr-flash。"
                        : key == "custom"
                            ? "自定义：按百炼兼容的 JSON 接口调用；地址和模型都自己填。"
                            : "自动：地址含 dashscope 走百炼，含 api.deepseek.com 走 DeepSeek。";
                voiceModelRow.Visibility = key == "deepseek" ? Visibility.Collapsed : Visibility.Visible;
            }
            voiceProvider.SelectionChanged += async (s, e) =>
            {
                if (_loading || !(voiceProvider.SelectedItem is ProviderItem picked)) return;
                var patch = new Dictionary<string, object> { ["voice.provider"] = picked.Key };
                if (picked.Key == "deepseek")
                {
                    patch["voice.baseUrl"] = "https://api.deepseek.com/v1/tools/asr";
                    patch["voice.model"] = "";
                }
                else if (picked.Key == "dashscope")
                {
                    patch["voice.baseUrl"] = "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation";
                    patch["voice.model"] = "qwen3-asr-flash";
                }
                PaintVoiceMode();
                var res = await _saver.SaveAsync(() => patch);
                if (!res.Ok) StatusText.Text = "语音设置保存失败：" + Api.Message(res);
            };
            _loaders["voice.provider"] = v =>
            {
                if (voiceProvider.IsKeyboardFocusWithin) return;
                _loading = true;
                var key = v.ValueKind == JsonValueKind.String ? v.GetString() : "follow";
                voiceProvider.SelectedItem = voiceProviderChoices.FirstOrDefault(x => x.Key == key) ?? voiceProviderChoices[0];
                _loading = false;
                PaintVoiceMode();
            };
            PaintVoiceMode();
            AddApiProbe(voiceBody, "voice", "测试语音识别（选择 WAV）");
            // ── 搜索：一个总开关；开启后才出现“添加 API” ──────────
            var apiEnabled = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            searchPanel.Children.Add(Row("启用搜索 API", apiEnabled,
                "关闭时不调用任何第三方搜索 API，并清空已添加的 API、删除已保存的搜索 Key。开启后先走 API，失败或没有结果再回退本地 Bing/360。", false, ModelLabel));

            var apiBody = new StackPanel { Visibility = Visibility.Collapsed };
            searchPanel.Children.Add(apiBody);
            var searchFollow = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            var searchFollowHint = new TextBlock { Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
            apiBody.Children.Add(Row("跟随主模型", searchFollow, "默认沿用聊天 Key；DeepSeek / 千问官方接口自动适配，失败回退本地搜索。", false, ModelLabel));
            apiBody.Children.Add(searchFollowHint);
            apiBody.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Small"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                Text = "跟随模式无需重复填写 Key，调用按服务商规则计费。关闭跟随后可手动添加搜索 API；失败或没有结果时回退本地 Bing/360。",
            });

            var apiAdd = new ComboBox { Height = 26, DisplayMemberPath = "Label", MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
            var apiAddButton = new Button { Content = "添加", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
            apiAddButton.Visibility = Visibility.Collapsed;
            var apiAddRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            apiAddRow.Children.Add(apiAdd);
            apiAddRow.Children.Add(apiAddButton);
            apiBody.Children.Add(apiAddRow);

            var apiChoices = new List<ProviderItem>
            {
                new ProviderItem { Key = "deepseek", Label = "DeepSeek 搜索" },
                new ProviderItem { Key = "zhihu", Label = "知乎搜索" },
            };
            var apiProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // BuildApiRow 在构造期就会被调用，没法直接引用后面才定义的 RefreshApiUi。
            Action refreshApi = null;

            StackPanel BuildApiRow(string key, string title, string note, params FrameworkElement[] fields)
            {
                var panel = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
                var head = new DockPanel { LastChildFill = false };
                var remove = new Button { Content = "移除", Style = (Style)FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Right };
                DockPanel.SetDock(remove, Dock.Right);
                head.Children.Add(remove);
                var caption = new StackPanel();
                caption.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
                caption.Children.Add(new TextBlock { Text = note, Style = (Style)FindResource("Small"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                head.Children.Add(caption);
                panel.Children.Add(head);
                foreach (var field in fields) panel.Children.Add(field);
                AddApiProbe(panel, key, "测试连接");

                remove.Click += async (s, e) =>
                {
                    if (_loading) return;
                    if (!AppDialog.Confirm(Window.GetWindow(this),
                        "移除「" + title + "」会同时删除它的 Key / Secret。\n\n继续吗？", "移除 API", okText: "移除")) return;
                    apiProviders.Remove(key);
                    var saved = await _saver.SaveAsync(() => new Dictionary<string, object>
                    {
                        ["websearch.api.providers"] = apiProviders.OrderBy(x => x).ToArray(),
                    });
                    if (!saved.Ok)
                    {
                        // 没落盘就不能只改界面：否则列表看起来移除了，运行时还在调用。
                        apiProviders.Add(key);
                        StatusText.Text = "移除失败：" + Api.Message(saved);
                        refreshApi?.Invoke();
                        return;
                    }
                    var cleared = await _saver.ClearSecretsAsync(new[] { SecretPathOf(key) });
                    if (!cleared.Ok) StatusText.Text = "已移除，但 Key 清除失败，请重试：" + Api.Message(cleared);
                    else StatusText.Text = "已移除「" + title + "」并删除 Key。";
                    refreshApi?.Invoke();
                };
                return panel;
            }

            string SecretPathOf(string key) => key == "zhihu" ? "websearch.zhihu.accessSecret" : "websearch.deepseek.apiKey";

            var apiDeepseekRow = BuildApiRow("deepseek", "DeepSeek 搜索",
                "POST /v1/tools/search · 5 元/千次成功请求；失败/无结果不计费。",
                BuildField(new FieldSpec { Path = "websearch.deepseek.apiKey", Label = "DeepSeek Key", Kind = "secret", Hint = "留空表示不改；只用于搜索 API" }, false, ModelLabel));
            var apiZhihuRow = BuildApiRow("zhihu", "知乎搜索",
                "官方开放平台 zhihu_search · 免费额度外 20 元/千次；每天最多 4999 次，超了只走本地。",
                BuildField(new FieldSpec { Path = "websearch.zhihu.accessSecret", Label = "Access Secret", Kind = "secret", Hint = "知乎开放平台个人中心申请；留空表示不改" }, false, ModelLabel),
                BuildField(new FieldSpec { Path = "websearch.zhihu.dailyLimit", Label = "每日上限", Kind = "num", Min = 0, Max = 100000, Hint = "0 = 不限；默认 4999，超过后不调用知乎" }, false, ModelLabel));
            apiBody.Children.Add(apiDeepseekRow);
            apiBody.Children.Add(apiZhihuRow);

            void RefreshApiUi()
            {
                var on = apiEnabled.IsChecked == true;
                var manual = searchFollow.IsChecked != true;
                apiBody.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                var selected = (apiAdd.SelectedItem as ProviderItem)?.Key ?? apiProviders.FirstOrDefault();
                apiDeepseekRow.Visibility = on && manual && selected == "deepseek" ? Visibility.Visible : Visibility.Collapsed;
                apiZhihuRow.Visibility = on && manual && selected == "zhihu" ? Visibility.Visible : Visibility.Collapsed;
                if (apiAdd.ItemsSource == null) apiAdd.ItemsSource = apiChoices;
                apiAddRow.Visibility = on && manual ? Visibility.Visible : Visibility.Collapsed;
            }

            searchFollow.Click += async (s, e) =>
            {
                if (_loading) return;
                var on = searchFollow.IsChecked == true;
                if (!await PatchAsync("websearch.api.followChat", on)) searchFollow.IsChecked = !on;
                RefreshApiUi();
            };
            _loaders["websearch.api.followChat"] = v =>
            {
                if (searchFollow.IsKeyboardFocusWithin) return;
                searchFollow.IsChecked = v.ValueKind == JsonValueKind.True;
                RefreshApiUi();
            };

            async Task<bool> SaveApiProvidersAsync()
            {
                var res = await _saver.SaveAsync(() => new Dictionary<string, object>
                {
                    ["websearch.api.providers"] = apiProviders.OrderBy(x => x).ToArray(),
                });
                if (!res.Ok) StatusText.Text = "搜索 API 列表保存失败：" + Api.Message(res);
                return res.Ok;
            }

            apiEnabled.Click += async (s, e) =>
            {
                if (_loading) return;
                var on = apiEnabled.IsChecked == true;
                if (!on && !AppDialog.Confirm(Window.GetWindow(this),
                    "关闭搜索 API 会：\n\n• 清空已添加的 API\n• 删除 DeepSeek 搜索 Key 和知乎 Access Secret\n\n下次开启需要重新添加并填写。继续吗？",
                    "关闭搜索 API", okText: "关闭并删除"))
                {
                    _loading = true;
                    apiEnabled.IsChecked = true;
                    _loading = false;
                    RefreshApiUi();
                    return;
                }
                var patch = new Dictionary<string, object> { ["websearch.api.enabled"] = on };
                if (!on) patch["websearch.api.providers"] = Array.Empty<string>();
                var res = await _saver.SaveAsync(() => patch);
                if (!res.Ok)
                {
                    StatusText.Text = "搜索 API 开关保存失败：" + Api.Message(res);
                    _loading = true;
                    apiEnabled.IsChecked = !on;
                    _loading = false;
                    RefreshApiUi();
                    return;
                }
                if (!on)
                {
                    apiProviders.Clear();
                    var cleared = await _saver.ClearSecretsAsync(new[]
                    {
                        "websearch.deepseek.apiKey",
                        "websearch.zhihu.accessSecret",
                    });
                    if (!cleared.Ok) StatusText.Text = "已关闭，但 Key 清除失败，请重试：" + Api.Message(cleared);
                    else StatusText.Text = "搜索 API 已关闭，列表和 Key 已清除。";
                }
                else
                {
                    StatusText.Text = "搜索 API 已开启，请添加要使用的 API。";
                }
                RefreshApiUi();
            };

            apiAddButton.Click += async (s, e) =>
            {
                if (_loading || !(apiAdd.SelectedItem is ProviderItem picked)) return;
                if (!apiProviders.Add(picked.Key)) return;
                if (!await SaveApiProvidersAsync())
                {
                    apiProviders.Remove(picked.Key);
                    StatusText.Text = "添加失败，未写入配置。";
                }
                RefreshApiUi();
            };

            _loaders["websearch.api.enabled"] = v =>
            {
                _loading = true;
                apiEnabled.IsChecked = v.ValueKind == JsonValueKind.True;
                _loading = false;
                RefreshApiUi();
            };
            _loaders["websearch.api.providers"] = v =>
            {
                _loading = true;
                apiProviders.Clear();
                if (v.ValueKind == JsonValueKind.Array)
                    foreach (var item in v.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            apiProviders.Add(item.GetString());
                apiAdd.ItemsSource = apiChoices;
                apiAdd.SelectedItem = apiChoices.FirstOrDefault(x => apiProviders.Contains(x.Key));
                _loading = false;
                RefreshApiUi();
            };
            refreshApi = RefreshApiUi;
            apiAdd.SelectionChanged += async (_, __) => {
                if (_loading || apiAdd.SelectedItem is not ProviderItem picked) return;
                var previous = apiProviders.ToArray();
                apiProviders.Clear();
                apiProviders.Add(picked.Key);
                if (!await SaveApiProvidersAsync())
                {
                    apiProviders.Clear();
                    foreach (var item in previous) apiProviders.Add(item);
                }
                RefreshApiUi();
            };
            RefreshApiUi();

            // 「深度思考」已经挪到聊天页签（和 /api/settings 共用 thinking），这里不再重复放一个配置项。

            _loadModel = (set, provs) =>
            {
                if (key.Password.Length == 0) chatKeyMask.Text = JsonRead.Str(set, "keyMasked");
                _providers = provs;
                if (chatSaves.HasPendingChanges || visionSaves.HasPendingChanges) return;
                _loading = true;
                // 每次换一个新的 List 实例：WPF 会按「数据源实例」缓存默认视图，
                // 对同一个 List 清空再填充后，下拉的 Items 还是旧的那批对象，
                // SelectedItem 指向新对象就匹配不上 → 显示成空白（换模型服务后就这样）。
                var list = new List<ProviderItem>();
                foreach (var kv in provs)
                    list.Add(new ProviderItem { Key = kv.Key, Label = JsonRead.Str(kv.Value, "label") + "（" + kv.Key + "）" });
                var keepKey = (cmb.SelectedItem as ProviderItem)?.Key;
                cmb.ItemsSource = list;
                var cur = JsonRead.Str(set, "provider");
                // 以服务端值为准；万一认不出（服务刚重启、列表还没刷新）就退回上一次选中的，
                // 再不行取第一项。以前焦点在下拉里时干脆不恢复，列表一重建就空白。
                var picked = list.FirstOrDefault(x => x.Key == cur)
                    ?? list.FirstOrDefault(x => x.Key == keepKey)
                    ?? list.FirstOrDefault();
                cmb.SelectedItem = picked;
                FillChatOptions(picked != null && provs.TryGetValue(picked.Key, out var pv) ? pv : default);
                if (!baseUrl.IsKeyboardFocusWithin) baseUrl.Text = JsonRead.Str(set, "baseUrl");
                if (!model.IsKeyboardFocusWithin) model.Text = JsonRead.Str(set, "model");
                RebuildVisionProviders();   // 自动项是否出现取决于聊天模型能不能识图
                var vis = set.ValueKind == JsonValueKind.Object && set.TryGetProperty("vision", out var vv) ? vv : default;
                var vmodel = JsonRead.Str(vis, "model");
                var manual = vis.ValueKind == JsonValueKind.Object && vis.TryGetProperty("manual", out var mf) && mf.ValueKind == JsonValueKind.True;
                var vprov = JsonRead.Str(vis, "provider");
                if (!visionProvider.IsKeyboardFocusWithin && !visionModel.IsKeyboardFocusWithin)
                {
                    if (manual && vmodel.Length > 0)
                    {
                        var customSaved = vprov == "custom"
                            || (vis.ValueKind == JsonValueKind.Object && vis.TryGetProperty("custom", out var cf) && cf.ValueKind == JsonValueKind.True);
                        if (customSaved && vprov.Length == 0) vprov = "custom";   // 老数据只标了 custom 没存 provider
                        if (vprov.Length == 0 && !customSaved) vprov = OwnerProviderOf(vmodel);
                        var hit = visionProviders.FirstOrDefault(x => x.Key == vprov);
                        if (hit == null) hit = visionProviders.FirstOrDefault(x => x.Key == cur);   // 认不出就用聊天服务商，别把手动模型弄丢
                        visionProvider.SelectedItem = hit ?? visionProviders.FirstOrDefault();
                        if (!visionUrl.IsKeyboardFocusWithin) visionUrl.Text = JsonRead.Str(vis, "baseUrl");
                        if (hit != null && hit.Key == "custom")
                        {
                            // 自定义：模型名可能不在任何预设里，直接放进可编辑下拉的文本框
                            visionModel.IsEditable = true;
                            visionModel.ItemsSource = null;
                            visionModel.Text = vmodel;
                        }
                        else
                        {
                            if (hit != null && hit.Key.Length > 0) FillVisionModels(hit.Key);
                            var sight = visionModel.ItemsSource as List<string> ?? new List<string>();
                            if (!sight.Contains(vmodel)) { sight.Add(vmodel); visionModel.ItemsSource = null; visionModel.ItemsSource = sight; }
                            visionModel.SelectedItem = vmodel;
                        }
                    }
                    else
                    {
                        // 自动跟随：聊天模型能识图时才存在这一项；不能识图就落到聊天服务商。
                        var autoItem = visionProviders.FirstOrDefault(x => x.Key == "");
                        visionProvider.SelectedItem = autoItem;
                        EnsureVisionSelection();
                        if (visionProvider.SelectedItem is ProviderItem sel && sel.Key.Length > 0) FillVisionModels(sel.Key);
                    }
                }
                if (!thinking.IsKeyboardFocusWithin)
                    thinking.IsChecked = set.ValueKind == JsonValueKind.Object
                        && set.TryGetProperty("thinking", out var th) && th.ValueKind == JsonValueKind.True;
                ApplyVisionMode();
                SyncVisionTip();
                _loading = false;
            };

            _loadVision = (set) =>
            {
                if (set.ValueKind == JsonValueKind.Object && set.TryGetProperty("voice", out var voiceStatus) && VoiceProviderKey() == "follow")
                {
                    voiceModeHint.Text = JsonRead.Str(voiceStatus, "hint") +
                        (voiceStatus.TryGetProperty("hasKey", out var hasVoiceKey) && hasVoiceKey.ValueKind == JsonValueKind.True ? " 已配置聊天 Key。" : " 当前没有可用的识别 Key。");
                    voiceModeHint.SetResourceReference(TextBlock.ForegroundProperty,
                        voiceStatus.TryGetProperty("supported", out var voiceSupported) && voiceSupported.ValueKind == JsonValueKind.False ? "Bad" : "Dim");
                }
                if (set.ValueKind == JsonValueKind.Object && set.TryGetProperty("search", out var searchStatus))
                    searchFollowHint.Text = JsonRead.Str(searchStatus, "hint");
                var v = set.ValueKind == JsonValueKind.Object && set.TryGetProperty("vision", out var vv) ? vv : default;
                var curModel = JsonRead.Str(v, "model");
                var manual = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("manual", out var mf) && mf.ValueKind == JsonValueKind.True;
                if (v.ValueKind == JsonValueKind.Object)
                {
                    if (visionKey.Password.Length == 0) visionKeyMask.Text = JsonRead.Str(v, "keyMasked");
                    var configured = v.TryGetProperty("configured", out var c) && c.ValueKind == JsonValueKind.True;
                    var masked = v.TryGetProperty("keyMasked", out var km) && km.ValueKind == JsonValueKind.String ? km.GetString() : "";
                    var follow = manual ? "" : "（自动）";
                    visionHint.Text = (configured ? "已配置：" + masked : "没配置。选「自动跟随」就用聊天那把 Key；本机/内网看图服务可以留空。")
                        + (curModel.Length > 0 ? " 当前看图模型：" + curModel + follow : "")
                        + " " + JsonRead.Str(v, "hint");
                    visionHint.SetResourceReference(TextBlock.ForegroundProperty,
                        v.TryGetProperty("supported", out var visionSupported) && visionSupported.ValueKind == JsonValueKind.False ? "Bad"
                        : JsonRead.Str(v, "capabilityHint").Length > 0 ? "Warn" : "Dim");
                    var ep = JsonRead.Str(v, "baseUrl");
                    var custom = v.ValueKind == JsonValueKind.Object && v.TryGetProperty("custom", out var cst) && cst.ValueKind == JsonValueKind.True;
                    visionEndpoint.Text = ep.Length > 0
                        ? "接口地址：" + ep + (custom ? "（自定义）" : manual ? "（手动指定）" : "（自动跟随聊天模型）")
                        : "";
                }
            };

            return card;
        }

        // ── 读写 ────────────────────────────────────────────────
        /// <summary>
        /// 周期刷新（MainWindow 2 秒定时器驱动）：只更新状态行，不动表单——
        /// 否则会把用户正在编辑的值冲掉，随后 LostFocus 又把旧值写回服务端（M30）。
        /// 全量回填只在页面载入（Loaded）做；表单还没载入过（比如内核后启动）时补一次。
        /// </summary>
        public async Task RefreshAsync()
        {
            if (_configLoaded && _loadedOnline != _sup.NodeRunning)
            {
                _configLoaded = false;
                _lastLoad = DateTime.MinValue;
            }
            KernelHint.Text = _sup.NodeRunning
                ? "改完立即写回 config.json（密钥留空表示不改）"
                : "可保存到本地，启动后生效。";
            if (SubPageView.Visibility == Visibility.Visible && SubPageContent.Content is IRefreshable sub)
            {
                await sub.RefreshAsync();
                return;
            }
            if (!_configLoaded) await LoadConfigAsync();      // 还没回填过，补一次
            else _previewLoaded.TrySetResult(true);
        }

        /// <summary>全量回填：把服务端的值刷回所有表单控件（跳过正在编辑的）。</summary>
        private async Task LoadConfigAsync()
        {
            if ((DateTime.Now - _lastLoad).TotalSeconds < 5) return;   // 节流：≥5 秒一次（照 SettingsPage 原有做法）
            if (_loadingConfig) return;
            _loadingConfig = true;
            _lastLoad = DateTime.Now;
            var generation = _pageLifetime.Capture();
            var revision = _configurationRevision;
            try
            {
                var cfg = await _sup.ReadConfigurationAsync("/api/config");
                // 首次挂载时 OnNavChanged 和 Loaded 可能各触发一次刷新，旧 generation 会被判失效；
                // 这一轮已经拿到数据就继续填表，避免用户首次打开设置要空白等 5 秒。
                if (!_pageLifetime.IsCurrent(generation) && _configLoaded) return;
                if (!cfg.Ok) { KernelHint.Text = cfg.Error; return; }
                var settings = await _sup.ReadConfigurationAsync("/api/settings");
                if ((!_pageLifetime.IsCurrent(generation) && _configLoaded)
                    || (_configLoaded && revision != _configurationRevision))
                {
                    _lastLoad = DateTime.MinValue;
                    return;
                }
                var config = cfg.Prop("config");
                _configRoot = config.ValueKind == JsonValueKind.Object ? config.Clone() : default;
                _configOverrides.Clear();
                var secrets = cfg.Prop("secrets");
                var secretMasks = cfg.Prop("secretMasks");
                foreach (var loader in _secretMaskLoaders)
                    loader.Value(JsonRead.Str(secretMasks, loader.Key));
                if (_adminSpellStatus != null)
                    _adminSpellStatus.Text = secrets.ValueKind == JsonValueKind.Object &&
                        secrets.TryGetProperty("admin.spell", out var spell) && spell.ValueKind == JsonValueKind.True
                        ? "已设置" : "未设置";
                _loading = true;
                try
                {
                    foreach (var kv in _loaders)
                        kv.Value(GetPath(config, kv.Key));
                }
                finally { _loading = false; }

                if (settings.Ok)
                {
                    var provs = new Dictionary<string, JsonElement>();
                    var p = settings.Prop("providers");
                    if (p.ValueKind == JsonValueKind.Object)
                        foreach (var it in p.EnumerateObject()) provs[it.Name] = it.Value;
                    _loadModel?.Invoke(settings.Root, provs);
                    _loadVision?.Invoke(settings.Root);
                }
                _configLoaded = true;
                _loadedOnline = _sup.NodeRunning;
                _previewLoaded.TrySetResult(true);
                RefreshSectionSummaries();
                RefreshFieldState();
                RefreshSearchIfActive();
            }
            finally
            {
                _loadingConfig = false;
            }
        }

        private static JsonElement GetPath(JsonElement root, string dotted)
        {
            var cur = root;
            foreach (var part in dotted.Split('.'))
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next)) return default;
                cur = next;
            }
            return cur;
        }
    }
}










