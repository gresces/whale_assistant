using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WhaleLauncher.Views
{
    public sealed class PresetItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public string Label { get; set; }
        public bool IsUser => string.Equals(Source, "user", StringComparison.OrdinalIgnoreCase);
        public override string ToString() => Label ?? Id ?? "";
    }

    public partial class PersonaPage : UserControl, IRefreshable, IEmbeddedPage
    {
        private readonly RuntimeSupervisor _sup;
        private readonly DispatcherTimer _saveTimer;
        private bool _loading;
        private bool _loaded;
        private bool _loadedOnline;
        private bool _kernelReachable;
        private bool _loadInProgress;
        private bool _applyingPreset;
        private long _editRevision;
        private long _savedRevision;
        private readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);
        private string _loadedEmoji = "正常";   // 上次载入的颜文字档位，用来判断用户有没有改过
        private string _currentPresetName = "";

        // 主人名单复用 ContactInfo：名字和 wxid 本来就是联系人的两个字段，
        // 再立一个 MasterEntry 只会多一套同样的模型。
        private readonly List<ContactInfo> _masterEntries = new List<ContactInfo>();
        private DispatcherTimer _masterSearchTimer;
        private int _masterSearchSeq;
        private bool _masterRendering;

        private string _currentPresetId = "";   // 当前人设来自哪一版，用来把下拉框选到正确项

        // ── 提示词画布 ──────────────────────────────────────────
        // 文本段一个编辑器一个 600ms 防抖计时器：分开计时，切着改两段不会互相取消。
        private readonly Dictionary<string, DispatcherTimer> _blockTimers = new Dictionary<string, DispatcherTimer>();
        private readonly Dictionary<string, TextBox> _blockEditors = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, TextBlock> _blockStatus = new Dictionary<string, TextBlock>();
        private readonly Dictionary<string, string> _blockDefaults = new Dictionary<string, string>();
        private readonly HashSet<string> _blockFailed = new HashSet<string>();
        private readonly List<CustomBlockView> _customBlocks = new List<CustomBlockView>();
        private JsonElement _personaRoot;

        private string _chatType = "private";
        private bool _previewAvailable;
        private bool _previewUnsupported;
        private bool _previewNarrow;
        private int _previewSeq;
        private readonly TaskCompletionSource<bool> _previewLoaded =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private const int BlockSaveDelayMs = 600;
        private const int MaxCustomBlocks = 10;
        private const string DefaultPersonality = "自然、友善、有分寸；会认真回答问题，也会正常闲聊，不刻意卖萌或扮演固定角色。";
        private const string DefaultBackground = "运行在这台电脑上的聊天伙伴。";
        // 主窗口最小 1080；页面还要减去 232 的导航栏，所以只看窗口宽度永远不会触发窄布局。
        // 窗口 <1080 或工作台可用宽度 <820 时，把预览收进底部，两种来源都覆盖。
        private const double NarrowWindowWidth = 1080;
        private const double NarrowPageWidth = 820;

        private sealed class CustomBlockView
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Text { get; set; }
        }

        public PersonaPage(RuntimeSupervisor sup)
        {
            InitializeComponent();
            _sup = sup;

            foreach (var field in new TextBox[] { TxtName, TxtAge, TxtJob, TxtLook, TxtOwner, TxtAliases,
                TxtPrivate, TxtGroup, TxtBubbles, TxtTurns, TxtGroupTurns })
                field.TextChanged += OnPersonaTextChanged;

            AttachBlock("personality", TxtPerson, PersonalityStatus, PersonalityCount, 500);
            AttachBlock("background", TxtBack, BackgroundStatus, BackgroundCount, 500);
            AttachBlock("custom", TxtPrompt, CustomStatus, CustomCount, 24000);

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _saveTimer.Tick += async (_, __) =>
            {
                _saveTimer.Stop();
                await SaveAllAsync("已保存");
            };

            _masterSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _masterSearchTimer.Tick += async (_, __) =>
            {
                _masterSearchTimer.Stop();
                await SearchMasterAsync(TxtMasterSearch.Text.Trim());
            };

            UpdateSpeechDerived();
            MarkPreviewTabs();
            SetSaveState("");
            SetPreviewStatus("等待读取…", "Dim");
            Loaded += async (_, __) => await RefreshAsync();
        }

        public bool Embedded
        {
            set
            {
                if (!value) return;
                PageRoot.Margin = new Thickness(0);
            }
        }

        /// <summary>截图工具等第一次加载完成用；失败也放行，免得截图卡住。</summary>
        public Task PreviewLoaded => _previewLoaded.Task;

        public async Task RefreshAsync()
        {
            if (_applyingPreset) return;
            if (_loaded && _loadedOnline == _sup.NodeRunning)
            {
                // 状态定时器每两秒调用此方法，不能反复回填表单。
                return;
            }
            if (_loaded && (_savedRevision != _editRevision || _blockFailed.Count > 0 || _saveGate.CurrentCount == 0)) return;
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (_loadInProgress) return;
            _loadInProgress = true;
            var revision = _editRevision;
            try
            {
                var res = await _sup.ReadConfigurationAsync("/api/persona");
                if (!res.Ok) { SetSaveState(res.Error, "Bad"); return; }

                var set = await _sup.ReadConfigurationAsync("/api/settings");
                var cfg = await _sup.ReadConfigurationAsync("/api/config");
                // 三份表单数据必须全部成功，不能拿旧控件或空名单完成一次“成功加载”。
                if (!set.Ok || !cfg.Ok)
                {
                    SetSaveState("读取配置失败：" + Api.Message(!set.Ok ? set : cfg), "Bad");
                    return;
                }
                if (revision != _editRevision ||
                    (_loaded && (_savedRevision != _editRevision || _saveGate.CurrentCount == 0)))
                    return;

                // 内核可能由上一个启动器实例启动：同端口健康就算在线，预览 / 风格 / 记忆都能读。
                _kernelReachable = await _sup.KernelReachableAsync();

                _loading = true;
                try
                {
                    var p = res.Prop("persona");
                    if (p.ValueKind == JsonValueKind.Object)
                    {
                        _personaRoot = p.Clone();
                        ChkNewBehavior.IsChecked = !p.TryGetProperty("newBehavior", out var behavior) ||
                            behavior.ValueKind != JsonValueKind.False;
                        TxtName.Text = S(p, "name");
                        TxtAge.Text = L(p, "age").ToString();
                        TxtJob.Text = S(p, "job");
                        TxtLook.Text = S(p, "appearance");
                        TxtPerson.Text = S(p, "personality");
                        TxtBack.Text = S(p, "background");
                        TxtPrompt.Text = S(p, "customPrompt");

                        TxtPrivate.Text = L(p, "privateMaxChars").ToString();
                        TxtGroup.Text = L(p, "groupMaxChars").ToString();
                        TxtBubbles.Text = L(p, "maxBubbles").ToString();
                        TxtTurns.Text = L(p, "historyTurns").ToString();
                        TxtGroupTurns.Text = L(p, "groupHistoryTurns").ToString();
                        var emoji = S(p, "emoji");
                        _loadedEmoji = string.IsNullOrEmpty(emoji) ? "正常" : emoji;
                        SldEmoji.Value = EmojiPercent(cfg, _loadedEmoji);

                        ChkProactive.IsChecked = B(p, "proactive");
                        ChkOpinions.IsChecked = B(p, "opinions");

                        var src = S(p, "sourcePreset");
                        _currentPresetId = src;
                        BuildCustomBlocksFromPersona(p, force: true);
                    }

                    if (set.Ok)
                    {
                        var owner = set.Str("userName");
                        TxtOwner.Text = string.IsNullOrEmpty(owner) ? S(p, "ownerName") : owner;
                    }

                    if (cfg.Ok && cfg.Prop("config").ValueKind == JsonValueKind.Object &&
                        cfg.Prop("config").TryGetProperty("persona", out var pcfg) && pcfg.ValueKind == JsonValueKind.Object)
                    {
                        TxtAliases.Text = JsonRead.Lines(pcfg, "aliases");
                        ChkRoast.IsChecked = B(pcfg, "masterRoast");
                        if (pcfg.TryGetProperty("master", out var m) && m.ValueKind == JsonValueKind.Object)
                        {
                            LoadMasterEntries(m);
                        }
                        else
                        {
                            _masterEntries.Clear();
                            RenderMasterChips();
                        }
                    }
                }
                finally { _loading = false; }

                _blockFailed.Clear();
                ResetBlockStatuses();
                UpdateSpeechDerived();
                _savedRevision = revision;
                _loaded = true;

                await LoadPresetsAsync(_currentPresetId);
                await RefreshPreviewAsync(applyBlocks: true);
                await LoadMemoryChatsAsync();
                await LoadStyleAsync();
                _loadedOnline = _sup.NodeRunning;
                SetSaveState(_kernelReachable ? "已保存 · " + Now : "可保存到本地 · 启动后生效");
            }
            finally
            {
                _loadInProgress = false;
                _previewLoaded.TrySetResult(true);
            }
        }

        // ── 文本段：600ms 防抖 / 失焦即保存 ──────────────────────

        private DispatcherTimer BlockTimer(string id)
        {
            if (_blockTimers.TryGetValue(id, out var timer)) return timer;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BlockSaveDelayMs) };
            timer.Tick += async (_, __) =>
            {
                timer.Stop();
                await SaveBlockAsync(id);
            };
            _blockTimers[id] = timer;
            return timer;
        }

        private void AttachBlock(string id, TextBox box, TextBlock status, TextBlock counter, int maxLength)
        {
            _blockEditors[id] = box;
            _blockStatus[id] = status;
            if (maxLength > 0) box.MaxLength = maxLength;
            UpdateBlockCounter(counter, box.Text);

            box.TextChanged += (_, __) =>
            {
                UpdateBlockCounter(counter, box.Text);
                if (_loading || !_loaded) return;
                _editRevision++;
                SetSaveState("等待自动保存…", "Dim");
                SetBlockStatus(status, "", "Dim");
                var timer = BlockTimer(id);
                timer.Stop();
                timer.Start();
            };
            box.LostFocus += async (_, __) =>
            {
                if (_loading || !_loaded) return;
                var timer = BlockTimer(id);
                if (!timer.IsEnabled) return;
                timer.Stop();
                await SaveBlockAsync(id);
            };
        }

        private static void UpdateBlockCounter(TextBlock counter, string text)
        {
            if (counter == null) return;
            counter.Text = (text ?? "").Length.ToString("N0") + " 字";
        }

        private static void SetBlockStatus(TextBlock status, string text, string tone)
        {
            if (status == null) return;
            status.Text = text ?? "";
            status.SetResourceReference(TextBlock.ForegroundProperty, tone);
        }

        private void ResetBlockStatuses()
        {
            foreach (var pair in _blockStatus)
            {
                var empty = pair.Key.StartsWith("custom-", StringComparison.Ordinal)
                    && _customBlocks.Any(b => b.Id == pair.Key && string.IsNullOrWhiteSpace(b.Text));
                SetBlockStatus(pair.Value, empty ? "空段落 · 不会进入提示词" : "", "Dim");
            }
        }

        private async Task SaveBlockAsync(string id)
        {
            if (!_blockEditors.TryGetValue(id, out var box) || !_loaded) return;
            var text = box.Text ?? "";
            var revision = _editRevision;
            var status = _blockStatus.TryGetValue(id, out var s) ? s : null;
            var isCustom = id.StartsWith("custom-", StringComparison.Ordinal);

            await _saveGate.WaitAsync();
            try
            {
                if (_loading) return;
                SetBlockStatus(status, "正在保存…", "Dim");
                ApiResult res = null;
                // 老内核没有 /api/persona/block：固定文本段退回 /api/persona，内容不丢。
                if (!_previewUnsupported || isCustom)
                {
                    res = await _sup.SaveConfigurationAsync("/api/persona/block",
                        new { id, text, chatType = _chatType, isMaster = 1 });
                }

                var unavailable = res == null || res.StatusCode == 0 || res.StatusCode == 404;
                if (unavailable && !isCustom)
                {
                    var fallback = await SaveTextFallbackAsync(id, text);
                    if (fallback != null && fallback.Ok)
                    {
                        _blockFailed.Remove(id);
                        _savedRevision = revision;
                        SetBlockStatus(status, "已保存（旧内核） · " + Now, "Ok");
                        SetSaveState(revision == _editRevision
                            ? "已保存 · " + Now
                            : "较早修改已保存 · 新修改等待保存…", "Text2");
                    }
                    else
                    {
                        _blockFailed.Add(id);
                        var reason = fallback == null ? Api.Message(res) : Api.Message(fallback);
                        SetBlockStatus(status, "保存失败：" + reason, "Bad");
                        SetSaveState("保存失败：" + reason, "Bad");
                    }
                }
                else if (res != null && res.Ok)
                {
                    ApplyPreview(res, false);
                    _blockFailed.Remove(id);
                    _savedRevision = revision;
                    SetBlockStatus(status, "已保存 · " + Now, "Ok");
                    SetSaveState(revision == _editRevision
                        ? "已保存 · " + Now
                        : "较早修改已保存 · 新修改等待保存…", "Text2");
                }
                else
                {
                    _blockFailed.Add(id);
                    var reason = res == null ? "内核没有响应" : Api.Message(res);
                    if (unavailable)
                        reason = _kernelReachable
                            ? "当前内核不支持段落保存，升级后可用"
                            : "启动内核后可保存这个段落";
                    SetBlockStatus(status, "保存失败：" + reason, "Bad");
                    SetSaveState("保存失败：" + reason, "Bad");
                }

                // 保存期间又改了：让计时器再走一轮，不把新内容当成已保存。
                if (!string.Equals(box.Text ?? "", text, StringComparison.Ordinal))
                {
                    var timer = BlockTimer(id);
                    timer.Stop();
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                _blockFailed.Add(id);
                SetBlockStatus(status, "保存失败：" + Api.FriendlyError(ex), "Bad");
                SetSaveState("保存失败：" + Api.FriendlyError(ex), "Bad");
            }
            finally { _saveGate.Release(); }
        }

        private async Task<ApiResult> SaveTextFallbackAsync(string id, string text)
        {
            if (id == "personality")
                return await _sup.SaveConfigurationAsync("/api/persona", new { personality = text });
            if (id == "background")
                return await _sup.SaveConfigurationAsync("/api/persona", new { background = text });
            if (id == "custom")
                return await _sup.SaveConfigurationAsync("/api/persona", new { customPrompt = text });
            return null;
        }

        private async void OnRestorePersonality(object sender, RoutedEventArgs e)
            => await RestoreBlockAsync("personality", TxtPerson, PersonalityStatus, "性格", DefaultPersonality);

        private async void OnRestoreBackground(object sender, RoutedEventArgs e)
            => await RestoreBlockAsync("background", TxtBack, BackgroundStatus, "经历与背景", DefaultBackground);

        private async Task RestoreBlockAsync(string id, TextBox box, TextBlock status,
            string label, string fallback)
        {
            if (!_loaded) return;
            if (!AppDialog.Confirm(Window.GetWindow(this),
                $"把「{label}」恢复成默认内容？\n\n当前内容会被替换，并自动保存。",
                "恢复默认", okText: "恢复默认", cancelText: "取消")) return;
            var text = _blockDefaults.TryGetValue(id, out var saved) && !string.IsNullOrWhiteSpace(saved)
                ? saved : fallback;
            box.Text = text;
            SetBlockStatus(status, "已恢复默认，正在保存…", "Dim");
            var timer = BlockTimer(id);
            timer.Stop();
            await SaveBlockAsync(id);
        }

        // ── 自定义段落 ──────────────────────────────────────────

        private void BuildCustomBlocksFromPersona(JsonElement persona, bool force = false)
        {
            if (!force && CustomBlocksHost.IsKeyboardFocusWithin) return;
            var list = new List<CustomBlockView>();
            var blocks = JsonRead.Prop(persona, "customBlocks");
            if (blocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in blocks.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = S(item, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    list.Add(new CustomBlockView
                    {
                        Id = id,
                        Title = S(item, "title"),
                        Text = S(item, "text"),
                    });
                }
            }
            RebuildCustomBlocks(list);
        }

        private void RebuildCustomBlocks(List<CustomBlockView> blocks)
        {
            var keep = new HashSet<string>(blocks.Select(b => b.Id));
            foreach (var old in _customBlocks)
            {
                if (_blockTimers.TryGetValue(old.Id, out var timer))
                {
                    timer.Stop();
                    _blockTimers.Remove(old.Id);
                }
                _blockEditors.Remove(old.Id);
                _blockStatus.Remove(old.Id);
                _blockDefaults.Remove(old.Id);
                if (!keep.Contains(old.Id)) _blockFailed.Remove(old.Id);
            }
            _customBlocks.Clear();
            _customBlocks.AddRange(blocks);
            CustomBlocksHost.Children.Clear();

            foreach (var block in _customBlocks)
                CustomBlocksHost.Children.Add(BuildCustomBlockCard(block));

            CustomBlocksCount.Text = _customBlocks.Count + " / " + MaxCustomBlocks + " 段";
            BtnAddBlock.IsEnabled = _customBlocks.Count < MaxCustomBlocks;
            BtnAddBlock.ToolTip = _customBlocks.Count < MaxCustomBlocks
                ? "标题会作为提示词里的小标题；空正文不会进入提示词。"
                : "已经到 " + MaxCustomBlocks + " 段上限，先删掉一段再添加。";
            if (_customBlocks.Count >= MaxCustomBlocks)
                CustomBlocksStatus.Text = "已到 " + MaxCustomBlocks + " 段上限";
        }

        private Border BuildCustomBlockCard(CustomBlockView block)
        {
            var panel = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 10),
                Margin = new Thickness(0, 0, 0, 10),
            };
            panel.SetResourceReference(Border.BorderBrushProperty, "LineSoft");

            var stack = new StackPanel();
            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(block.Title) ? "未命名段落" : block.Title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            header.Children.Add(title);

            var remove = new Button
            {
                Content = "删除",
                Style = (Style)FindResource("Ghost"),
                Padding = new Thickness(9, 2, 9, 2),
                FontSize = 12,
                Tag = block.Id,
            };
            remove.Click += OnDeleteBlock;
            AutomationProperties.SetName(remove, "删除自定义段落 " + title.Text);
            Grid.SetColumn(remove, 1);
            header.Children.Add(remove);
            stack.Children.Add(header);

            var body = new TextBox
            {
                Text = block.Text ?? "",
                MinHeight = 84,
                MaxLength = 2000,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            AutomationProperties.SetName(body, "段落 " + title.Text + " 正文");
            stack.Children.Add(body);

            var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            count.SetResourceReference(TextBlock.StyleProperty, "Small");
            DockPanel.SetDock(count, Dock.Right);
            footer.Children.Add(count);

            var status = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0),
            };
            status.SetResourceReference(TextBlock.StyleProperty, "Small");
            footer.Children.Add(status);
            stack.Children.Add(footer);

            panel.Child = stack;
            AttachBlock(block.Id, body, status, count, 2000);
            if (string.IsNullOrWhiteSpace(block.Text))
                SetBlockStatus(status, "空段落 · 不会进入提示词", "Dim");
            return panel;
        }

        private async void OnAddBlock(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            if (_customBlocks.Count >= MaxCustomBlocks)
            {
                SetCustomBlocksStatus("已到 " + MaxCustomBlocks + " 段上限，先删掉一段再添加。", "Bad");
                return;
            }
            var title = AppDialog.Prompt(Window.GetWindow(this),
                "段落标题会作为提示词里的小标题，最多 40 字。正文可以留空，之后在左侧填写。",
                "添加自定义段落", "补充说明");
            if (title == null) return;
            title = title.Trim();
            if (title.Length == 0 || title.Length > 40)
            {
                SetCustomBlocksStatus("段落标题要 1~40 个字。", "Bad");
                return;
            }

            BtnAddBlock.IsEnabled = false;
            SetCustomBlocksStatus("正在添加…", "Dim");
            try
            {
                var res = await _sup.SaveConfigurationAsync("/api/persona/blocks", new { title, text = "" });
                if (!res.Ok)
                {
                    SetCustomBlocksStatus(res.StatusCode == 404 || res.StatusCode == 0
                        ? "当前内核还没有自定义段落接口，升级后可用。"
                        : "添加失败：" + Api.Message(res), "Bad");
                    return;
                }

                var id = res.Str("id");
                if (res.Prop("blocks").ValueKind == JsonValueKind.Array)
                    ApplyBlocksFrom(res.Prop("blocks"), force: true);
                await RefreshPreviewAsync();
                SetCustomBlocksStatus("已添加「" + title + "」，空正文不会进入提示词。", "Ok");

                if (!string.IsNullOrWhiteSpace(id) && _blockEditors.TryGetValue(id, out var editor))
                {
                    editor.Focus();
                    editor.CaretIndex = 0;
                }
            }
            catch (Exception ex)
            {
                SetCustomBlocksStatus("添加失败：" + Api.FriendlyError(ex), "Bad");
            }
            finally
            {
                BtnAddBlock.IsEnabled = _customBlocks.Count < MaxCustomBlocks;
            }
        }

        private async void OnDeleteBlock(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is string id)) return;
            var block = _customBlocks.FirstOrDefault(b => b.Id == id);
            if (block == null) return;
            var title = string.IsNullOrWhiteSpace(block.Title) ? "这个段落" : "「" + block.Title + "」";
            if (!AppDialog.Confirm(Window.GetWindow(this),
                "删除" + title + "？\n\n正文不会进入提示词；删除后内容无法恢复。",
                "删除自定义段落", danger: true, okText: "删除", cancelText: "取消")) return;

            button.IsEnabled = false;
            try
            {
                var res = await Api.Delete(_sup.Url, "/api/persona/blocks/" + Uri.EscapeDataString(id), 15);
                if (!res.Ok)
                {
                    SetCustomBlocksStatus(res.StatusCode == 404 ? "当前内核不支持删除自定义段落，升级后可用。"
                        : "删除失败：" + Api.Message(res), "Bad");
                    return;
                }
                if (res.Prop("blocks").ValueKind == JsonValueKind.Array)
                    ApplyBlocksFrom(res.Prop("blocks"), force: true);
                await RefreshPreviewAsync();
                SetCustomBlocksStatus("已删除" + title + "。", "Ok");
            }
            catch (Exception ex)
            {
                SetCustomBlocksStatus("删除失败：" + Api.FriendlyError(ex), "Bad");
            }
            finally { button.IsEnabled = true; }
        }

        private void SetCustomBlocksStatus(string text, string tone)
        {
            CustomBlocksStatus.Text = text ?? "";
            CustomBlocksStatus.SetResourceReference(TextBlock.ForegroundProperty, tone);
        }

        // ── 提示词预览 ──────────────────────────────────────────

        private async Task RefreshPreviewAsync(bool applyBlocks = false)
        {
            var seq = ++_previewSeq;
            SetPreviewStatus("正在更新…", "Dim");
            var chat = _chatType == "group" ? "group" : "private";
            var res = await Api.Get(_sup.Url, "/api/persona/prompt?chatType=" + chat + "&isMaster=1", 15);
            if (seq != _previewSeq) return;

            if (ApplyPreview(res, applyBlocks))
            {
                _previewLoaded.TrySetResult(true);
                return;
            }

            if (res.StatusCode == 404)
            {
                _previewUnsupported = true;
                ShowPreviewPlaceholder("当前内核不支持提示词预览，升级后可用", "预览不可用", "Warn", applyBlocks);
            }
            else if (res.StatusCode == 0)
            {
                _previewUnsupported = false;
                ShowPreviewPlaceholder("启动内核后可预览提示词", "预览不可用 · 启动内核后重试", "Idle", applyBlocks);
            }
            else
            {
                _previewUnsupported = false;
                ShowPreviewPlaceholder("读取提示词失败：" + Api.Message(res), "预览更新失败", "Bad", applyBlocks);
            }
            _previewLoaded.TrySetResult(true);
        }

        private bool ApplyPreview(ApiResult res, bool applyBlocks)
        {
            if (res == null || !res.Ok) return false;
            var system = res.Prop("system");
            if (system.ValueKind != JsonValueKind.String) return false;

            _previewAvailable = true;
            _previewUnsupported = false;
            PreviewText.Text = system.GetString() ?? "";
            PreviewText.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewText.ScrollToHome();
            var tokens = res.Long("tokens");
            PreviewTokens.Text = tokens > 0 ? tokens.ToString("N0") + " token" : "—";
            PreviewTokens.ToolTip = "由内核按当前设置估算";
            BtnCopyPrompt.IsEnabled = true;
            SetPreviewStatus("已更新 · " + Now, "Ok");
            ShowPromptWarnings(res.Prop("warnings"));
            if (applyBlocks)
            {
                ApplyBlocksFrom(res.Prop("blocks"));
                _previewLoaded.TrySetResult(true);
            }
            return true;
        }

        private void ShowPreviewPlaceholder(string message, string status, string tone, bool applyBlocks)
        {
            _previewAvailable = false;
            PreviewText.Text = "";
            PreviewText.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Text = message;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewTokens.Text = "—";
            BtnCopyPrompt.IsEnabled = false;
            SetPreviewStatus(status, tone);
            if (applyBlocks) BuildCustomBlocksFromPersona(_personaRoot);
        }

        private void SetPreviewStatus(string text, string tone)
        {
            PreviewStatus.Text = text ?? "";
            PreviewStatus.SetResourceReference(TextBlock.ForegroundProperty, tone);
            PreviewDot.SetResourceReference(Shape.FillProperty,
                tone == "Ok" ? "Ok" : tone == "Bad" ? "Bad" : tone == "Warn" ? "Warn" : "Idle");
        }

        private void ApplyBlocksFrom(JsonElement blocks, bool force = false)
        {
            if (blocks.ValueKind != JsonValueKind.Array) return;
            var custom = new List<CustomBlockView>();
            foreach (var item in blocks.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = S(item, "id");
                var kind = S(item, "kind");
                if (kind == "text")
                {
                    var max = L(item, "maxLength");
                    if (max > 0)
                    {
                        if (id == "personality") TxtPerson.MaxLength = (int)Math.Min(int.MaxValue, max);
                        else if (id == "background") TxtBack.MaxLength = (int)Math.Min(int.MaxValue, max);
                        else if (id == "custom") TxtPrompt.MaxLength = (int)Math.Min(int.MaxValue, max);
                    }
                    var fallback = S(item, "defaultText");
                    if (!string.IsNullOrWhiteSpace(fallback) &&
                        (id == "personality" || id == "background")) _blockDefaults[id] = fallback;
                }
                if (kind == "text" && B(item, "custom"))
                {
                    var label = S(item, "title");
                    if (string.IsNullOrWhiteSpace(label)) label = S(item, "label");
                    custom.Add(new CustomBlockView { Id = id, Title = label, Text = S(item, "text") });
                }
            }

            // 用户正在段落里打字时不要整块重建，否则焦点和光标都会丢。
            // 增删段落是结构变化，必须强制重建。
            if (force || !CustomBlocksHost.IsKeyboardFocusWithin) RebuildCustomBlocks(custom);
        }

        private void ShowPromptWarnings(JsonElement warnings)
        {
            var lines = new List<string>();
            if (warnings.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in warnings.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) continue;
                    var text = message.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) lines.Add("· " + text.Trim());
                }
            }
            if (lines.Count == 0)
            {
                PromptWarnings.Text = "";
                PromptWarnings.Visibility = Visibility.Collapsed;
                return;
            }
            PromptWarnings.Text = "可能的提示词冲突\n" + string.Join("\n", lines);
            PromptWarnings.Visibility = Visibility.Visible;
        }

        private async void OnPreviewPrivate(object sender, RoutedEventArgs e) => await SetChatTypeAsync("private");

        private async void OnPreviewGroup(object sender, RoutedEventArgs e) => await SetChatTypeAsync("group");

        private async Task SetChatTypeAsync(string type)
        {
            if (_chatType == type) return;
            _chatType = type;
            MarkPreviewTabs();
            await RefreshPreviewAsync();
        }

        private void MarkPreviewTabs()
        {
            MarkPreviewTab(BtnPreviewPrivate, _chatType == "private");
            MarkPreviewTab(BtnPreviewGroup, _chatType == "group");
        }

        private static void MarkPreviewTab(Button button, bool active)
        {
            if (button == null) return;
            button.SetResourceReference(Control.BackgroundProperty, active ? "AccentSoft" : "Transparent");
            button.SetResourceReference(Control.BorderBrushProperty, active ? "Accent" : "LineSoft");
            button.SetResourceReference(Control.ForegroundProperty, active ? "Accent" : "Muted");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private async void OnRefreshPrompt(object sender, RoutedEventArgs e)
        {
            BtnRefreshPrompt.IsEnabled = false;
            try { await RefreshPreviewAsync(); }
            finally { BtnRefreshPrompt.IsEnabled = true; }
        }

        private void OnCopyPrompt(object sender, RoutedEventArgs e)
        {
            if (!_previewAvailable) return;
            try
            {
                Clipboard.SetText(PreviewText.Text ?? "");
                SetPreviewStatus("已复制全文 · " + Now, "Ok");
            }
            catch (Exception ex)
            {
                SetPreviewStatus("复制失败：" + Api.FriendlyError(ex), "Bad");
            }
        }

        private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout();

        /// <summary>窄窗口把预览从右栏收进底部折叠区；宽回来再挂回右栏。</summary>
        private void ApplyResponsiveLayout()
        {
            if (PreviewPanel == null || Workbench == null || ActualWidth < 320) return;
            var windowWidth = Window.GetWindow(this)?.ActualWidth ?? ActualWidth;
            var narrow = windowWidth < NarrowWindowWidth || ActualWidth < NarrowPageWidth;
            if (narrow == _previewNarrow) return;
            _previewNarrow = narrow;

            if (narrow)
            {
                Workbench.Children.Remove(PreviewPanel);
                PreviewPanel.Height = 420;
                NarrowPreviewHost.Content = PreviewPanel;
                NarrowPreviewExpander.Visibility = Visibility.Visible;
                Workbench.ColumnDefinitions[1].Width = new GridLength(0);
                Workbench.ColumnDefinitions[2].Width = new GridLength(0);
            }
            else
            {
                NarrowPreviewHost.Content = null;
                NarrowPreviewExpander.Visibility = Visibility.Collapsed;
                NarrowPreviewExpander.IsExpanded = false;
                Workbench.Children.Add(PreviewPanel);
                Grid.SetColumn(PreviewPanel, 2);
                Grid.SetRow(PreviewPanel, 0);
                PreviewPanel.Height = double.NaN;
                Workbench.ColumnDefinitions[1].Width = new GridLength(16);
                Workbench.ColumnDefinitions[2].Width = new GridLength(9, GridUnitType.Star);
            }
        }

        private async Task LoadMemoryChatsAsync()
        {
            BtnResetMemory.IsEnabled = false;
            CmbMemoryChat.ItemsSource = null;
            if (!_kernelReachable)
            {
                MemoryHint.Text = "请先启动内核，再刷新对话列表。";
                return;
            }
            var res = await Api.Get(_sup.Url, "/api/memory/chats");
            if (!res.Ok)
            {
                MemoryHint.Text = "读取对话失败：" + Api.Message(res);
                return;
            }
            var groups = new List<MemoryChatItem>();
            var arr = res.Prop("chats");
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var g in arr.EnumerateArray())
                {
                    var title = S(g, "title");
                    var key = S(g, "key");
                    groups.Add(new MemoryChatItem
                    {
                        Key = key,
                        Title = title,
                        Label = $"【{(S(g, "chatType") == "group" ? "群聊" : "私聊")}】{title} · {L(g, "messageCount")} 条记录 · {L(g, "peopleCount")} 人记忆（{key}）"
                    });
                }
            CmbMemoryChat.ItemsSource = groups;
            if (groups.Count > 0) CmbMemoryChat.SelectedIndex = 0;
            BtnResetMemory.IsEnabled = groups.Count > 0;
            MemoryHint.Text = groups.Count > 0 ? "列出本应用当前保存了记忆的所有对话，包含群聊和私聊。" : "暂无已保存记忆的对话。";
        }

        private sealed class MemoryChatItem
        {
            public string Key { get; set; }
            public string Title { get; set; }
            public string Label { get; set; }
        }

        private async void OnRefreshMemoryChats(object sender, RoutedEventArgs e)
        {
            BtnRefreshMemory.IsEnabled = false;
            try { await LoadMemoryChatsAsync(); }
            finally { BtnRefreshMemory.IsEnabled = true; }
        }

        private async void OnResetChatMemory(object sender, RoutedEventArgs e)
        {
            if (!_kernelReachable || CmbMemoryChat.SelectedItem is not MemoryChatItem group) return;
            if (!AppDialog.Confirm(Window.GetWindow(this),
                $"确定清空「{group.Title}」的全部内部记忆吗？\n\n" +
                "将清除：上下文聊天记录、摘要、人物卡、事件与关系记忆。\n" +
                "微信聊天记录、其他会话记忆和人设设置保留。\n\n" +
                "此操作无法直接撤销；之后从新消息开始记忆。已有的手动备份与自动快照保留。",
                "清空该对话记忆", danger: true, okText: "清空", cancelText: "取消"))
                return;
            BtnResetMemory.IsEnabled = false;
            BtnRefreshMemory.IsEnabled = false;
            CmbMemoryChat.IsEnabled = false;
            try
            {
                var res = await Api.PostJson(_sup.Url, "/api/memory/chats/reset", new { key = group.Key, confirm = true });
                if (!res.Ok)
                {
                    MemoryHint.Text = "清空失败：" + Api.Message(res);
                    return;
                }
                await LoadMemoryChatsAsync();
                MemoryHint.Text = $"已清空「{group.Title}」的内部记忆，微信聊天记录保留。";
            }
            finally
            {
                BtnRefreshMemory.IsEnabled = true;
                CmbMemoryChat.IsEnabled = true;
                BtnResetMemory.IsEnabled = CmbMemoryChat.SelectedItem != null;
            }
        }

        // ── 对话风格学习 ────────────────────────────────────────
        // 统计和摘要都由内核生成；这里只负责展示、触发分析和切换模式。
        // 模式回填必须静默，否则刷新页面会把默认值当用户操作写回去。

        private bool _styleBusy;
        private bool _styleModeBusy;
        private bool _styleClearing;
        private bool _styleHasData;
        private bool _styleSyncing;
        private string _styleMode = "adapt";

        private async Task LoadStyleAsync()
        {
            if (!_kernelReachable)
            {
                _styleHasData = false;
                SetStyleModeSilently("adapt");
                StyleState.Text = "尚未学习";
                StyleDot.Fill = (Brush)FindResource("Idle");
                TxtStylePreview.Text = "点击学习后，这里会显示摘要和统计。";
                TxtStylePreview.Foreground = (Brush)FindResource("Dim");
                StyleHint.Text = "启动内核后可分析已有记忆。先在身份关系里配置主要联系人，结果会更准确。";
                SetStyleControlsEnabled(false);
                return;
            }

            var res = await Api.Get(_sup.Url, "/api/persona/style", 15);
            if (!res.Ok)
            {
                _styleHasData = false;
                StyleState.Text = "尚未学习";
                StyleDot.Fill = (Brush)FindResource("Idle");
                TxtStylePreview.Text = "点击学习后，这里会显示摘要和统计。";
                TxtStylePreview.Foreground = (Brush)FindResource("Dim");
                StyleHint.Text = res.StatusCode == 404
                    ? "当前内核还没有风格学习接口，更新内核后再试。"
                    : "读取风格状态失败：" + Api.Message(res);
                SetStyleControlsEnabled(true);
                return;
            }

            ApplyStyleResponse(res);
            SetStyleControlsEnabled(true);
        }

        private void ApplyStyleResponse(ApiResult res)
        {
            var root = res.Root;
            var style = Obj(root, "style") ?? Obj(root, "profile") ?? root;
            var data = Obj(style, "profile") ?? Obj(style, "stats")
                ?? Obj(root, "profile") ?? Obj(root, "stats") ?? style;

            SetStyleModeSilently(StyleString(data, style, root, "mode"));

            var count = StyleLong(data, style, root,
                "messageCount", "totalMessages", "messages", "sampleCount", "samples", "count", "total");
            var time = StyleTime(data, style, root);
            var summary = StyleString(data, style, root, "summary", "preview", "text");
            var avg = StyleDouble(data, style, root,
                "avgLength", "avgChars", "avgSentenceLength", "avgSentenceChars", "sentenceLength", "avgLen");
            var tone = StyleList(data, style, root, "tone", "tones", "toneTags", "toneSummary");
            var punct = StyleList(data, style, root, "punctuation", "punctuationStyle", "punctuationHabits");
            var emoji = StyleList(data, style, root, "emoji", "emojiUsage", "emojiHabit");
            var topics = StyleList(data, style, root, "topics", "topTopics", "frequentTopics");

            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(summary))
                lines.Add(summary.Trim());
            else
            {
                var explicitLines = JsonRead.Strings(data, "lines");
                if (explicitLines.Count == 0) explicitLines = JsonRead.Strings(style, "lines");
                if (explicitLines.Count == 0) explicitLines = JsonRead.Strings(root, "lines");
                lines.AddRange(explicitLines);
            }

            if (lines.Count == 0)
            {
                if (avg > 0) lines.Add("平均句长：约 " + Math.Round(avg) + " 字");
                if (tone.Length > 0) lines.Add("语气：" + tone);
                if (punct.Length > 0) lines.Add("标点习惯：" + punct);
                if (emoji.Length > 0) lines.Add("颜文字 / 表情：" + emoji);
                if (topics.Length > 0) lines.Add("常聊话题：" + topics);
            }

            var hasData = count > 0 || time > 0 || lines.Count > 0
                || JsonRead.Bool(data, "hasProfile") || JsonRead.Bool(style, "hasProfile")
                || JsonRead.Bool(root, "hasProfile");
            _styleHasData = hasData;
            if (lines.Count > 0)
            {
                TxtStylePreview.Text = string.Join("\n", lines);
                TxtStylePreview.Foreground = (Brush)FindResource("Text");
            }
            else
            {
                TxtStylePreview.Text = hasData
                    ? "已生成风格画像，暂时没有文字摘要。"
                    : "点击学习后，这里会显示摘要和统计。";
                TxtStylePreview.Foreground = (Brush)FindResource(hasData ? "Text2" : "Dim");
            }

            if (count > 0 && time > 0)
                StyleState.Text = $"已从 {count:N0} 条消息生成，时间 {FormatStyleTime(time)}";
            else if (count > 0)
                StyleState.Text = $"已从 {count:N0} 条消息生成";
            else if (time > 0)
                StyleState.Text = "已生成风格画像，时间 " + FormatStyleTime(time);
            else if (hasData)
                StyleState.Text = "已生成风格画像";
            else
                StyleState.Text = "尚未学习";

            StyleDot.Fill = (Brush)FindResource(hasData ? "Ok" : "Idle");
            StyleHint.Text = hasData
                ? "只保存统计和摘要，不保存原始聊天正文。"
                : "点击学习后，这里会显示摘要和统计。先在身份关系里配置主要联系人，结果会更准确。";
        }

        private async void OnStyleModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_styleSyncing || !_loaded || _styleBusy || _styleModeBusy || _styleClearing) return;
            if (!(CmbStyleMode.SelectedItem is ComboBoxItem item)
                || !(item.Tag is string mode) || mode == _styleMode) return;
            if (!_kernelReachable)
            {
                SetStyleModeSilently(_styleMode);
                StyleHint.Text = "启动内核后才能切换学习方式。";
                return;
            }

            _styleModeBusy = true;
            SetStyleControlsEnabled(true);
            StyleHint.Text = "正在保存学习方式…";
            try
            {
                var res = await Api.PostJson(_sup.Url, "/api/persona/style/mode", new { mode }, 20);
                if (!res.Ok)
                {
                    SetStyleModeSilently(_styleMode);
                    StyleHint.Text = "切换失败：" + Api.Message(res);
                    return;
                }

                _styleMode = mode;
                StyleHint.Text = mode == "mimic" ? "已切换为模仿对方表达。" : "已切换为贴合对方习惯。";
            }
            finally
            {
                _styleModeBusy = false;
                SetStyleControlsEnabled(_kernelReachable);
            }
        }

        private async void OnStyleLearnMemory(object sender, RoutedEventArgs e)
            => await RunStyleAnalyzeAsync("memory", null);

        private async void OnStyleLearnExport(object sender, RoutedEventArgs e)
        {
            if (_styleBusy || _styleModeBusy || _styleClearing) return;
            var file = AppFilePicker.Open("选择微信导出", ".json");
            if (string.IsNullOrWhiteSpace(file)) return;
            await RunStyleAnalyzeAsync("export", file);
        }

        private async void OnStyleClear(object sender, RoutedEventArgs e)
        {
            if (_styleBusy || _styleModeBusy || _styleClearing || !_styleHasData) return;
            if (!_kernelReachable)
            {
                StyleHint.Text = "请先启动内核，再清除学习结果。";
                return;
            }
            if (!AppDialog.Confirm(Window.GetWindow(this),
                "清除本机已学习的对话风格画像？\n\n"
                + "只会删除统计摘要和后续对话里的风格参考，不改人设和聊天记忆，也不删原始聊天记录。清除后需要重新学习。",
                "清除风格画像", danger: true, okText: "清除", cancelText: "取消")) return;

            _styleClearing = true;
            SetStyleControlsEnabled(true);
            StyleHint.Text = "正在清除…";
            try
            {
                var res = await Api.PostJson(_sup.Url, "/api/persona/style/clear", new { }, 30);
                if (!res.Ok)
                {
                    StyleDot.Fill = (Brush)FindResource("Warn");
                    StyleHint.Text = "清除失败：" + Api.Message(res);
                    return;
                }

                _styleHasData = false;
                await LoadStyleAsync();
                StyleHint.Text = "已清除。点击学习后，这里会显示摘要和统计。";
            }
            catch (Exception ex)
            {
                StyleDot.Fill = (Brush)FindResource("Warn");
                StyleHint.Text = "清除失败：" + Api.FriendlyError(ex);
            }
            finally
            {
                _styleClearing = false;
                SetStyleControlsEnabled(_kernelReachable);
            }
        }

        private async Task RunStyleAnalyzeAsync(string source, string file)
        {
            if (_styleBusy || _styleModeBusy || _styleClearing) return;
            if (!_kernelReachable)
            {
                StyleHint.Text = "请先启动内核，再开始学习。";
                return;
            }

            _styleBusy = true;
            SetStyleControlsEnabled(true);
            StyleHint.Text = "正在分析…这一步可能要十几秒，请稍等。";
            try
            {
                object body = file == null
                    ? new { source, useLlm = ChkStyleUseLlm.IsChecked == true }
                    : new { source, file, useLlm = ChkStyleUseLlm.IsChecked == true };
                var res = await Api.PostJson(_sup.Url, "/api/persona/style/analyze", body, 120);
                if (!res.Ok)
                {
                    StyleDot.Fill = (Brush)FindResource("Warn");
                    StyleHint.Text = "分析失败：" + Api.Message(res);
                    return;
                }

                ApplyStyleResponse(res);
                // 有的实现只回 {ok:true}，成功后补一次状态读取，预览才不会停在旧值。
                if (!res.Has("style") && !res.Has("profile") && !res.Has("summary")
                    && !res.Has("preview") && !res.Has("messageCount"))
                    await LoadStyleAsync();
                StyleHint.Text = "分析完成。后续对话会参考学到的风格。";
            }
            catch (Exception ex)
            {
                StyleDot.Fill = (Brush)FindResource("Warn");
                StyleHint.Text = "分析失败：" + Api.FriendlyError(ex);
            }
            finally
            {
                _styleBusy = false;
                SetStyleControlsEnabled(_kernelReachable);
            }
        }

        private void SetStyleControlsEnabled(bool online)
        {
            var enabled = online && !_styleBusy && !_styleModeBusy && !_styleClearing;
            BtnStyleMemory.IsEnabled = enabled;
            BtnStyleExport.IsEnabled = enabled;
            CmbStyleMode.IsEnabled = enabled;
            BtnStyleClear.IsEnabled = enabled && _styleHasData;
            BtnStyleMemory.Content = _styleBusy ? "正在分析…" : "从已有记忆学习";
            BtnStyleExport.Content = _styleBusy ? "正在分析…" : "从微信导出文件学习…";
            BtnStyleClear.Content = _styleClearing ? "正在清除…" : "清除学习结果";
        }

        private void SetStyleModeSilently(string mode)
        {
            _styleMode = string.Equals(mode, "mimic", StringComparison.OrdinalIgnoreCase) ? "mimic" : "adapt";
            _styleSyncing = true;
            try
            {
                foreach (var item in CmbStyleMode.Items)
                {
                    if (item is ComboBoxItem row
                        && string.Equals(row.Tag as string, _styleMode, StringComparison.Ordinal))
                    {
                        CmbStyleMode.SelectedItem = row;
                        break;
                    }
                }
            }
            finally { _styleSyncing = false; }
        }

        private static JsonElement? Obj(JsonElement e, string name)
        {
            var value = JsonRead.Prop(e, name);
            return value.ValueKind == JsonValueKind.Object ? value : (JsonElement?)null;
        }

        private static string StyleString(JsonElement data, JsonElement style, JsonElement root, params string[] names)
        {
            foreach (var element in new[] { data, style, root })
                foreach (var name in names)
                {
                    var text = JsonRead.Str(element, name);
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            return "";
        }

        private static string StyleList(JsonElement data, JsonElement style, JsonElement root, params string[] names)
        {
            foreach (var element in new[] { data, style, root })
                foreach (var name in names)
                {
                    var list = JsonRead.Strings(element, name);
                    if (list.Count > 0) return string.Join("、", list);
                    var text = JsonRead.Str(element, name);
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
            return "";
        }

        private static long StyleLong(JsonElement data, JsonElement style, JsonElement root, params string[] names)
        {
            foreach (var element in new[] { data, style, root })
                foreach (var name in names)
                {
                    var value = JsonRead.Long(element, name);
                    if (value > 0) return value;
                }
            return 0;
        }

        private static double StyleDouble(JsonElement data, JsonElement style, JsonElement root, params string[] names)
        {
            foreach (var element in new[] { data, style, root })
                foreach (var name in names)
                {
                    var value = JsonRead.Double(element, name, 0);
                    if (value > 0) return value;
                }
            return 0;
        }

        private static long StyleTime(JsonElement data, JsonElement style, JsonElement root)
        {
            foreach (var element in new[] { data, style, root })
                foreach (var name in new[] { "updatedAt", "generatedAt", "learnedAt", "analyzedAt", "createdAt", "at", "time" })
                {
                    var stamp = EpochMillis(JsonRead.Prop(element, name));
                    if (stamp > 0) return stamp;
                }
            return 0;
        }

        private static long EpochMillis(JsonElement value)
        {
            long number;
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (!value.TryGetInt64(out number)) return 0;
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var text = (value.GetString() ?? "").Trim();
                if (text.Length == 0) return 0;
                if (!long.TryParse(text, out number))
                    return DateTimeOffset.TryParse(text, out var parsed) ? parsed.ToUnixTimeMilliseconds() : 0;
            }
            else return 0;

            if (number <= 0) return 0;
            return number < 100000000000L ? number * 1000 : number;
        }

        private static string FormatStyleTime(long millis)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime.ToString("yyyy-MM-dd HH:mm"); }
            catch { return ""; }
        }

        // ── 说话方式派生说明 ────────────────────────────────────

        private void UpdateSpeechDerived()
        {
            if (SpeechDerived == null) return;
            var priv = string.IsNullOrWhiteSpace(TxtPrivate.Text) ? "100" : TxtPrivate.Text.Trim();
            var grp = string.IsNullOrWhiteSpace(TxtGroup.Text) ? "70" : TxtGroup.Text.Trim();
            var bubbles = string.IsNullOrWhiteSpace(TxtBubbles.Text) ? "2" : TxtBubbles.Text.Trim();
            var pct = SldEmoji == null ? 0 : (int)Math.Round(SldEmoji.Value);
            SpeechDerived.Text = "私聊最多 " + priv + " 字 · 群聊最多 " + grp + " 字 · 最多 " + bubbles + " 条 · 颜文字 " + pct + "%";
            if (EmojiValue != null) EmojiValue.Text = pct + "%";
        }

        /// <summary>拖动条位置：优先用显式概率，没设过就按档位换算（关/少/正常/多 → 0/12/30/65）。</summary>
        private static double EmojiPercent(ApiResult cfg, string level)
        {
            if (string.Equals(level, "关", StringComparison.Ordinal)) return 0;
            if (cfg != null && cfg.Ok && cfg.Prop("config").ValueKind == JsonValueKind.Object
                && cfg.Prop("config").TryGetProperty("chat", out var chat) && chat.ValueKind == JsonValueKind.Object
                && chat.TryGetProperty("emojiChance", out var ec) && ec.ValueKind == JsonValueKind.Number)
            {
                var explicitPct = ec.GetDouble() * 100;
                if (explicitPct > 0) return Math.Clamp(explicitPct, 0, 100);
            }
            return level switch
            {
                "关" => 0,
                "少" => 12,
                "多" => 65,
                _ => 30
            };
        }

        // ── 保存 ────────────────────────────────────────────────

        private void OnPersonaTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSpeechDerived();
            if (_loading || !_loaded) return;
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            if (_saveTimer == null || _loading || !_loaded) return;
            _editRevision++;
            SetSaveState("等待自动保存…", "Dim");
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnFieldLostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            UpdateSpeechDerived();
            ScheduleSave();
        }

        private void OnToggleField(object sender, RoutedEventArgs e)
        {
            ScheduleSave();
        }

        private void OnEmojiChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateSpeechDerived();
            ScheduleSave();
        }

        private async void OnReload(object sender, RoutedEventArgs e)
        {
            if ((_savedRevision != _editRevision || _blockFailed.Count > 0) &&
                !AppDialog.Confirm(Window.GetWindow(this), "重新读取会丢弃尚未保存的修改，是否继续？",
                    "重新读取", danger: true, okText: "丢弃并读取")) return;
            _saveTimer.Stop();
            foreach (var timer in _blockTimers.Values) timer.Stop();
            _loaded = false;
            SetSaveState("重新读取…", "Dim");
            await LoadAsync();
        }

        private async Task<bool> SaveAllAsync(string okText)
        {
            await _saveGate.WaitAsync();
            try
            {
                if (_loading || !_loaded || _applyingPreset) return false;
                var revision = _editRevision;

                if (!TxtAge.TryRead(out var age)) { SetSaveState(TxtAge.ErrorText, "Bad"); return false; }
                if (!TxtPrivate.TryRead(out var priv)) { SetSaveState(TxtPrivate.ErrorText, "Bad"); return false; }
                if (!TxtGroup.TryRead(out var grp)) { SetSaveState(TxtGroup.ErrorText, "Bad"); return false; }
                if (!TxtTurns.TryRead(out var turns)) { SetSaveState(TxtTurns.ErrorText, "Bad"); return false; }
                if (!TxtGroupTurns.TryRead(out var gturns)) { SetSaveState(TxtGroupTurns.ErrorText, "Bad"); return false; }
                if (!TxtBubbles.TryRead(out var bubbles)) { SetSaveState(TxtBubbles.ErrorText, "Bad"); return false; }

                var pct = Math.Clamp((int)Math.Round(SldEmoji.Value), 0, 100);
                // 0% 用档位「关」做硬关；>0 时把档位恢复成可用值，真正生效的是下面的精确概率
                var emoji = pct == 0
                    ? "关"
                    : (string.Equals(_loadedEmoji, "关", StringComparison.Ordinal) ? "正常" : _loadedEmoji);
                var emojiChance = pct / 100.0;
                // 一次保存使用同一份表单快照，等待 HTTP 时的新编辑留给下一次自动保存。
                var owner = TxtOwner.Text.Trim();
                var aliases = LineList(TxtAliases);
                var extraPatch = new Dictionary<string, object>
                {
                    ["persona.masterRoast"] = ChkRoast.IsChecked == true,
                    ["persona.aliases"] = aliases,
                    ["persona.master.names"] = MasterList(e => e.Name),
                    ["persona.master.wxids"] = MasterList(e => e.Wxid),
                    ["chat.emojiChance"] = emojiChance
                };

                var res = await _sup.SaveConfigurationAsync("/api/persona", new
                {
                    newBehavior = ChkNewBehavior.IsChecked == true,
                    name = TxtName.Text.Trim(),
                    age,
                    job = TxtJob.Text.Trim(),
                    appearance = TxtLook.Text.Trim(),
                    privateMaxChars = priv,
                    groupMaxChars = grp,
                    maxBubbles = bubbles,
                    historyTurns = turns,
                    groupHistoryTurns = gturns,
                    emoji,
                    proactive = ChkProactive.IsChecked == true,
                    opinions = ChkOpinions.IsChecked == true,
                    ownerName = owner,
                    aliases,
                    masterRoast = ChkRoast.IsChecked == true,
                    emojiChance
                });
                if (!res.Ok)
                {
                    SetSaveState("保存失败：" + Api.Message(res), "Bad");
                    return false;
                }

                // 称呼（persona 接口不收，走 settings）；群聊规则统一在「设置 → 群聊」，这里不再写 config
                var ownerResult = await _sup.SaveConfigurationAsync("/api/settings", new { userName = owner });
                if (!ownerResult.Ok)
                {
                    SetSaveState("人设已保存，但称呼保存失败：" + Api.Message(ownerResult), "Bad");
                    return false;
                }
                var configResult = await _sup.PatchConfigurationAsync(extraPatch);
                if (!configResult.Ok)
                {
                    SetSaveState("人设与称呼已保存，但其他设置保存失败：" + Api.Message(configResult), "Bad");
                    return false;
                }
                _loadedEmoji = emoji;
                _savedRevision = revision;

                // 表单 / 开关 / 数值保存后重新拉一次最终提示词，token 和正文同步变化。
                await RefreshPreviewAsync();

                var warned = CountWarnings(res.Prop("warnings"));
                var blocked = _blockFailed.Count > 0 ? " · 有段落尚未保存" : "";
                SetSaveState(revision == _editRevision
                    ? okText + " · " + Now + (warned > 0 ? " · 提示词有 " + warned + " 处冲突提醒" : "") + blocked
                    : "较早修改已保存 · 新修改等待保存…", "Text2");
                return revision == _editRevision;
            }
            catch (Exception ex)
            {
                SetSaveState("保存失败：" + Api.FriendlyError(ex), "Bad");
                return false;
            }
            finally { _saveGate.Release(); }
        }

        private int CountWarnings(JsonElement warnings)
        {
            var count = 0;
            if (warnings.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in warnings.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) continue;
                    if (!string.IsNullOrWhiteSpace(message.GetString())) count++;
                }
            }
            return count;
        }

        private async Task<bool> FlushEditsAsync()
        {
            _saveTimer.Stop();
            foreach (var id in _blockTimers.Where(kv => kv.Value.IsEnabled).Select(kv => kv.Key).ToList())
            {
                _blockTimers[id].Stop();
                await SaveBlockAsync(id);
            }
            await _saveGate.WaitAsync();
            _saveGate.Release();
            if (_blockFailed.Count > 0)
            {
                SetSaveState("有段落尚未保存，先按上面的提示处理", "Bad");
                return false;
            }
            return _savedRevision == _editRevision || await SaveAllAsync("已保存");
        }

        private void SetSaveState(string text, string tone = "Text2")
        {
            if (SaveState == null) return;
            SaveState.Text = text ?? "";
            SaveState.SetResourceReference(TextBlock.ForegroundProperty, tone);
        }

        private static string Now => DateTime.Now.ToString("HH:mm:ss");

        // ── 预设 ────────────────────────────────────────────────

        private async Task LoadPresetsAsync(string currentId = "")
        {
            var res = await _sup.ReadConfigurationAsync("/api/persona/presets");
            if (!res.Ok)
            {
                CmbPreset.ItemsSource = null;
                SetSaveState("预设列表读取失败：" + Api.Message(res), "Warn");
                return;
            }
            var items = new List<PresetItem>();
            var arr = res.Prop("presets");
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in arr.EnumerateArray())
                {
                    var id = S(p, "id");
                    var name = S(p, "name");
                    var ver = S(p, "version");
                    var source = S(p, "source");
                    var tag = source switch
                    {
                        "user" => "我的",
                        _ => "内置",
                    };
                    items.Add(new PresetItem
                    {
                        Id = id,
                        Name = name,
                        Source = source,
                        Label = $"【{tag}】{(string.IsNullOrEmpty(ver) ? $"{name}（{id}）" : $"{name} · {ver}")}",
                    });
                }
            }
            CmbPreset.ItemsSource = items;
            // 选中「当前人设实际来自哪一版」，而不是永远选第一个，
            // 否则导入 v4 再保存，下拉框会跳回 v1，看起来像没导入成功。
            var hit = items.FirstOrDefault(x => string.Equals(x.Id, currentId, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                CmbPreset.SelectedItem = hit;
                _currentPresetName = hit.Name;
            }
            else if (items.Count > 0)
            {
                CmbPreset.SelectedIndex = 0;
                _currentPresetName = items[0].Name;
            }
            else SetSaveState("presets 目录里没有人设版本。", "Warn");
            UpdatePresetButtons();
        }

        private void UpdatePresetButtons()
        {
            if (BtnUpdatePreset == null || BtnDeletePreset == null || BtnRenamePreset == null) return;
            var selected = CmbPreset.SelectedItem as PresetItem;
            var isCurrent = selected != null && string.Equals(selected.Id, _currentPresetId, StringComparison.OrdinalIgnoreCase);
            BtnUpdatePreset.IsEnabled = selected != null && selected.IsUser && isCurrent;
            BtnRenamePreset.IsEnabled = selected != null && selected.IsUser;
            BtnDeletePreset.IsEnabled = selected != null;
        }

        private void OnTogglePresetManage(object sender, RoutedEventArgs e)
        {
            var open = PresetManagePanel.Visibility != Visibility.Visible;
            PresetManagePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            BtnManagePresets.Content = open ? "收起预设管理" : "预设管理";
        }

        // ── 主人名单：搜索选择，一次填好名字和 wxid ──────────────
        //
        // 以前是两个多行文本框，名字和 wxid 各填一份，很容易填成「A 的名字 + B 的 wxid」。
        // 微信只按 wxid 认主人，这种错配不会报错，却会让 B 拿到主人权限。
        // 现在统一从联系人里搜、选中，名字和 wxid 一起带出来，不再给手填错配的机会。
        private void LoadMasterEntries(JsonElement master)
        {
            var names = JsonRead.Strings(master, "names");
            var wxids = JsonRead.Strings(master, "wxids");
            _masterEntries.Clear();
            // 旧配置按行号配对：保存时也是这个顺序，所以来回切换不会把已有值弄丢。
            var count = Math.Max(names.Count, wxids.Count);
            for (var i = 0; i < count; i++)
            {
                _masterEntries.Add(new ContactInfo
                {
                    Name = i < names.Count ? names[i] : "",
                    Wxid = i < wxids.Count ? wxids[i] : "",
                });
            }
            RenderMasterChips();
        }

        private void RenderMasterChips()
        {
            MasterChips.Children.Clear();
            foreach (var entry in _masterEntries.ToList())
            {
                var label = string.IsNullOrWhiteSpace(entry.Name) ? entry.Wxid : entry.Name;
                if (!string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(entry.Wxid)
                    && !string.Equals(entry.Name, entry.Wxid, StringComparison.Ordinal))
                    label = entry.Name + " · " + entry.Wxid;

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("Text"),
                });
                var remove = new Button
                {
                    Content = "×",
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(4, 0, 4, 0),
                    Style = (Style)FindResource("Ghost"),
                    Tag = entry,
                    ToolTip = "移除这个主要联系人",
                };
                remove.Click += OnRemoveMaster;
                row.Children.Add(remove);

                MasterChips.Children.Add(new Border
                {
                    Background = (Brush)FindResource("Surface3"),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10, 3, 5, 3),
                    Margin = new Thickness(0, 0, 6, 6),
                    Child = row,
                });
            }

            var incomplete = _masterEntries.Any((e) =>
                string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Wxid));
            MasterWarn.Text = incomplete
                ? "有主要联系人只填了昵称或只填了 wxid。微信只按 wxid 认主要联系人，请重新搜索选择一次，避免名字和 id 不是同一个人。"
                : "";
            MasterWarn.Visibility = incomplete ? Visibility.Visible : Visibility.Collapsed;
            MasterHint.Text = _kernelReachable
                ? "搜索名字或 wxid，选中后自动填好昵称和 wxid。"
                : "启动内核后可以搜索联系人；当前显示的是已保存的配置。";
        }

        private async void OnMasterSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _masterRendering) return;
            _masterSearchTimer.Stop();
            var q = TxtMasterSearch.Text.Trim();
            if (q.Length == 0)
            {
                _masterSearchSeq++;
                MasterSuggest.ItemsSource = null;
                MasterSuggest.Visibility = Visibility.Collapsed;
                RenderMasterChips();
                return;
            }
            _masterSearchTimer.Start();
        }

        private async Task SearchMasterAsync(string q)
        {
            var seq = ++_masterSearchSeq;
            if (!_kernelReachable)
            {
                MasterSuggest.ItemsSource = null;
                MasterSuggest.Visibility = Visibility.Collapsed;
                MasterHint.Text = "启动内核后才能搜索联系人。";
                return;
            }

            var snap = await _sup.Contacts.LoadAsync();
            if (seq != _masterSearchSeq) return;

            if (!snap.Ok && snap.Items.Count == 0)
            {
                MasterSuggest.ItemsSource = null;
                MasterSuggest.Visibility = Visibility.Collapsed;
                MasterHint.Text = "联系人接口还没就绪（GET /api/wechat/contacts 失败）。" + snap.Error;
                return;
            }

            var items = new List<ContactItem>();
            foreach (var info in snap.Items)
            {
                if (info.IsGroup) continue;
                if (_masterEntries.Any((m) => m.Wxid == info.Wxid)) continue;
                if (!ContactDirectory.Matches(info, q)) continue;
                items.Add(new ContactItem
                {
                    Wxid = info.Wxid,
                    Name = info.Name,
                    Remark = info.Remark,
                    IsGroup = false
                });
            }

            _masterRendering = true;
            try
            {
                MasterSuggest.ItemsSource = items;
                MasterSuggest.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _masterRendering = false; }

            MasterHint.Text = items.Count > 0
                ? "点一个结果设为主要联系人。"
                : "没有匹配「" + q + "」的联系人；换个名字或直接搜 wxid。";
        }

        private void OnMasterSuggestionPicked(object sender, SelectionChangedEventArgs e)
        {
            if (_masterRendering || _loading) return;
            if (!(MasterSuggest.SelectedItem is ContactItem item)) return;

            var display = string.IsNullOrWhiteSpace(item.Display) ? item.Wxid : item.Display;
            var sameId = _masterEntries.FirstOrDefault((m) => m.Wxid == item.Wxid);
            if (sameId != null)
            {
                if (string.IsNullOrWhiteSpace(sameId.Name)) sameId.Name = display;
            }
            else
            {
                // 旧配置里只有昵称、没有 wxid 的那一条，选中同名联系人时直接补上 id，
                // 而不是再加一条重复的主人。
                var nameOnly = _masterEntries.FirstOrDefault((m) =>
                    string.IsNullOrWhiteSpace(m.Wxid)
                    && string.Equals(m.Name, display, StringComparison.OrdinalIgnoreCase));
                if (nameOnly != null) nameOnly.Wxid = item.Wxid;
                else
                    _masterEntries.Add(new ContactInfo
                    {
                        Name = string.Equals(display, item.Wxid, StringComparison.Ordinal) ? "" : display,
                        Wxid = item.Wxid,
                    });
            }

            _masterRendering = true;
            try
            {
                MasterSuggest.SelectedItem = null;
                MasterSuggest.ItemsSource = null;
                MasterSuggest.Visibility = Visibility.Collapsed;
                TxtMasterSearch.Text = "";
            }
            finally { _masterRendering = false; }

            RenderMasterChips();
            ScheduleSave();
        }

        private void OnRemoveMaster(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is ContactInfo entry)) return;
            _masterEntries.Remove(entry);
            RenderMasterChips();
            ScheduleSave();
        }

        private List<string> MasterList(Func<ContactInfo, string> pick)
        {
            var list = new List<string>();
            foreach (var entry in _masterEntries)
            {
                var value = (pick(entry) ?? "").Trim();
                if (value.Length > 0) list.Add(value);
            }
            return list;
        }

        // ── 预设操作 ────────────────────────────────────────────

        private async void OnImportPreset(object sender, RoutedEventArgs e)
        {
            if (_applyingPreset) return;
            if (!(CmbPreset.SelectedItem is PresetItem p)) return;
            if (!await FlushEditsAsync()) return;
            _applyingPreset = true;
            _saveTimer.Stop();
            Workbench.IsEnabled = false;
            await _saveGate.WaitAsync();
            try
            {
                var res = await _sup.SaveConfigurationAsync("/api/persona/preset", new { id = p.Id });
                if (!res.Ok)
                {
                    SetSaveState("导入失败：" + Api.Message(res), "Bad");
                    return;
                }
                _loaded = false;
                await LoadAsync();                       // LoadAsync 会按新的 sourcePreset 选中下拉框
                SetSaveState("人设预设已导入 · " + Now, "Text2");
            }
            finally
            {
                _saveGate.Release();
                _applyingPreset = false;
                Workbench.IsEnabled = true;
            }
        }

        private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
        {
            CmbPreset.ToolTip = CmbPreset.SelectedItem is PresetItem p ? p.Label : null;
            UpdatePresetButtons();
        }

        private async Task<bool> FlushBeforePresetAsync()
        {
            _saveTimer.Stop();
            if (!await FlushEditsAsync()) return false;
            return true;
        }

        private async void OnExportPersona(object sender, RoutedEventArgs e)
            => await ExportPersonaAsync(false);

        private async void OnExportPrompt(object sender, RoutedEventArgs e)
            => await ExportPersonaAsync(true);

        private async Task ExportPersonaAsync(bool promptOnly)
        {
            if (!_loaded) return;
            if (!AppDialog.Confirm(Window.GetWindow(this),
                promptOnly
                    ? "只导出自定义补充提示词，不含身份字段和页面开关。导入后会出现在预设列表。\n请确认提示词正文不包含个人信息。"
                    : "导出当前人设、称呼、别名、顶嘴与交流偏好、颜文字和长度设置。\n不包含主要联系人账号、密钥、聊天记忆或系统权限。\n请检查人设正文及称呼是否写有个人信息。",
                "打包分享", okText: "继续")) return;
            var title = AppDialog.Prompt(Window.GetWindow(this), "分享包名称", "打包分享",
                string.IsNullOrWhiteSpace(_currentPresetName) ? TxtName.Text : _currentPresetName);
            if (title == null) return;
            var author = AppDialog.Prompt(Window.GetWindow(this), "作者昵称（写入包内信息）", "打包分享");
            if (author == null) return;
            var id = AppDialog.Prompt(Window.GetWindow(this), "包 ID：小写字母开头，只用小写字母、数字、短横线，最多 48 位。更新同一包时保持不变。",
                "打包分享", "persona-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            if (id == null) return;
            var version = AppDialog.Prompt(Window.GetWindow(this), "包版本，更新时提高版本号", "打包分享", "1.0.0");
            if (version == null) return;
            var file = AppFilePicker.Save("保存人设包", ".zip", "persona.zip");
            if (file == null) return;
            try
            {
                _saveTimer.Stop();
                if (!await FlushBeforePresetAsync()) return;
                var result = await _sup.SaveConfigurationAsync("/api/persona/export", new { });
                if (!result.Ok) { SetSaveState(Api.Message(result), "Bad"); return; }
                PersonaPackage.Export(file, result.Prop("persona"), title.Trim(), author.Trim(), id.Trim(), version.Trim(), promptOnly);
                SetSaveState(promptOnly
                    ? "纯提示词包已导出 · " + Now
                    : "已打包 · " + Now, "Text2");
            }
            catch (Exception ex) { SetSaveState("打包失败：" + Api.FriendlyError(ex), "Bad"); }
        }

        private async void OnSavePreset(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            var name = AppDialog.Prompt(Window.GetWindow(this),
                "给这份人设起个标题（只用于列表展示，不会改这个角色的名字）。以后保存人设会自动更新它。",
                "另存为人设预设");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || name.Length > 40)
            {
                SetSaveState("标题要 1~40 个字", "Bad");
                return;
            }
            _saveTimer.Stop();
            if (!await FlushBeforePresetAsync()) return;
            var res = await _sup.SaveConfigurationAsync("/api/persona/preset/save", new { name });
            if (!res.Ok)
            {
                SetSaveState("保存失败：" + Api.Message(res), "Bad");
                return;
            }
            _loaded = false;
            await LoadAsync();
            SetSaveState("已保存为预设「" + name + "」 · " + Now, "Text2");
        }

        private async void OnRenamePreset(object sender, RoutedEventArgs e)
        {
            if (!_loaded || !(CmbPreset.SelectedItem is PresetItem p) || !p.IsUser) return;
            var name = AppDialog.Prompt(Window.GetWindow(this),
                "只改预设标题，不动人设内容和当前使用状态。", "重命名自建预设", p.Name);
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0 || name.Length > 40)
            {
                SetSaveState("名字要 1~40 个字", "Bad");
                return;
            }
            var res = await _sup.SaveConfigurationAsync("/api/persona/preset/rename", new { id = p.Id, name });
            if (!res.Ok)
            {
                SetSaveState("重命名失败：" + Api.Message(res), "Bad");
                return;
            }
            await LoadPresetsAsync(_currentPresetId);
            SetSaveState("预设已重命名为「" + name + "」 · " + Now, "Text2");
        }

        private async void OnUpdatePreset(object sender, RoutedEventArgs e)
        {
            if (!_loaded || !(CmbPreset.SelectedItem is PresetItem p) || !p.IsUser) return;
            if (!string.Equals(p.Id, _currentPresetId, StringComparison.OrdinalIgnoreCase))
            {
                SetSaveState("先「应用选中预设」再更新，避免把当前人设写到别的预设里", "Bad");
                return;
            }
            if (!AppDialog.Confirm(Window.GetWindow(this),
                $"用当前表单里的内容覆盖预设「{p.Name}」？", "更新自建预设", okText: "更新")) return;
            if (!await FlushBeforePresetAsync()) return;
            var res = await _sup.SaveConfigurationAsync("/api/persona/preset/save", new { id = p.Id, name = p.Name });
            if (!res.Ok)
            {
                SetSaveState("更新失败：" + Api.Message(res), "Bad");
                return;
            }
            _loaded = false;
            await LoadAsync();
            SetSaveState("已更新预设「" + p.Name + "」 · " + Now, "Text2");
        }

        private async void OnImportPersonaFile(object sender, RoutedEventArgs e)
        {
            if (_applyingPreset) return;
            var file = AppFilePicker.Open("选择人设包", ".zip");
            if (file == null) return;
            try
            {
                SetSaveState("正在导入…", "Dim");
                // 只读本地文件，内核没启动也能先落到 data\personas，启动后列表自然出现。
                var imported = await Task.Run(() => PersonaPackage.Import(file, _sup.DataDir));
                await LoadPresetsAsync(imported.Id);
                var listed = CmbPreset.SelectedItem is PresetItem item
                    && string.Equals(item.Id, imported.Id, StringComparison.OrdinalIgnoreCase);
                SetSaveState(listed
                    ? "已导入「" + imported.Name + "」，点「应用选中预设」后生效"
                    : "已导入到 data\\personas\\，启动内核后会出现在预设列表里", "Text2");
            }
            catch (Exception ex)
            {
                SetSaveState("导入失败：" + Api.FriendlyError(ex), "Bad");
            }
        }

        private async void OnDeletePreset(object sender, RoutedEventArgs e)
        {
            if (!_loaded || !(CmbPreset.SelectedItem is PresetItem p)) return;
            var detail = p.IsUser
                ? "这是你自己保存的预设，会直接删除文件。"
                : "内置预设只会从列表隐藏，不删除安装目录里的文件；升级后仍保持隐藏。";
            if (!AppDialog.Confirm(Window.GetWindow(this),
                $"删除预设「{p.Name}」？\n\n{detail}\n\n正在使用的预设不能删除，请先导入别的人设。",
                "删除预设", danger: true, okText: "删除")) return;
            var res = await _sup.SaveConfigurationAsync("/api/persona/preset/delete", new { id = p.Id });
            if (!res.Ok)
            {
                SetSaveState("删除失败：" + Api.Message(res), "Bad");
                return;
            }
            _loaded = false;
            await LoadAsync();
            SetSaveState("已删除预设「" + p.Name + "」 · " + Now, "Text2");
        }

        // ── 截图 / 预览辅助 ────────────────────────────────────

        /// <summary>截图工具用：group = 切到群聊预览；advanced = 展开风格学习并滚到那里。</summary>
        public async Task PreviewSectionAsync(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (key == "group")
            {
                await SetChatTypeAsync("group");
                return;
            }
            if (key == "advanced")
            {
                StyleExpander.IsExpanded = true;
                MemoryExpander.IsExpanded = false;
                Body.ScrollToTop();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                StyleExpander.BringIntoView();
            }
        }

        /// <summary>多行输入框 → 字符串列表</summary>
        private static List<string> LineList(System.Windows.Controls.TextBox box)
            => (box.Text ?? "").Split('\n')
                .Select(x => x.Trim()).Where(x => x.Length > 0)
                .ToList();

        private static string S(JsonElement e, string n) => JsonRead.Str(e, n);

        private static bool B(JsonElement e, string n) => JsonRead.Bool(e, n);

        private static long L(JsonElement e, string n) => JsonRead.Long(e, n);
    }
}
