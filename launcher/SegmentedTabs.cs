using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace WhaleLauncher
{
    /// <summary>
    /// 精简后的页内分段页签。标签保持短词，选中态只用品牌色底色 + 1px 描边；
    /// 状态摘要放在文字后面的 12px 角标里，不另起一行。
    /// 五个主页面和功能子页共用同一套手感。
    /// </summary>
    public static class SegmentedTabs
    {
        public static Button Make(string text, RoutedEventHandler onClick)
            => Make(text, out _, onClick);

        public static Button Make(string text, out TextBlock badge, RoutedEventHandler onClick)
        {
            badge = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(badge);
            var button = new Button
            {
                Content = content,
                Height = 32,
                MinWidth = 72,
                Padding = new Thickness(14, 4, 14, 4),
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Style = (Style)Application.Current.FindResource("Ghost"),
            };
            AutomationProperties.SetName(button, text + "标签页");
            if (onClick != null) button.Click += onClick;
            return button;
        }

        public static void Mark(Button button, bool active)
        {
            if (button == null) return;
            button.SetResourceReference(Control.BackgroundProperty, active ? "AccentSoft" : "Transparent");
            button.SetResourceReference(Control.BorderBrushProperty, active ? "Accent" : "LineSoft");
            button.SetResourceReference(Control.ForegroundProperty, active ? "Accent" : "Muted");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}
