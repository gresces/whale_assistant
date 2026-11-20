!!! MUST 这个文件不允许任何agent助手以任何方式修改

# Microsoft Fluent 2 设计系统

## Microsoft Fluent 2 设计系统界面预览

在项目简介和人员之间切换，启用或关闭网站主题，并尝试本地添加无效、重复和成功的内容。不会发送邀请。

## Microsoft Fluent 2 设计系统设计参考

Fluent UI React v9 网站参考。全局数值来自链接的 React 令牌来源。下方颜色值依据 brandWeb 解析浅色别名映射，而非 alias/lightColor.ts 中过时的示例十六进制注释。深色和 Teams 主题使用单独的映射。网站字体排印表目前将 Subtitle1 列为 20/26；React fonts.ts 源文件采用 20/28，本实现遵循该源文件。来源核查日期：2026 年 10 月 3 日；请以 lockfile 中记录的版本为准。

### 浅色主题中性角色

| 名称 / 规则 | 值 / 配置 | 用途 |
| --- | --- | --- |
| colorNeutralForeground1 | #242424 | 主要文档文本。 |
| colorNeutralForeground2 | #424242 | 次级标签。 |
| colorNeutralForeground3 | #616161 | 元数据；仍需检查最终对比度。 |
| colorNeutralBackground1 | #ffffff | 主要界面表面。 |
| colorNeutralBackground2 | #fafafa | 克制的辅助界面表面。 |
| colorNeutralBackground3 | #f5f5f5 | 工作区画布。 |
| colorNeutralStroke1 | #d1d1d1 | 常规组件边界。 |
| colorNeutralStroke2 | #e0e0e0 | 细微分隔线。 |
| colorNeutralStrokeAccessible | #616161 | 对比度更高的控件边界。 |
| colorNeutralForegroundOnBrand | #ffffff | 在受支持的品牌色填充上显示的文本。 |
| colorStrokeFocus1 / colorStrokeFocus2 | #ffffff / #000000 | 两种焦点颜色；确保组件边缘处的对比度。 |
### 从 brandWeb 解析的网站品牌角色

| 名称 / 规则 | 值 / 配置 | 用途 |
| --- | --- | --- |
| colorBrandBackground | #0f6cbd · brandWeb[80] | webLightTheme 中的主要操作。 |
| colorBrandBackgroundHover | #115ea3 · brandWeb[70] | 该操作的悬停状态。 |
| colorBrandBackgroundPressed | #0c3b5e · brandWeb[40] | 按下状态；并非通用的选中状态。 |
| colorBrandBackground2 | #ebf3fc · brandWeb[160] | 浅色品牌背景。 |
| colorBrandForegroundLink | #115ea3 · brandWeb[70] | 浅色网站主题中的链接前景色。 |
### 水平和垂直间距

| 名称 / 规则 | 值 / 配置 | 用途 |
| --- | --- | --- |
| spacingHorizontalNone / spacingVerticalNone | 0 | 根据布局方向选择轴；共用间距步长。 |
| spacingHorizontalXXS / spacingVerticalXXS | 2px | 根据布局方向选择轴；共用间距步长。 |
| spacingHorizontalXS / spacingVerticalXS | 4px | 根据布局方向选择轴；共用间距步长。 |
| spacingHorizontalSNudge / spacingVerticalSNudge | 6px | 根据布局方向选择轴；图标对齐微调。 |
| spacingHorizontalS / spacingVerticalS | 8px | 根据布局方向选择轴；共用间距步长。 |
| spacingHorizontalMNudge / spacingVerticalMNudge | 10px | 根据布局方向选择轴；图标对齐微调。 |
| spacingHorizontalM / spacingVerticalM | 12px | 根据布局方向选择轴；共用间距步长。 |
| spacingHorizontalL / spacingVerticalL | 16px | 根据布局方向选择轴；共用间距步长。 |
| spacingHorizontalXL / spacingVerticalXL | 20px | 根据布局方向选择轴；共用间距步长。 |
| spacingHorizontalXXL / spacingVerticalXXL | 24px | 根据布局方向选择轴；共用间距步长。 |
| spacingHorizontalXXXL / spacingVerticalXXXL | 32px | 根据布局方向选择轴；共用间距步长。 |
### React 网站字体排印组合

| 名称 / 规则 | 值 / 配置 | 用途 |
| --- | --- | --- |
| Caption2 · fontSizeBase100 | 10px / 14px / 400 | 字号 / 行高 / 字重。与 lineHeightBase100 配合使用。 |
| Caption1 · fontSizeBase200 | 12px / 16px / 400 | 字号 / 行高 / 字重。与 lineHeightBase200 配合使用。 |
| Body1 · fontSizeBase300 | 14px / 20px / 400 | 字号 / 行高 / 字重。与 lineHeightBase300 配合使用。 |
| Body2 / Subtitle2 · fontSizeBase400 | 16px / 22px / 400 / 600 | 字号 / 行高 / 字重。与 lineHeightBase400 配合使用。 |
| Subtitle1 · fontSizeBase500 | 20px / 28px / 600 | 字号 / 行高 / 字重。与 lineHeightBase500 配合使用。 |
| Title3 · fontSizeBase600 | 24px / 32px / 600 | 字号 / 行高 / 字重。与 lineHeightBase600 配合使用。 |
| Title2 · fontSizeHero700 | 28px / 36px / 600 | 字号 / 行高 / 字重。与 lineHeightHero700 配合使用。 |
| Title1 · fontSizeHero800 | 32px / 40px / 600 | 字号 / 行高 / 字重。与 lineHeightHero800 配合使用。 |
| LargeTitle · fontSizeHero900 | 40px / 52px / 600 | 字号 / 行高 / 字重。与 lineHeightHero900 配合使用。 |
| Display · fontSizeHero1000 | 68px / 92px / 600 | 字号 / 行高 / 字重。与 lineHeightHero1000 配合使用。 |
### 字体系列和字重

| 名称 / 规则 | 值 / 配置 | 用途 |
| --- | --- | --- |
| fontFamilyBase | 'Segoe UI', 'Segoe UI Web (West European)', -apple-system, BlinkMacSystemFont, Roboto, 'Helvetica Neue', sans-serif | 提供方默认的网站字体栈；不要将周围网站使用的字体重新标记为 Fluent 字体。 |
| fontFamilyMonospace | Consolas, 'Courier New', Courier, monospace | 代码和技术标识符。 |
| fontWeightRegular / Medium / Semibold / Bold | 400 / 500 / 600 / 700 | 将角色字重与配套的字体排印阶梯一起使用。 |
### 形状令牌

| 名称 / 规则 | 值 / 配置 | 用途 |
| --- | --- | --- |
| borderRadiusNone | 0 | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadiusSmall | 2px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadiusMedium | 4px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadiusLarge | 6px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadiusXLarge | 8px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadius2XLarge | 12px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadius3XLarge | 16px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadius4XLarge | 24px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadius5XLarge | 32px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadius6XLarge | 40px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
| borderRadiusCircular | 10000px | 优先使用组件默认值；这是可用令牌，并不意味着每个界面表面都必须使用它。 |
### 动效时长阶梯

