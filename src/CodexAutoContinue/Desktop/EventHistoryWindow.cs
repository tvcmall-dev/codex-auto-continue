using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.Desktop
{
    internal sealed class EventHistoryWindow : Window
    {
        public EventHistoryWindow(IReadOnlyList<MonitorEvent> events)
        {
            Style = (Style)FindResource(typeof(Window));
            Icon = Application.Current?.MainWindow?.Icon;
            Title = "Codex 续航 · 事件记录"; Width = 850; Height = 620; MinWidth = 650; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowAppearance.ApplyDarkCaption(this);
            var grid = new Grid { Margin = new Thickness(27) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
            header.Children.Add(new TextBlock { Text = "事件记录", FontSize = 24 });
            header.Children.Add(new TextBlock { Text = "本地监控与通知的最近事件 · 时间均为本地时间", Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 10, 0, 0) });
            grid.Children.Add(header);
            var list = new ListBox(); Grid.SetRow(list, 1);
            var template = new DataTemplate();
            var stack = new FrameworkElementFactory(typeof(StackPanel)); stack.SetValue(FrameworkElement.MarginProperty, new Thickness(14, 11, 14, 11));
            var time = new FrameworkElementFactory(typeof(TextBlock)); time.SetBinding(TextBlock.TextProperty, new Binding("TimeLabel")); time.SetResourceReference(TextBlock.StyleProperty, "Eyebrow"); stack.AppendChild(time);
            var message = new FrameworkElementFactory(typeof(TextBlock)); message.SetBinding(TextBlock.TextProperty, new Binding("Message")); message.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); message.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 7, 0, 0)); stack.AppendChild(message);
            template.VisualTree = stack; list.ItemTemplate = template;
            list.ItemsSource = events.OrderByDescending(e => e.TimeUtc).Take(300).Select(e => new EventItem { TimeLabel = e.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  ·  " + e.Kind, Message = e.Message }).ToArray();
            if (list.Items.Count == 0) list.ItemsSource = new[] { new EventItem { TimeLabel = "暂无记录", Message = "观察到新事件或执行操作后，记录会显示在这里。" } };
            grid.Children.Add(list); Content = grid;
        }
    }
}
