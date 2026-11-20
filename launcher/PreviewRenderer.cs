using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
                await Task.Delay(600);
                // 分身页有五个分段；功能页有四个页签；设置页可展开高级 / 带搜索词。
                // 截图时点名叫哪一段，否则只能拍到默认段。
                if (!string.IsNullOrWhiteSpace(section))
                {
                    if (win.Host.Content is Views.SelfPage self) self.PreviewSection(section);
                    else if (win.Host.Content is Views.FunctionPage function) function.PreviewSection(section);
                    else if (win.Host.Content is Views.SettingsPage settings) settings.PreviewSection(section);
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

                win.Close();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Trace("FAILED: " + ex);
                Application.Current?.Shutdown(3);
            }
        }
    }
}