| 名称 / 规则 | 值 / 配置 | 用途 |
| --- | --- | --- |
| durationUltraFast | 50ms | 可用的运行时令牌；示例在悬停时使用 Fast，并在减少动态效果偏好下移除过渡效果。 |
| durationFaster | 100ms | 可用的运行时令牌；示例在悬停时使用 Fast，并在减少动态效果偏好下移除过渡效果。 |
| durationFast | 150ms | 可用的运行时令牌；示例在悬停时使用 Fast，并在减少动态效果偏好下移除过渡效果。 |
| durationNormal | 200ms | 可用的运行时令牌；示例在悬停时使用 Fast，并在减少动态效果偏好下移除过渡效果。 |
| durationGentle | 250ms | 可用的运行时令牌；示例在悬停时使用 Fast，并在减少动态效果偏好下移除过渡效果。 |
| durationSlow | 300ms | 可用的运行时令牌；示例在悬停时使用 Fast，并在减少动态效果偏好下移除过渡效果。 |
| durationSlower | 400ms | 可用的运行时令牌；示例在悬停时使用 Fast，并在减少动态效果偏好下移除过渡效果。 |
| durationUltraSlow | 500ms | 可用的运行时令牌；示例在悬停时使用 Fast，并在减少动态效果偏好下移除过渡效果。 |
### 主题变化是语义变化，不是反相处理

| 名称 / 规则 | 值 / 配置 | 用途 |
| --- | --- | --- |
| colorNeutralBackground1 | #292929 | 深色主要界面表面，取代浅色 #ffffff。 |
| colorNeutralForeground1 | #ffffff | 深色主要文本，取代浅色 #242424。 |
| colorNeutralForeground2 | #d6d6d6 | 深色次要文本。 |
| colorBrandBackground | brandWeb[70] = #115ea3 | 深色主要填充色；浅色模式使用 brandWeb[80]。 |
| colorBrandForegroundLink | brandWeb[100] = #479ef5 | 深色链接角色；与主按钮填充色不同。 |
## Microsoft Fluent 2 设计系统完整指南

实用指南，介绍如何为真实界面选择、应用和评审此系统。

完整指导、实现起点和源代码链接。

## Microsoft Fluent 2 设计系统与 React v9 实现

### 明确选择网站实现

> Fluent 2 是一种跨平台设计语言。本指南做了更具体、可直接使用的选择：@fluentui/react-components，即统一版 React v9 库。根组件是 FluentProvider，本示例使用 webLightTheme 或 webDarkTheme。Teams 主题是单独的品牌配置；Teams 中常见的紫色并非 Web 默认的蓝色色阶。Windows、iOS 和 Android 原生资源也不能与 React 包互换。

下面的完整示例呈现一份项目简报和本地访问权限列表。它结合了用于构建层级的 Title3 和 Body1、用于切换两个视图的 TabList 和 Tab，以及用于决定访问权限的 Dialog、Field、Input 和 Select。代码并未声称可以邀请真实用户。这样的区分既能演示组件行为，也不会凭空添加共享服务。

阅读配套规范

### Provider 实际提供的内容

FluentProvider 会将主题变量应用到其子树中。使用 tokens.colorNeutralBackground2 设置样式的自定义表面会随所选主题变化，而硬编码为白色的表面则不会。请将 Provider 放在需要使用主题的组件上层。该库通过自己的样式运行时为组件设置样式；本示例不会要求你导入一个虚构的 fluent.css 文件。

阅读配套规范

## Microsoft Fluent 2 设计系统：语义颜色与真实的 Web 品牌色阶

### 根据所选品牌色解析别名

浅色别名源将 colorBrandBackground 指向 brand[80]。Web 主题提供 brandWeb，其中第 80 级为 #0f6cbd。因此，在此示例中，#0f6cbd 是解析后的主填充色。别名文件包含较早示例中的十六进制注释；若将这些注释当作当前 Web 调色板，就会得出错误结果。决定该值的是源关系，而不是注释。

简报使用 colorNeutralForeground1，辅助标签使用 colorNeutralForeground2，周边工作区使用 colorNeutralBackground3。主要操作可以使用 colorBrandBackground，并搭配 colorNeutralForegroundOnBrand。主题变化时，角色名称仍是约定依据。人员权限以文字表示；蓝色边框或蓝色标签指示器表示选中状态，而非授权。

阅读配套规范

### 深色模式会改变映射关系

深色模式会将主表面解析为 #292929，将主要文字解析为 #ffffff。主品牌填充色使用第 70 级，而链接前景色使用第 100 级。因此，预览会一并切换令牌角色，而不只是反转页面背景。切换主题后，请对比访问权限对话框、选中的标签和弱化的元数据：它们都属于同一个主题边界。

阅读配套规范

## Microsoft Fluent 2 设计系统排版：将文字角色映射到运行时令牌

### 使用成对的字号和行高值

Body1 是基础阅读样式，字号为 14px，行高为 20px。Caption1 为 12/16，Subtitle2 为 16/22 并使用半粗体，Title3 为 24/32 并使用半粗体。这些成对数值能形成清晰的标题、标签和正文层级，无需任意添加中间字号。上表列出了更大的字号级别供参考，但紧凑型工作区不需要展示级的大标题。

React 令牌源与设计网站目前在一处实用细节上存在差异：运行时源中的 fontSizeBase500 / lineHeightBase500 为 20/28，而 Web 设计图表将 Subtitle1 列为 20/26。本示例采用运行时中的这组数值。遇到此类差异时，应核对版本和实现情况，而不是默默混用最适合模型图的数值。

阅读配套规范

### 文本预设与字体栈

除了视觉预设，也要使用语义化的 HTML：Title3 as="h1" 才是标题；仅设置样式不会形成文档大纲。基础字体栈以 Segoe UI 开头，并包含平台回退字体。不要仅为匹配截图而打包专有字体。本示例使用文档说明的字体栈，因此具体字形取决于查看设备上安装的字体。

自定义文字时，typographyStyles.body1 会组合字体系列、字号、字重和行高。相比只复制 fontSizeBase300，优先使用这组完整样式。访问列表中的电子邮件地址清晰易读，并支持换行；权限标签通过半粗体强调，但不会与页面标题争夺视觉焦点。

阅读配套规范

## Microsoft Fluent 2 设计系统：工作区中的间距、形状与密度

### React 的间距名称并非设计工具中的名称

Fluent 通用布局指南使用全局尺寸名称和以 4 为单位的节奏，其中包括用于对齐微调的 2、6 和 10。React 提供按轴区分的别名：spacingHorizontalS 为 8px，M 为 12px，L 为 16px，XXL 为 24px；垂直方向也有对应名称。不要将 size120 转换为 spacingHorizontal120；React 中不存在此标识符。

此布局使用 24px 面板内边距、表单字段之间 16px 的间隔，以及访问权限行之间 12px 的间距。确切的页面宽度和双栏布局由应用自行决定，并非官方 Fluent 断点。在窄屏上，团队辅助信息会移到简报下方，而两个标签名称仍保持可见。

阅读配套规范

### 形状体现组件尺寸

