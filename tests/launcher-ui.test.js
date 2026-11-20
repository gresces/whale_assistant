'use strict';

/**
 * 启动器 UI 契约回归测试。
 *
 * 这一组不启动 WPF，也不依赖 Windows：只盯住这次重设计里容易再次踩坏的东西：
 *   1. App.xaml 里的主题画刷和 Appearance.cs 的浅色/深色表必须一一对应；
 *   2. 所有 {DynamicResource X} 都必须能在 App.xaml 找到 X，防止改名后局部不跟主题；
 *   3. 主导航只有 6 页；分身夹在聊天和微信之间，设置页单列，诊断改从设置子页进入；
 *   4. 低频项统一进默认收起的高级折叠，页内不再铺满小字说明；
 *   5. 卡片不再给内容挂 DropShadowEffect（会把输入框一起投影成竖线）；
 *   6. 滚动条是自定义圆角滑块；输入框/复选框/页面关掉默认焦点虚线；
 *   7. 启动脚本和发布 exe 都在。
 */

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const launcher = path.join(root, 'launcher');

const read = (rel) => fs.readFileSync(path.join(root, rel), 'utf8').replace(/^\uFEFF/, '');
const readLauncher = (rel) => fs.readFileSync(path.join(launcher, rel), 'utf8').replace(/^\uFEFF/, '');

test('统一提示、确认、输入和文本弹层不创建独立窗口', () => {
  const dialog = readLauncher('AppDialog.cs');
  assert.doesNotMatch(dialog, /new\s+Window\s*\{|\.ShowDialog\s*\(/);
  assert.match(dialog, /root\.Children\.Add\(overlay\)/);
  assert.match(dialog, /root\.Children\.Remove\(overlay\)/);
  assert.match(dialog, /window\.Closed\s*\+=/);
  assert.match(dialog, /new BlurEffect/);
  assert.match(dialog, /SetCurrentValue\(UIElement\.IsEnabledProperty, false\)/);
  assert.match(dialog, /KeyboardNavigationMode\.Cycle/);
  assert.match(dialog, /Keyboard\.FocusedElement == input/);
  assert.match(dialog, /SetName\(close, "关闭弹窗"\)/);
  for (const file of ['Views/SettingsPage.xaml.cs', 'Views/StickerPage.xaml.cs',
    'Views/PersonaPage.xaml.cs', 'Views/DiagPage.xaml.cs',
    'Views/BridgePage.xaml.cs', 'Views/ReminderPage.xaml.cs', 'ColorPickerWindow.cs']) {
    assert.doesNotMatch(readLauncher(file), /ShowDialog\s*\(|new\s+(?:Microsoft\.Win32\.)?(?:OpenFileDialog|SaveFileDialog|Window)\b|:\s*Window\b/, file);
  }
  assert.match(readLauncher('AppFilePicker.cs'), /确认覆盖/);
  const picker = readLauncher('AppFilePicker.cs');
  assert.match(picker, /if \(!save\) name\.Clear\(\)/);
  assert.match(picker, /path\.PreviewKeyDown/);
  assert.match(picker, /Directory\.Exists\(file\)/);
  assert.match(dialog, /DockPanel\.SetDock\(header, Dock\.Top\)/);
  assert.match(dialog, /DockPanel\.SetDock\(row, Dock\.Bottom\)/);
});

const THEME_KEYS = [
  'Bg', 'BgDeep', 'BgNav', 'BgTop', 'CardBg', 'CardBorder', 'Line', 'LineSoft',
  'Text', 'Text2', 'Muted', 'Dim', 'Accent', 'AccentDim', 'AccentSoft',
  'Ok', 'Warn', 'Bad', 'Idle', 'PrimaryBg', 'PrimaryFg', 'PrimaryHover',
  'GhostBg', 'GhostHover', 'Hover', 'FieldBg', 'FieldBorder', 'PopupBg',
  'ChipBg', 'Surface3', 'TopBtnHover', 'ScrollThumb', 'BackdropTint',
];

const appXaml = readLauncher('App.xaml');
const appearance = readLauncher('Appearance.cs');
const resources = new Set([...appXaml.matchAll(/x:Key="([^"]+)"/g)].map((m) => m[1]));

test('人设读取不覆盖编辑、失败或并发保存；普通滑块保护待保存值', () => {
  const persona = readLauncher('Views/PersonaPage.xaml.cs');
  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  assert.match(persona, /field\.TextChanged \+= OnPersonaTextChanged/);
  assert.match(persona, /_savedRevision != _editRevision/);
  assert.match(persona, /revision != _editRevision/);
  assert.match(persona, /if \(_loadInProgress\) return/);
  assert.match(persona, /状态定时器每两秒调用此方法，不能反复回填表单/);
  assert.doesNotMatch(persona, /if \(_locked\) _saveTimer\.Stop\(\)/);
  assert.match(settings, /if \(sliderPending\) return/);
});

test('App.xaml 里定义了全部主题画刷', () => {
  for (const key of THEME_KEYS) {
    assert.ok(resources.has(key), `App.xaml 缺少画刷 ${key}`);
  }
});

test('所有 {DynamicResource X} 都能在 App.xaml 找到，不会出现半明半暗', () => {
  const files = [];
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full);
      else if (entry.name.endsWith('.xaml')) files.push(full);
    }
  };
  walk(launcher);

  const missing = [];
  for (const file of files) {
    const text = fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '');
    for (const match of text.matchAll(/\{DynamicResource\s+([A-Za-z0-9_]+)\}/g)) {
      if (!resources.has(match[1])) missing.push(`${path.relative(root, file)}: ${match[1]}`);
    }
  }
  assert.deepEqual(missing, [], '存在找不到的 DynamicResource 键');
});

function themeBlock(name) {
  const marker = `${name} = new Dictionary`;
  const start = appearance.indexOf(marker);
  assert.ok(start >= 0, `Appearance.cs 缺少 ${name} 主题表`);
  const end = appearance.indexOf('};', start);
  assert.ok(end > start, `${name} 主题表没有正常结束`);
  return appearance.slice(start, end);
}

