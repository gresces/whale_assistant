using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace WhaleLauncher.Views
{
    // ── 列表行 ─────────────────────────────────────────────────
    public sealed class ScopePill
    {
        public string Key { get; set; }
        public string Label { get; set; }
    }

    public sealed class KnowledgeRow : ObservableItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public List<string> Scopes { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public double Importance { get; set; } = 0.5;
        public double Confidence { get; set; } = 0.7;
        public string SourceRaw { get; set; } = "manual";
        public JsonElement SourceElement { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }

        public string Summary => Text ?? "";
        public string SourceLabel { get; set; } = "手动录入";
        public bool FromObject { get; set; }
        public List<ScopePill> ScopePills { get; set; } = new List<ScopePill>();

        /// <summary>source 里只有 uid，没有名字；等联系人策略读回来后补成人名。</summary>
        public void ResolveSourceName(IReadOnlyDictionary<string, PolicyRow> policies)
        {
            if (!FromObject || SourceElement.ValueKind != JsonValueKind.Object) return;
            var uid = JsonRead.Str(SourceElement, "uid");
            if (uid.Length == 0) return;
            if (policies != null && policies.TryGetValue(uid, out var row) && !string.IsNullOrWhiteSpace(row.Name))
                SourceLabel = "来自 " + row.Name;
            else
                SourceLabel = "来自对象 " + uid;
            OnPropertyChanged(nameof(SourceLabel));
            OnPropertyChanged(nameof(FromObject));
        }

        public Dictionary<string, object> ToJson()
        {
            var item = new Dictionary<string, object>
            {
                ["id"] = Id,
                ["title"] = Title ?? "",
                ["text"] = Text ?? "",
                ["scopes"] = Scopes ?? new List<string>(),
                ["tags"] = Tags ?? new List<string>(),
                ["importance"] = Importance,
                ["confidence"] = Confidence,
                ["createdAt"] = CreatedAt,
                ["updatedAt"] = UpdatedAt,
            };
            // 来自对象（L2）的 source 是结构化对象，整块原样带回；不能顺手改成 manual，
            // 否则一次保存就把「只有这个对象可见」的出处抹掉了。
            if (SourceElement.ValueKind == JsonValueKind.Object || SourceElement.ValueKind == JsonValueKind.String)
                item["source"] = SourceElement.Clone();
            else
                item["source"] = string.IsNullOrWhiteSpace(SourceRaw) ? "manual" : SourceRaw;
            return item;
        }
    }

    public sealed class AssetRow
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public List<string> Scopes { get; set; } = new List<string>();
        public long Size { get; set; }
        public string Kind { get; set; }
        public string Mime { get; set; }
        public int UsableBy { get; set; }
        public string MetaLine { get; set; }
        public string KeywordsLine { get; set; }
        public List<ScopePill> ScopePills { get; set; } = new List<ScopePill>();
    }

    public sealed class PendingRow : ObservableItem
    {
        private bool _isEditing;
        private string _editText;
        private string _editError;

        public string Id { get; set; }
        public string Uid { get; set; }
        public string Name { get; set; }
        public string Text { get; set; }
        public string Reason { get; set; }
        public string ReasonText { get; set; }
        public List<string> Assets { get; set; } = new List<string>();
        public long At { get; set; }
        public string TimeText { get; set; }
        public string AssetLine { get; set; }
        public bool HasAssets => Assets != null && Assets.Count > 0;
        public string SourceLine { get; set; }
        public bool HasSourceLine => !string.IsNullOrWhiteSpace(SourceLine);
        // 队列状态沿用内核的 pending / queued / sent / failed / uncertain；discarded 只出现在旧数据里。
        public string Status { get; set; } = "pending";
        public string KindText { get; set; }
        public bool HasKind => !string.IsNullOrWhiteSpace(KindText);
        public bool IsHandled { get; set; }
        public bool HasText => !string.IsNullOrWhiteSpace(Text);
        // 回执的文字本身说明结果，颜色只做辅助编码，不单靠颜色区分。
        public string ReceiptText { get; set; }
        public string ReceiptTone { get; set; } = "dim";

        public bool IsIdle => !IsEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                if (!Set(ref _isEditing, value)) return;
                OnPropertyChanged(nameof(IsIdle));
            }
        }
        public string EditText
        {
            get => _editText;
            set => Set(ref _editText, value);
        }
        public string EditError
        {
            get => _editError;
            set
            {
                if (!Set(ref _editError, value)) return;
                OnPropertyChanged(nameof(HasEditError));
            }
        }
        public bool HasEditError => !string.IsNullOrWhiteSpace(EditError);
    }

    public sealed class PolicyRow : ObservableItem
    {
        public string Uid { get; set; }
        public string Name { get; set; }
        public string KindLabel { get; set; } = "私聊";
        public bool Registered { get; set; }
        public List<string> Scopes { get; set; } = new List<string>();
        public List<string> DenyScopes { get; set; } = new List<string>();
        public string OnDuty { get; set; } = "off";
        public bool CanReceiveAssets { get; set; }
        public string DiscloseAI { get; set; } = "onAsk";
        public string Note { get; set; } = "";
        public string AddressAs { get; set; } = "";
        public string ToneOverride { get; set; } = "";
        public List<string> TabooTopics { get; set; } = new List<string>();
        public string SearchText { get; set; } = "";

        public string DisplayName => !string.IsNullOrWhiteSpace(Name)
            ? Name
            : (KindLabel == "群聊" ? "未命名群聊" : "未命名联系人");

        public string Summary
        {
            get
            {
                if (!Registered) return "默认仅公开，在岗关闭";
                var scopes = Scopes == null || Scopes.Count == 0
                    ? "无可见域"
                    : string.Join("、", Scopes.Select(SelfPage.ScopeLabel));
                return scopes + " · " + SelfPage.OnDutyLabel(OnDuty) + (CanReceiveAssets ? " · 可收文件" : " · 不收文件");
            }
        }

        public void RefreshSummary() => OnPropertyChanged(nameof(Summary));
    }

    public sealed class ApiIssue
    {
        public string Message { get; set; }
        public string Detail { get; set; }
    }

    public partial class SelfPage : UserControl, IRefreshable
    {
        private const int AssetLimitBytes = 20 * 1024 * 1024;
        private const int AssetLimitCount = 500;
        private const int HandledLimit = 20;
        private const int HandledCap = 50;
        private const string OutdatedKernel = "内核版本过旧，请先启动内核。";

        private static readonly (string Key, string Label)[] ScopeDefs =
        {
            ("public", "公开"), ("friend", "朋友"), ("work", "工作"), ("family", "家人"), ("private", "私有"),
        };

        private static readonly string[] DefaultSensitive =
        {
            "转账", "合同", "签字", "密码", "验证码", "身份证", "医疗", "法律", "借钱",
        };

        // 内核用 JS getDay()：0 = 周日、1 = 周一 … 6 = 周六。标签按中国人的习惯从周一排到周日。
        private static readonly string[] WeekNames = { "日", "一", "二", "三", "四", "五", "六" };

        private readonly RuntimeSupervisor _sup;
        private readonly DispatcherTimer _autoSave;
        private readonly List<Button> _tabButtons = new List<Button>();
        private readonly Dictionary<string, TextBlock> _tabBadges = new Dictionary<string, TextBlock>(StringComparer.Ordinal);
        private readonly List<ToggleButton> _knowledgeEditorChips = new List<ToggleButton>();
        private readonly List<ToggleButton> _assetEditorChips = new List<ToggleButton>();
        private readonly List<ToggleButton> _policyChips = new List<ToggleButton>();
        private readonly List<ToggleButton> _dayChips = new List<ToggleButton>();
        private readonly List<KnowledgeRow> _knowledge = new List<KnowledgeRow>();
        private readonly List<AssetRow> _assets = new List<AssetRow>();
        private readonly List<PendingRow> _pending = new List<PendingRow>();
        private readonly List<PendingRow> _handled = new List<PendingRow>();
        private readonly Dictionary<string, PolicyRow> _policyMap = new Dictionary<string, PolicyRow>(StringComparer.Ordinal);
        private readonly List<Button> _onDutyButtons = new List<Button>();
        private readonly List<Button> _discloseButtons = new List<Button>();

        private string _tab = "self";
        private JsonElement _selfDoc;
        private bool _selfLoaded;
        private bool _selfLoading;
        private bool _selfDirty;
        private bool _savingSelf;
        private bool _loading;
        private DateTime _lastLoadedAt = DateTime.MinValue;

        private string _editingKnowledgeId;
        private string _importPath;
        private bool _assetImportOpen;

        private string _selectedUid;
        private bool _suppressPolicyFill;
        private bool _policyDirty;
        private bool _policyLoaded;
        private string _policyOnDuty = "off";
        private string _policyDisclose = "onAsk";

        private bool _dutyLoaded;
        private bool _dutyEnabled;
        private bool _dutyDirty;
        private bool _suppressDuty;
        private bool _handledExpanded;
        private bool _clearingHandled;
        private DateTime _lastDutyRead = DateTime.MinValue;

        public SelfPage(RuntimeSupervisor sup)
        {
            InitializeComponent();
            _sup = sup;

            BuildTabs();
            BuildScopeEditors();
            BuildDutyDays();
            BuildPolicyChoiceButtons();
            _autoSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            _autoSave.Tick += async (_, __) =>
            {
                _autoSave.Stop();
                if (_selfDirty) await SaveSelfAsync(auto: true);
            };

            Loaded += async (_, __) =>
            {
                if (_lastLoadedAt == DateTime.MinValue) await LoadAllAsync();
            };
            Unloaded += (_, __) =>
            {
                _autoSave.Stop();
                // 切到别的导航时页面会被换下；还没落盘的编辑在这里补一次，不能等定时器。
                if (_selfDirty && _selfLoaded && !_savingSelf) _ = SaveSelfAsync(auto: true);
            };
        }

        // ── 页内分段 ────────────────────────────────────────────
        /// <summary>截图工具用：直接切到某一段，不影响正常运行路径。</summary>
        public void PreviewSection(string key)
        {
            if (key == "pending")
            {
                ShowTab("duty");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayout();
                    Body.UpdateLayout();
                    try
                    {
                        var point = PendingHeading.TranslatePoint(new Point(0, 0), Body);
                        Body.ScrollToVerticalOffset(Math.Max(0, Body.VerticalOffset + point.Y - 8));
                    }
                    catch
                    {
                        PendingHeading.BringIntoView();
                    }
                }), DispatcherPriority.Loaded);
                return;
            }
            if (key == "self" || key == "knowledge" || key == "policy" || key == "assets" || key == "duty")
                ShowTab(key);
        }

        private void BuildTabs()
        {
            AddTab("self", "自我档案");
            AddTab("knowledge", "信息与权限");
            AddTab("policy", "对象权限");
            AddTab("assets", "可分享资产");
            AddTab("duty", "在岗");
            ShowTab("self");
        }

        private void AddTab(string key, string label)
        {
            var button = SegmentedTabs.Make(label, out var badge, (_, __) => ShowTab(key));
            button.Margin = new Thickness(0, 0, 4, 0);
            _tabButtons.Add(button);
            _tabBadges[key] = badge;
            TabBar.Children.Add(button);
        }

        /// <summary>页签上只放最短状态：未展开也知道填没填、有几个对象、在岗怎么回。</summary>
        private void UpdateTabSummaries()
        {
            void Set(string key, string text)
            {
                if (_tabBadges.TryGetValue(key, out var badge)) badge.Text = text;
            }
            Set("self", string.IsNullOrWhiteSpace(TxtSelfName.Text) ? "未填写" : "已填写");
            Set("knowledge", _knowledge.Count == 0 ? "0 条" : _knowledge.Count + " 条");
            Set("policy", _policyMap.Values.Count(x => x.Registered) + " 个对象");
            Set("assets", _assets.Count + " 个文件");
            var auto = _policyMap.Values.Count(x => x.Registered && x.OnDuty == "auto");
            Set("duty", !_dutyEnabled ? "关闭" : auto > 0 ? "自动" : "确认制");
        }

        private void ShowTab(string key)
        {
            _tab = key;
            SecSelf.Visibility = key == "self" ? Visibility.Visible : Visibility.Collapsed;
            SecKnowledge.Visibility = key == "knowledge" ? Visibility.Visible : Visibility.Collapsed;
            SecPolicy.Visibility = key == "policy" ? Visibility.Visible : Visibility.Collapsed;
            SecAssets.Visibility = key == "assets" ? Visibility.Visible : Visibility.Collapsed;
            SecOnDuty.Visibility = key == "duty" ? Visibility.Visible : Visibility.Collapsed;
            for (var i = 0; i < _tabButtons.Count; i++)
            {
                var tabKeys = new[] { "self", "knowledge", "policy", "assets", "duty" };
                SegmentedTabs.Mark(_tabButtons[i], tabKeys[i] == key);
            }
            UpdateTabSummaries();
            Body.ScrollToTop();
        }

        // ── 加载 ────────────────────────────────────────────────
        private async Task LoadAllAsync()
        {
            if (_loading) return;
            _loading = true;
            BtnReload.IsEnabled = false;
            var issues = new List<ApiIssue>();
            try
            {
                await LoadSelfAsync(issues);
                await LoadPolicyAsync(issues, reportSessions: false);
                await LoadAssetsAsync(issues);
                await LoadDutyAsync(issues);
                _lastLoadedAt = DateTime.Now;
            }
            catch (Exception ex)
            {
                // 内核返回的形状再怪也不能把整页打崩；留一句能看懂的提示，其余控件继续可用。
                issues.Add(new ApiIssue { Message = "读取分身设置失败：" + ex.Message, Detail = "本地解析" });
            }
            finally
            {
                _loading = false;
                _lastLoadedAt = DateTime.Now;
                BtnReload.IsEnabled = true;
                ShowIssues(issues);
            }
        }

        private async Task LoadSelfAsync(List<ApiIssue> issues)
        {
            var res = await Api.Get(_sup.Url, "/api/self", 15);
            if (!res.Ok)
            {
                _selfLoaded = false;
                SetSelfFieldsEnabled(false);
                BtnNewKnowledge.IsEnabled = false;
                KnowledgeEmpty.Text = "暂时读不到知识条目。";
                AddIssue(issues, res, "/api/self");
                return;
            }

            var model = res.Prop("self");
            if (model.ValueKind != JsonValueKind.Object) model = res.Prop("model");
            if (model.ValueKind != JsonValueKind.Object) model = res.Root;
            if (model.ValueKind != JsonValueKind.Object)
            {
                _selfLoaded = false;
                SetSelfFieldsEnabled(false);
                BtnNewKnowledge.IsEnabled = false;
                issues.Add(new ApiIssue { Message = "内核返回的自我档案格式不对。", Detail = "/api/self · 缺少 model 对象" });
                return;
            }

            _selfDoc = model.Clone();
            _selfLoaded = true;
            SetSelfFieldsEnabled(true);
            BtnNewKnowledge.IsEnabled = true;

            var identity = JsonRead.Prop(model, "identity");
            var voice = JsonRead.Prop(model, "voice");
            _selfLoading = true;
            try
            {
                TxtSelfName.Text = JsonRead.Str(identity, "name");
                TxtSelfAliases.Text = string.Join("、", ReadStringArray(JsonRead.Prop(identity, "aliases")));
                var age = JsonRead.Int(identity, "age", 0);
                TxtAge.Text = age > 0 ? age.ToString(CultureInfo.InvariantCulture) : "";
                TxtSelfCity.Text = JsonRead.Str(identity, "city");
                TxtSelfJob.Text = JsonRead.Str(identity, "job");
                TxtSelfCompany.Text = JsonRead.Str(identity, "company");

                TxtSelfTone.Text = JsonRead.Str(voice, "tone");
                TxtSelfSentence.Text = JsonRead.Str(voice, "sentenceStyle");
                TxtSelfCatchphrases.Text = string.Join("、", ReadStringArray(JsonRead.Prop(voice, "catchphrases")));
                TxtSelfEmoji.Text = JsonRead.Str(voice, "emojiHabits");
                TxtSelfTaboo.Text = string.Join("、", ReadStringArray(JsonRead.Prop(voice, "tabooWords")));
            }
            finally
            {
                _selfLoading = false;
            }

            _knowledge.Clear();
            var arr = JsonRead.Prop(model, "knowledge");
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    _knowledge.Add(ParseKnowledge(item));
                }
            }
            RebuildKnowledgeList();

            _selfDirty = false;
            SelfSaveState.Text = "已读取 " + DateTime.Now.ToString("HH:mm");
            SelfSaveState.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            SelfError.Visibility = Visibility.Collapsed;
            UpdateTabSummaries();

            var warnings = ReadStringArray(res.Prop("warnings"));
            if (warnings.Count > 0)
            {
                SelfError.Text = "提醒：" + string.Join("；", warnings.Take(3));
                SelfError.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
                SelfError.Visibility = Visibility.Visible;
            }
        }

        private void SetSelfFieldsEnabled(bool enabled)
        {
            foreach (var box in new Control[]
            {
                TxtSelfName, TxtSelfAliases, TxtAge, TxtSelfCity, TxtSelfJob, TxtSelfCompany,
                TxtSelfTone, TxtSelfSentence, TxtSelfCatchphrases, TxtSelfEmoji, TxtSelfTaboo,
            })
                box.IsEnabled = enabled;
            BtnSaveSelf.IsEnabled = enabled;
            if (!enabled) SelfSaveState.Text = "还没读到现有档案，暂不能保存";
        }

        private void OnReloadAll(object sender, RoutedEventArgs e) => _ = LoadAllAsync();

        // ── 自我档案：自动保存 + 手动保存 ─────────────────────────
        private void OnSelfFieldChanged(object sender, TextChangedEventArgs e)
        {
            if (_selfLoading || !_selfLoaded) return;
            _selfDirty = true;
            SelfSaveState.Text = "未保存";
            SelfSaveState.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            SelfError.Visibility = Visibility.Collapsed;
            _autoSave.Stop();
            _autoSave.Start();
        }

        private void OnSaveSelfClick(object sender, RoutedEventArgs e)
        {
            _autoSave.Stop();
            _ = SaveSelfAsync(auto: false);
        }

        private async Task<bool> SaveSelfAsync(bool auto)
        {
            if (!_selfLoaded) return false;
            // 自动保存和手动保存可能撞在一起。等前一次落地后再发，避免旧内容覆盖新内容。
            for (var i = 0; i < 300 && _savingSelf; i++) await Task.Delay(50);
            if (_savingSelf) return false;
            if (!TryBuildSelfModel(out var model, out var error))
            {
                SelfSaveState.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                SelfSaveState.Text = auto ? "还没填完整" : "未保存";
                if (!auto)
                {
                    SelfError.Text = error;
                    SelfError.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                    SelfError.Visibility = Visibility.Visible;
                }
                return false;
            }

            _savingSelf = true;
            SelfSaveState.Text = "正在保存…";
            SelfError.Visibility = Visibility.Collapsed;
            try
            {
                var res = await Api.PostJson(_sup.Url, "/api/self", new Dictionary<string, object> { ["model"] = model }, 15);
                if (!res.Ok)
                {
                    SelfSaveState.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                    SelfSaveState.Text = "保存失败";
                    SelfError.Text = Friendly(res);
                    SelfError.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                    SelfError.Visibility = Visibility.Visible;
                    ShowFailure("保存自我档案失败", res, "/api/self");
                    return false;
                }
                _selfDirty = false;
                SelfSaveState.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                SelfSaveState.Text = "已保存 " + DateTime.Now.ToString("HH:mm");
                UpdateTabSummaries();
                var warnings = ReadStringArray(res.Prop("warnings"));
                if (warnings.Count > 0)
                {
                    SelfError.Text = "提醒：" + string.Join("；", warnings.Take(3));
                    SelfError.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
                    SelfError.Visibility = Visibility.Visible;
                }
                return true;
            }
            finally
            {
                _savingSelf = false;
            }
        }

        private bool TryBuildSelfModel(out Dictionary<string, object> model, out string error)
        {
            model = null;
            error = "";
            if (string.IsNullOrWhiteSpace(TxtSelfName.Text))
            {
                error = "姓名不能为空：分身要靠它自称。";
                return false;
            }
            if (!TxtAge.TryRead(out var age))
            {
                error = TxtAge.ErrorText;
                return false;
            }

            var identity = new Dictionary<string, object>
            {
                ["name"] = TxtSelfName.Text.Trim(),
                ["aliases"] = SplitTags(TxtSelfAliases.Text),
                ["age"] = age,
                ["city"] = TxtSelfCity.Text.Trim(),
                ["job"] = TxtSelfJob.Text.Trim(),
                ["company"] = TxtSelfCompany.Text.Trim(),
            };
            // 合同里有 timezone，界面不编辑，但保存时不能丢。
            var tz = JsonRead.Str(JsonRead.Prop(_selfDoc, "identity"), "timezone");
            if (tz.Length > 0) identity["timezone"] = tz;

            var voice = new Dictionary<string, object>
            {
                ["tone"] = TxtSelfTone.Text.Trim(),
                ["sentenceStyle"] = TxtSelfSentence.Text.Trim(),
                ["catchphrases"] = SplitTags(TxtSelfCatchphrases.Text),
                ["emojiHabits"] = TxtSelfEmoji.Text.Trim(),
                ["tabooWords"] = SplitTags(TxtSelfTaboo.Text),
            };

            model = new Dictionary<string, object>
            {
                ["version"] = JsonRead.Int(_selfDoc, "version", 1),
                ["updatedAt"] = NowMs(),
                ["identity"] = identity,
                ["voice"] = voice,
                ["knowledge"] = _knowledge.Select(x => x.ToJson()).ToList(),
            };
            // 立场 / 话术这一版没有界面，但整体保存时必须原样带上，不能因为改个名字就清空。
            CopyJsonField(_selfDoc, model, "stances");
            CopyJsonField(_selfDoc, model, "templates");
            return true;
        }

        // ── 信息与权限 ──────────────────────────────────────────
        private void BuildScopeEditors()
        {
            BuildChips(KnowledgeScopes, _knowledgeEditorChips, includePrivate: true, OnKnowledgeScopeChanged);
            BuildChips(AssetScopes, _assetEditorChips, includePrivate: true, OnAssetScopeChanged);
            // 对象不设 private：私有域本来就不会发给任何对象，摆出来只会让人误配。
            BuildChips(PolicyScopes, _policyChips, includePrivate: false, MarkPolicyDirty);
        }

        private void BuildChips(Panel host, List<ToggleButton> output, bool includePrivate, Action onChange)
        {
            host.Children.Clear();
            output.Clear();
            foreach (var def in ScopeDefs)
            {
                if (def.Key == "private" && !includePrivate) continue;
                var chip = new ToggleButton
                {
                    Content = def.Label,
                    Tag = def.Key,
                    Style = (Style)FindResource("ScopeChip"),
                    FontSize = 12,
                };
                AutomationProperties.SetName(chip, def.Key == "private" ? "私有域（不会发给任何对象）" : def.Label + "域");
                chip.Click += (_, __) => onChange();
                host.Children.Add(chip);
                output.Add(chip);
            }
        }

        private void OnKnowledgeScopeChanged()
        {
            // 私有必须自己说明白：这里不弹窗，只让正在编辑的人看到一句。
            KnowledgeError.Visibility = Visibility.Collapsed;
        }

        private void OnAssetScopeChanged() => AssetImportError.Visibility = Visibility.Collapsed;

        private void OnNewKnowledge(object sender, RoutedEventArgs e) => OpenKnowledgeEditor(null);

        private void OnEditKnowledge(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is KnowledgeRow row) OpenKnowledgeEditor(row);
        }

        private void OpenKnowledgeEditor(KnowledgeRow row)
        {
            _editingKnowledgeId = row?.Id;
            KnowledgeEditorTitle.Text = row == null ? "新增知识" : "编辑知识";
            TxtKnowledgeTitle.Text = row?.Title ?? "";
            TxtKnowledgeText.Text = row?.Text ?? "";
            SetChipSelection(_knowledgeEditorChips, row?.Scopes ?? new List<string>());
            KnowledgeError.Visibility = Visibility.Collapsed;
            KnowledgeCounter.Text = TxtKnowledgeText.Text.Length + " / 800";
            KnowledgeEditor.Visibility = Visibility.Visible;
            KnowledgeEditor.BringIntoView();
            Dispatcher.BeginInvoke(new Action(() => TxtKnowledgeTitle.Focus()), DispatcherPriority.Input);
        }

        private void OnCancelKnowledge(object sender, RoutedEventArgs e)
        {
            KnowledgeEditor.Visibility = Visibility.Collapsed;
            _editingKnowledgeId = null;
            KnowledgeError.Visibility = Visibility.Collapsed;
            BtnNewKnowledge.Focus();
        }

        private void OnKnowledgeTextChanged(object sender, TextChangedEventArgs e)
        {
            KnowledgeCounter.Text = (TxtKnowledgeText.Text ?? "").Length + " / 800";
        }

        private async void OnSaveKnowledge(object sender, RoutedEventArgs e)
        {
            var title = (TxtKnowledgeTitle.Text ?? "").Trim();
            var text = (TxtKnowledgeText.Text ?? "").Trim();
            var scopes = SelectedChips(_knowledgeEditorChips);
            if (title.Length == 0)
            {
                ShowKnowledgeError("标题不能为空。");
                return;
            }
            if (text.Length == 0)
            {
                ShowKnowledgeError("内容不能为空。");
                return;
            }
            if (text.Length > 800)
            {
                ShowKnowledgeError("内容最多 800 字，现在 " + text.Length + " 字。");
                return;
            }
            if (scopes.Count == 0)
            {
                ShowKnowledgeError("至少选一个域，否则这条知识不会出现在任何对话里。");
                return;
            }

            var now = NowMs();
            KnowledgeRow row;
            if (_editingKnowledgeId != null && _knowledge.FirstOrDefault(x => x.Id == _editingKnowledgeId) is KnowledgeRow existing)
            {
                row = existing;
                row.Title = title;
                row.Text = text;
                row.Scopes = scopes;
                row.UpdatedAt = now;
            }
            else
            {
                row = new KnowledgeRow
                {
                    Id = "k-" + DateTime.Now.ToString("yyyyMMdd") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4),
                    Title = title,
                    Text = text,
                    Scopes = scopes,
                    Tags = new List<string>(),
                    Importance = 0.5,
                    Confidence = 0.7,
                    SourceRaw = "manual",
                    CreatedAt = now,
                    UpdatedAt = now,
                    SourceLabel = "手动录入",
                    FromObject = false,
                };
                _knowledge.Insert(0, row);
            }
            row.ScopePills = MakePills(row.Scopes);

            BtnSaveKnowledge.IsEnabled = false;
            try
            {
                var ok = await SaveSelfAsync(auto: false);
                if (!ok)
                {
                    ShowKnowledgeError("保存失败，内容还在，稍后可以再试。");
                    return;
                }
                KnowledgeEditor.Visibility = Visibility.Collapsed;
                _editingKnowledgeId = null;
                RebuildKnowledgeList();
                BtnNewKnowledge.Focus();
            }
            finally
            {
                BtnSaveKnowledge.IsEnabled = true;
            }
        }

        private async void OnDeleteKnowledge(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not KnowledgeRow row) return;
            if (!AppDialog.Confirm(Window.GetWindow(this),
                "删除「" + row.Title + "」？\n\n删除后所有会话立即不再看到这条信息。",
                "删除知识", danger: true, okText: "删除", cancelText: "取消")) return;
            var index = _knowledge.IndexOf(row);
            if (index < 0) return;
            _knowledge.RemoveAt(index);
            RebuildKnowledgeList();
            if (await SaveSelfAsync(auto: false)) return;
            _knowledge.Insert(Math.Min(index, _knowledge.Count), row);
            RebuildKnowledgeList();
            await LoadSelfAsync(new List<ApiIssue>());
        }

        private void ShowKnowledgeError(string message)
        {
            KnowledgeError.Text = message;
            KnowledgeError.Visibility = Visibility.Visible;
            AutomationProperties.SetName(KnowledgeError, message);
        }

        private void RebuildKnowledgeList()
        {
            KnowledgeList.ItemsSource = null;
            KnowledgeList.ItemsSource = _knowledge;
            KnowledgeCount.Text = _knowledge.Count + " 条";
            KnowledgeEmpty.Visibility = _knowledge.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static KnowledgeRow ParseKnowledge(JsonElement item)
        {
            var row = new KnowledgeRow
            {
                Id = JsonRead.Str(item, "id"),
                Title = JsonRead.Str(item, "title"),
                Text = JsonRead.Str(item, "text"),
                Scopes = ReadStringArray(JsonRead.Prop(item, "scopes")),
                Tags = ReadStringArray(JsonRead.Prop(item, "tags")),
                Importance = JsonRead.Double(item, "importance", 0.5),
                Confidence = JsonRead.Double(item, "confidence", 0.7),
                CreatedAt = JsonRead.Long(item, "createdAt"),
                UpdatedAt = JsonRead.Long(item, "updatedAt"),
                ScopePills = new List<ScopePill>(),
            };
            row.ScopePills = MakePills(row.Scopes);
            ParseSource(item, row);
            return row;
        }

        private static void ParseSource(JsonElement item, KnowledgeRow row)
        {
            var source = JsonRead.Prop(item, "source");
            if (source.ValueKind == JsonValueKind.String)
            {
                var text = source.GetString() ?? "";
                row.SourceRaw = text;
                row.SourceElement = source.Clone();
                if (text == "manual" || text == "self" || text.Length == 0)
                {
                    row.SourceLabel = "手动录入";
                    return;
                }
                row.SourceLabel = "来自对象";
                row.FromObject = true;
                return;
            }
            if (source.ValueKind == JsonValueKind.Object)
            {
                row.SourceElement = source.Clone();
                var name = JsonRead.Str(source, "name");
                if (name.Length == 0) name = JsonRead.Str(source, "title");
                if (name.Length == 0) name = JsonRead.Str(source, "uid");
                if (name.Length == 0) name = JsonRead.Str(source, "chatId");
                row.SourceLabel = name.Length > 0 ? "来自 " + name : "来自对象";
                row.FromObject = true;
                return;
            }
            var origin = JsonRead.Str(item, "origin");
            if (origin == "l2" || origin == "relation")
            {
                row.SourceLabel = "来自对象";
                row.FromObject = true;
                return;
            }
            row.SourceLabel = "手动录入";
        }

        // ── 对象权限 ────────────────────────────────────────────
        private async Task LoadPolicyAsync(List<ApiIssue> issues, bool reportSessions)
        {
            _policyMap.Clear();
            var res = await Api.Get(_sup.Url, "/api/self/policy", 15);
            _policyLoaded = res.Ok;
            if (res.Ok)
            {
                var contacts = res.Prop("contacts");
                if (contacts.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in contacts.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object) continue;
                        AddPolicyRow(ParsePolicy(property.Name, property.Value));
                    }
                }
                else if (contacts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in contacts.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object)
                            AddPolicyRow(ParsePolicy(JsonRead.Str(item, "uid"), item));
                }
                else
                {
                    var items = res.Prop("items");
                    if (items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Object)
                                AddPolicyRow(ParsePolicy(JsonRead.Str(item, "uid"), item));
                }
            }
            else
            {
                AddIssue(issues, res, "/api/self/policy");
            }

            // 会话列表用现有 /api/memory/chats，只是补充「可以登记谁」；它失败不影响策略读取。
            var chats = await Api.Get(_sup.Url, "/api/memory/chats", 12);
            if (chats.Ok)
            {
                var arr = chats.Prop("chats");
                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chat in arr.EnumerateArray())
                    {
                        if (chat.ValueKind != JsonValueKind.Object) continue;
                        var key = JsonRead.Str(chat, "key");
                        var uid = ChatUid(key);
                        if (uid.Length == 0 || uid == "*") continue;
                        var isGroup = JsonRead.Str(chat, "chatType") == "group" || uid.EndsWith("@chatroom");
                        var title = ChatTitle(JsonRead.Str(chat, "title"), uid);
                        if (_policyMap.TryGetValue(uid, out var existing))
                        {
                            if (string.IsNullOrWhiteSpace(existing.Name) && title.Length > 0) existing.Name = title;
                            existing.KindLabel = isGroup ? "群聊" : "私聊";
                            existing.SearchText = BuildSearch(existing);
                            continue;
                        }
                        AddPolicyRow(new PolicyRow
                        {
                            Uid = uid,
                            Name = title,
                            KindLabel = isGroup ? "群聊" : "私聊",
                            Registered = false,
                            SearchText = "",
                        });
                    }
                }
            }
            else if (reportSessions)
            {
                AddIssue(issues, chats, "/api/memory/chats");
            }

            foreach (var row in _policyMap.Values)
            {
                row.SearchText = BuildSearch(row);
            }

            RebuildPolicyLists();
            foreach (var item in _knowledge) item.ResolveSourceName(_policyMap);
            RebuildKnowledgeList();
            if (_selectedUid != null && _policyMap.TryGetValue(_selectedUid, out var selected))
                ShowPolicyEditor(selected);
            else if (PolicyEditorBody.Visibility == Visibility.Visible)
                ShowPolicyEditor(null);
            UpdateTabSummaries();
        }

        private void AddPolicyRow(PolicyRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Uid)) return;
            _policyMap[row.Uid] = row;
        }

        private static PolicyRow ParsePolicy(string uid, JsonElement item)
        {
            var realUid = JsonRead.Str(item, "uid");
            if (realUid.Length == 0) realUid = uid;
            var name = JsonRead.Str(item, "name");
            return new PolicyRow
            {
                Uid = realUid,
                Name = name,
                KindLabel = realUid.EndsWith("@chatroom") ? "群聊" : "私聊",
                Registered = true,
                Scopes = ReadStringArray(JsonRead.Prop(item, "scopes")),
                DenyScopes = ReadStringArray(JsonRead.Prop(item, "denyScopes")),
                OnDuty = NormalizeOnDuty(JsonRead.Str(item, "onDuty")),
                CanReceiveAssets = JsonRead.Bool(item, "canReceiveAssets"),
                DiscloseAI = NormalizeDisclose(JsonRead.Str(item, "discloseAI")),
                Note = JsonRead.Str(item, "note"),
                AddressAs = JsonRead.Str(item, "addressAs"),
                ToneOverride = JsonRead.Str(item, "toneOverride"),
                TabooTopics = ReadStringArray(JsonRead.Prop(item, "tabooTopics")),
            };
        }

        private static string BuildSearch(PolicyRow row)
            => ((row.Name ?? "") + " " + (row.Uid ?? "") + " " + (row.Note ?? "")).ToLowerInvariant();

        private static string ChatUid(string key)
        {
            var raw = (key ?? "").Trim();
            if (raw.StartsWith("wechat:", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(7);
            // 会话 key 有三种：wechat:<wxid>、wechat:u:<wxid>、wechat:g:<群 ID>。
            if (raw.StartsWith("g:", StringComparison.Ordinal) || raw.StartsWith("u:", StringComparison.Ordinal))
                raw = raw.Substring(2);
            return raw.Trim();
        }

        /// <summary>会话标题有时就是原始 key（老记忆没存昵称），别把 wechat: / u: 直接铺在界面上。</summary>
        private static string ChatTitle(string title, string uid)
        {
            var text = (title ?? "").Trim();
            if (text.StartsWith("wechat:", StringComparison.OrdinalIgnoreCase)) text = text.Substring(7);
            if (text.StartsWith("g:", StringComparison.Ordinal) || text.StartsWith("u:", StringComparison.Ordinal))
                text = text.Substring(2);
            return text == uid ? "" : text;
        }

        private void RebuildPolicyLists()
        {
            var filter = (TxtPolicyFilter.Text ?? "").Trim().ToLowerInvariant();
            var all = _policyMap.Values
                .Where(x => filter.Length == 0 || x.SearchText.Contains(filter))
                .ToList();
            var registered = all.Where(x => x.Registered)
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var unregistered = all.Where(x => !x.Registered)
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            RegisteredList.ItemsSource = registered;
            UnregisteredList.ItemsSource = unregistered.Take(60).ToList();
            RegisteredEmpty.Visibility = registered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RegisteredEmpty.Text = _policyMap.Values.Any(x => x.Registered)
                ? "没有匹配的已登记对象。"
                : "还没有登记对象。未登记对象默认仅公开、在岗关闭。";
            UnregisteredMore.Text = unregistered.Count > 60
                ? "还有 " + (unregistered.Count - 60) + " 个未登记对象，用上面的筛选查找。"
                : (unregistered.Count == 0 ? "没有未登记对象了。" : "");
            PolicySummary.Text = "共 " + _policyMap.Count + " 个对象 · "
                + _policyMap.Values.Count(x => x.Registered) + " 个已登记";

            if (_selectedUid != null)
            {
                if (registered.FirstOrDefault(x => x.Uid == _selectedUid) is PolicyRow r) RegisteredList.SelectedItem = r;
                else if (unregistered.FirstOrDefault(x => x.Uid == _selectedUid) is PolicyRow u) UnregisteredList.SelectedItem = u;
            }
            else if (registered.Count > 0)
            {
                // 首次进入直接展开第一个已登记对象，别让右边一直是一块空白占位。
                RegisteredList.SelectedItem = registered[0];
            }
        }

        private void OnPolicyFilterChanged(object sender, TextChangedEventArgs e) => RebuildPolicyLists();

        private void OnRegisteredSelected(object sender, SelectionChangedEventArgs e)
        {
            if (RegisteredList.SelectedItem is not PolicyRow row) return;
            _selectedUid = row.Uid;
            UnregisteredList.SelectedItem = null;
            ShowPolicyEditor(row);
        }

        private void OnUnregisteredSelected(object sender, SelectionChangedEventArgs e)
        {
            if (UnregisteredList.SelectedItem is not PolicyRow row) return;
            _selectedUid = row.Uid;
            RegisteredList.SelectedItem = null;
            ShowPolicyEditor(row);
        }

        private void ShowPolicyEditor(PolicyRow row)
        {
            if (row == null)
            {
                PolicyEditorBody.Visibility = Visibility.Collapsed;
                PolicyEditorEmpty.Visibility = Visibility.Visible;
                return;
            }

            PolicyEditorEmpty.Visibility = Visibility.Collapsed;
            PolicyEditorBody.Visibility = Visibility.Visible;
            PolicyName.Text = row.DisplayName;
            PolicyUid.Text = row.Uid;
            PolicyKind.Text = "· " + row.KindLabel;
            PolicyUnregisteredPanel.Visibility = row.Registered ? Visibility.Collapsed : Visibility.Visible;
            PolicySettingsPanel.Visibility = row.Registered ? Visibility.Visible : Visibility.Collapsed;
            BtnRegisterPolicy.IsEnabled = _policyLoaded;
            BtnRegisterPolicy.ToolTip = _policyLoaded ? null : "内核不支持对象策略接口，先启动新内核";
            PolicySaveState.Text = "";
            if (!row.Registered) return;

            _suppressPolicyFill = true;
            try
            {
                SetChipSelection(_policyChips, row.Scopes);
                _policyOnDuty = NormalizeOnDuty(row.OnDuty);
                _policyDisclose = NormalizeDisclose(row.DiscloseAI);
                MarkChoice(_onDutyButtons, _policyOnDuty);
                MarkChoice(_discloseButtons, _policyDisclose);
                ChkPolicyAssets.IsChecked = row.CanReceiveAssets;
                TxtAddressAs.Text = row.AddressAs ?? "";
                TxtToneOverride.Text = row.ToneOverride ?? "";
                TxtTabooTopics.Text = string.Join("、", row.TabooTopics ?? new List<string>());
                TxtPolicyNote.Text = row.Note ?? "";
            }
            finally
            {
                _suppressPolicyFill = false;
            }
            UpdatePolicyHints();
            UpdateRelationSummary();
            _policyDirty = false;
        }

        private void BuildPolicyChoiceButtons()
        {
            AddChoice(PolicyOnDutyPanel, _onDutyButtons, "关闭", "off");
            AddChoice(PolicyOnDutyPanel, _onDutyButtons, "先给我确认", "draft");
            AddChoice(PolicyOnDutyPanel, _onDutyButtons, "直接发送", "auto");

            AddChoice(PolicyDisclosePanel, _discloseButtons, "不主动说", "never");
            AddChoice(PolicyDisclosePanel, _discloseButtons, "被问才说", "onAsk");
            AddChoice(PolicyDisclosePanel, _discloseButtons, "总是说明", "always");
        }

        private void AddChoice(Panel panel, List<Button> group, string text, string value)
        {
            var button = new Button
            {
                Content = text,
                Tag = value,
                Style = (Style)FindResource("Ghost"),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Height = 30,
            };
            AutomationProperties.SetName(button, text);
            button.Click += (_, __) =>
            {
                if (ReferenceEquals(group, _onDutyButtons)) _policyOnDuty = value;
                else _policyDisclose = value;
                MarkChoice(group, value);
                UpdatePolicyHints();
                MarkPolicyDirty();
            };
            panel.Children.Add(button);
            group.Add(button);
        }

        private void UpdatePolicyHints()
        {
            PolicyOnDutyHint.Text = _policyOnDuty switch
            {
                "draft" => "先给我确认：生成后进待确认队列，你点了才发。",
                "auto" => "直接发送：不经确认就以你的身份发出去；碰到敏感话题仍会停下。",
                _ => "关闭：这个对象不会收到任何代回。",
            };
            PolicyDiscloseHint.Text = _policyDisclose switch
            {
                "never" => "不主动说；被直接问到时不撒谎。",
                "always" => "每轮或每 24 小时首条自动说明是分身代回。",
                _ => "被直接问到时才说明这条是分身代回的。",
            };
        }

        private void UpdateRelationSummary()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(TxtAddressAs.Text)) parts.Add("称呼：" + TxtAddressAs.Text.Trim());
            if (!string.IsNullOrWhiteSpace(TxtToneOverride.Text)) parts.Add("语气：已覆盖");
            var topics = SplitTags(TxtTabooTopics.Text);
            if (topics.Count > 0) parts.Add("不聊：" + topics.Count + " 个话题");
            PolicyRelationSummary.Text = parts.Count == 0
                ? "关系层：未设置，沿用共享层的语气与称呼。"
                : "关系层： " + string.Join(" · ", parts);
        }

        private void OnPolicyFieldChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressPolicyFill) return;
            UpdateRelationSummary();
            MarkPolicyDirty();
        }

        private void OnPolicyFieldChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressPolicyFill) return;
            UpdateRelationSummary();
            MarkPolicyDirty();
        }

        private void MarkPolicyDirty()
        {
            if (_suppressPolicyFill) return;
            _policyDirty = true;
            PolicySaveState.Text = "未保存";
            PolicySaveState.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        }

        private async void OnRegisterPolicy(object sender, RoutedEventArgs e)
        {
            if (_selectedUid == null || !_policyMap.TryGetValue(_selectedUid, out var row)) return;
            BtnRegisterPolicy.IsEnabled = false;
            try
            {
                var body = PolicyBody(row, new List<string> { "public" }, "off", false, "onAsk",
                    "", "", new List<string>(), "", new List<string>());
                var res = await Api.PostJson(_sup.Url, "/api/self/policy", body, 12);
                if (!res.Ok)
                {
                    ShowFailure("登记对象失败", res, "/api/self/policy");
                    return;
                }
                row.Registered = true;
                row.Scopes = new List<string> { "public" };
                row.OnDuty = "off";
                row.CanReceiveAssets = false;
                row.DiscloseAI = "onAsk";
                row.RefreshSummary();
                RebuildPolicyLists();
                ShowPolicyEditor(row);
                PolicySaveState.Text = "已登记 · 现在可以详细设置";
            }
            finally
            {
                BtnRegisterPolicy.IsEnabled = _policyLoaded;
            }
        }

        private async void OnSavePolicy(object sender, RoutedEventArgs e)
        {
            if (_selectedUid == null || !_policyMap.TryGetValue(_selectedUid, out var row) || !row.Registered) return;
            var scopes = SelectedChips(_policyChips);
            var addressAs = (TxtAddressAs.Text ?? "").Trim();
            var toneOverride = (TxtToneOverride.Text ?? "").Trim();
            var topics = SplitTags(TxtTabooTopics.Text);
            var note = (TxtPolicyNote.Text ?? "").Trim();

            BtnSavePolicy.IsEnabled = false;
            try
            {
                var body = PolicyBody(row, scopes, _policyOnDuty, ChkPolicyAssets.IsChecked == true,
                    _policyDisclose, addressAs, toneOverride, topics, note, row.DenyScopes);
                var res = await Api.PostJson(_sup.Url, "/api/self/policy", body, 12);
                if (!res.Ok)
                {
                    PolicySaveState.Text = "保存失败";
                    PolicySaveState.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                    ShowFailure("保存对象设置失败", res, "/api/self/policy");
                    return;
                }
                row.Scopes = scopes;
                row.OnDuty = _policyOnDuty;
                row.CanReceiveAssets = ChkPolicyAssets.IsChecked == true;
                row.DiscloseAI = _policyDisclose;
                row.AddressAs = addressAs;
                row.ToneOverride = toneOverride;
                row.TabooTopics = topics;
                row.Note = note;
                row.SearchText = BuildSearch(row);
                row.RefreshSummary();
                _policyDirty = false;
                RebuildPolicyLists();
                UpdatePolicySummaryOnly();
                PolicySaveState.Text = "已保存 " + DateTime.Now.ToString("HH:mm");
                PolicySaveState.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            }
            finally
            {
                BtnSavePolicy.IsEnabled = true;
            }
        }

        private static Dictionary<string, object> PolicyBody(PolicyRow row, List<string> scopes, string onDuty,
            bool canReceiveAssets, string disclose, string addressAs, string toneOverride, List<string> topics,
            string note, List<string> denyScopes)
        {
            return new Dictionary<string, object>
            {
                ["uid"] = row.Uid,
                ["name"] = row.Name ?? "",
                ["scopes"] = scopes ?? new List<string>(),
                ["denyScopes"] = denyScopes ?? new List<string>(),
                ["onDuty"] = NormalizeOnDuty(onDuty),
                ["canReceiveAssets"] = canReceiveAssets,
                ["discloseAI"] = NormalizeDisclose(disclose),
                ["note"] = note ?? "",
                // 关系层三项：旧内核不认识会忽略，缺字段按未设置处理。
                ["addressAs"] = addressAs ?? "",
                ["toneOverride"] = toneOverride ?? "",
                ["tabooTopics"] = topics ?? new List<string>(),
            };
        }

        private void UpdatePolicySummaryOnly()
        {
            PolicySummary.Text = "共 " + _policyMap.Count + " 个对象 · "
                + _policyMap.Values.Count(x => x.Registered) + " 个已登记";
        }

        private async void OnDeletePolicy(object sender, RoutedEventArgs e)
        {
            if (_selectedUid == null || !_policyMap.TryGetValue(_selectedUid, out var row)) return;
            if (!AppDialog.Confirm(Window.GetWindow(this),
                "删除「" + row.DisplayName + "」的登记？\n\n删除后回到默认：只能看公开信息，在岗关闭。\n关系层记忆不会被删除。",
                "删除对象登记", danger: true, okText: "删除登记", cancelText: "取消")) return;
            BtnDeletePolicy.IsEnabled = false;
            try
            {
                var res = await Api.Delete(_sup.Url, "/api/self/policy/" + Uri.EscapeDataString(row.Uid), 12);
                if (!res.Ok)
                {
                    ShowFailure("删除对象登记失败", res, "/api/self/policy/" + row.Uid);
                    return;
                }
                row.Registered = false;
                row.Scopes = new List<string>();
                row.OnDuty = "off";
                row.CanReceiveAssets = false;
                row.DiscloseAI = "onAsk";
                row.AddressAs = "";
                row.ToneOverride = "";
                row.TabooTopics = new List<string>();
                row.RefreshSummary();
                RebuildPolicyLists();
                ShowPolicyEditor(row);
            }
            finally
            {
                BtnDeletePolicy.IsEnabled = true;
            }
        }

        // ── 可分享资产 ──────────────────────────────────────────
        private async Task LoadAssetsAsync(List<ApiIssue> issues)
        {
            var res = await Api.Get(_sup.Url, "/api/self/assets", 15);
            if (!res.Ok)
            {
                AddIssue(issues, res, "/api/self/assets");
                AssetEmpty.Text = "暂时读不到资产列表。";
                return;
            }
            var arr = res.Prop("assets");
            if (arr.ValueKind != JsonValueKind.Array) arr = res.Prop("items");
            _assets.Clear();
            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var row = new AssetRow
                    {
                        Id = JsonRead.Str(item, "id"),
                        Title = JsonRead.Str(item, "title"),
                        Scopes = ReadStringArray(JsonRead.Prop(item, "scopes")),
                        Size = JsonRead.Long(item, "size"),
                        Kind = JsonRead.Str(item, "kind"),
                        Mime = JsonRead.Str(item, "mime"),
                        UsableBy = JsonRead.Int(item, "audience", JsonRead.Int(item, "usableBy", 0)),
                    };
                    row.ScopePills = MakePills(row.Scopes);
                    var kind = row.Kind == "image" ? "图片" : "文件";
                    var ext = Path.GetExtension(JsonRead.Str(item, "path"));
                    var meta = kind + (ext.Length > 0 ? " · " + ext.TrimStart('.').ToUpperInvariant() : "")
                        + " · " + FormatSize(row.Size);
                    if (row.UsableBy > 0) meta += " · " + row.UsableBy + " 个对象可用";
                    row.MetaLine = meta;
                    var keywords = ReadStringArray(JsonRead.Prop(item, "keywords"));
                    row.KeywordsLine = keywords.Count > 0 ? "关键词：" + string.Join("、", keywords) : "";
                    _assets.Add(row);
                }
            }
            RebuildAssetList();
        }

        private void RebuildAssetList()
        {
            AssetList.ItemsSource = null;
            AssetList.ItemsSource = _assets;
            AssetCount.Text = _assets.Count + " / " + AssetLimitCount;
            AssetEmpty.Visibility = _assets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AssetEmpty.Text = "还没有可分享文件。点「导入文件」选一个。";
            AssetLimit.Text = "单文件不超过 20 MiB，最多 " + AssetLimitCount + " 个。";
            UpdateTabSummaries();
        }

        private void OnImportAsset(object sender, RoutedEventArgs e)
        {
            var path = AppFilePicker.Open("导入可分享资产", "");
            if (string.IsNullOrWhiteSpace(path)) return;
            string size;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    ShowAssetImportError("这个文件不见了，重新选一次。");
                    return;
                }
                if (info.Length > AssetLimitBytes)
                {
                    ShowAssetImportError("这个文件 " + FormatSize(info.Length) + "，超过单文件 20 MiB 上限。");
                    return;
                }
                size = FormatSize(info.Length);
            }
            catch (Exception ex)
            {
                ShowAssetImportError("读不到这个文件：" + ex.Message);
                return;
            }

            _importPath = path;
            _assetImportOpen = true;
            AssetImportFile.Text = Path.GetFileName(path) + " · " + size;
            TxtAssetTitle.Text = Path.GetFileNameWithoutExtension(path);
            TxtAssetKeywords.Text = Path.GetFileNameWithoutExtension(path);
            TxtAssetNote.Text = "";
            SetChipSelection(_assetEditorChips, new List<string>());
            AssetImportError.Visibility = Visibility.Collapsed;
            AssetImportPanel.Visibility = Visibility.Visible;
            AssetImportPanel.BringIntoView();
            Dispatcher.BeginInvoke(new Action(() => TxtAssetTitle.Focus()), DispatcherPriority.Input);
        }

        private void OnCancelAsset(object sender, RoutedEventArgs e)
        {
            _assetImportOpen = false;
            _importPath = null;
            AssetImportPanel.Visibility = Visibility.Collapsed;
            BtnImportAssets.Focus();
        }

        private void ShowAssetImportError(string message)
        {
            AssetImportPanel.Visibility = Visibility.Visible;
            AssetImportError.Text = message;
            AssetImportError.Visibility = Visibility.Visible;
        }

        private async void OnSaveAsset(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_importPath))
            {
                ShowAssetImportError("先选一个文件。");
                return;
            }
            var title = (TxtAssetTitle.Text ?? "").Trim();
            var scopes = SelectedChips(_assetEditorChips);
            if (title.Length == 0)
            {
                ShowAssetImportError("标题不能为空。");
                return;
            }
            if (scopes.Count == 0)
            {
                ShowAssetImportError("至少选一个域，否则这个文件不会发给任何人。");
                return;
            }
            BtnSaveAsset.IsEnabled = false;
            try
            {
                var body = new Dictionary<string, object>
                {
                    ["sourcePath"] = _importPath,
                    ["title"] = title,
                    ["scopes"] = scopes,
                    ["keywords"] = SplitTags(TxtAssetKeywords.Text),
                    ["note"] = (TxtAssetNote.Text ?? "").Trim(),
                };
                var res = await Api.PostJson(_sup.Url, "/api/self/assets", body, 60);
                if (!res.Ok)
                {
                    ShowAssetImportError("导入失败：" + Friendly(res));
                    ShowFailure("导入资产失败", res, "/api/self/assets");
                    return;
                }
                _assetImportOpen = false;
                _importPath = null;
                AssetImportPanel.Visibility = Visibility.Collapsed;
                var issues = new List<ApiIssue>();
                await LoadAssetsAsync(issues);
                ShowIssues(issues);
                BtnImportAssets.Focus();
            }
            finally
            {
                BtnSaveAsset.IsEnabled = true;
            }
        }

        private async void OnDeleteAsset(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not AssetRow row) return;
            if (!AppDialog.Confirm(Window.GetWindow(this),
                "删除「" + row.Title + "」？\n\n资产文件和清单会一起删除，之后不能再发送。",
                "删除资产", danger: true, okText: "删除", cancelText: "取消")) return;

            var res = await Api.Delete(_sup.Url, "/api/self/assets/" + Uri.EscapeDataString(row.Id), 15);
            if (!res.Ok)
            {
                ShowFailure("删除资产失败", res, "/api/self/assets/" + row.Id);
                return;
            }
            _assets.Remove(row);
            RebuildAssetList();
            AssetLimit.Text = "已删除「" + row.Title + "」。单文件不超过 20 MiB，最多 " + AssetLimitCount + " 个。";
        }

        // ── 在岗 ────────────────────────────────────────────────
        private void BuildDutyDays()
        {
            var order = new[] { 1, 2, 3, 4, 5, 6, 0 };
            foreach (var day in order)
            {
                var chip = new ToggleButton
                {
                    Content = WeekNames[day],
                    Tag = day,
                    Style = (Style)FindResource("ScopeChip"),
                    Width = 34,
                    Padding = new Thickness(0, 3, 0, 3),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                AutomationProperties.SetName(chip, "周" + WeekNames[day]);
                chip.Click += (_, __) =>
                {
                    AutomationProperties.SetItemStatus(chip, chip.IsChecked == true ? "已选中" : "未选中");
                    if (_suppressDuty) return;
                    _dutyDirty = true;
                    DutyError.Visibility = Visibility.Collapsed;
                };
                AutomationProperties.SetItemStatus(chip, "未选中");
                DutyDays.Children.Add(chip);
                _dayChips.Add(chip);
            }
        }

        private async Task LoadDutyAsync(List<ApiIssue> issues, bool silent = false)
        {
            var res = await Api.Get(_sup.Url, "/api/self/on-duty", 15);
            if (!res.Ok)
            {
                // 静默刷新失败时保留上一次的状态，别让页面突然显示「已关闭 / 默认时段」。
                if (silent && _dutyLoaded) return;
                _dutyLoaded = false;
                SetDutyEnabled(false);
                PrefillDutyDefaults();
                AddIssue(issues, res, "/api/self/on-duty");
                return;
            }

            var state = res.Prop("onDuty");
            if (state.ValueKind != JsonValueKind.Object) state = res.Prop("state");
            var root = state.ValueKind == JsonValueKind.Object ? state : res.Root;
            _dutyLoaded = true;
            SetDutyEnabled(true);
            _dutyEnabled = JsonRead.Bool(root, "enabled");
            var hours = JsonRead.Prop(root, "workingHours");
            var start = ClockText(JsonRead.Prop(hours, "start"));
            var end = ClockText(JsonRead.Prop(hours, "end"));
            var days = ReadIntArray(JsonRead.Prop(hours, "days"));
            var topics = ReadStringArray(JsonRead.Prop(root, "sensitiveTopics"));

            _suppressDuty = true;
            try
            {
                TxtDutyStart.Text = start.Length > 0 ? start : "09:00";
                TxtDutyEnd.Text = end.Length > 0 ? end : "19:00";
                if (days.Count == 0) days = new List<int> { 1, 2, 3, 4, 5 };
                foreach (var chip in _dayChips)
                {
                    var day = (int)chip.Tag;
                    chip.IsChecked = days.Contains(day);
                    AutomationProperties.SetItemStatus(chip, chip.IsChecked == true ? "已选中" : "未选中");
                }
                TxtSensitive.Text = string.Join("\n",
                    (topics.Count > 0 ? topics : DefaultSensitive.ToList()));
            }
            finally
            {
                _suppressDuty = false;
            }

            ApplyQueue(root, res.Root);
            RebuildQueueLists();
            UpdateDutySummary();
            _lastDutyRead = DateTime.Now;
            _dutyDirty = false;
            // 静默刷新不盖掉「已交给发送队列」这类刚给用户的结果提示。
            if (!silent) SetPendingHint("这些回复还没发出去。「直接发送」会立即以你的身份发给对方。", bad: false);
        }

        private void PrefillDutyDefaults()
        {
            _dutyEnabled = false;
            _suppressDuty = true;
            try
            {
                TxtDutyStart.Text = "09:00";
                TxtDutyEnd.Text = "19:00";
                foreach (var chip in _dayChips)
                {
                    chip.IsChecked = (int)chip.Tag >= 1 && (int)chip.Tag <= 5;
                    AutomationProperties.SetItemStatus(chip, chip.IsChecked == true ? "已选中" : "未选中");
                }
                TxtSensitive.Text = string.Join("\n", DefaultSensitive);
            }
            finally
            {
                _suppressDuty = false;
            }
            _pending.Clear();
            _handled.Clear();
            RebuildQueueLists();
            UpdateDutySummary();
            _dutyDirty = false;
            SetPendingHint("对端内核还不支持在岗接口，这里先显示默认值。", bad: false);
        }

        private void SetDutyEnabled(bool enabled)
        {
            ChkOnDuty.IsEnabled = enabled;
            BtnSaveDuty.IsEnabled = enabled;
            foreach (var chip in _dayChips) chip.IsEnabled = enabled;
            TxtDutyStart.IsEnabled = enabled;
            TxtDutyEnd.IsEnabled = enabled;
            TxtSensitive.IsEnabled = enabled;
        }

        private void UpdateDutySummary()
        {
            _suppressDuty = true;
            try { ChkOnDuty.IsChecked = _dutyEnabled; }
            finally { _suppressDuty = false; }
            UpdateTabSummaries();

            OnDutyStateTitle.Text = _dutyEnabled ? "在岗已开启" : "在岗已关闭";
            OnDutyStateTitle.SetResourceReference(TextBlock.ForegroundProperty, _dutyEnabled ? "Ok" : "Muted");

            if (!_dutyEnabled)
            {
                OnDutyStateDetail.Text = "开启后才会代回；每个对象仍要单独设为「先给我确认」或「直接发送」。";
                return;
            }
            var days = SelectedDays();
            var start = TxtDutyStart.Text.Trim();
            var end = TxtDutyEnd.Text.Trim();
            var draft = _policyMap.Values.Count(x => x.Registered && x.OnDuty == "draft");
            var auto = _policyMap.Values.Count(x => x.Registered && x.OnDuty == "auto");
            OnDutyStateDetail.Text = FormatDays(days) + " " + start + "–" + end
                + (IsWithinWorkingHours(start, end, days) ? " · 当前在时段内" : " · 当前不在时段内")
                + " · " + auto + " 个直接发送 / " + draft + " 个先确认";
        }

        private void OnToggleOnDuty(object sender, RoutedEventArgs e)
        {
            if (_suppressDuty) return;
            _ = ToggleOnDutyAsync(ChkOnDuty.IsChecked == true);
        }

        private async Task ToggleOnDutyAsync(bool enabled)
        {
            if (!_dutyLoaded)
            {
                _suppressDuty = true;
                try { ChkOnDuty.IsChecked = _dutyEnabled; }
                finally { _suppressDuty = false; }
                return;
            }

            if (enabled && !AppDialog.Confirm(Window.GetWindow(this),
                "开启后，分身会在工作时段内，按每个对象的设置替你回复：\n\n"
                + "· 「先给我确认」：生成后进待确认队列，你点了才发\n"
                + "· 「直接发送」：不经确认就以你的身份发出去\n"
                + "· 涉及钱、合同、密码等敏感话题一律先问你\n"
                + "· 私有信息永远不会发给任何对象\n\n"
                + "可以随时在「分身 → 在岗」关闭。现在开启吗？",
                "开启在岗", okText: "开启在岗", cancelText: "先不开"))
            {
                _suppressDuty = true;
                try { ChkOnDuty.IsChecked = false; }
                finally { _suppressDuty = false; }
                return;
            }

            if (enabled)
            {
                if (!TryReadDuty(out var start, out var end, out var days, out var topics, out var error))
                {
                    DutyError.Text = error;
                    DutyError.Visibility = Visibility.Visible;
                    _suppressDuty = true;
                    try { ChkOnDuty.IsChecked = false; }
                    finally { _suppressDuty = false; }
                    return;
                }
                var body = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["workingHours"] = new Dictionary<string, object> { ["start"] = start, ["end"] = end, ["days"] = days },
                    ["sensitiveTopics"] = topics,
                };
                var res = await Api.PostJson(_sup.Url, "/api/self/on-duty", body, 12);
                if (!res.Ok)
                {
                    _suppressDuty = true;
                    try { ChkOnDuty.IsChecked = false; }
                    finally { _suppressDuty = false; }
                    DutyError.Text = "开启失败：" + Friendly(res);
                    DutyError.Visibility = Visibility.Visible;
                    ShowFailure("开启在岗失败", res, "/api/self/on-duty");
                    return;
                }
                DutyError.Visibility = Visibility.Collapsed;
            }
            else
            {
                var res = await Api.PostJson(_sup.Url, "/api/self/on-duty",
                    new Dictionary<string, object> { ["enabled"] = false }, 12);
                if (!res.Ok)
                {
                    _suppressDuty = true;
                    try { ChkOnDuty.IsChecked = true; }
                    finally { _suppressDuty = false; }
                    DutyError.Text = "关闭失败：" + Friendly(res);
                    DutyError.Visibility = Visibility.Visible;
                    ShowFailure("关闭在岗失败", res, "/api/self/on-duty");
                    return;
                }
            }

            _dutyEnabled = enabled;
            UpdateDutySummary();
            _dutyDirty = false;
            DutySaveState.Text = enabled ? "已开启 " + DateTime.Now.ToString("HH:mm") : "已关闭";
            DutySaveState.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            if (Window.GetWindow(this) is MainWindow main) main.OnDutyStateChanged();
        }

        private async void OnSaveDuty(object sender, RoutedEventArgs e)
        {
            if (!_dutyLoaded) return;
            if (!TryReadDuty(out var start, out var end, out var days, out var topics, out var error))
            {
                DutyError.Text = error;
                DutyError.Visibility = Visibility.Visible;
                return;
            }
            DutyError.Visibility = Visibility.Collapsed;
            BtnSaveDuty.IsEnabled = false;
            try
            {
                var body = new Dictionary<string, object>
                {
                    ["enabled"] = _dutyEnabled,
                    ["workingHours"] = new Dictionary<string, object> { ["start"] = start, ["end"] = end, ["days"] = days },
                    ["sensitiveTopics"] = topics,
                };
                var res = await Api.PostJson(_sup.Url, "/api/self/on-duty", body, 12);
                if (!res.Ok)
                {
                    DutySaveState.Text = "保存失败";
                    DutySaveState.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
                    DutyError.Text = "保存失败：" + Friendly(res);
                    DutyError.Visibility = Visibility.Visible;
                    ShowFailure("保存在岗设置失败", res, "/api/self/on-duty");
                    return;
                }
                DutySaveState.Text = "已保存 " + DateTime.Now.ToString("HH:mm");
                DutySaveState.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
                _dutyDirty = false;
                UpdateDutySummary();
            }
            finally
            {
                BtnSaveDuty.IsEnabled = true;
            }
        }

        private void OnDutyFieldChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressDuty || !_dutyLoaded) return;
            _dutyDirty = true;
            DutyError.Visibility = Visibility.Collapsed;
        }

        private bool TryReadDuty(out string start, out string end, out List<int> days, out List<string> topics, out string error)
        {
            start = end = "";
            days = new List<int>();
            topics = new List<string>();
            error = "";
            if (!TxtDutyStart.TryRead(out start) || start.Length == 0)
            {
                error = "开始时间要填 24 小时制时间，例如 09:00。";
                return false;
            }
            if (!TxtDutyEnd.TryRead(out end) || end.Length == 0)
            {
                error = "结束时间要填 24 小时制时间，例如 19:00。";
                return false;
            }
            days = SelectedDays();
            if (days.Count == 0)
            {
                error = "至少选一天。";
                return false;
            }
            topics = SplitTags(TxtSensitive.Text);
            if (topics.Count == 0)
            {
                error = "至少保留一个敏感话题；全部清空会让分身不再为这些话题停下来。";
                return false;
            }
            return true;
        }

        private List<int> SelectedDays()
            => _dayChips.Where(x => x.IsChecked == true).Select(x => (int)x.Tag).OrderBy(x => x).ToList();

        private void ApplyQueue(JsonElement root, JsonElement fallback)
        {
            _pending.Clear();
            _handled.Clear();
            // 新内核给 items（含全部状态）；旧内核只有 pending，退回旧字段兼容。
            var items = JsonRead.Prop(root, "items");
            if (items.ValueKind != JsonValueKind.Array) items = JsonRead.Prop(root, "pending");
            if (items.ValueKind == JsonValueKind.Object) items = JsonRead.Prop(items, "items");
            if (items.ValueKind != JsonValueKind.Array) items = JsonRead.Prop(fallback, "items");
            if (items.ValueKind != JsonValueKind.Array) items = JsonRead.Prop(fallback, "pending");
            if (items.ValueKind == JsonValueKind.Object) items = JsonRead.Prop(items, "items");
            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var row = ParsePending(item);
                    if (row.IsHandled) _handled.Add(row);
                    else _pending.Add(row);
                }
            }
            // 服务端已经按最新在前排好；本地再兜一次，旧内核的顺序不一定是倒序。
            _pending.Sort((a, b) => b.At.CompareTo(a.At));
            _handled.Sort((a, b) => b.At.CompareTo(a.At));
            if (_handled.Count > HandledCap)
                _handled.RemoveRange(HandledCap, _handled.Count - HandledCap);
            if (_handled.Count <= HandledLimit) _handledExpanded = false;
        }

        /// <summary>发送/丢弃后只重读队列，不动正在编辑的工作时段表单。</summary>
        private async Task<bool> RefreshQueueAsync()
        {
            var res = await Api.Get(_sup.Url, "/api/self/on-duty", 15);
            if (!res.Ok) return false;
            var state = res.Prop("onDuty");
            if (state.ValueKind != JsonValueKind.Object) state = res.Prop("state");
            var root = state.ValueKind == JsonValueKind.Object ? state : res.Root;
            ApplyQueue(root, res.Root);
            RebuildQueueLists();
            _lastDutyRead = DateTime.Now;
            return true;
        }

        private void RebuildQueueLists()
        {
            PendingList.ItemsSource = null;
            PendingList.ItemsSource = _pending;
            PendingCount.Text = _pending.Count == 0 ? "" : "共 " + _pending.Count + " 条";
            PendingEmpty.Visibility = _pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PendingEmpty.Text = _dutyLoaded ? "没有待确认的回复。" : "读取不到待确认队列。";

            // 回执默认只露最近 20 条，历史再多也不把页面拉成一条长墙。
            var shown = _handledExpanded ? _handled.ToList() : _handled.Take(HandledLimit).ToList();
            HandledList.ItemsSource = null;
            HandledList.ItemsSource = shown;
            HandledCount.Text = _handled.Count == 0 ? ""
                : shown.Count < _handled.Count
                    ? "显示 " + shown.Count + " / 共 " + _handled.Count + " 条"
                    : "共 " + _handled.Count + " 条";
            HandledEmpty.Visibility = _handled.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HandledEmpty.Text = _dutyLoaded ? "还没有已处理记录。" : "读取不到发送回执。";
            BtnMoreHandled.Visibility = _handled.Count > HandledLimit ? Visibility.Visible : Visibility.Collapsed;
            BtnMoreHandled.Content = _handledExpanded ? "收起" : "显示更多";
            BtnClearHandled.IsEnabled = _handled.Count > 0 && !_clearingHandled;
        }

        private static PendingRow ParsePending(JsonElement item)
        {
            var row = new PendingRow
            {
                Id = JsonRead.Str(item, "id"),
                Uid = JsonRead.Str(item, "uid"),
                Name = JsonRead.Str(item, "name"),
                Text = JsonRead.Str(item, "text"),
                Reason = JsonRead.Str(item, "reason"),
                At = JsonRead.Long(item, "at"),
                Assets = new List<string>(),
            };
            if (string.IsNullOrWhiteSpace(row.Name)) row.Name = row.Uid;
            row.TimeText = FormatTime(row.At);
            row.ReasonText = ReasonLabel(row.Reason);
            row.EditText = row.Text;
            row.KindText = PendingKind(item);
            ApplyDeliveryState(row, item);
            var assets = JsonRead.Prop(item, "assets");
            if (assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind == JsonValueKind.String) row.Assets.Add(asset.GetString());
                    else if (asset.ValueKind == JsonValueKind.Object)
                    {
                        var title = JsonRead.Str(asset, "title");
                        if (title.Length == 0) title = JsonRead.Str(asset, "name");
                        if (title.Length == 0) title = JsonRead.Str(asset, "id");
                        if (title.Length > 0) row.Assets.Add(title);
                    }
                }
            }
            row.AssetLine = row.Assets.Count > 0 ? "附件：" + string.Join("、", row.Assets) : "";
            row.SourceLine = ReadPendingSources(item);
            return row;
        }

        private static string ReadPendingSources(JsonElement item)
        {
            var raw = JsonRead.Prop(item, "sources");
            if (raw.ValueKind != JsonValueKind.Array) raw = JsonRead.Prop(item, "sourceTags");
            var labels = new List<string>();
            if (raw.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in raw.EnumerateArray())
                {
                    var text = value.ValueKind == JsonValueKind.String ? value.GetString() : "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    labels.Add(text == "l2" || text == "relation" ? "关系" : text == "l1" || text == "shared" ? "共享" : text);
                }
            }
            return labels.Count > 0 ? "依据：" + string.Join(" · ", labels.Distinct()) : "";
        }

        private static string PendingKind(JsonElement item)
        {
            var kind = JsonRead.Str(item, "kind");
            if (kind.Length == 0) kind = JsonRead.Str(item, "chatType");
            if (kind == "group" || kind == "群聊") return "群聊";
            if (kind == "private" || kind == "私聊") return "私聊";
            var key = JsonRead.Str(item, "key");
            var uid = JsonRead.Str(item, "uid");
            if (key.IndexOf(":g:", StringComparison.Ordinal) >= 0
                || uid.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase)) return "群聊";
            return uid.Length > 0 ? "私聊" : "";
        }

        /// <summary>回执状态只认一套词：pending / queued / sent / failed / uncertain。</summary>
        private static void ApplyDeliveryState(PendingRow row, JsonElement item)
        {
            var delivery = JsonRead.Prop(item, "delivery");
            var state = JsonRead.Str(delivery, "state");
            var status = JsonRead.Str(item, "status");
            var sendStatus = JsonRead.Str(item, "sendStatus");
            if (state.Length == 0)
            {
                state = status.Length > 0 && status != "pending" ? status : "";
                // 旧内核没有 delivery；发送队列的 sendStatus 比 pending.json 的 status 更晚更新。
                if (sendStatus.Length > 0 && (state.Length == 0 || state == "queued"))
                    state = MapSendStatus(sendStatus);
            }
            if (state.Length == 0) state = "pending";
            row.Status = state;
            row.IsHandled = state != "pending";

            var error = JsonRead.Str(delivery, "error");
            if (error.Length == 0) error = JsonRead.Str(item, "error");
            var at = JsonRead.Long(delivery, "at");
            if (at <= 0) at = row.At;
            var clock = ClockTime(at);

            if (state == "sent")
            {
                row.ReceiptText = clock.Length > 0 ? "已发送 · " + clock : "已发送";
                row.ReceiptTone = "ok";
            }
            else if (state == "failed")
            {
                row.ReceiptText = error.Length > 0 ? "发送失败：" + error : "发送失败";
                row.ReceiptTone = "bad";
            }
            else if (state == "uncertain")
            {
                row.ReceiptText = "结果未确认";
                row.ReceiptTone = "warn";
            }
            else if (state == "queued")
            {
                row.ReceiptText = clock.Length > 0 ? "等待发送 · " + clock : "等待发送";
                row.ReceiptTone = "dim";
            }
            else if (state == "discarded")
            {
                row.ReceiptText = clock.Length > 0 ? "已丢弃 · " + clock : "已丢弃";
                row.ReceiptTone = "dim";
            }
            else
            {
                row.ReceiptText = clock.Length > 0 ? "已处理 · " + clock : "已处理";
                row.ReceiptTone = "dim";
            }
        }

        private static string MapSendStatus(string status) => status switch
        {
            "done" => "sent",
            "unconfirmed" => "uncertain",
            "sending" => "queued",
            "retrying" => "queued",
            "expired" => "discarded",
            "cancelled" => "discarded",
            _ => status,
        };

        private static string ClockTime(long ms)
        {
            if (ms <= 0) return "";
            try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("HH:mm"); }
            catch { return ""; }
        }

        private void OnSendPending(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PendingRow row) return;
            if (!AppDialog.Confirm(Window.GetWindow(this),
                "这条会直接发给「" + row.Name + "」：\n\n" + row.Text + "\n\n发送后不能撤回。",
                "直接发送", okText: "直接发送", cancelText: "取消")) return;
            _ = SendPendingAsync(row, null);
        }

        private void OnEditPending(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PendingRow row) return;
            row.EditText = row.Text;
            row.EditError = "";
            row.IsEditing = true;
            PendingList.ScrollIntoView(row);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PendingList.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container
                    && FindChild<TextBox>(container) is TextBox box)
                {
                    box.Focus();
                    box.SelectAll();
                }
            }), DispatcherPriority.Loaded);
        }

        private void OnCancelEditPending(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PendingRow row) return;
            row.IsEditing = false;
            row.EditError = "";
        }

        private async void OnSendPendingEdited(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PendingRow row) return;
            var text = (row.EditText ?? "").Trim();
            if (text.Length == 0)
            {
                row.EditError = "内容不能为空。";
                return;
            }
            row.EditError = "";
            await SendPendingAsync(row, text);
        }

        private async Task SendPendingAsync(PendingRow row, string editedText)
        {
            var body = new Dictionary<string, object> { ["action"] = "send" };
            if (!string.IsNullOrWhiteSpace(editedText)) body["text"] = editedText;
            var res = await Api.PostJson(_sup.Url,
                "/api/self/on-duty/pending/" + Uri.EscapeDataString(row.Id), body, 60);
            if (!res.Ok)
            {
                SetPendingHint("发送失败：" + Friendly(res) + "。这条还在队列里。", bad: true);
                ShowFailure("发送待确认回复失败", res, "/api/self/on-duty/pending/" + row.Id);
                return;
            }
            // 不要立刻删行：先和队列对一次状态，条目会带着回执落到「已处理」。
            await RefreshAfterActionAsync(row);
            SetPendingHint("已交给发送队列，结果会显示在下面", bad: false);
        }

        private async void OnDiscardPending(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PendingRow row) return;
            var res = await Api.PostJson(_sup.Url,
                "/api/self/on-duty/pending/" + Uri.EscapeDataString(row.Id),
                new Dictionary<string, object> { ["action"] = "discard" }, 20);
            if (!res.Ok)
            {
                SetPendingHint("丢弃失败：" + Friendly(res) + "。这条还在队列里。", bad: true);
                ShowFailure("丢弃待确认回复失败", res, "/api/self/on-duty/pending/" + row.Id);
                return;
            }
            await RefreshAfterActionAsync(row);
            SetPendingHint("已丢弃「" + row.Name + "」这条回复，没有发送。", bad: false);
        }

        /// <summary>动作成功后重读队列；别的行正在编辑时只摘掉这一条，别把用户的草稿冲掉。</summary>
        private async Task RefreshAfterActionAsync(PendingRow row)
        {
            if (_pending.Any(x => !ReferenceEquals(x, row) && x.IsEditing))
            {
                _pending.Remove(row);
                RebuildQueueLists();
                return;
            }
            // 读不到最新队列时至少把这一条从待确认摘掉，别让用户对着已处理的条目再点一次。
            if (!await RefreshQueueAsync())
            {
                _pending.Remove(row);
                RebuildQueueLists();
            }
        }

        private void OnToggleHandled(object sender, RoutedEventArgs e)
        {
            _handledExpanded = !_handledExpanded;
            RebuildQueueLists();
        }

        private async void OnClearHandled(object sender, RoutedEventArgs e)
        {
            if (_clearingHandled || _handled.Count == 0) return;
            var count = _handled.Count;
            if (!AppDialog.Confirm(Window.GetWindow(this),
                "清除 " + count + " 条已处理记录？\n\n只是把这里的记录收起来，不会撤回或取消任何消息。",
                "清除已处理", okText: "清除", cancelText: "取消")) return;

            _clearingHandled = true;
            BtnClearHandled.Content = "正在清除…";
            RebuildQueueLists();
            var failed = 0;
            var firstError = "";
            try
            {
                foreach (var row in _handled.ToList())
                {
                    var res = await Api.PostJson(_sup.Url,
                        "/api/self/on-duty/pending/" + Uri.EscapeDataString(row.Id),
                        new Dictionary<string, object> { ["action"] = "dismiss" }, 20);
                    // 已经不在了（404/409）说明别人先清掉了，不再算失败。
                    if (!res.Ok && res.StatusCode != 404 && res.StatusCode != 409)
                    {
                        failed++;
                        if (firstError.Length == 0) firstError = Friendly(res);
                        continue;
                    }
                    _handled.Remove(row);
                }
                RebuildQueueLists();
                if (failed > 0)
                    SetPendingHint("有 " + failed + " 条没清掉：" + firstError + "。", bad: true);
                else
                    SetPendingHint("已清除 " + count + " 条已处理记录。", bad: false);
                await RefreshQueueAsync();
            }
            finally
            {
                _clearingHandled = false;
                BtnClearHandled.Content = "清除已处理";
                RebuildQueueLists();
            }
        }

        private void SetPendingHint(string text, bool bad)
        {
            PendingHint.Text = text;
            PendingHint.SetResourceReference(TextBlock.ForegroundProperty, bad ? "Bad" : "Text2");
        }

        // ── 刷新（主窗每 2 秒调一次，这里只低频刷新在岗队列）────────
        public async Task RefreshAsync()
        {
            if (!IsLoaded || _loading) return;
            if ((DateTime.Now - _lastLoadedAt).TotalSeconds < 30) return;
            if ((DateTime.Now - _lastDutyRead).TotalSeconds < 15) return;
            if (_selfDirty || _policyDirty || _dutyDirty || _clearingHandled || _pending.Any(x => x.IsEditing) || _assetImportOpen) return;
            var issues = new List<ApiIssue>();
            try { await LoadDutyAsync(issues, silent: true); }   // 静默刷新；失败不盖掉用户正在看的提示
            catch { /* 主窗的刷新循环会兜底；这里不让页面抛到 UI 线程 */ }
            _lastLoadedAt = DateTime.Now;
        }

        // ── 提示 ────────────────────────────────────────────────
        private void ShowIssues(List<ApiIssue> issues)
        {
            if (issues == null || issues.Count == 0)
            {
                Banner.Visibility = Visibility.Collapsed;
                return;
            }
            BannerText.Text = issues[0].Message;
            BannerDetail.Text = issues[0].Detail ?? "";
            if (issues.Count > 1)
                BannerDetail.Text += (BannerDetail.Text.Length > 0 ? " · " : "") + "另有 " + (issues.Count - 1) + " 项读取失败";
            BannerDetail.Visibility = BannerDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Banner.Visibility = Visibility.Visible;
        }

        private void ShowFailure(string prefix, ApiResult res, string endpoint)
            => ShowIssues(new List<ApiIssue> { IssueOf(res, endpoint, prefix) });

        private static void AddIssue(List<ApiIssue> issues, ApiResult res, string endpoint)
            => issues.Add(IssueOf(res, endpoint, ""));

        private static ApiIssue IssueOf(ApiResult res, string endpoint, string prefix)
        {
            var message = Friendly(res);
            if (!string.IsNullOrEmpty(prefix)) message = prefix + "：" + message;
            var detail = endpoint ?? "";
            if (res != null && res.StatusCode > 0) detail += " · HTTP " + res.StatusCode;
            return new ApiIssue { Message = message, Detail = detail };
        }

        private static string Friendly(ApiResult res)
        {
            if (res == null) return "内核没有响应。";
            if (res.StatusCode == 404)
            {
                // 旧内核没有这个路由时只回 "not found"；具体资源不存在（比如待确认条目已被处理）
                // 会带一句人话，原样给用户，别一律说成「内核版本过旧」。
                var body = (res.Error ?? "").Trim();
                if (body.Length > 0 && !body.Equals("not found", StringComparison.OrdinalIgnoreCase))
                    return body;
                return OutdatedKernel;
            }
            var message = Api.Message(res);
            if (string.IsNullOrWhiteSpace(message)) message = "内核没有返回原因。";
            if (message.Contains("内核没在运行", StringComparison.Ordinal))
                return "内核没在运行，先点顶栏「启动」。";
            if (message.Contains("内核没响应", StringComparison.Ordinal))
                return "内核没响应，等几秒再试。";
            return message;
        }

        // ── 小工具 ──────────────────────────────────────────────
        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static List<string> SplitTags(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Split(new[] { ',', '，', '、', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.CurrentCulture)
                .ToList();
        }

        private static List<string> ReadStringArray(JsonElement element)
        {
            var output = new List<string>();
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        output.Add(item.GetString().Trim());
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                output.AddRange(SplitTags(element.GetString()));
            }
            return output;
        }

        private static List<int> ReadIntArray(JsonElement element)
        {
            var output = new List<int>();
            if (element.ValueKind != JsonValueKind.Array) return output;
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var value)) output.Add(value);
                else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var parsed)) output.Add(parsed);
            }
            return output;
        }

        private static void CopyJsonField(JsonElement source, Dictionary<string, object> target, string name)
        {
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value)
                && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
                target[name] = value.Clone();
        }

        private static List<ScopePill> MakePills(IEnumerable<string> scopes)
        {
            var list = new List<ScopePill>();
            if (scopes == null) return list;
            foreach (var key in scopes)
            {
                var label = ScopeDefs.FirstOrDefault(x => x.Key == key).Label;
                list.Add(new ScopePill { Key = key, Label = string.IsNullOrEmpty(label) ? key : label });
            }
            return list;
        }

        internal static string ScopeLabel(string key)
        {
            var label = ScopeDefs.FirstOrDefault(x => x.Key == key).Label;
            return string.IsNullOrEmpty(label) ? key : label;
        }

        internal static string OnDutyLabel(string value) => NormalizeOnDuty(value) switch
        {
            "draft" => "先给我确认",
            "auto" => "直接发送",
            _ => "在岗关闭",
        };

        private static string NormalizeOnDuty(string value)
            => value == "draft" || value == "auto" ? value : "off";

        private static string NormalizeDisclose(string value)
            => value == "never" || value == "always" ? value : "onAsk";

        private static string ReasonLabel(string reason) => reason switch
        {
            "sensitive" => "涉及敏感话题",
            "draft" => "对象设为先确认",
            "no_evidence" => "没有可用的依据",
            "policy" => "对象策略要求先确认",
            "disclose" => "涉及是否说明分身",
            "error" => "生成失败，需要你看一眼",
            _ => "需要你确认",
        };

        // 内核配置里 workingHours 存的是「从零点起的分钟数」，契约示例写的是 HH:mm。两个都认。
        private static string ClockText(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var minutes))
            {
                minutes = Math.Max(0, Math.Min(24 * 60, minutes));
                return (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":"
                    + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? "";
                return TimeField.TryNormalizeClock(text, out var clock) ? clock : text;
            }
            return "";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 KB";
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return bytes + " B";
        }

        private static string FormatTime(long ms)
        {
            if (ms <= 0) return "";
            try
            {
                var time = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                return time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("MM-dd HH:mm");
            }
            catch
            {
                return "";
            }
        }

        private static string FormatDays(List<int> days)
        {
            if (days == null || days.Count == 0) return "没有工作日";
            if (days.SequenceEqual(new List<int> { 1, 2, 3, 4, 5 })) return "工作日";
            if (days.OrderBy(x => x).SequenceEqual(new List<int> { 0, 6 })) return "周末";
            if (days.Count == 7) return "每天";
            var ordered = days.OrderBy(x => x == 0 ? 7 : x);
            return string.Join("、", ordered.Select(x => "周" + WeekNames[Math.Max(0, Math.Min(6, x))]));
        }

        private static bool IsWithinWorkingHours(string start, string end, List<int> days)
        {
            if (days == null || days.Count == 0) return false;
            var dow = (int)DateTime.Now.DayOfWeek;   // 0 = 周日，和内核一致
            if (!days.Contains(dow)) return false;
            if (!TimeField.TryNormalizeClock(start, out var s) || !TimeField.TryNormalizeClock(end, out var e)) return false;
            var now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            var startMin = int.Parse(s.Substring(0, 2)) * 60 + int.Parse(s.Substring(3, 2));
            var endMin = int.Parse(e.Substring(0, 2)) * 60 + int.Parse(e.Substring(3, 2));
            return startMin <= endMin ? now >= startMin && now < endMin : now >= startMin || now < endMin;
        }

        private static List<string> SelectedChips(IEnumerable<ToggleButton> chips)
            => chips.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToList();

        private static void SetChipSelection(IEnumerable<ToggleButton> chips, IEnumerable<string> selected)
        {
            var set = new HashSet<string>(selected ?? Enumerable.Empty<string>());
            foreach (var chip in chips)
            {
                var active = set.Contains((string)chip.Tag);
                chip.IsChecked = active;
                AutomationProperties.SetItemStatus(chip, active ? "已选中" : "未选中");
            }
        }

        private static void MarkChoice(IEnumerable<Button> buttons, string value)
        {
            foreach (var button in buttons)
            {
                var active = (string)button.Tag == value;
                SegmentedTabs.Mark(button, active);
                AutomationProperties.SetItemStatus(button, active ? "已选中" : "未选中");
            }
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var nested = FindChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}