可用的圆角令牌包括 4px 的 Medium 和 8px 的 XLarge，但这并不意味着所有控件都应使用相同的圆角。HTML 示例将 4px 控件形状和 8px 文档表面形状作为明确的研究选择。除非所选设计要求确有需要，否则原生组件应保留其受支持的默认结构。不要通过修改生成的类名来强制改变形状。

阅读配套规范

## Microsoft Fluent 2 设计系统 TabList：选择状态、面板与键盘行为

### 选中值属于应用状态

示例在 React 状态中管理标签，并将其传递给 selectedValue。onTabSelect 会接收 data.value，并据此更新该状态。每个 Tab 都有稳定的 value，并通过 aria-controls 指向其面板。每个面板都设置了 role="tabpanel"、aria-labelledby 和隐藏条件。组件不会提供项目简报或访问列表；这些内容由示例明确渲染。

API 将聚焦与选择分开：selectTabOnFocus 默认值为 false。在独立示例中，按 Left/Right 和 Home/End 可移动焦点，按 Enter 或 Space 可激活当前聚焦的选项卡。这种手动激活方式让用户可以查看选项卡，而不会意外切换内容。选项卡标签应保持简短；官方布局指南指出，水平选项卡不会自动换行或滚动。

阅读配套规范

### 类别增多时，应改用其他模式

Brief 和 People 是同一工作区中两个密切相关的视图，因此适合使用选项卡。组织中的所有项目列表属于导航，而不是另一组选项卡。如果类别在窄屏上无法容纳，应有意设计溢出处理或采用其他选择模式；把标签缩小到难以辨认并不是响应式策略。

阅读配套规范

## Microsoft Fluent 2 设计系统指南：无障碍标签与有效验证

### 字段是一个组合，而不只是经过样式处理的输入框

Field 由标签插槽、子控件、可选提示和验证消息组成。其记录的控件契约包括 id、aria-labelledby、aria-describedby、aria-invalid 和 aria-required。Fluent 输入控件可以直接作为子元素；自定义控件则可以使用 render 函数的属性。这就是为什么用 Field 包裹电子邮件 Input，比在输入框上方放置无关文本更有用。

在示例中，只有在本地提交无效后，validationState 才会设为 error。应用会检查值是否符合电子邮件格式，以及该地址是否已存在于内存列表中。它会保留已输入的值，并通过 validationMessage 提供修改提示。Field 负责呈现并关联验证结果；它不会判断地址是否真实存在、发送邀请或授予访问权限。

阅读配套规范

### 将验证状态与权限区分开

权限 Select 包含 Can view 和 Can edit 两个选项，而电子邮件字段有自己的错误状态。不要因为一个地址尚未填写完整，就让整个对话框变红。required 是无障碍提示，不能替代服务器端的授权或验证。示例表单使用 noValidate，以便一致地展示显式验证消息；生产服务必须再次验证请求。

- 地址为空或格式错误：保持对话框打开，并说明可接受的格式。

- 地址已存在于本地访问列表中：显示针对重复地址的提示，不要再添加一行。

- 地址有效且尚未添加：在本地添加所选权限，关闭对话框，并如实说明结果仅在本地生效。

阅读配套规范

## Microsoft Fluent 2 设计系统对话框：在受支持的结构中构建表单

### 将提交按钮放在表单中

该组合由 Dialog、DialogTrigger、DialogSurface、form 元素，以及包含 DialogTitle、DialogContent 和 DialogActions 的 DialogBody 构成。form 包裹 body，使页脚中的提交按钮也能参与表单提交。取消按钮的类型为 type="button"，并位于用于关闭对话框的 DialogTrigger 中；否则，默认按钮类型可能会意外提交表单。

open 和 onOpenChange 用于控制显示状态。disableButtonEnhancement 可避免对已具备按钮行为的 Fluent Button 再次进行冗余增强。成功处理程序仅在本地验证通过后关闭对话框。验证失败时，已输入的电子邮件和所选权限会保留，供用户修改。

阅读配套规范

### 根据实际决策选择模态行为

这个简短的访问权限决策使用默认的模态类型。非模态对话框允许用户继续处理背景内容；警报对话框则用于紧急决策，且关闭行为限制更严格。普通的访问权限表单不需要警报语义。官方指南要求将焦点置于对话框内，并在关闭后将焦点返回触发器；独立实现使用原生 dialog 元素，并显式将焦点还给触发器。

阅读配套规范

## Microsoft Fluent 2 设计系统自定义样式：不破坏主题

### 使用 makeStyles 和语义变量

完整代码使用 makeStyles，并通过设计令牌设置界面表面、间距和圆角。这能让自定义面板与 provider 保持一致。生成的类名属于实现细节，不要从 DevTools 中复制到样式表。试验主题时，应更改 provider 主题，并一并检查原生控件和自定义区域。

已发布的时长阶梯范围为 50ms 至 500ms。HTML 示例使用 150ms 的 durationFast 处理简单的悬停变化，并在 prefers-reduced-motion 生效时移除这些过渡效果。这是应用对现有令牌的选择，并不表示每个 Fluent 交互都必须持续 150ms。即使移除动效，内容和错误消息仍应清晰易懂。

阅读配套规范

### 测试实现方式的适用边界

嵌入示例明确标注为 HTML/CSS，而不是原生 Fluent 实现。它展示已验证的值和访问权限对话框交互，且不会在预览中下载框架。评估原生样式、portal、焦点管理和组件更新时，请使用完整的 React 示例。独立交互检查通过，只能说明此示例符合要求，并不代表生产级共享应用已通过认证。

阅读配套规范

## Microsoft Fluent 2 设计系统安装并验证完整组合

### 提供可运行入口，而不是任务提纲

此设置面向已有的 React 应用，该应用需包含 id 为 root 的元素。安装 v9 套件，然后将 JSX 入口放入应用。导入语句、provider、组件树、处理程序、状态和根节点渲染代码均已一并提供。不会调用外部共享 API。如果项目模板已经渲染 App，请保留现有根入口，并导出 App，而不是再创建一个根节点。

在 lockfile 中记录解析后的软件包版本。链接的源代码参考资料描述的是经检查的 React v9 实现，而 master URL 可能随时间变化。升级前，请比较导出的主题，以及此处使用的 Field、TabList 和 Dialog 的具体行为。此设置不会混用 v8 @fluentui/react 组件与 v9 示例。

1. 在现有的 React 项目中安装 @fluentui/react-components@9，并保留生成的锁文件。

2. 在一个 FluentProvider 下渲染提供的入口，并以 webLightTheme 作为初始主题。

3. 打开对话框，提交无效地址，修正地址，并确认新增人员仅出现在本地状态中。

4. 在 webDarkTheme 下重复测试并使用键盘导航；验证面板关联、错误播报和焦点返回。

阅读配套规范

### 真实共享产品需要接入的内容

只有在定义好服务契约后，才能用服务调用替代本地添加：契约需明确已验证身份的操作者、目标工作区、允许的权限值、邀请结果和重复邀请的处理方式。如果服务拒绝请求，应保持对话框打开并显示已确认的结果，而不是乐观地声称对方已获得访问权限。这些规则不属于 Fluent 的范畴，但代码明确标出了交接点，而不是把它藏在通用的提交按钮后面。