function themeKeys(block) {
  return new Set([...block.matchAll(/\["([^"]+)"\]\s*=\s*"#[0-9A-Fa-f]{8}"/g)].map((m) => m[1]));
}

test('浅色/深色主题表都覆盖全部颜色键，且都是 ARGB', () => {
  const light = themeBlock('Light');
  const dark = themeBlock('Dark');
  const lightKeys = themeKeys(light);
  const darkKeys = themeKeys(dark);

  for (const key of THEME_KEYS) {
    assert.ok(lightKeys.has(key), `Light 缺少 ${key}`);
    assert.ok(darkKeys.has(key), `Dark 缺少 ${key}`);
  }
  assert.deepEqual([...lightKeys].sort(), [...darkKeys].sort(), '浅色/深色颜色键必须完全一致');
});

test('切换主题是换资源实例，配合 DynamicResource 才会全窗口刷新', () => {
  assert.match(
    appearance,
    /Application\.Current\.Resources\[kv\.Key\]\s*=\s*new SolidColorBrush\(color\)/,
    '主题切换必须替换 Application.Resources，而不是只改旧画刷'
  );
});

test('顶栏有太阳/月亮开关，背景图有独立图层和模糊', () => {
  const main = readLauncher('MainWindow.xaml');
  assert.ok(main.includes('x:Name="BtnTheme"'), '缺少主题按钮');
  assert.ok(main.includes('Click="OnToggleTheme"'), '主题按钮没接事件');
  assert.ok(main.includes('x:Name="BackdropLayer"'), '缺少背景图层');
  assert.ok(main.includes('x:Name="BackdropImage"'), '缺少背景图 Image');
  assert.ok(main.includes('<BlurEffect'), '背景图应该做模糊，避免抢文字');
});

test('官网和赞助入口收进设置关于，链接仍直连公开地址', () => {
  const main = readLauncher('MainWindow.xaml');
  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  assert.ok(!main.includes('BtnWebsite'), '侧栏不再放官网按钮');
  assert.ok(!main.includes('BtnSponsor'), '侧栏不再放赞助按钮');
  assert.match(settings, /private const string OfficialSite = "https:\/\/bot\.zyyc\.cc\/"/, '官网地址应为正式域名');
  assert.match(settings, /private const string SponsorPage = "https:\/\/afdian\.com\/a\/Banan1ce"/, '赞助地址应为团队爱发电主页');
  assert.match(settings, /Content = "官网"/, '设置页关于卡缺少官网入口');
  assert.match(settings, /Content = "赞助"/, '设置页关于卡缺少赞助入口');
  assert.match(settings, /ProcessStartInfo\(url\)\s*\{\s*UseShellExecute\s*=\s*true\s*\}/, '链接应交给系统默认浏览器');
});

test('背景模糊与透明度可调，玻璃面板 + 自动蒙版保证文字可读', () => {
  const main = readLauncher('MainWindow.xaml');
  const code = readLauncher('MainWindow.xaml.cs');
  const settings = readLauncher('Views/SettingsPage.xaml.cs');

  assert.ok(main.includes('x:Name="BackdropBlurEffect"'), '背景模糊效果要能被代码调半径');
  assert.ok(code.includes('BackdropImage.Opacity = Appearance.BackdropOpacity'), '背景图透明度要接到设置');
  assert.ok(code.includes('BackdropBlurEffect.Radius = Appearance.BackdropBlur'), '背景模糊要接到设置');

  for (const member of ['SetBackdropBlur', 'SetBackdropFit', 'SetBackdropOpacity', 'SetSurfaceOpacity', 'BuildPalettes', 'ApplyPalette', 'ApplyTextPalette', 'ResolveTint', 'ApplyTextHalo', 'ApplyGlassSurfaces', 'SetPaletteColor', 'ResetPalette']) {
    assert.ok(appearance.includes(member), 'Appearance.cs 缺少 ' + member);
  }
  assert.match(appearance, /backdropBlur\s*=\s*BackdropBlur/, '模糊度要写进 launcher-ui.json');
  assert.match(appearance, /backdropOpacity\s*=\s*BackdropOpacity/, '透明度要写进 launcher-ui.json');
  assert.match(appearance, /surfaceOpacity\s*=\s*SurfaceOpacity/, '面板不透明度要写进 launcher-ui.json');
  assert.match(appearance, /backdropFit\s*=\s*BackdropFit/, '背景填充要写进 launcher-ui.json');
  assert.match(appearance, /accentColor\s*=\s*AccentOverride/, '主题色要写进 launcher-ui.json');
  // 莫奈取色：从背景图统计色相，而不是写死颜色
  assert.ok(appearance.includes('TryGetMonet') && appearance.includes('Hsv'), '缺少莫奈取色');
  // 卡片从 0 调到上限：默认只留一层淡玻璃，不再盖住背景图
  assert.match(appearance, /\("CardBg", 0\.00, 0\.86\)/, '卡片没有做成可调透明');
  // 文字自带反色描边，面板拉到 0 也能读
  assert.ok(appXaml.includes('x:Key="TextHalo"'), 'App.xaml 缺少文字描边资源');
  assert.match(appXaml, /TargetType="TextBlock"[\s\S]*?Effect" Value="\{DynamicResource TextHalo\}"/, 'TextBlock 没挂描边');

  assert.ok(settings.includes('背景模糊') && settings.includes('背景透明度'), '设置页缺少背景滑块');
  assert.ok(settings.includes('面板不透明度'), '设置页缺少面板不透明度滑块');
  assert.ok(settings.includes('SetBackdropBlur') && settings.includes('SetBackdropOpacity') && settings.includes('SetSurfaceOpacity'), '滑块没接到 Appearance');
});

test('人设工作台：左侧段落编辑 + 右侧最终提示词预览，8 个稳定段落', () => {
  const xaml = readLauncher('Views/PersonaPage.xaml');
  const code = readLauncher('Views/PersonaPage.xaml.cs');
  const between = (start, end) => {
    const a = xaml.indexOf(start);
    const b = xaml.indexOf(end);
    assert.ok(a >= 0 && b > a, `找不到段落边界：${start} → ${end}`);
    return xaml.slice(a, b);
  };

  // 双栏：左 55 / 右 45；窄于阈值收进底部折叠区。
  assert.ok(xaml.includes('x:Name="Workbench"'), '缺少左右分栏容器');
  assert.match(xaml, /<ColumnDefinition Width="11\*" \/>[\s\S]{0,240}<ColumnDefinition Width="9\*" \/>/,
    '左右列应按 55 / 45 分配');
  assert.ok(xaml.includes('x:Name="PreviewPanel"'), '缺少右侧预览面板');
  assert.ok(xaml.includes('x:Name="NarrowPreviewExpander"'), '窄窗口缺少底部可展开预览');
  assert.match(code, /NarrowWindowWidth = 1080/, '窄于 1080 要切换布局');
  assert.match(code, /Workbench\.Children\.Remove\(PreviewPanel\)/, '窄窗口要把预览挂到底部');
  assert.ok(xaml.includes('x:Name="Body"'), '主体滚动区还在');

  // 契约顺序：identity → personality → background → speech → behavior → custom → custom-* → memory
  const order = [...xaml.matchAll(/x:Name="(Sec[A-Za-z]+)"/g)].map((m) => m[1]);
  assert.deepEqual(order, ['SecIdentity', 'SecPersonality', 'SecBackground', 'SecSpeech',
    'SecBehavior', 'SecCustom', 'SecBlocks', 'SecMemory'], '段落顺序必须与契约 v1 一致');

  // 身份：五个字段同一张卡，不再散成多块。
  const identity = between('x:Name="SecIdentity"', 'x:Name="SecPersonality"');
  for (const field of ['TxtName', 'TxtAge', 'TxtJob', 'TxtLook', 'TxtOwner']) {
    assert.ok(identity.includes(`x:Name="${field}"`), '身份卡缺少字段：' + field);
  }
  assert.ok(identity.indexOf('TxtName') < identity.indexOf('TxtOwner'), '名字和称呼应在同一张身份卡');

  // 性格 / 经历：多行文本 + 字数 + 恢复默认。
  const personality = between('x:Name="SecPersonality"', 'x:Name="SecBackground"');
  assert.ok(personality.includes('x:Name="TxtPerson"'));
  assert.ok(personality.includes('x:Name="PersonalityCount"'));
  assert.ok(personality.includes('x:Name="BtnDefaultPersonality"'));
  const background = between('x:Name="SecBackground"', 'x:Name="SecSpeech"');
  assert.ok(background.includes('x:Name="TxtBack"'));
  assert.ok(background.includes('x:Name="BackgroundCount"'));
  assert.ok(background.includes('x:Name="BtnDefaultBackground"'));

  // 说话方式：四个紧凑控件 + 一句派生说明。
  const speech = between('x:Name="SecSpeech"', 'x:Name="SecBehavior"');
  for (const field of ['TxtPrivate', 'TxtGroup', 'TxtBubbles', 'SldEmoji']) {
    assert.ok(speech.includes(`x:Name="${field}"`), '说话方式缺少控件：' + field);
  }
  assert.ok(speech.includes('x:Name="SpeechDerived"'), '缺少派生说明');
  assert.match(code, /私聊最多 .*群聊最多 .*最多 .*条 · 颜文字 /, '派生说明要跟设置同步');

  // 回复行为：四个开关 + 折叠的风格学习，功能原样保留。
  const behavior = between('x:Name="SecBehavior"', 'x:Name="SecCustom"');
  for (const field of ['ChkRoast', 'ChkProactive', 'ChkOpinions', 'ChkNewBehavior']) {
    assert.ok(behavior.includes(`x:Name="${field}"`), '回复行为缺少开关：' + field);
  }
  assert.match(behavior, /Header="对话风格学习"[^>]*IsExpanded="False"|IsExpanded="False"[^>]*Header="对话风格学习"/,
    '风格学习要收进默认收起的折叠小节');
  for (const keep of ['CmbStyleMode', 'ChkStyleUseLlm', 'BtnStyleMemory', 'BtnStyleExport',
    'BtnStyleClear', 'TxtStylePreview', 'StyleState', 'StyleHint']) {
    assert.ok(behavior.includes(`x:Name="${keep}"`), '风格学习缺功能：' + keep);
  }

  // 补充要求 / 自定义段落 / 记忆。
  assert.ok(between('x:Name="SecCustom"', 'x:Name="SecBlocks"').includes('x:Name="TxtPrompt"'));
  const blocks = between('x:Name="SecBlocks"', 'x:Name="SecMemory"');
  assert.ok(blocks.includes('x:Name="CustomBlocksHost"'));
  assert.ok(blocks.includes('x:Name="BtnAddBlock"'));
  assert.match(xaml, /空段落不会进入提示词/, '空段落要有元数据提示');
  assert.match(code, /MaxCustomBlocks = 10/, '自定义段落上限应为 10');
  assert.match(code, /AppDialog\.Confirm[\s\S]{0,260}删除自定义段落/, '删除段落必须确认');
  const memory = between('x:Name="SecMemory"', 'x:Name="NarrowPreviewExpander"');
  assert.match(memory, /Header="身份关系与记忆"/);
  for (const keep of ['TxtMasterSearch', 'TxtAliases', 'TxtTurns', 'TxtGroupTurns', 'BtnResetMemory', 'CmbMemoryChat']) {
    assert.ok(memory.includes(`x:Name="${keep}"`), '记忆卡缺功能：' + keep);
  }

  // 旧散块（各自独立的名字/年龄/性格/外观/补充提示词/字数/颜文字/分条/主动行为）不再出现。
  for (const legacy of ['SecPreset', 'SecExpression', 'SecRelation', 'Text="内在性格"',
    'Header="外观与经历"', 'Header="自定义补充提示词"', 'Text="表达偏好"', 'Text="人设与预设"']) {
    assert.ok(!xaml.includes(legacy), '仍在渲染旧散块：' + legacy);
  }
});

test('提示词画布：自动保存、预览随保存刷新、404 降级不炸整页', () => {
  const xaml = readLauncher('Views/PersonaPage.xaml');
  const code = readLauncher('Views/PersonaPage.xaml.cs');

  // 右栏：标题 + 私聊/群聊 + token + 复制 + 刷新状态 + 只读正文。
  assert.match(xaml, /模型收到的提示词/);
  for (const name of ['BtnPreviewPrivate', 'BtnPreviewGroup', 'PreviewTokens', 'BtnCopyPrompt',
    'BtnRefreshPrompt', 'PreviewStatus', 'PreviewText', 'PreviewPlaceholder']) {
    assert.ok(xaml.includes(`x:Name="${name}"`), '预览区缺少控件：' + name);
  }
  assert.ok(xaml.includes('Content="私聊"') && xaml.includes('Content="群聊"'), '缺少私聊 / 群聊切换');
  assert.match(xaml, /x:Name="PreviewText"[^>]*IsReadOnly="True"|IsReadOnly="True"[^>]*x:Name="PreviewText"/,
    '预览正文必须只读');
  assert.match(code, /\/api\/persona\/prompt\?chatType=/, '预览必须来自 GET /api/persona/prompt');
  assert.doesNotMatch(code, /buildPrompt|systemParts|拼提示词/, '前端不能自己拼提示词');

  // 文本段：600ms 防抖 + 失焦即保存，走 /api/persona/block。
  assert.match(code, /BlockSaveDelayMs = 600/, '文本段要用 600ms 防抖');
  assert.match(code, /box\.LostFocus \+= async/, '文本段失焦要保存');
  assert.match(code, /\/api\/persona\/block/, '文本段保存要走 block 接口');
  assert.match(code, /ApplyPreview\(res, false\)/, '保存后直接用返回的 system 更新预览');
  assert.match(code, /PreviewTokens\.Text = tokens/, 'token 数要跟保存结果同步');

  // 表单 / 开关 / 数值：沿用 /api/persona，成功后重新拉预览。
  assert.match(code, /SaveConfigurationAsync\("\/api\/persona"/, '表单仍然走 /api/persona');
  const formSave = code.slice(code.indexOf('private async Task<bool> SaveAllAsync'), code.indexOf('private int CountWarnings'));
  assert.match(formSave, /await RefreshPreviewAsync\(\)/, '表单保存成功后要重新拉预览');

  // 404 / 离线降级：右栏给可读提示，左栏不被禁用。
  assert.match(code, /当前内核不支持提示词预览，升级后可用/);
  assert.match(code, /StatusCode == 404/);
  assert.match(code, /StatusCode == 0/);
  assert.doesNotMatch(code, /Workbench\.IsEnabled = false;\s*\/\/\s*预览/, '预览失败不能禁用工作台');
  assert.match(code, /FlushBeforePresetAsync|FlushEditsAsync/, '预设操作要先冲刷未保存段落');

  // 自定义段落增删。
  assert.match(code, /SaveConfigurationAsync\("\/api\/persona\/blocks"/, '新增段落要走 /api/persona/blocks');
  assert.match(code, /Api\.Delete\(_sup\.Url, "\/api\/persona\/blocks\//, '删除段落要走 DELETE /api/persona/blocks/:id');
  assert.match(code, /BuildCustomBlocksFromPersona/, '预览不可用时要从 /api/persona 的 customBlocks 兜底');
});


test('实现开关唯一收纳到设置高级组，已有专页能力不再重复', () => {
  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  for (const key of ['envContext', 'relations', 'groupAddressGuard', 'fightQuoteCheck', 'staticSystem', 'memorySnapshot', 'mediaCleanup']) {
    assert.equal(settings.split(`Path = "features.${key}"`).length - 1, 1);
  }
  assert.doesNotMatch(settings, /Path = "features\.stickers"/);
});

test('所有人设可编辑和打包分享，新版开关默认打开，说明默认折叠', () => {
  const xaml = readLauncher('Views/PersonaPage.xaml');
  const code = readLauncher('Views/PersonaPage.xaml.cs');
  const dialog = readLauncher('AppDialog.cs');
  for (const text of ['创建自定义人设', '更新此预设', '重命名', '删除预设']) {
    assert.ok(xaml.includes(text), '预设区缺少按钮：' + text);
  }
  for (const handler of ['OnSavePreset', 'OnUpdatePreset', 'OnRenamePreset', 'OnDeletePreset']) {
    assert.ok(xaml.includes('Click="' + handler + '"'), '按钮没接事件：' + handler);
    assert.ok(code.includes(handler), '代码里没有处理函数：' + handler);
  }
  assert.match(code, /\/api\/persona\/preset\/save/, '另存/复制要走预设保存接口');
  assert.match(code, /\/api\/persona\/preset\/rename/, '改名要走预设重命名接口');
  assert.match(code, /\/api\/persona\/preset\/delete/, '删除要走预设删除接口');
  assert.doesNotMatch(code, /_locked|ApplyPersonaEditState/);
  assert.match(xaml, /x:Name="ChkNewBehavior"[^>]*IsChecked="True"/);
  assert.match(xaml, /小贴士 · 新旧版区别" IsExpanded="False"/);
  assert.match(code, /newBehavior = ChkNewBehavior\.IsChecked == true/);
  assert.match(xaml, /Content="打包分享".*Click="OnExportPersona"/);
  assert.match(xaml, /Content="仅分享提示词".*Click="OnExportPrompt"/);
  assert.match(code, /OnExportPrompt[\s\S]*ExportPersonaAsync\(true\)/);
  assert.match(code, /\/api\/persona\/export/);
  assert.match(code, /PersonaPackage\.Export/);
  assert.match(code, /FlushBeforePresetAsync|FlushEditsAsync/);
  assert.match(dialog, /public static string Prompt\(/, '另存和改名需要输入框');
  assert.doesNotMatch(xaml, /全部保存/, '人设改为热保存后不该再有保存按钮');
});

test('人设预设低频操作收进「预设管理」，默认只留应用与创建', () => {
  const xaml = readLauncher('Views/PersonaPage.xaml');
  const code = readLauncher('Views/PersonaPage.xaml.cs');
  assert.ok(xaml.includes('x:Name="BtnManagePresets"'), '缺少预设管理入口');
  assert.match(xaml, /x:Name="PresetManagePanel"[^>]*Visibility="Collapsed"/, '预设管理面板默认收起');
  assert.match(xaml, /Click="OnTogglePresetManage"/, '预设管理要接展开事件');
  assert.match(code, /private void OnTogglePresetManage/, '缺少预设管理展开逻辑');
  assert.ok(xaml.indexOf('预设管理') < xaml.indexOf('打包分享'), '打包分享应在预设管理面板里');
  const defaultRow = xaml.slice(xaml.indexOf('应用选中预设'), xaml.indexOf('预设管理'));
  assert.ok(defaultRow.includes('创建自定义人设'), '默认行应保留创建自定义人设');
  assert.ok(xaml.includes('x:Name="SaveState"'), '已保存状态应留在顶部一行');
  for (const text of ['从文件导入…', '打包分享', '仅分享提示词', '更新此预设', '重命名', '删除预设']) {
    assert.ok(xaml.slice(xaml.indexOf('预设管理')).includes(text), '预设管理缺少：' + text);
  }
});

test('人设支持从本地 ZIP 导入，不再有市场来源', () => {
  const xaml = readLauncher('Views/PersonaPage.xaml');
  const code = readLauncher('Views/PersonaPage.xaml.cs');
  const pkg = readLauncher('PersonaPackage.cs');
  assert.match(xaml, /Content="从文件导入…"[^>]*Click="OnImportPersonaFile"/, '预设管理缺少本地导入入口');
  assert.match(code, /OnImportPersonaFile[\s\S]*AppFilePicker\.Open\("选择人设包", "\.zip"\)/, '导入要选 .zip 人设包');
  assert.match(code, /PersonaPackage\.Import\(file, _sup\.DataDir\)/, '导入要写进 data/personas');
  assert.match(pkg, /internal static ImportedPersona Import\(string file, string dataDir\)/);
  assert.match(pkg, /Path\.Combine\(dataDir, "personas"\)/);
  assert.match(pkg, /"user-" \+ Guid\.NewGuid/, '本地导入要写 user-*.json');
  assert.match(pkg, /GetEntry\("persona\.json"\)/, '导入读 persona.json');
  assert.match(pkg, /GetEntry\("plugin\.json"\)/, '旧包兼容读取 plugin.json');
  assert.match(pkg, /IsSymlink/, '导入要拒绝符号链接');
  assert.doesNotMatch(code, /market|Market|OnResetMarket|BtnResetMarket/, '人设页不应再有市场来源分支');
  assert.doesNotMatch(pkg, /PluginMarket|PluginInfo|PluginTrust/, '人设包不应依赖插件市场');
});

test('人设包保留自定义段落，空数组也不会被省略', () => {
  const pkg = readLauncher('PersonaPackage.cs');
  assert.match(pkg, /TryGetProperty\("customBlocks"/, '导入/导出要识别 customBlocks');
  assert.match(pkg, /result\["customBlocks"\] = blocks/, '空数组也要写回，否则清不掉旧段落');
  assert.match(pkg, /blocks\.Count >= 10/, '最多 10 段');
  assert.match(pkg, /\^custom-\[0-9a-f\]\{8\}\$/, '段落 id 要按内核格式校验');
  assert.match(pkg, /text\.Length > 2000/, '正文超过 2000 字要截断');
  assert.match(pkg, /blocks\.Add/, '坏条目应被丢弃而不是整包失败');
});

test('设置页只保留外观/高级/诊断/关于，单列无侧栏', () => {
  const xaml = readLauncher('Views/SettingsPage.xaml');
  const code = readLauncher('Views/SettingsPage.xaml.cs');

  assert.ok(!xaml.includes('SettingsNav'), '设置页不该再有分类侧栏');
  assert.ok(!xaml.includes('SelectionChanged="OnSettingsNavChanged"'), '侧栏跳转事件应删掉');
  // Fluent 改版后各页统一有 H1 页头；设置页标题与导航项一致，保留标题、不再保留侧栏。
  assert.ok(xaml.includes('Text="设置"'), '设置页应有与导航一致的「设置」页头');
  assert.ok(xaml.includes('外观、高级选项与诊断工具'), '设置页页头应有说明行');
  assert.ok(xaml.includes('x:Name="SectionsHost"'), '缺少单列卡片容器');
  assert.ok(xaml.includes('x:Name="MainScroll"'), '缺少滚动区');
  assert.ok(xaml.includes('x:Name="KernelHint"'), '内核提示行不该丢');
  assert.ok(xaml.includes('x:Name="StatusText"'), '保存状态行不该丢');
  assert.ok(xaml.includes('x:Name="SubPageView"'), '诊断要有设置子页容器');

  // 顺序：外观 → 高级 → 诊断工具 → 关于；模型接入已移到聊天页，不再在设置页建卡。
  const build = code.slice(code.indexOf('private void BuildOnce()'), code.indexOf('private void AddSection'));
  const order = ['BuildAppearanceSection()', 'BuildAdvancedSection()', 'BuildEntriesSection()', 'BuildAboutSection()']
    .map((x) => build.indexOf(x));
  assert.ok(order.every((i) => i >= 0), 'BuildOnce 应该显式建 外观/高级/扩展/关于 四张卡');
  assert.ok(order[0] < order[1] && order[1] < order[2] && order[2] < order[3], '顺序必须是 外观 → 高级 → 扩展 → 关于');
  assert.ok(build.indexOf('BuildModelSection()') < build.indexOf('BuildAppearanceSection()'),
    '模型卡只应出现在 ModelOnly 分支，不能进整页设置');

  // 模型卡仍保留在同一个 SettingsPage 的 ModelOnly 视图里，由聊天页内嵌复用。
  assert.ok(code.includes('ViewMode.ModelOnly') && code.includes('AddSection(BuildModelSection())'),
    '聊天页应复用同一份模型设置');
  const chat = readLauncher('Views/ChatPage.xaml.cs');
  assert.match(chat, /new SettingsPage\(_sup, SettingsPage\.ViewMode\.ModelOnly\)/, '聊天页应内嵌模型设置');

  // 模型卡默认只展示聊天模型；独立模型、语音、搜索和采样都在底部高级折叠。
  const modelSection = code.slice(code.indexOf('private Border BuildModelSection()'), code.indexOf('// ── 读写'));
  assert.match(modelSection, /Header = "高级",\s*IsExpanded = false/, '模型高级折叠默认收起');
  for (const tab of ['看图（视觉）', '语音（识别）', '搜索']) {
    assert.ok(modelSection.includes(tab), '高级折叠缺少页签：' + tab);
  }
  assert.ok(modelSection.includes('"聊天模型"'), '默认聊天模型区缺少标题');
  assert.ok(!modelSection.includes('MakeTab("聊天（文本）"'), '聊天模型不再是页签');
  assert.ok(modelSection.includes('"看图 / 语音 / 搜索默认跟随聊天模型。"'), '缺少跟随说明');
  assert.ok(modelSection.includes('采样'), '采样参数应在高级折叠内');
  assert.ok(modelSection.includes('语音（识别）') && modelSection.includes('voice.model'), '识别参数应留在模型卡');
  assert.ok(!modelSection.includes('Label = "语音回复"'), '识别页不应显示语音回复开关');

  // 高级：一张卡默认收起，含四组
  const advSection = code.slice(code.indexOf('private Border BuildAdvancedSection()'), code.indexOf('// 官网和赞助入口'));
  assert.match(advSection, /Visibility = Visibility\.Collapsed/, '高级分组默认要收起');
  assert.match(advSection, /Header = "高级设置",\s*IsExpanded = false/, '高级设置整体默认收起');
  assert.ok(advSection.includes('MakeTab'), '高级模式要用和模型卡一样的胶囊页签');
  assert.ok(advSection.includes('MarkTab'), '页签选中态要复用同一套样式');
  for (const t of ['群聊与回复', '微信与发送', '联网与检索', '对话与记忆', '高级与其他']) {
    assert.ok(advSection.includes(t) || code.includes('Title = "' + t + '"'), '高级模式缺少分组：' + t);
  }

  // 语音不再单独占一个 Specs 分区
  const specs = code.slice(code.indexOf('private static List<SectionSpec> Specs()'), code.indexOf('private void BuildOnce'));
  // 连续消息收集的两个时间在群聊分组里，保存后由内核热更新
  assert.ok(specs.includes('chat.messageSettleMs'), '缺少连续消息等待设置');
  assert.ok(specs.includes('chat.groupMaxBatchWaitMs'), '缺少群聊最大等待设置');
  const titles = [...specs.matchAll(/Title = "([^"]+)"/g)].map((m) => m[1]);
  assert.ok(titles.length >= 4, `Specs 里应该有多个分组，实际 ${titles.length} 个`);
  assert.ok(!titles.includes('语音'), '语音已并进模型卡，不该再占 Specs 分组');
});

test('设置首屏只留主题与背景入口，外观细节默认收起', () => {
  const code = readLauncher('Views/SettingsPage.xaml.cs');
  const app = code.slice(code.indexOf('private Border BuildAppearanceSection()'), code.indexOf('// 官网和赞助入口'));
  assert.match(app, /Header = "外观细节",\s*IsExpanded = false/, '外观细节应默认收起');
  const beforeDetails = app.slice(0, app.indexOf('外观细节'));
  assert.ok(beforeDetails.includes('跟随系统'), '首屏应保留主题三态');
  assert.ok(beforeDetails.includes('深色'), '首屏应保留深色选项');
  assert.ok(beforeDetails.includes('背景图片'), '首屏应保留背景图片');
  assert.ok(beforeDetails.includes('选择图片'), '首屏应保留选择图片');
  for (const text of ['背景填充', '背景模糊', '背景透明度', '面板不透明度', '莫奈取色', '主题色', '遮罩色', '文字色', '文字描边', '恢复自动配色']) {
    assert.ok(app.slice(app.indexOf('外观细节')).includes(text), '外观细节缺少：' + text);
  }
  assert.match(app, /preview\.Visibility = hasBackdrop \? Visibility\.Visible : Visibility\.Collapsed/, '未选图时不显示预览');
  assert.match(app, /clear\.Visibility = hasBackdrop \? Visibility\.Visible : Visibility\.Collapsed/, '未选图时不显示清除按钮');
});

test('设置高级只有“高级设置 → 高级与其他”两级，不重复', () => {
  const code = readLauncher('Views/SettingsPage.xaml.cs');
  const adv = code.slice(code.indexOf('private Border BuildAdvancedSection()'), code.indexOf('// 官网和赞助入口'));
  assert.doesNotMatch(adv, /Card\("高级"/, '高级卡不应再套同名标题');
  assert.match(code, /Title = "高级与其他"/, '最后分组应命名为「高级与其他」');
  assert.doesNotMatch(code, /Title = "高级"/, '分组不应再叫「高级」');
  assert.match(code, /if \(spec\.Title == "高级与其他"\) panel\.Children\.Add\(BuildMemoryImportBlock\(\)\)/,
    '旧版记忆入口应在「高级与其他」分组');
});

test('对比度与键盘焦点：输入框边框、复选框、分段页签、账号卡、滑块', () => {
  const light = themeBlock('Light');
  const dark = themeBlock('Dark');
  assert.match(light, /\["FieldBorder"\] = "#FF757575"/, '浅色输入框边框应达到 3:1');
  assert.match(dark, /\["FieldBorder"\] = "#FF9A9A9A"/, '深色输入框边框应达到 3:1');
  assert.match(appXaml, /<Trigger Property="IsChecked" Value="True">[\s\S]{0,300}Background" Value="\{DynamicResource PrimaryBg\}"/,
    '复选框勾选应使用品牌填充色');
  assert.match(appXaml, /<Trigger Property="IsChecked" Value="True">[\s\S]{0,300}BorderBrush" Value="\{DynamicResource PrimaryBg\}"/,
    '复选框勾选边框应使用品牌填充色');
  assert.doesNotMatch(readLauncher('SegmentedTabs.cs'), /FocusVisualStyle = null/, '分段页签不能清除焦点');
  assert.match(readLauncher('Views/BridgePage.xaml'), /x:Key="AccountPick"[\s\S]{0,300}FocusVisualStyle" Value="\{StaticResource FluentFocusVisual\}"/,
    '账号卡要有 Fluent 焦点框');
  assert.match(readLauncher('Views/SettingsPage.xaml'), /x:Key="BudgetSlider"[\s\S]{0,300}FocusVisualStyle" Value="\{StaticResource FluentFocusVisual\}"/,
    '滑块要有 Fluent 焦点框');
  assert.match(readLauncher('Views/SettingsPage.xaml.cs'), /_subPageReturnTarget[\s\S]{0,400}target\.Focus\(\)/,
    '设置子页返回后焦点应回到入口');
});

test('指路文案使用新的页面路径', () => {
  assert.ok(readLauncher('Views/DiagPage.xaml.cs').includes('微信 → 高级与日志'));
  assert.ok(readLauncher('RuntimeSupervisor.cs').includes('微信 → 高级与日志'));
  assert.ok(readLauncher('MainWindow.xaml.cs').includes('设置 → 高级设置 → 其他 → 旧版记忆（3.x）'));
});

test('导航只有 6 页；分身夹在聊天与微信之间，诊断和语音发送不占主导航', () => {
  const main = readLauncher('MainWindow.xaml.cs');
  const nav = main.slice(main.indexOf('private static readonly NavEntry[] NavKeys'), main.indexOf('};', main.indexOf('private static readonly NavEntry[] NavKeys')));
  const labels = [...nav.matchAll(/Label = "([^"]+)"/g)].map((m) => m[1]);
  assert.deepEqual(labels, ['状态', '聊天', '分身', '微信', '功能', '设置'], '主导航必须正好是 6 页');
  assert.match(main, /case "聊天": return new ChatPage/, '聊天页要接上宿主页');
  assert.match(main, /case "分身": return new SelfPage/, '分身页要接上宿主页');
  assert.match(main, /case "微信": return new BridgePage/, '微信页要接上桥接页');
  assert.match(main, /case "功能": return new FunctionPage/, '功能页要接上宿主页');
  assert.match(main, /case "诊断": return new DiagPage/, '诊断仍要能从设置子页打开');
  assert.doesNotMatch(main, /PluginMarket|插件市场/, '启动器不应再有插件市场页面');

  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  assert.match(settings, /OpenSubPage\("诊断", "诊断"\)/, '设置页缺少诊断入口');
  assert.doesNotMatch(settings, /PluginMarket|插件市场/, '设置页不应再有插件市场入口');
  assert.match(settings, /Card\("诊断工具"/, '设置扩展卡应精简为诊断工具');

  for (const rel of ['Views/PluginMarketPage.xaml', 'Views/PluginMarketPage.xaml.cs',
    'Views/PluginToolPage.xaml', 'Views/PluginToolPage.xaml.cs',
    'PluginMarket.cs', 'PluginTrust.cs', 'PluginHostActions.cs']) {
    assert.ok(!fs.existsSync(path.join(launcher, rel)), '插件页面/宿主应已删除：' + rel);
  }
  assert.ok(!fs.existsSync(path.join(root, 'tests', 'plugin-market-ui.test.js')), '插件市场测试应删除');

  const top = readLauncher('MainWindow.xaml');
  assert.ok(!top.includes('BtnRestart'), '重启不再占顶栏');
  assert.ok(!top.includes('BtnUpdate'), '检查更新不再占顶栏');
  // 在岗胶囊只在开启时出现：默认 Collapsed，不能常驻一枚灰胶囊。
  assert.match(top, /x:Name="ChipOnDuty"[\s\S]{0,160}Visibility="Collapsed"/, '在岗胶囊默认应隐藏');
  assert.match(top, /x:Name="ChipOnDuty"[\s\S]{0,200}Style="\{StaticResource Chip\}"/, '在岗胶囊要复用顶栏胶囊样式');
  assert.match(readLauncher('MainWindow.xaml.cs'), /RefreshOnDutyChipAsync/, '主窗要负责轮询在岗状态');
  assert.match(readLauncher('Views/StatusPage.xaml'), /x:Name="BtnRestart"/, '重启要收进状态页');
});

test('分身页：五个分段、共享层 / 关系层都有关键控件', () => {
  const xaml = readLauncher('Views/SelfPage.xaml');
  const code = readLauncher('Views/SelfPage.xaml.cs');

  // 分段用现有 SegmentedTabs，且「在岗」不能丢。
  for (const label of ['自我档案', '信息与权限', '对象权限', '可分享资产', '在岗']) {
    assert.ok(code.includes('"' + label + '"'), '分身页缺少分段：' + label);
  }
  assert.match(code, /SegmentedTabs\.Make\(label/, '分段要复用现有 SegmentedTabs');

  // 自我档案
  for (const name of ['TxtSelfName', 'TxtSelfAliases', 'TxtAge', 'TxtSelfCity', 'TxtSelfJob',
    'TxtSelfCompany', 'TxtSelfTone', 'TxtSelfSentence', 'TxtSelfCatchphrases', 'TxtSelfEmoji', 'TxtSelfTaboo']) {
    assert.ok(xaml.includes('x:Name="' + name + '"'), '自我档案缺少字段：' + name);
  }
  assert.ok(xaml.includes('Header="口头禅、表情与禁忌词（高级）"'), '低频语气项应折叠');

  // 信息与权限：知识列表 + 多选胶囊 + 来源标签
  for (const name of ['KnowledgeList', 'KnowledgeScopes', 'TxtKnowledgeTitle', 'TxtKnowledgeText',
    'KnowledgeEditor', 'KnowledgeCount', 'KnowledgeCounter']) {
    assert.ok(xaml.includes('x:Name="' + name + '"'), '知识区缺少控件：' + name);
  }
  assert.ok(xaml.includes('不会发给任何对象'), '私有域必须说明不会发给任何对象');
  assert.ok(xaml.includes('尚未提升为共享信息，只有这个对象可见'), '来自对象的条目要标注未提升');
  assert.match(code, /SourceLabel = "手动录入"/, '手动录入要有来源标签');
  assert.match(code, /SourceLabel = .*来自 /, '来自对象的条目要显示来源');
  assert.doesNotMatch(code, /Promote|提升为共享|OnPromote/, '本阶段不做提升按钮');

  // 对象权限：已登记 / 未登记两组 + 关系层三项
  for (const name of ['RegisteredList', 'UnregisteredList', 'PolicyScopes', 'PolicyOnDutyPanel',
    'PolicyDisclosePanel', 'ChkPolicyAssets', 'TxtAddressAs', 'TxtToneOverride', 'TxtTabooTopics',
    'PolicyRelationExpander', 'BtnRegisterPolicy']) {
    assert.ok(xaml.includes('x:Name="' + name + '"'), '对象权限缺少控件：' + name);
  }
  assert.ok(xaml.includes('默认仅公开，在岗关闭'), '未登记对象要显示默认策略');
  assert.ok(xaml.includes('Header="关系层设置（只对这个对象生效）"'), '关系层设置要单独折叠');
  assert.ok(xaml.includes('IsExpanded="False"'), '低频折叠默认收起');
  for (const field of ['addressAs', 'toneOverride', 'tabooTopics']) {
    assert.ok(code.includes('["' + field + '"]'), '对象策略保存要带 ' + field);
  }

  // 可分享资产
  for (const name of ['AssetList', 'AssetScopes', 'BtnImportAssets', 'AssetImportPanel',
    'TxtAssetTitle', 'TxtAssetKeywords']) {
    assert.ok(xaml.includes('x:Name="' + name + '"'), '资产区缺少控件：' + name);
  }
  assert.ok(xaml.includes('20 MiB'), '资产区要显示单文件上限');

  // 在岗
  for (const name of ['ChkOnDuty', 'DutyDays', 'TxtDutyStart', 'TxtDutyEnd', 'TxtSensitive', 'PendingList']) {
    assert.ok(xaml.includes('x:Name="' + name + '"'), '在岗区缺少控件：' + name);
  }
  assert.match(code, /DefaultSensitive/, '敏感话题要有契约里的默认值');
  for (const word of ['转账', '合同', '签字', '密码', '验证码', '身份证', '医疗', '法律', '借钱']) {
    assert.ok(code.includes('"' + word + '"'), '默认敏感话题缺：' + word);
  }

  // 三处必须确认：删除资产、开启在岗、直接发送待确认条目。
  const confirmBlocks = [...code.matchAll(/AppDialog\.Confirm\([\s\S]{0,420}?\)\)/g)].map((m) => m[0]);
  assert.ok(confirmBlocks.some((block) => block.includes('删除「') && block.includes('删除资产')), '删除资产必须确认');
  assert.ok(confirmBlocks.some((block) => block.includes('开启后，分身会') && block.includes('开启在岗')), '开启在岗必须风险确认');
  assert.ok(confirmBlocks.some((block) => block.includes('这条会直接发给') && block.includes('发送后不能撤回')), '直接发送待确认消息必须确认');

  // 失败降级：404 不能崩，给可读提示。
  assert.ok(code.includes('内核版本过旧，请先启动内核'), '404 要有旧内核提示');
  assert.match(code, /StatusCode == 404/, '要识别 404');
  assert.match(code, /Api\.Message\(res\)|Friendly\(res\)/, '失败提示要复用 Api.Message');
  assert.doesNotMatch(code, /MessageBox\.Show/, '不能退回系统弹窗');
  assert.doesNotMatch(xaml + code, /PluginMarket|插件市场/, '分身页不应出现插件市场');

  // 键盘焦点：胶囊、对象行、待确认行都不能只靠颜色。
  assert.match(xaml, /x:Key="ScopeChip"[\s\S]*?IsKeyboardFocused/, '域胶囊要有可见键盘焦点');
  assert.match(xaml, /x:Key="RowItem"[\s\S]*?IsKeyboardFocusWithin/, '对象行/待确认行要有可见键盘焦点');
});

test('在岗队列分两组：待确认与已处理，回执文字 + 颜色双编码，可清除已处理', () => {
  const xaml = readLauncher('Views/SelfPage.xaml');
  const code = readLauncher('Views/SelfPage.xaml.cs');

  // 两组标题、两条独立列表；已处理默认最多 20 条（服务端最多给 50）。
  assert.match(xaml, /Text="待确认"/, '缺少「待确认」组标题');
  assert.match(xaml, /Text="已处理"/, '缺少「已处理」组标题');
  for (const name of ['PendingList', 'HandledList', 'HandledCount', 'HandledEmpty', 'BtnClearHandled']) {
    assert.ok(xaml.includes(`x:Name="${name}"`), '在岗区缺少控件：' + name);
  }
  assert.match(code, /HandledLimit = 20/, '已处理默认只显示 20 条');
  assert.match(code, /_handled\.Take\(HandledLimit\)/, '默认展示要真的截到 20 条');
  assert.match(code, /if \(row\.IsHandled\) _handled\.Add\(row\);/, '非 pending 状态要进「已处理」');

  // 回执文案：成功带时间、失败带原因、未确认用黄字；文字本身说明结果，颜色只做辅助。
  assert.match(code, /"已发送 · " \+ clock/, '成功回执要带发送时间');
  assert.match(code, /"发送失败：" \+ error/, '失败回执要带原因');
  assert.ok(code.includes('"结果未确认"'), '未确认回执缺少文案');
  assert.match(xaml, /Binding="\{Binding ReceiptTone\}" Value="ok"[\s\S]{0,120}DynamicResource Ok/, '成功回执要用绿色');
  assert.match(xaml, /Binding="\{Binding ReceiptTone\}" Value="bad"[\s\S]{0,120}DynamicResource Bad/, '失败回执要用红色');
  assert.match(xaml, /Binding="\{Binding ReceiptTone\}" Value="warn"[\s\S]{0,120}DynamicResource Warn/, '未确认回执要用黄色');
  assert.match(xaml, /Text="\{Binding KindText\}"/, '两组都要保留群聊/私聊标签');
  assert.match(xaml, /Text="\{Binding Text\}"/, '两组都要保留正文');
  assert.match(xaml, /Text="\{Binding AssetLine\}"/, '两组都要保留附件行');

  // 发送成功不立刻删行：先刷新队列，再提示结果会显示在下面。
  assert.match(code, /await RefreshAfterActionAsync\(row\);[\s\S]{0,120}已交给发送队列，结果会显示在下面/,
    '发送成功后要刷新列表并提示结果在「已处理」');
  assert.doesNotMatch(code, /已交给发送队列 · /, '旧的「最近动态」提示应替换');

  // 清除已处理：确认后逐条 dismiss，不新造端点。
  assert.match(xaml, /Content="清除已处理"/, '缺少「清除已处理」按钮');
  const clear = code.slice(code.indexOf('private async void OnClearHandled'), code.indexOf('private void SetPendingHint'));
  assert.match(clear, /AppDialog\.Confirm/, '清除已处理必须确认');
  assert.match(clear, /\["action"\] = "dismiss"/, '清除要走 dismiss');
  assert.match(clear, /\/api\/self\/on-duty\/pending\//, '清除要复用待确认条目接口');
  assert.doesNotMatch(clear, /dismiss-all|clear-all/, '不要新造批量端点');
});

test('低频项统一收进“高级”折叠，默认全部收起', () => {
  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  assert.match(settings, /Header = "高级设置",\s*IsExpanded = false/, '设置页高级折叠应默认收起');
  for (const [file, label] of [
    ['Views/StickerPage.xaml', '表情包'],
    ['Views/MomentsPage.xaml', '朋友圈'],
    ['Views/ReminderPage.xaml', '主动提醒'],
    ['Views/BridgePage.xaml', '微信'],
  ]) {
    const xaml = readLauncher(file);
    assert.match(xaml, /<Expander[^>]*Header="高级[^"]*"[^>]*IsExpanded="False"/s,
      label + '缺少默认收起的高级折叠');
  }
  assert.match(readLauncher('Views/VoiceSendPage.cs'), /new Expander\{Header="高级",IsExpanded=false/,
    '语音页高级折叠应默认收起');
  assert.match(readLauncher('Views/PersonaPage.xaml'), /Header="身份关系与记忆"/,
    '人设页身份关系与记忆应折叠');
});

test('Expander 标题跟随主题前景色，深色下不会黑字压黑底', () => {
  const start = appXaml.indexOf('<Style TargetType="Expander">');
  assert.ok(start >= 0, '缺少全局 Expander 样式');
  const block = appXaml.slice(start, appXaml.indexOf('</Style>', start));
  assert.match(block, /Foreground="\{TemplateBinding Foreground\}"/, '标题按钮必须继承 Expander 主题前景色');
  assert.match(block, /TextElement\.Foreground="\{TemplateBinding Foreground\}"/, '标题文字必须显式使用主题前景色');
  assert.doesNotMatch(block, /Foreground="(?:Black|#FF000000)"/, 'Expander 标题不能写死黑色');
});

test('Fluent 2 排版：正文 14/20、元数据 12/16、标题 24/32 与 16/22', () => {
  for (const [style, size, line] of [['H1', '24', '32'], ['H2', '16', '22'], ['Body1', '14', '20'], ['Small', '12', '16']]) {
    const start = appXaml.indexOf('x:Key="' + style + '"');
    assert.ok(start >= 0, 'App.xaml 缺少 ' + style);
    const block = appXaml.slice(start, appXaml.indexOf('</Style>', start));
    assert.match(block, new RegExp('FontSize" Value="' + size + '"'), style + ' 字号应为 ' + size);
    assert.match(block, new RegExp('LineHeight" Value="' + line + '"'), style + ' 行高应为 ' + line);
  }
  // 10–11px 不再当正文；元数据最小 12px。
  for (const file of ['MainWindow.xaml', ...fs.readdirSync(path.join(launcher, 'Views'))
    .filter((f) => f.endsWith('.xaml')).map((f) => 'Views/' + f)]) {
    assert.doesNotMatch(readLauncher(file), /FontSize="(?:10|10\.5|11|11\.5)"/, file + ' 仍有 10–11px 小字');
  }
  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  const card = settings.slice(settings.indexOf('private Border Card('), settings.indexOf('private TextBlock ValueLabel'));
  assert.match(card, /FontSize = 14/, '卡片说明应使用正文 14px，不再用 11px 小字墙');
  assert.match(card, /SetResourceReference\(TextBlock\.ForegroundProperty, "Text2"\)/, '设置卡片副标题必须用 DynamicResource，切主题才会跟着变');
  assert.doesNotMatch(settings, /Foreground = \(Brush\)FindResource\("Text2"\)/, '副标题不能固定旧主题画刷');
});

test('「深度思考」开关在模型卡的聊天页里，默认关，读写 settings.thinking', () => {
  const code = readLauncher('Views/SettingsPage.xaml.cs');
  const chatStart = code.indexOf('// ── 聊天面板');
  const voiceStart = code.indexOf('// ── 语音面板');
  assert.ok(chatStart >= 0 && voiceStart > chatStart, '找不到聊天 / 语音面板分界');
  const chatPanel = code.slice(chatStart, voiceStart);
  assert.ok(chatPanel.includes('"深度思考"'), '聊天页缺少深度思考开关');
  assert.ok(chatPanel.includes('思考 token 计入输出预算'), '要说明变慢 / 变贵');
  // 不能再挂 config 的 deepseek.thinking：它会被启动器保存的 settings.thinking 盖掉
  assert.ok(!code.includes('Path = "deepseek.thinking"'), '不要再用 config 的 deepseek.thinking');
  assert.ok(!code.includes('思考（打开后会减慢说话速度）'), '旧语音页开关要删掉');
  // 读写都走 data/settings.json：保存时带 thinking，加载时回填
  assert.ok(code.includes('["thinking"]'), '保存请求要带 thinking');
  assert.ok(code.includes('TryGetProperty("thinking"'), '加载时要回填 thinking');
  // 默认必须是关：DEFAULT_CONFIG 里 thinking=false
  const config = read('src/infra/config.js');
  assert.match(config, /thinking: false/, '默认值应为 false');
});
test('诊断页内置 UIA 自检与数据维护，不用再开 bat', () => {
  const xaml = readLauncher('Views/DiagPage.xaml');
  const code = readLauncher('Views/DiagPage.xaml.cs');
  for (const t of ['UIA 自检', '备份记忆', '恢复记忆', '重置全部']) {
    assert.ok(xaml.includes(t), '诊断页缺少按钮：' + t);
  }
  assert.ok(code.includes('memory-tool.js'), '备份/恢复应该走 memory-tool.js');
  assert.ok(code.includes('factory-reset.ps1'), '重置全部应该走 factory-reset.ps1');
  assert.match(code, /_sup\.StopAll\(\)/, '恢复记忆前必须先停内核');
  assert.ok(xaml.includes('uia-check.py') || code.includes('uia-check.py'), 'UIA 自检入口');
  assert.equal((code.match(/AppDialog\.Confirm\(/g) || []).length >= 2, true, '重置全部必须两次应用内确认');
  // 底部两张卡同属第 3 行，按顺序堆叠，不能再出现第 4 行/重叠
  assert.equal((xaml.match(/<RowDefinition/g) || []).length, 3, '诊断页应只有 3 行定义');
  assert.doesNotMatch(xaml, /Grid\.Row="3"/, '诊断页不应再引用不存在的第 4 行');
  assert.ok(xaml.indexOf('Text="运行与通道"') < xaml.indexOf('Text="数据维护"'), '运行卡应在数据维护之上');
  // 命令行工具要给启动器吐 JSON 列表
  const tool = readLauncher('../scripts/memory-tool.js');
  assert.match(tool, /--json/, 'memory-tool list 要支持 --json');
});

test('卡片不再把 DropShadowEffect 挂在内容上，避免竖线/糊字', () => {
  const start = appXaml.indexOf('<Style TargetType="Border" x:Key="Card">');
  assert.ok(start >= 0, '找不到 Card 样式');
  const end = appXaml.indexOf('</Style>', start);
  const card = appXaml.slice(start, end);
  assert.doesNotMatch(card, /Effect\s*=/, 'Card 不能挂 Effect，否则会投影里面的控件');
});

test('滚动条是自定义圆角滑块，页面/输入控件关掉默认焦点虚线', () => {
  assert.ok(appXaml.includes('x:Key="ScrollThumbStyle"'), '缺少滚动条滑块样式');
  assert.ok(appXaml.includes('<Track x:Name="PART_Track"'), '滚动条没有自定义 Track');
  assert.match(appXaml, /TargetType="ScrollBar"/);
  assert.match(appXaml, /TargetType="CheckBox"[\s\S]*?FocusVisualStyle" Value="\{x:Null\}"/, 'CheckBox 还留着焦点虚线');
  assert.match(appXaml, /TargetType="TextBox"[\s\S]*?FocusVisualStyle" Value="\{x:Null\}"/, 'TextBox 还留着焦点虚线');
  assert.match(readLauncher('Views/SettingsPage.xaml'), /FocusVisualStyle="\{x:Null\}"/, '设置页 FocusVisualStyle 没关');
});

test('所有文本输入框共用一套 TextBox 模板，聚焦加粗边框不会裁字', () => {
  const boxStart = appXaml.indexOf('<Style TargetType="TextBox">');
  assert.ok(boxStart >= 0, '找不到全局 TextBox 样式');
  const box = appXaml.slice(boxStart, appXaml.indexOf('</Style>', boxStart));
  assert.match(box, /MinHeight" Value="(3[2-9]|[4-9]\d)"/, '输入框最小高度太小，行高会被裁');
  assert.match(box, /Padding" Value="\d+,[4-9]"/, '输入框上下内边距不够，聚焦边框会削字');
  assert.doesNotMatch(box, /PART_ContentHost[^>]*VerticalAlignment="\{TemplateBinding VerticalContentAlignment\}"/,
    'PART_ContentHost 不能再绑 VerticalAlignment，否则内容会被压成一条并裁字');
  const moments = readLauncher('Views/MomentsPage.xaml');
  assert.doesNotMatch(moments, /<TextBox[^>]*Height="\d+"/, '朋友圈输入框还在写死高度，应交给全局样式');
  assert.doesNotMatch(moments, /<TextBox[^>]*VerticalContentAlignment=/, '朋友圈输入框还在单独设垂直对齐');
  const number = readLauncher('NumberField.cs');
  const time = readLauncher('TimeField.cs');
  assert.ok(!number.includes('VerticalContentAlignment ='), 'NumberField 不应再单独改垂直对齐');
  assert.ok(!time.includes('VerticalContentAlignment ='), 'TimeField 不应再单独改垂直对齐');
});

test('时间输入统一走 TimeField：日记用 Clock，搭话用 Ranges，设置页能改全局时区', () => {
  const field = readLauncher('TimeField.cs');
  assert.ok(field.includes('TryNormalizeClock') && field.includes('TryNormalizeRanges'), 'TimeField 缺少统一解析入口');
  assert.match(field, /24:00/, '时间段结束点应该支持 24:00');
  assert.match(field, /Replace\('：', ':'\)/, '24:00 的全角冒号也应规范化');
  const xaml = readLauncher('Views/ReminderPage.xaml');
  assert.match(xaml, /<ui:TimeField[^>]*x:Name="TxtDiaryTime"[^>]*Mode="Clock"/, '日记时间没用 TimeField(Clock)');
  assert.match(xaml, /<ui:TimeField[^>]*x:Name="TxtCareWindows"[^>]*Mode="Ranges"/, '搭话时间段没用 TimeField(Ranges)');
  const code = readLauncher('Views/ReminderPage.xaml.cs');
  assert.ok(code.includes('TxtDiaryTime.TryRead') && code.includes('TxtCareWindows.TryRead'), '保存时应该走 TimeField.TryRead');
  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  assert.ok(settings.includes('Path = "env.timezone"'), '设置页缺少全局时区');
});

test('表情包「打开总目录」在离线/功能关闭时也能落到 data\\stickers，并用 explorer 参数打开', () => {
  const code = readLauncher('Views/StickerPage.xaml.cs');
  assert.match(code, /Path\.Combine\(_sup\.DataDir,\s*"stickers"\)/, '根目录要有本地兜底，不能只依赖内核');
  assert.ok(code.includes('Arguments = "\\"" + target + "\\""'), '目录打开要用 explorer 的 Arguments 字符串');
  assert.ok(code.includes('Arguments = "/select,\\"" + target + "\\""'), '定位单图要用 /select, 参数');
  assert.doesNotMatch(code, /UseShellExecute\s*=\s*true\s*\};\s*psi\.ArgumentList/, 'UseShellExecute=true 不能配 ArgumentList');
});

test('发言范围唯一入口在微信页，主动提醒不重复弹窗，UIA 自检输出强制 UTF-8', () => {
  const bridgeXaml = readLauncher('Views/BridgePage.xaml');
  const bridgeCode = readLauncher('Views/BridgePage.xaml.cs');
  assert.match(bridgeXaml, /Content="发言范围"[\s\S]*OnChatPolicy/);
  assert.match(bridgeCode, /OnChatPolicy[\s\S]*new ChatPolicyWindow/);

  const reminderXaml = readLauncher('Views/ReminderPage.xaml');
  const reminderCode = readLauncher('Views/ReminderPage.xaml.cs');
  assert.doesNotMatch(reminderXaml, /BtnCarePolicy|OnOpenCarePolicy|黑白名单/, '主动提醒不应再有第二个发言范围入口');
  assert.doesNotMatch(reminderCode, /OnOpenCarePolicy|new ChatPolicyWindow/, '主动提醒不应再弹第二个对话框');

  const diag = readLauncher('Views/DiagPage.xaml.cs');
  assert.match(diag, /PYTHONIOENCODING/);
  assert.match(diag, /PYTHONUTF8/);
  const check = fs.readFileSync(path.join(root, 'scripts', 'tools', 'uia-check.py'), 'utf8');
  assert.match(check, /reconfigure\(encoding="utf-8"/);
});

test('发言范围：两段名单、顶部状态摘要、仅白名单开关与即时保存', () => {
  const xaml = readLauncher('Views/ChatPolicyWindow.xaml');
  const code = readLauncher('Views/ChatPolicyWindow.xaml.cs');
  const listXaml = readLauncher('Views/ContactCheckList.xaml');
  const listCode = readLauncher('Views/ContactCheckList.xaml.cs');

  // 两段结构：黑名单在前、白名单在后，用现有的分段页签切换。
  const mutedAt = xaml.indexOf('x:Name="MutedPanel"');
  const whiteAt = xaml.indexOf('x:Name="WhitelistPanel"');
  assert.ok(mutedAt >= 0 && whiteAt > mutedAt, '黑名单段必须在白名单段之前');
  assert.match(code, /SegmentedTabs\.Make\("黑名单"/, '黑名单段要用分段页签');
  assert.match(code, /SegmentedTabs\.Make\("白名单"/, '白名单段要用分段页签');

  // 顶部状态摘要：一句当前生效范围，不铺说明墙。
  assert.ok(xaml.includes('x:Name="SummaryText"'), '缺少状态摘要');
  assert.ok(code.includes('仅白名单 · '), '摘要缺少仅白名单口径');
  assert.ok(code.includes('个会话可发言'), '摘要缺少可发言会话数');
  assert.ok(code.includes('全部会话都不处理'), '黑名单全选时摘要要说全部不处理');
  assert.match(code, /已保存 · /, '顶部要显示已保存时间');

  // 仅白名单模式属于白名单段顶部；关闭时给「仅用于主动搭话」次级标签。
  const switchAt = xaml.indexOf('x:Name="WhitelistOnly"');
  assert.ok(switchAt > whiteAt && switchAt < xaml.indexOf('x:Name="WhitelistHost"'),
    '仅白名单开关要放在白名单段顶部');
  assert.ok(xaml.includes('x:Name="WhitelistTag"') && xaml.includes('仅用于主动搭话'),
    '关闭时要显示「仅用于主动搭话」标签');
  assert.match(code, /WhitelistTag\.Visibility = only \? Visibility\.Collapsed : Visibility\.Visible/,
    '标签要跟开关联动');

  // 影响预览只在黑名单段出现。
  const impactAt = xaml.indexOf('对这些会话：不回复、不解码图片/表情、不主动搭话');
  assert.ok(impactAt > mutedAt && impactAt < whiteAt, '影响预览只属于黑名单段');

  // 已选行：名称 + 私聊/群聊 + 右侧移除；添加会话走现有联系人目录。
  assert.match(listXaml, /Content="添加会话"/, '缺少添加会话入口');
  assert.match(listXaml, /Content="移除"/, '每行缺少移除按钮');
  assert.ok(listXaml.includes('{Binding Kind}'), '每行要显示私聊/群聊标签');
  assert.match(listXaml, /Style="\{StaticResource TopBtn\}"/, '移除按钮要复用现有按钮样式');
  assert.match(listXaml, /FocusVisualStyle="\{StaticResource FluentFocusVisual\}"/, '移除按钮要有可见焦点');
  assert.match(listXaml, /KeyboardNavigation\.TabNavigation="Continue"/, '列表要能用 Tab 遍历');
  assert.ok(listCode.includes('没有黑名单会话。需要彻底忽略某个群时再加。'), '缺少黑名单空状态提示');
  assert.ok(listCode.includes('白名单是空的'), '缺少白名单空状态提示');

  // 超过 8 个才显示筛选；添加支持多选并立刻保存。
  assert.match(listCode, /SearchThreshold = 8/, '筛选阈值应为 8');
  assert.match(listCode, /_chosen\.Count > SearchThreshold/, '超过 8 个才显示筛选框');
  assert.match(listCode, /_pending\.Add\(row\.Wxid\)/, '添加会话要支持多选');
  assert.match(listCode, /if \(_preview\) return;[\s\S]{0,40}var res = await _saver\.SaveAsync\(BuildPatch\)/,
    '添加 / 移除后要立即保存');

  // 失败回滚；没有「保存」大按钮。
  assert.match(listCode, /Revert\(\)/, '保存失败要回滚名单');
  assert.doesNotMatch(xaml, /Content="保存"/, '发言范围不应再有保存大按钮');
  assert.ok(xaml.includes('x:Name="SaveState"'), '保存状态要显示在顶部');

  // 尺寸与滚动：560×560 卡片（正文 512×412），长名单只在列表内部滚动。
  assert.match(xaml, /Width="512"/, '正文宽度应固定为 512');
  assert.match(xaml, /Height="412"/, '正文高度应固定为 412');
  assert.doesNotMatch(xaml, /<ScrollViewer/, '发言范围不应该整窗滚动');
  assert.match(listXaml, /ScrollViewer\.VerticalScrollBarVisibility="Auto"/, '名单要在卡片内部滚动');
  assert.match(readLauncher('Views/BridgePage.xaml.cs'),
    /AppDialog\.ShowContent\("发言范围", new ChatPolicyWindow\(_sup\), 560\)/, '弹层卡片宽度应为 560');

  // Esc 关闭沿用统一弹层；微信页加一行图片识别提示。
  assert.match(readLauncher('AppDialog.cs'), /Key\.Escape/, 'Esc 要关闭弹层');
  assert.ok(readLauncher('Views/BridgePage.xaml')
    .includes('图片识别需要先在微信里点开任意一张图片（4.x 加密）'), '微信页缺少图片识别提示');
});

test('启动器不再使用系统 MessageBox，统一走应用内 AppDialog', () => {
  const files = fs.readdirSync(path.join(root, 'launcher'), { recursive: true })
    .filter((rel) => String(rel).endsWith('.cs'));
  for (const rel of files) {
    const text = fs.readFileSync(path.join(root, 'launcher', String(rel)), 'utf8');
    assert.equal(text.includes('MessageBox.Show'), false, '还在用系统弹窗：launcher/' + rel);
  }
  assert.ok(fs.existsSync(path.join(root, 'launcher', 'AppDialog.cs')));
});

test('新版 exe 都在仓库里', () => {
  for (const rel of ['WhaleLauncher.exe', 'launcher/publish/WhaleLauncher.exe']) {
    const file = path.join(root, rel);
    assert.ok(fs.existsSync(file), `缺少 ${rel}`);
    assert.ok(fs.statSync(file).size > 100000, `${rel} 看起来不是完整 exe`);
  }
});

test('图标接好了：csproj 的 exe 图标 + 窗口图标 + 安装包图标', () => {
  const csproj = readLauncher('WhaleLauncher.csproj');
  assert.match(csproj, /<ApplicationIcon>Assets\\app\.ico<\/ApplicationIcon>/, 'csproj 没有 ApplicationIcon');
  assert.match(csproj, /<Resource Include="Assets\\app\.ico"\s*\/>/, 'app.ico 没当 WPF 资源，窗口 Icon 会找不到');

  const win = readLauncher('MainWindow.xaml').slice(0, 800);
  assert.match(win, /Icon="Assets\/app\.ico"/, 'MainWindow 没挂窗口图标');

  const ico = path.join(launcher, 'Assets', 'app.ico');
  assert.ok(fs.existsSync(ico), '缺少 launcher/Assets/app.ico');
  const head = fs.readFileSync(ico).subarray(0, 4);
  assert.deepEqual([...head], [0x00, 0x00, 0x01, 0x00], 'app.ico 不是 ICO 文件');
  assert.ok(fs.statSync(ico).size > 20000, 'app.ico 太小，多半只有单尺寸');

  const iss = read('installer/whale.iss');
  assert.match(iss, /SetupIconFile=\{#SrcDir\}\\launcher\\Assets\\app\.ico/, '安装包没有 SetupIconFile');
});

test('设置搜索：常驻搜索框、五组字段可搜、命中显示分组/字段/当前值、Esc 退出', () => {
  const xaml = readLauncher('Views/SettingsPage.xaml');
  const code = readLauncher('Views/SettingsPage.xaml.cs');
  assert.match(xaml, /x:Name="SettingsSearch"/, '缺少设置搜索框');
  assert.match(xaml, /TextChanged="OnSearchChanged"/, '搜索框要输入即筛');
  assert.match(xaml, /PreviewKeyDown="OnSearchKeyDown"/, '搜索框要支持 Esc');
  assert.match(xaml, /搜索设置项：主题、预算、链接/, '搜索框要有和现有输入框一致的占位提示');
  assert.match(code, /private void RegisterEntry\(/, '缺少设置项索引');
  for (const group of ['群聊与回复', '微信与发送', '联网与检索', '对话与记忆', '高级与其他']) {
    assert.ok(code.includes('"' + group + '"'), '搜索分组缺少：' + group);
  }
  assert.match(code, /RegisterEntry\("外观"/, '外观项没有进搜索');
  assert.match(code, /RegisterEntry\("诊断工具"/, '诊断入口没有进搜索');
  assert.match(code, /RegisterEntry\("关于"/, '关于项没有进搜索');
  assert.match(code, /Text = entry\.Section/, '命中行要显示分组');
  assert.match(code, /Text = entry\.Label/, '命中行要显示字段名');
  assert.match(code, /SafeSummary\(entry\)/, '命中行要显示当前值摘要');
  assert.match(code, /没有匹配的设置项/, '无结果要给文字提示');
  assert.match(code, /Key\.Escape/, 'Esc 要退出搜索');
  assert.match(code, /HighlightTarget\(/, '点击命中要滚动并高亮');
  assert.match(code, /BeginTime = TimeSpan\.FromMilliseconds\(900\)[\s\S]{0,120}Duration = TimeSpan\.FromMilliseconds\(600\)/, '高亮要先停留再 1.5 秒渐隐');
  assert.match(code, /public void PreviewSearch/, '截图工具要能展示搜索命中');
});

test('设置搜索支持同义词：通知/推送、深色/暗色/主题、代理/网络、语音/识别、记忆/上下文、预算/token', () => {
  const code = readLauncher('Views/SettingsPage.xaml.cs');
  for (const pair of [
    ['通知', '推送'], ['深色', '暗色'], ['代理', '网络'],
    ['语音', '识别'], ['记忆', '上下文'], ['预算', 'token'],
  ]) {
    assert.ok(code.includes('"' + pair[0] + '"') && code.includes('"' + pair[1] + '"'), '同义词缺失：' + pair.join('/'));
  }
  assert.match(code, /SynonymGroups/, '同义词要集中维护');
  assert.match(code, /ExpandSearch\(/, '查询要先展开同义词');
});

test('高级五组标题旁有真实状态摘要，切换后立即刷新', () => {
  const code = readLauncher('Views/SettingsPage.xaml.cs');
  assert.match(code, /_sectionSummaries\.Add\(\(badge,/, '每个分组页签要挂摘要');
  for (const summary of ['@ 才回', '全部回复', '后台严格', '自动发送', '本地搜索 · 开', '预算 ', '默认值', '项已改']) {
    assert.ok(code.includes(summary), '缺少状态摘要文案：' + summary);
  }
  assert.match(code, /SummaryForSection\(sectionTitle\)/, '摘要要按分组计算');
  assert.match(code, /GetConfigBool\("group\.requireAt"/, '群聊摘要要读实际配置');
  assert.match(code, /GetConfigBool\("wechat\.bgSend\.enabled"/, '微信摘要要读实际发送方式');
  assert.match(code, /GetConfigLong\("memory\.dialogueBudget"/, '对话摘要要读实际预算');
  assert.match(code, /RefreshSectionSummaries\(\)/, '配置变化后要刷新摘要');
});

test('主题三态：浅色 / 深色 / 跟随系统，读 AppsUseLightTheme 并实时跟随', () => {
  const appearance = readLauncher('Appearance.cs');
  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  const main = readLauncher('MainWindow.xaml.cs');
  assert.match(appearance, /string Theme \{ get; private set; \} = "system"/, '默认主题应跟随系统');
  assert.match(appearance, /AppsUseLightTheme/, '跟随系统要读 Windows 应用模式');
  assert.match(appearance, /StartSystemThemeWatch/, '缺少系统主题监听');
  assert.match(appearance, /WM_SETTINGCHANGE|WM_THEMECHANGED/, '监听要处理系统主题变化消息');
  assert.match(appearance, /public static string CycleTheme\(\)/, '顶栏按钮要有三态循环');
  assert.match(settings, /SegmentedTabs\.Make\("跟随系统"/, '设置页要有跟随系统选项');
  assert.match(main, /Appearance\.CycleTheme\(\)/, '顶栏主题按钮要保持可用');
  assert.match(main, /主题：.*点击切换/s, '顶栏提示要说明当前模式');
  assert.match(readLauncher('MainWindow.xaml'), /x:Name="IconSystem"/, '跟随系统要有独立图标');
});

test('依赖联动：总开关关闭时子项置灰；诊断入口保持可用', () => {
  const settings = readLauncher('Views/SettingsPage.xaml.cs');
  assert.match(settings, /private void RefreshFieldState\(\)/, '设置页缺少依赖联动');
  assert.match(settings, /GetConfigBool\("group\.requireAt"[\s\S]{0,260}group\.masterAlwaysReply/, '关闭回复全部消息后 @ 例外要置灰');
  assert.match(settings, /links\.timeoutMs[\s\S]{0,80}links/, '链接总开关要控制超时/缓存');
  assert.match(settings, /RegisterEntry\("诊断工具"[\s\S]{0,300}diagnostic/, '诊断入口不能被依赖联动藏起来');

  const sticker = readLauncher('Views/StickerPage.xaml.cs');
  assert.match(sticker, /private void UpdateManageState\(bool enabled\)/, '表情包总开关要联动分类/上传');
  for (const name of ['SldDemand', 'ChkEnsure', 'TxtNewCat', 'BtnAddCat', 'BtnDelCat', 'BtnUpload']) {
    assert.ok(sticker.includes(name + '.IsEnabled = canManage'), '表情包子项没有联动：' + name);
  }

  const moments = readLauncher('Views/MomentsPage.cs');
  assert.match(moments, /void ApplyPublishState\(\)/, '朋友圈总开关要联动发表子项');
  for (const name of ['ChkPublish', 'ChkPublishImage', 'TxtPublishInterval', 'TxtPublishLimit', 'BtnTestPublish', 'ChkAutoLike']) {
    assert.ok(moments.includes(name + '.IsEnabled = on'), '朋友圈子项没有联动：' + name);
  }

  const reminder = readLauncher('Views/ReminderPage.xaml.cs');
  assert.match(reminder, /private void UpdateDependencies\(\)/, '提醒页缺少依赖联动');
  assert.match(reminder, /TxtMaxPerDay\.IsEnabled = rules/, '关闭提醒后每天上限要置灰');
  assert.match(reminder, /TxtCareEvery[\s\S]{0,200}IsEnabled = care/, '关闭主动搭话后间隔/名单/时间段要置灰');
});

test('三处「保存」按钮已移除：提醒日记/规则/搭话，表情包频率，朋友圈发表', () => {
  const reminder = readLauncher('Views/ReminderPage.xaml');
  const sticker = readLauncher('Views/StickerPage.xaml');
  const moments = readLauncher('Views/MomentsPage.xaml');
  for (const name of ['BtnSaveDiary', 'BtnSaveRules', 'BtnSaveCare']) {
    assert.ok(!reminder.includes(name), '提醒页仍有保存按钮：' + name);
  }
  assert.ok(!sticker.includes('BtnSaveDemand'), '表情包仍是手动保存频率');
  assert.ok(!sticker.includes('保存频率'), '表情包不应再有保存频率按钮');
  assert.ok(!moments.includes('BtnSavePublish'), '朋友圈仍有保存按钮');

  assert.match(readLauncher('Views/ReminderPage.xaml.cs'), /DiaryTitleStatus/, '日记标题旁要显示已保存时间');
  assert.match(readLauncher('Views/ReminderPage.xaml.cs'), /RulesTitleStatus/, '提醒规则标题旁要显示已保存时间');
  assert.match(readLauncher('Views/ReminderPage.xaml.cs'), /CareTitleStatus/, '主动搭话标题旁要显示已保存时间');
  assert.match(readLauncher('Views/StickerPage.xaml.cs'), /ScheduleDemandSave/, '频率要拖动结束即保存');
  assert.match(readLauncher('Views/MomentsPage.cs'), /OnPublishFieldLostFocus/, '朋友圈字段失焦要即保存');
});

test('功能页签有状态摘要；分身页签有短状态；微信账号区有登录摘要', () => {
  const fn = readLauncher('Views/FunctionPage.xaml.cs');
  const sticker = readLauncher('Views/StickerPage.xaml.cs');
  const moments = readLauncher('Views/MomentsPage.cs');
  const reminder = readLauncher('Views/ReminderPage.xaml.cs');
  const voice = readLauncher('Views/VoiceSendPage.cs');
  assert.match(fn, /ITabStatus/, '功能页要读子页状态');
  assert.match(fn, /_badges\[key\] = badge/, '页签要显示状态角标');
  assert.match(sticker, /已启用 · " \+ total \+ " 张/, '表情包摘要要显示已启用张数');
  assert.match(moments, /未启用[\s\S]{0,200}每天 /, '朋友圈摘要要显示未启用/每天条数');
  assert.match(reminder, /提醒开[\s\S]{0,120}搭话开/, '主动提醒摘要要显示两个开关');
  assert.match(voice, /实验性功能，未启用/, '语音摘要要显示实验性未启用');

  const self = readLauncher('Views/SelfPage.xaml.cs');
  assert.match(self, /UpdateTabSummaries/, '分身页缺少页签摘要');
  for (const text of ['已填写', '未填写', '" 个对象"', '" 个文件"', '"关闭"', '"自动"', '"确认制"']) {
    assert.ok(self.includes(text), '分身页签摘要缺少：' + text);
  }
  const bridge = readLauncher('Views/BridgePage.xaml.cs');
  assert.match(bridge, /AccountState\.Text = s\.BrOnline && s\.BrAccountOk/, '账号区要显示已登录/未登录摘要');
  assert.match(readLauncher('Views/BridgePage.xaml'), /x:Name="AccountState"/, '账号标题旁缺少状态元素');
});

test('恢复默认后可撤销：保存改前值并在 12 秒内提供撤销入口', () => {
  const code = readLauncher('Views/SettingsPage.xaml.cs');
  assert.match(code, /ShowUndo\(/, '恢复默认后要给撤销提示');
  assert.match(code, /OnUndoReset/, '缺少撤销实现');
  assert.match(code, /_undoReset = before/, '撤销要保留改前的值');
  assert.match(code, /Interval = TimeSpan\.FromSeconds\(12\)/, '撤销入口要限时消失');
  assert.match(readLauncher('Views/SettingsPage.xaml'), /x:Name="UndoBar"/, '设置页缺少撤销条');
});
