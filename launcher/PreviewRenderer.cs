using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace WhaleLauncher
{
    /// <summary>
    /// 开发用离屏渲染：把主窗口按 data/launcher-ui.json 的配置画成 PNG。
    /// 调外观时不用真开窗口、不用手动截图，也不抢焦点：
    ///   WhaleLauncher.exe --render-preview out.png [scale]
    /// </summary>
    public static class PreviewRenderer
    {
        private static readonly string TracePath =
            Path.Combine(AppContext.BaseDirectory, "data", "render-preview.log");

        private static void Trace(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TracePath) ?? AppContext.BaseDirectory);
                File.AppendAllText(TracePath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        public static async Task RunAsync(string output, double scale, int navIndex, string section = "")
        {
            Trace("start output=" + output + " scale=" + scale + " nav=" + navIndex);
            try
            {
                Appearance.Load();
                Trace("loaded theme=" + Appearance.Theme + " backdrop=" + Appearance.Backdrop + " has=" + Appearance.HasBackdrop);
                Appearance.ApplyTheme();
                Trace("theme applied");

                var win = new MainWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowActivated = false,
                };
                Trace("window created");
                win.Show();
                Trace("window shown");

                // Loaded 之后页面会异步拉一次数据；本地配置要起一次 node，给它足够时间填表。
                await Task.Delay(2600);
                if (navIndex >= 0 && navIndex < win.Nav.Items.Count) win.Nav.SelectedIndex = navIndex;
                // 页面 Loaded 后还会异步拉一次配置；等它把字段和摘要填出来再截图。
                if (win.Host.Content is Views.SettingsPage settingsPage)
                    await Task.WhenAny(settingsPage.PreviewLoaded, Task.Delay(9000));
                // 人设工作台要等表单、预设和最终提示词都读完，右栏才有内容可拍。
                if (win.Host.Content is Views.ChatPage chatPage)
                    await Task.WhenAny(chatPage.PreviewLoaded, Task.Delay(9000));
                await Task.Delay(600);
                // 分身页有五个分段；功能页有四个页签；设置页可展开高级 / 带搜索词。
                // 截图时点名叫哪一段，否则只能拍到默认段。
                if (!string.IsNullOrWhiteSpace(section))
                {
                    if (win.Host.Content is Views.SelfPage self) self.PreviewSection(section);
                    else if (win.Host.Content is Views.FunctionPage function) function.PreviewSection(section);
                    else if (win.Host.Content is Views.SettingsPage settings) settings.PreviewSection(section);
                    else if (win.Host.Content is Views.ChatPage chat) await chat.PreviewSectionAsync(section);
                }

                // 发言范围弹层不是页面，单独按真实弹层尺寸搭一层再拍；
                // 示例名单来自 PreviewSeed，不读真实配置。
                Grid policyOverlay = null;
                if (section == "policy" || section == "policy-blacklist")
                {
                    var policy = new Views.ChatPolicyWindow(win.Supervisor);
                    policy.PreviewSeed(whitelistOnly: true, blacklist: section == "policy-blacklist");
                    policyOverlay = ShowPolicyPreview(win, policy);
                    await Task.WhenAny(policy.PreviewLoaded, Task.Delay(2000));
                    await Task.Delay(300);
                }
                await Task.Delay(600);
                win.UpdateLayout();
                Trace("layout " + win.ActualWidth + "x" + win.ActualHeight);

                var w = win.ActualWidth > 0 ? win.ActualWidth : win.Width;
                var h = win.ActualHeight > 0 ? win.ActualHeight : win.Height;
                var dpi = 96 * scale;
                var bmp = new RenderTargetBitmap(
                    (int)Math.Round(w * scale), (int)Math.Round(h * scale), dpi, dpi, PixelFormats.Pbgra32);
                bmp.Render(win);
                Trace("rendered");

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                var full = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                using (var fs = File.Create(full)) enc.Save(fs);
                Trace("saved " + full);

                if (policyOverlay != null) RemovePolicyPreview(win, policyOverlay);
                win.Close();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Trace("FAILED: " + ex);
                Application.Current?.Shutdown(3);
            }
        }

        /// <summary>按 AppDialog.ShowContent 的同一套尺寸搭出弹层，只为截图，不跑消息循环。</summary>
        private static Grid ShowPolicyPreview(Window win, Views.ChatPolicyWindow policy)
        {
            if (!(win.Content is Grid root)) return null;

            var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(166, 0, 0, 0)) };
            Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));
            Grid.SetColumnSpan(overlay, Math.Max(1, root.ColumnDefinitions.Count));
            Panel.SetZIndex(overlay, int.MaxValue);

            var card = new Border
            {
                Width = 560,
                Height = 560,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            card.SetResourceReference(Border.BackgroundProperty, "PopupBg");
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

            var layout = new DockPanel();
            card.Child = layout;

            var header = new DockPanel { Margin = new Thickness(24, 16, 24, 0) };
            DockPanel.SetDock(header, Dock.Top);
            var title = new TextBlock
            {
                Text = "发言范围",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var close = new Button
            {
                Content = "×",
                FontSize = 18,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Style = Application.Current?.TryFindResource("Ghost") as Style,
                IsHitTestVisible = false
            };
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(title);
            layout.Children.Add(header);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(22, 12, 22, 18)
            };
            footer.Children.Add(new Button
            {
                Content = "关闭",
                Style = Application.Current?.TryFindResource("Primary") as Style,
                Padding = new Thickness(16, 5, 16, 5),
                IsHitTestVisible = false
            });
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            var body = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
            body.Children.Add(policy);
            layout.Children.Add(new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false
            });

            overlay.Children.Add(card);
            root.Children.Add(overlay);

            foreach (UIElement child in root.Children)
            {
                if (ReferenceEquals(child, overlay)) continue;
                child.SetCurrentValue(UIElement.IsEnabledProperty, false);
                child.SetCurrentValue(UIElement.EffectProperty, new BlurEffect { Radius = 8 });
            }
            return overlay;
        }

        private static void RemovePolicyPreview(Window win, Grid overlay)
        {
            if (!(win.Content is Grid root)) return;
            root.Children.Remove(overlay);
            foreach (UIElement child in root.Children)
            {
                child.SetCurrentValue(UIElement.IsEnabledProperty, true);
                child.SetCurrentValue(UIElement.EffectProperty, null);
            }
        }
    }
}