阅读配套规范

## Microsoft Fluent 2 设计系统组件示例

### 完整的独立交互示例

将完整文档保存为 index.html，并在浏览器中打开。这是对 Fluent UI React v9 Web 令牌以及已记录的 Field、TabList 和 Dialog 结构的独立 HTML/CSS 呈现，并非原生 Fluent 组件运行时。

```
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Fluent web workspace study</title><style>
:root{color-scheme:light;--colorNeutralForeground1:#242424;--colorNeutralForeground2:#424242;--colorNeutralForeground3:#616161;--colorNeutralBackground1:#fff;--colorNeutralBackground2:#fafafa;--colorNeutralBackground3:#f5f5f5;--colorNeutralStroke1:#d1d1d1;--colorNeutralStroke2:#e0e0e0;--colorBrandBackground:#0f6cbd;--colorBrandBackgroundHover:#115ea3;--colorBrandBackgroundPressed:#0c3b5e;--colorBrandBackground2:#ebf3fc;--colorBrandForegroundLink:#115ea3;--colorNeutralForegroundOnBrand:#fff;--borderRadiusMedium:4px;--borderRadiusXLarge:8px;--spacingHorizontalS:8px;--spacingHorizontalM:12px;--spacingHorizontalL:16px;--spacingHorizontalXXL:24px;--durationFast:150ms}
[data-theme=dark]{color-scheme:dark;--colorNeutralForeground1:#fff;--colorNeutralForeground2:#d6d6d6;--colorNeutralForeground3:#adadad;--colorNeutralBackground1:#292929;--colorNeutralBackground2:#1f1f1f;--colorNeutralBackground3:#141414;--colorNeutralStroke1:#666;--colorNeutralStroke2:#525252;--colorBrandBackground:#115ea3;--colorBrandBackgroundHover:#0f6cbd;--colorBrandBackground2:#082338;--colorBrandForegroundLink:#479ef5}
*{box-sizing:border-box}body{margin:0;background:var(--colorNeutralBackground3);color:var(--colorNeutralForeground1);font-family:'Segoe UI','Segoe UI Web (West European)',-apple-system,BlinkMacSystemFont,Roboto,'Helvetica Neue',sans-serif;font-size:14px;line-height:20px}button,input,select{font:inherit}button{cursor:pointer;min-height:32px;padding:5px 12px;border:1px solid var(--colorNeutralStroke1);border-radius:var(--borderRadiusMedium);background:var(--colorNeutralBackground1);color:var(--colorNeutralForeground1);font-weight:600;transition:background var(--durationFast)}button:hover{background:var(--colorNeutralBackground3)}button:focus-visible,a:focus-visible,input:focus-visible,select:focus-visible,[tabindex]:focus-visible{outline:2px solid var(--colorBrandForegroundLink);outline-offset:3px}.primary{background:var(--colorBrandBackground);color:var(--colorNeutralForegroundOnBrand);border-color:transparent}.primary:hover{background:var(--colorBrandBackgroundHover)}.primary:active{background:var(--colorBrandBackgroundPressed)}.topbar{display:flex;justify-content:space-between;align-items:center;padding:12px 24px;background:var(--colorNeutralBackground1);border-bottom:1px solid var(--colorNeutralStroke2);gap:16px}.brand{display:flex;align-items:center;gap:12px;font-weight:600}.mark{display:grid;grid-template-columns:repeat(2,7px);gap:3px}.mark i{width:7px;height:7px;background:var(--colorBrandBackground)}.top-note{font-size:12px;color:var(--colorNeutralForeground3)}.page{max-width:1100px;margin:auto;padding:24px}.heading{display:flex;justify-content:space-between;align-items:center;gap:16px;margin-bottom:16px}.eyebrow{font-size:12px;line-height:16px;color:var(--colorNeutralForeground3);margin-bottom:4px}h1{font-size:24px;line-height:32px;font-weight:600;margin:0 0 4px}h2{font-size:20px;line-height:28px;font-weight:600;margin:0 0 16px}h3{font-size:16px;line-height:22px;font-weight:600;margin:20px 0 8px}p{margin:0 0 12px}.muted{color:var(--colorNeutralForeground3)}.tabs{display:flex;gap:16px;border-bottom:1px solid var(--colorNeutralStroke2);margin-bottom:24px}.tab{position:relative;background:none;border:0;border-radius:0;font-weight:400;padding:12px 4px;color:var(--colorNeutralForeground2)}.tab[aria-selected=true]{font-weight:600;color:var(--colorBrandForegroundLink)}.tab[aria-selected=true]:after{content:'';position:absolute;bottom:0;left:4px;right:4px;height:3px;background:var(--colorBrandBackground);border-radius:3px}.layout{display:grid;grid-template-columns:minmax(0,1fr) 240px;gap:24px}.document,.aside,.people{background:var(--colorNeutralBackground1);border:1px solid var(--colorNeutralStroke2);border-radius:var(--borderRadiusXLarge);padding:24px}.document{max-width:720px}.docmeta{display:flex;gap:8px;align-items:center;color:var(--colorNeutralForeground3);font-size:12px;line-height:16px;margin-bottom:24px}.avatar{flex:none;display:grid;place-items:center;background:var(--colorBrandBackground2);color:var(--colorBrandForegroundLink);font-weight:600;width:32px;height:32px;border-radius:50%}.quote{padding:12px 16px;background:var(--colorNeutralBackground2);border-left:3px solid var(--colorBrandBackground);margin:16px 0}.checklist{list-style:none;padding:0;margin:12px 0}.checklist li{display:flex;gap:8px;margin:8px 0}.checklist input{accent-color:var(--colorBrandBackground);width:16px;height:16px;margin-top:2px}.aside{padding:20px;height:fit-content}.aside h2{font-size:16px;line-height:22px;margin-bottom:16px}.person{display:flex;gap:8px;align-items:center;margin-bottom:16px}.person p{margin:0}.small{font-size:12px;line-height:16px}.aside hr{border:0;border-top:1px solid var(--colorNeutralStroke2);margin:20px 0}.tag{display:inline-block;border:1px solid var(--colorNeutralStroke1);padding:2px 8px;border-radius:4px;font-size:12px;color:var(--colorNeutralForeground2)}.people-head{display:flex;align-items:center;justify-content:space-between;margin-bottom:16px;gap:12px}.people-head h2{margin:0}.people ul{list-style:none;padding:0;margin:0}.member{display:flex;align-items:center;gap:12px;padding:16px 0;border-top:1px solid var(--colorNeutralStroke2)}.member-info{flex:1;min-width:0;overflow-wrap:anywhere}.member-info p{margin:0}.member .tag{flex:none}.caption{margin:20px 0 0;color:var(--colorNeutralForeground3);font-size:12px;line-height:16px}.status{margin-top:16px;overflow-wrap:anywhere;color:var(--colorNeutralForeground2)}.status:empty{display:none}[hidden]{display:none!important}dialog{width:min(520px,calc(100vw - 32px));padding:24px;border:1px solid var(--colorNeutralStroke1);border-radius:8px;background:var(--colorNeutralBackground1);color:var(--colorNeutralForeground1);max-height:calc(100dvh - 32px);overflow:auto}dialog::backdrop{background:rgba(0,0,0,.4)}dialog h2{font-size:20px;line-height:28px;margin-bottom:12px}.field{margin:20px 0}.field label{display:block;font-weight:600;margin-bottom:8px}.required{color:#b10e1e}.field input,.field select{width:100%;min-height:36px;background:var(--colorNeutralBackground1);color:var(--colorNeutralForeground1);padding:7px 12px;border:1px solid var(--colorNeutralStroke1);border-bottom-color:var(--colorNeutralForeground3);border-radius:4px}.field input:focus{border-bottom:2px solid var(--colorBrandBackground);padding-bottom:6px}.hint{font-size:12px;line-height:16px;color:var(--colorNeutralForeground3);margin-top:8px}.error{font-size:12px;line-height:16px;color:#b10e1e;margin-top:8px}.field input[aria-invalid=true]{border-color:#b10e1e}.actions{display:flex;justify-content:flex-end;gap:8px;margin-top:24px}[data-theme=dark] .error,[data-theme=dark] .required{color:#ff99a4}footer{padding:0 24px 20px;text-align:center;font-size:12px;color:var(--colorNeutralForeground3)}@media(max-width:700px){.page{padding:16px}.topbar{padding:12px 16px}.top-note{display:none}.layout{grid-template-columns:1fr}.document,.people{padding:20px}.aside{display:grid;grid-template-columns:1fr 1fr;gap:8px 16px}.aside h2,.aside hr{grid-column:1/-1}.aside hr{margin:0}.heading{align-items:flex-start}.heading .primary{flex:none}.member{flex-wrap:wrap}.member .tag{margin-left:44px}.topbar button,.heading button{min-height:40px}}@media(max-width:420px){h1{font-size:20px;line-height:28px}.heading{flex-direction:column;gap:12px}.aside{display:block}.member .tag{margin-left:0}}@media(prefers-reduced-motion:reduce){*{transition:none!important}}
</style></head><body>
<header class="topbar"><div class="brand"><span class="mark" aria-hidden="true"><i></i><i></i><i></i><i></i></span>Northstar workspace <span class="top-note">/ Product team</span></div><button id="theme" aria-pressed="false">Dark theme</button></header>
<main class="page"><div class="heading"><div><div class="eyebrow">Team workspace · October launch</div><h1>Launch workspace</h1><p class="muted">One brief. A clear decision. The right people.</p></div><button class="primary" id="share">Add a person</button></div>
<div class="tabs" role="tablist" aria-label="Workspace views"><button class="tab" id="brief-tab" role="tab" aria-controls="brief-panel" aria-selected="true" tabindex="0">Brief</button><button class="tab" id="people-tab" role="tab" aria-controls="people-panel" aria-selected="false" tabindex="-1">People <span id="count">2</span></button></div>
<section id="brief-panel" role="tabpanel" aria-labelledby="brief-tab" tabindex="0"><div class="layout"><article class="document"><div class="docmeta"><span class="avatar" aria-hidden="true">AC</span><span>Alex Chen · Product design<br>Updated today at 09:30</span></div><h2>A more welcoming first project</h2><p>Help a new team move from an empty workspace to its first useful result. This review focuses on the welcome screen and the first-project setup.</p><h3>The decision</h3><div class="quote">Should a sample project be created automatically, or offered as an option?</div><p>We want a clear starting point without filling the workspace with content someone did not choose. Review both paths before the team walkthrough.</p><h3>Review checklist</h3><ul class="checklist"><li><input id="c1" type="checkbox" checked><label for="c1">Welcome copy explains the next step</label></li><li><input id="c2" type="checkbox"><label for="c2">Empty workspace offers a sample project</label></li><li><input id="c3" type="checkbox"><label for="c3">Keyboard path reaches the first project action</label></li></ul><p class="small muted">Checklist changes are local to this example.</p></article><aside class="aside"><h2>Working together</h2><div class="person"><span class="avatar" aria-hidden="true">AC</span><div><p>Alex Chen</p><span class="small muted">Workspace owner</span></div></div><div class="person"><span class="avatar" aria-hidden="true">MP</span><div><p>Maya Patel</p><span class="small muted">Can edit</span></div></div><hr><div><h3>Next review</h3><p>Thursday, 10:00<br><span class="muted small">Team walkthrough · UTC</span></p></div><div><h3>Access</h3><span class="tag">Specific people</span><p class="small muted" style="margin-top:8px">Adding a person here updates only this local preview.</p></div></aside></div></section>
<section id="people-panel" role="tabpanel" aria-labelledby="people-tab" tabindex="0" hidden><div class="people"><div class="people-head"><h2>People with access</h2><span class="tag">Local example</span></div><p class="muted">The roles below are fictional. No invitation or real access change is made.</p><ul id="members"><li class="member"><span class="avatar" aria-hidden="true">AC</span><div class="member-info"><p>Alex Chen</p><span class="small muted">alex@example.com</span></div><span class="tag">Owner</span></li><li class="member"><span class="avatar" aria-hidden="true">MP</span><div class="member-info"><p>Maya Patel</p><span class="small muted">maya@example.com</span></div><span class="tag">Can edit</span></li></ul></div></section><p class="status" id="status" role="status"></p><p class="caption">Fluent UI React v9 web-token study · HTML/CSS interpretation, not native library components. Try tabs, theme, checkboxes and the access form.</p></main>
<dialog id="access" aria-labelledby="dialog-title"><form id="access-form" novalidate><h2 id="dialog-title">Add a person to Launch workspace</h2><p class="muted">Choose an address and permission. This demonstration updates the local list only.</p><div class="field"><label for="email">Email address <span class="required" aria-hidden="true">*</span></label><input id="email" type="email" autocomplete="email" aria-required="true" aria-describedby="email-hint" autofocus><div class="hint" id="email-hint">For example, jordan@example.com</div><div class="error" id="error" role="alert" hidden></div></div><div class="field"><label for="permission">Permission</label><select id="permission" aria-describedby="permission-hint"><option>Can view</option><option>Can edit</option></select><div class="hint" id="permission-hint">Viewers can read; editors can change the brief in a real application</div></div><div class="actions"><button type="button" id="cancel">Cancel</button><button class="primary" type="submit">Add locally</button></div></form></dialog>
<script>
const $=id=>document.getElementById(id),tabs=[...document.querySelectorAll('[role=tab]')],emails=new Set(['alex@example.com','maya@example.com']);
function selectTab(tab){tabs.forEach(t=>{const chosen=t===tab;t.setAttribute('aria-selected',String(chosen));t.tabIndex=chosen?0:-1;$(t.getAttribute('aria-controls')).hidden=!chosen;});}
tabs.forEach((tab,index)=>{tab.addEventListener('click',()=>selectTab(tab));tab.addEventListener('keydown',event=>{let next;if(event.key==='ArrowRight')next=(index+1)%tabs.length;if(event.key==='ArrowLeft')next=(index+tabs.length-1)%tabs.length;if(event.key==='Home')next=0;if(event.key==='End')next=tabs.length-1;if(next!==undefined){event.preventDefault();tabs.forEach(t=>t.tabIndex=-1);tabs[next].tabIndex=0;tabs[next].focus();}});});
$('theme').onclick=()=>{const dark=document.documentElement.dataset.theme!=='dark';document.documentElement.dataset.theme=dark?'dark':'light';$('theme').textContent=dark?'Light theme':'Dark theme';$('theme').setAttribute('aria-pressed',String(dark));};
const dialog=$('access'),email=$('email'),error=$('error');function clearError(){error.hidden=true;error.textContent='';email.removeAttribute('aria-invalid');email.setAttribute('aria-describedby','email-hint');}function showError(message){error.textContent=message;error.hidden=false;email.setAttribute('aria-invalid','true');email.setAttribute('aria-describedby','email-hint error');email.focus();}
$('share').onclick=()=>{clearError();dialog.showModal();email.focus();};$('cancel').onclick=()=>dialog.close();dialog.addEventListener('close',()=>$('share').focus());dialog.addEventListener('click',event=>{if(event.target===dialog){const r=dialog.getBoundingClientRect();if(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom)dialog.close();}});email.addEventListener('input',clearError);
$('access-form').addEventListener('submit',event=>{event.preventDefault();const value=email.value.trim().toLowerCase();if(!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)){showError('Enter an email address such as name@example.com');return;}if(emails.has(value)){showError('This address already appears in the local access list');return;}const permission=$('permission').value;emails.add(value);const item=document.createElement('li');item.className='member';const avatar=document.createElement('span');avatar.className='avatar';avatar.setAttribute('aria-hidden','true');avatar.textContent=value.slice(0,2).toUpperCase();const info=document.createElement('div');info.className='member-info';const name=document.createElement('p');name.textContent=value;const sub=document.createElement('span');sub.className='small muted';sub.textContent='Added in this local example';info.append(name,sub);const role=document.createElement('span');role.className='tag';role.textContent=permission;item.append(avatar,info,role);$('members').append(item);$('count').textContent=emails.size;$('status').textContent='Added '+value+' as '+permission.toLowerCase()+' locally. No invitation was sent.';email.value='';clearError();selectTab($('people-tab'));dialog.close();});
</script></body></html>
```

代码参考

- 本文档中的访问对话框和选项卡使用原生 HTML 控件；运行单独的 React v9 入口，以评估 FluentProvider、Field 和 Dialog 的行为。

### 完整的原生实现入口

此处汇总了从设置到完整实现的内容，并附有重点说明 API。请按照设置中的文档说明，满足软件包和构建前置条件。

```
// Terminal: npm install @fluentui/react-components@9
// Existing React application; index.html must contain <div id="root"></div>.
// Entry file: src/main.jsx
import * as React from 'react';
import { createRoot } from 'react-dom/client';
import {
  FluentProvider, webLightTheme, webDarkTheme, makeStyles, tokens,
  Button, Body1, Title3, TabList, Tab, Field, Input, Select,
  Dialog, DialogTrigger, DialogSurface, DialogBody, DialogTitle,
  DialogContent, DialogActions
} from '@fluentui/react-components';

const useStyles = makeStyles({
  page: { maxWidth: '880px', margin: '0 auto', padding: tokens.spacingHorizontalXXL },
  header: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: tokens.spacingHorizontalL },
  panel: { padding: tokens.spacingHorizontalXXL, marginTop: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusXLarge },
  form: { display: 'grid', gap: tokens.spacingVerticalL },
  row: { display: 'flex', flexWrap: 'wrap', justifyContent: 'space-between', gap: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalM, paddingBottom: tokens.spacingVerticalM },
});

function Workspace({ dark, setDark }) {
  const styles = useStyles();
  const [tab, setTab] = React.useState('brief');
  const [open, setOpen] = React.useState(false);
  const [email, setEmail] = React.useState('');
  const [permission, setPermission] = React.useState('Can view');
  const [error, setError] = React.useState('');
  const [notice, setNotice] = React.useState('');
  const [people, setPeople] = React.useState([{ email: 'alex@example.com', permission: 'Owner' }]);
  function submit(event) {
    event.preventDefault();
    const value = email.trim().toLowerCase();
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)) {
      setError('Enter an email address such as name@example.com'); return;
    }
    if (people.some(person => person.email === value)) {
      setError('This address already appears in the local access list'); return;
    }
    setPeople(previous => [...previous, { email: value, permission }]);
    setNotice(`Added ${value} as ${permission.toLowerCase()} in this local example. No invitation was sent.`);
    setEmail(''); setError(''); setOpen(false); setTab('people');
  }
  return <main className={styles.page}>
    <div className={styles.header}>
      <Title3 as="h1">Launch workspace</Title3>
      <Button onClick={() => setDark(!dark)}>{dark ? 'Light theme' : 'Dark theme'}</Button>
    </div>
    <Body1>Project brief and access settings · local demonstration</Body1>
    <TabList aria-label="Workspace views" selectedValue={tab}
      onTabSelect={(_, data) => setTab(data.value)}>
      <Tab id="brief-tab" value="brief" aria-controls="brief-panel">Brief</Tab>
      <Tab id="people-tab" value="people" aria-controls="people-panel">People</Tab>
    </TabList>
    <section id="brief-panel" role="tabpanel" aria-labelledby="brief-tab"
      hidden={tab !== 'brief'} className={styles.panel} tabIndex={0}>
      <h2>Launch a clearer onboarding flow</h2>
      <p>Review the welcome screen and first-project setup before the team walkthrough.</p>
      <p>Decision needed: should the sample project be created automatically or offered as an option?</p>
    </section>
    <section id="people-panel" role="tabpanel" aria-labelledby="people-tab"
      hidden={tab !== 'people'} className={styles.panel} tabIndex={0}>
      <h2>People with access</h2>
      <ul>{people.map(person => <li key={person.email} className={styles.row}>
        <span>{person.email}</span><strong>{person.permission}</strong>
      </li>)}</ul>
    </section>
    <Dialog open={open} onOpenChange={(_, data) => setOpen(data.open)}>
      <DialogTrigger disableButtonEnhancement><Button appearance="primary">Add a person</Button></DialogTrigger>
      <DialogSurface>
        <form noValidate onSubmit={submit}>
          <DialogBody>
            <DialogTitle>Add a person to Launch workspace</DialogTitle>
            <DialogContent className={styles.form}>
              <p>This example updates local state only. It does not send email or change real access.</p>
              <Field label="Email address" required validationState={error ? 'error' : 'none'}
                validationMessage={error || undefined}>
                <Input type="email" value={email} onChange={(_, data) => { setEmail(data.value); setError(''); }} />
              </Field>
              <Field label="Permission" hint="Viewers can read; editors can change the brief in a real application">
                <Select value={permission} onChange={(_, data) => setPermission(data.value)}>
                  <option>Can view</option><option>Can edit</option>
                </Select>
              </Field>
            </DialogContent>
            <DialogActions>
              <DialogTrigger disableButtonEnhancement><Button type="button">Cancel</Button></DialogTrigger>
              <Button type="submit" appearance="primary">Add locally</Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
    <p role="status">{notice}</p>
  </main>;
}
function App() {
  const [dark, setDark] = React.useState(false);
  return <FluentProvider theme={dark ? webDarkTheme : webLightTheme}>
    <Workspace dark={dark} setDark={setDark} />
  </FluentProvider>;
}
createRoot(document.getElementById('root')).render(<App />);
```

代码参考

- 适用于现有 React 应用的完整 React v9 组件组合。独立预览是单独的 HTML/CSS 呈现。此代码仅执行本地验证和状态更新；不会发送电子邮件，也不会实际更改权限。源 API 已核对；在目标项目中测试之前，不保证能通过原生包编译。

### Field + Input + Select：带标签的访问权限表单

以下代码片段使用下方完整配置中的状态变量和导入项。Field 负责标签、提示和验证状态的呈现；电子邮件规则和重复项检查由应用负责。

```
<Field label="Email address" required
  validationState={error ? 'error' : 'none'}
  validationMessage={error || undefined}>
  <Input type="email" value={email}
    onChange={(_, data) => { setEmail(data.value); setError(''); }} />
</Field>
<Field label="Permission" hint="Choose what this person can do">
  <Select value={permission}
    onChange={(_, data) => setPermission(data.value)}>
    <option>Can view</option><option>Can edit</option>
  </Select>
</Field>
```

代码参考

- required 会提供可访问的必填标示，但不会实现应用的验证规则。

- 存在 validationMessage 时，默认验证状态会变为 error。没有错误需要显示时，请显式设置为 none。

- Input 的 onChange 会通过 data.value 提供当前字符串；不要将其处理函数与仅接收普通 DOM 事件的输入处理函数混淆。

### 受控 TabList 与显式关联的面板

TabList 负责选项卡选择和行为。请自行渲染内容面板，并确保每个面板的 id 和可访问名称都与对应的 Tab 关联。

```
<TabList selectedValue={tab} aria-label="Workspace views"
  onTabSelect={(_, data) => setTab(data.value)}>
  <Tab id="brief-tab" value="brief" aria-controls="brief-panel">Brief</Tab>
  <Tab id="people-tab" value="people" aria-controls="people-panel">People</Tab>
</TabList>
<section id="brief-panel" role="tabpanel" aria-labelledby="brief-tab"
  hidden={tab !== 'brief'} tabIndex={0}>Project brief</section>
<section id="people-panel" role="tabpanel" aria-labelledby="people-tab"
  hidden={tab !== 'people'} tabIndex={0}>People with access</section>
```

代码参考

- selectedValue 应设置在 TabList 上；每个 Tab 都应使用不同的 value。

- selectTabOnFocus 默认为 false。方向键移动焦点和激活选项是分开的，除非有意更改此行为。

- Fluent 水平选项卡不会自动换行或滚动；示例使用两个较短的标签，以确保它们始终可见。

## Microsoft Fluent 2 设计系统 Agent 配置

### 实现起点

适用于现有 React 应用的完整 React v9 组件组合。独立预览是单独的 HTML/CSS 呈现。此代码仅执行本地验证和状态更新；不会发送电子邮件，也不会实际更改权限。源 API 已核对；在目标项目中测试之前，不保证能通过原生包编译。

```
// Terminal: npm install @fluentui/react-components@9
// Existing React application; index.html must contain <div id="root"></div>.
// Entry file: src/main.jsx
import * as React from 'react';
import { createRoot } from 'react-dom/client';
import {
  FluentProvider, webLightTheme, webDarkTheme, makeStyles, tokens,
  Button, Body1, Title3, TabList, Tab, Field, Input, Select,
  Dialog, DialogTrigger, DialogSurface, DialogBody, DialogTitle,
  DialogContent, DialogActions
} from '@fluentui/react-components';

const useStyles = makeStyles({
  page: { maxWidth: '880px', margin: '0 auto', padding: tokens.spacingHorizontalXXL },
  header: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: tokens.spacingHorizontalL },
  panel: { padding: tokens.spacingHorizontalXXL, marginTop: tokens.spacingVerticalL,
    backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusXLarge },
  form: { display: 'grid', gap: tokens.spacingVerticalL },
  row: { display: 'flex', flexWrap: 'wrap', justifyContent: 'space-between', gap: tokens.spacingHorizontalM,
    paddingTop: tokens.spacingVerticalM, paddingBottom: tokens.spacingVerticalM },
});

function Workspace({ dark, setDark }) {
  const styles = useStyles();
  const [tab, setTab] = React.useState('brief');
  const [open, setOpen] = React.useState(false);
  const [email, setEmail] = React.useState('');
  const [permission, setPermission] = React.useState('Can view');
  const [error, setError] = React.useState('');
  const [notice, setNotice] = React.useState('');
  const [people, setPeople] = React.useState([{ email: 'alex@example.com', permission: 'Owner' }]);
  function submit(event) {
    event.preventDefault();
    const value = email.trim().toLowerCase();
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)) {
      setError('Enter an email address such as name@example.com'); return;
    }
    if (people.some(person => person.email === value)) {
      setError('This address already appears in the local access list'); return;
    }
    setPeople(previous => [...previous, { email: value, permission }]);
    setNotice(`Added ${value} as ${permission.toLowerCase()} in this local example. No invitation was sent.`);
    setEmail(''); setError(''); setOpen(false); setTab('people');
  }
  return <main className={styles.page}>
    <div className={styles.header}>
      <Title3 as="h1">Launch workspace</Title3>
      <Button onClick={() => setDark(!dark)}>{dark ? 'Light theme' : 'Dark theme'}</Button>
    </div>
    <Body1>Project brief and access settings · local demonstration</Body1>
    <TabList aria-label="Workspace views" selectedValue={tab}
      onTabSelect={(_, data) => setTab(data.value)}>
      <Tab id="brief-tab" value="brief" aria-controls="brief-panel">Brief</Tab>
      <Tab id="people-tab" value="people" aria-controls="people-panel">People</Tab>
    </TabList>
    <section id="brief-panel" role="tabpanel" aria-labelledby="brief-tab"
      hidden={tab !== 'brief'} className={styles.panel} tabIndex={0}>
      <h2>Launch a clearer onboarding flow</h2>
      <p>Review the welcome screen and first-project setup before the team walkthrough.</p>
      <p>Decision needed: should the sample project be created automatically or offered as an option?</p>
    </section>
    <section id="people-panel" role="tabpanel" aria-labelledby="people-tab"
      hidden={tab !== 'people'} className={styles.panel} tabIndex={0}>
      <h2>People with access</h2>
      <ul>{people.map(person => <li key={person.email} className={styles.row}>
        <span>{person.email}</span><strong>{person.permission}</strong>
      </li>)}</ul>
    </section>
    <Dialog open={open} onOpenChange={(_, data) => setOpen(data.open)}>
      <DialogTrigger disableButtonEnhancement><Button appearance="primary">Add a person</Button></DialogTrigger>
      <DialogSurface>
        <form noValidate onSubmit={submit}>
          <DialogBody>
            <DialogTitle>Add a person to Launch workspace</DialogTitle>
            <DialogContent className={styles.form}>
              <p>This example updates local state only. It does not send email or change real access.</p>
              <Field label="Email address" required validationState={error ? 'error' : 'none'}
                validationMessage={error || undefined}>
                <Input type="email" value={email} onChange={(_, data) => { setEmail(data.value); setError(''); }} />
              </Field>
              <Field label="Permission" hint="Viewers can read; editors can change the brief in a real application">
                <Select value={permission} onChange={(_, data) => setPermission(data.value)}>
                  <option>Can view</option><option>Can edit</option>
                </Select>
              </Field>
            </DialogContent>
            <DialogActions>
              <DialogTrigger disableButtonEnhancement><Button type="button">Cancel</Button></DialogTrigger>
              <Button type="submit" appearance="primary">Add locally</Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
    <p role="status">{notice}</p>
  </main>;
}
function App() {
  const [dark, setDark] = React.useState(false);
  return <FluentProvider theme={dark ? webDarkTheme : webLightTheme}>
    <Workspace dark={dark} setDark={setDark} />
  </FluentProvider>;
}
createRoot(document.getElementById('root')).render(<App />);
```

### 适用于你项目的提示词

```
通过 @fluentui/react-components 使用 Fluent UI React v9。使用 webLightTheme 渲染 FluentProvider，并提供 webDarkTheme 切换功能。使用 Title3 和 Body1 组合简介；使用受控的 TabList 和选项卡视图，并显式关联面板；创建包含 Field/Input/Select 和真实表单的访问对话框。自定义界面表面和间距时使用令牌。遵循提供的组件示例和运行时字号配对，包括 20/28 的 Subtitle1。保留无效输入、拒绝本地重复地址、恢复对话框焦点，并如实说明本地添加的实际情况。只有在获得授权和结果契约后，才接入共享服务。
```

## Microsoft Fluent 2 设计系统的来源与范围

这是对 Fluent UI React v9 的 OpenDesign 应用研究，并非 Microsoft 产品或在线共享服务。独立预览是对已核实的 Web 令牌和组件结构的 HTML/CSS 呈现；下方的 React 代码使用原生 Fluent 组件。参考值基于 2026 年 10 月 3 日核对的关联源快照，并不保证每个版本、平台或品牌都使用完全相同的值。

- React v9 包入口 Provider 与套件安装。

- 主题架构 语义令牌的使用方式和可用主题。

- 运行时排版 字体栈、字号、行高和字重。

- 文本样式映射 预设样式与全局令牌的映射关系。

- 设计字号阶梯 设计语言字号表；Subtitle1 与运行时字号配对不同。

- 运行时间距 水平和垂直令牌名称。

- 布局指南 全局间距和响应式设计注意事项。

- 圆角令牌 可用的运行时形状值。

- 动效时长 运行时时长值。

- 浅色语义映射 中性色和品牌别名的映射。

- 深色语义映射 深色主题别名的映射。

- 品牌色阶 Web 和 Teams 的实际品牌色值。

- Web 主题构建 webLightTheme 使用 brandWeb。

- Field 用法 标签、验证和组件组合指南。

- Field API 插槽及可访问子元素契约。

- TabList 用法 布局和类别选择指南。

- TabList API 受控选择和焦点选项。

- Dialog 用法 表单位置和模态交互指南。

- Dialog API 受控可见性和模态类型。

## Microsoft Fluent 2 设计系统相关资源

### Monday Vibe

使用 Vibe 4 令牌和 React 控件，构建可运行的交付看板。了解语义操作颜色与看板标签的区别、命名间距级别、字体简写，以及新应用所需的显式 CSS 设置。

### Morningstar 设计系统

构建一个让数字对齐、资产颜色保持原有含义、文字清晰易读的研究界面。探索 MDS 常量、Vue 3 集成、Morningstar Intrinsic 迁移，以及一个可排序的虚构资产配置记录簿。

### Mozilla Protocol

一份编辑式 Protocol 指南，包含真实的字体栈、间距和容器尺度、响应式图文阅读页面，以及完整的原生 CSS 表单示例。


- [实现参考](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-components/README.md)
- [来源](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/alias/lightColor.ts)
- [来源](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/brandColors.ts)
- [来源](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/spacings.ts)
- [来源](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/fonts.ts)
- [来源](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/borderRadius.ts)
- [来源](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/durations.ts)
- [来源](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/alias/darkColor.ts)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-components/README.md)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/docs/architecture/design-tokens.md)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/alias/lightColor.ts)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/alias/darkColor.ts)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/fonts.ts)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/typographyStyles.ts)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/spacings.ts)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/borderRadius.ts)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-tabs/library/src/components/TabList/TabList.types.ts)
- [阅读配套规范](https://fluent2.microsoft.design/components/web/react/core/tablist/usage)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-field/library/src/components/Field/Field.types.ts)
- [阅读配套规范](https://fluent2.microsoft.design/components/web/react/core/field/usage)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-dialog/library/src/components/Dialog/Dialog.types.ts)
- [阅读配套规范](https://fluent2.microsoft.design/components/web/react/core/dialog/usage)
- [阅读配套规范](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/durations.ts)
- [代码参考](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-components/README.md)
- [代码参考](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-field/library/src/components/Field/Field.types.ts)
- [代码参考](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-tabs/library/src/components/TabList/TabList.types.ts)
- [React v9 包入口 Provider 与套件安装。](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-components/README.md)
- [主题架构 语义令牌的使用方式和可用主题。](https://github.com/microsoft/fluentui/blob/master/docs/architecture/design-tokens.md)
- [运行时排版 字体栈、字号、行高和字重。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/fonts.ts)
- [文本样式映射 预设样式与全局令牌的映射关系。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/typographyStyles.ts)
- [设计字号阶梯 设计语言字号表；Subtitle1 与运行时字号配对不同。](https://fluent2.microsoft.design/typography)
- [运行时间距 水平和垂直令牌名称。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/spacings.ts)
- [布局指南 全局间距和响应式设计注意事项。](https://fluent2.microsoft.design/layout)
- [圆角令牌 可用的运行时形状值。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/borderRadius.ts)
- [动效时长 运行时时长值。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/durations.ts)
- [浅色语义映射 中性色和品牌别名的映射。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/alias/lightColor.ts)
- [深色语义映射 深色主题别名的映射。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/alias/darkColor.ts)
- [品牌色阶 Web 和 Teams 的实际品牌色值。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/brandColors.ts)
- [Web 主题构建 webLightTheme 使用 brandWeb。](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/themes/web/lightTheme.ts)
- [Field 用法 标签、验证和组件组合指南。](https://fluent2.microsoft.design/components/web/react/core/field/usage)
- [Field API 插槽及可访问子元素契约。](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-field/library/src/components/Field/Field.types.ts)
- [TabList 用法 布局和类别选择指南。](https://fluent2.microsoft.design/components/web/react/core/tablist/usage)
- [TabList API 受控选择和焦点选项。](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-tabs/library/src/components/TabList/TabList.types.ts)
- [Dialog 用法 表单位置和模态交互指南。](https://fluent2.microsoft.design/components/web/react/core/dialog/usage)
- [Dialog API 受控可见性和模态类型。](https://github.com/microsoft/fluentui/blob/master/packages/react-components/react-dialog/library/src/components/Dialog/Dialog.types.ts)
