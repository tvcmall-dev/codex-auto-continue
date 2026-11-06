using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAutoContinue.Core;
using CodexAutoContinue.Updating;
using Forms = System.Windows.Forms;

namespace CodexAutoContinue.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly IApplicationService service;
        private readonly IUpdateService updates;
        private readonly ObservableCollection<SessionItem> items = new ObservableCollection<SessionItem>();
        private readonly ICollectionView view;
        private readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly Forms.NotifyIcon tray;
        private readonly System.Drawing.Icon trayIcon;
        private bool refreshing, exiting, continuing, updateTickRunning;
        private int tick, updateTick;
        private string filter = "all";
        public MainWindow(IApplicationService service, IUpdateService updates = null)
        {
            this.service = service;
            this.updates = updates;
            InitializeComponent();
            Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
            view = CollectionViewSource.GetDefaultView(items);
            view.Filter = FilterItem;
            view.SortDescriptions.Add(new SortDescription("LastActivityUtc", ListSortDirection.Descending));
            SessionList.ItemsSource = view;
            trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "Codex 续航", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示面板", null, (s, e) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
            menu.Items.Add("暂停 / 恢复监控", null, (s, e) => Dispatcher.BeginInvoke(new Action(() => service.SetGlobalMonitoring(!service.Settings.GlobalMonitoring))));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出程序", null, (s, e) => Dispatcher.BeginInvoke(new Action(ExitApplication)));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (s, e) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
            service.Changed += OnServiceChanged;
            if (updates != null)
            {
                updates.Changed += OnUpdateChanged;
                updates.IsUiIdle = () => ReadUiValue(() => IsLoaded && !exiting && !refreshing && !continuing && OwnedWindows.Count == 0);
                updates.IsWindowVisible = () => ReadUiValue(() => IsVisible && WindowState != WindowState.Minimized);
                updates.RestartRequested = () => { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(ExitApplication)); };
            }
            clock.Tick += ClockTick;
            Loaded += async (s, e) => { clock.Start(); await RefreshNowAsync(); if (updates != null) await CheckUpdatesAsync(); };
            Closing += OnClosing;
            Closed += (s, e) =>
            {
                clock.Stop(); service.Changed -= OnServiceChanged;
                if (updates != null) { updates.Changed -= OnUpdateChanged; updates.IsUiIdle = null; updates.IsWindowVisible = null; updates.RestartRequested = null; }
                tray.Visible = false; tray.Dispose(); trayIcon.Dispose();
            };
            UpdateView();
            UpdateUpdateStatus();
        }
        private bool ReadUiValue(Func<bool> read)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return false;
            return Dispatcher.CheckAccess() ? read() : Dispatcher.Invoke(read);
        }
        private async void ClockTick(object sender, EventArgs e)
        {
            ClockText.Text = DateTime.Now.ToString("yyyy.MM.dd  HH:mm:ss");
            UpdateDetails();
            if (++tick >= Math.Max(1, service.Settings.PollSeconds)) { tick = 0; await RefreshNowAsync(); }
            if (updates == null || ++updateTick < 30 || updateTickRunning) return;
            updateTick = 0; updateTickRunning = true;
            try { await updates.TickAsync(); }
            catch { UpdateStatusButton.Content = "升级检查暂不可用"; }
            finally { updateTickRunning = false; }
        }
        private async Task CheckUpdatesAsync()
        {
            try { await updates.CheckAsync(false); }
            catch { UpdateStatusButton.Content = "升级检查暂不可用"; }
        }
        private void OnUpdateChanged() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(UpdateUpdateStatus)); }
        private void UpdateUpdateStatus()
        {
            if (updates == null) { UpdateStatusButton.Content = "应用升级"; UpdateStatusButton.ToolTip = "打开应用升级设置"; return; }
            string status = updates.StatusText ?? "等待检查更新";
            UpdateStatusButton.Content = updates.IsReady ? "新版本已就绪 · " + updates.AvailableVersion : updates.IsBusy ? "应用升级 · " + Math.Max(0, updates.Progress) + "%" : "v" + updates.CurrentVersion + " · 应用升级";
            UpdateStatusButton.ToolTip = status;
        }
        public async Task RefreshNowAsync()
        {
            if (refreshing) return;
            refreshing = true;
            try { await service.RefreshAsync(); UpdateView(); }
            catch { FooterStatus.Text = "读取失败，请检查连接与应用设置。"; }
            finally { refreshing = false; }
        }
        private void OnServiceChanged() { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(UpdateView)); }
        private bool FilterItem(object value)
        {
            var item = (SessionItem)value;
            if (filter == "active" && item.State != SessionState.Running && item.State != SessionState.InternalRetry) return false;
            if (filter == "attention" && !StatePresentation.NeedsAttention(item.State)) return false;
            string query = SearchBox == null ? "" : SearchBox.Text.Trim();
            return query.Length == 0 || ((item.Title ?? "") + " " + item.Workspace + " " + item.StateLabel).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        private void UpdateView()
        {
            string selected = (SessionList.SelectedItem as SessionItem)?.ThreadId;
            var records = service.Sessions.ToArray();
            var ids = new HashSet<string>(records.Select(r => r.ThreadId));
            for (int i = items.Count - 1; i >= 0; i--) if (!ids.Contains(items[i].ThreadId)) items.RemoveAt(i);
            foreach (var record in records.OrderByDescending(r => r.LastActivityUtc))
            {
                var item = items.FirstOrDefault(i => i.ThreadId == record.ThreadId);
                if (item == null) { item = new SessionItem(); items.Add(item); }
                item.Update(record);
            }
            RefreshFilter();
            if (selected != null) SessionList.SelectedItem = items.FirstOrDefault(i => i.ThreadId == selected && FilterItem(i));
            if (SessionList.SelectedItem == null && !view.IsEmpty) SessionList.SelectedIndex = 0;
            RunningCount.Text = records.Count(r => r.State == SessionState.Running || r.State == SessionState.InternalRetry).ToString("00");
            AttentionCount.Text = records.Count(r => StatePresentation.NeedsAttention(r.State)).ToString("00");
            MonitoredCount.Text = records.Count(r => r.Monitoring).ToString("00");
            TotalCount.Text = "共 " + records.Length + " 个本地会话";
            GlobalButton.Content = service.Settings.GlobalMonitoring ? "●  暂停监控" : "○  恢复监控";
            ConnectionBanner.Text = service.ConnectionStatus ?? "连接状态待确认";
            FooterStatus.Text = (service.Settings.GlobalMonitoring ? "监控已开启" : "监控已暂停") + "  ·  " + (service.IsConnected ? "本地数据已连接" : "连接待确认") + "  ·  " + (service.NotificationStatus ?? "通知未配置");
            tray.Text = "Codex 续航 · 执行 " + RunningCount.Text + " / 关注 " + AttentionCount.Text;
            UpdateDetails();
        }
        private void UpdateDetails()
        {
            var item = SessionList.SelectedItem as SessionItem;
            var record = item == null ? null : service.Sessions.FirstOrDefault(s => s.ThreadId == item.ThreadId);
            if (record == null)
            {
                DetailGlyph.State = SessionState.Unknown;
                DetailState.Text = "等待选择";
                DetailTitle.Text = "选择一个会话";
                DetailReason.Text = "在列表中选择会话，查看状态、重试计划与最近事件。";
                DetailAttempts.Text = "—"; DetailRetry.Text = "—"; DetailId.Text = "—";
                ContinueButton.IsEnabled = false; ContinueButton.Content = "立即继续";
                ContinueHint.Text = "发送后需等待真实执行证据，再确认恢复。";
                EventList.ItemsSource = null;
                return;
            }
            DetailGlyph.State = record.State;
            DetailState.Text = StatePresentation.Label(record.State);
            DetailState.Foreground = new SolidColorBrush(StatePresentation.Color(record.State));
            DetailTitle.Text = string.IsNullOrWhiteSpace(record.Title) ? "未命名会话" : record.Title;
            DetailReason.Text = string.IsNullOrWhiteSpace(record.Reason) ? "本地保存记录不等同于实时运行状态。观察到新事件后更新。" : record.Reason;
            DetailAttempts.Text = record.Attempts + " / " + service.Settings.MaxAttempts + " 次";
            DetailRetry.Text = !record.Monitoring ? "单会话监控未启用" : !service.Settings.GlobalMonitoring ? "全局监控已暂停" : record.NextRetryUtc.HasValue ? record.NextRetryUtc.Value.ToLocalTime().ToString("HH:mm:ss") + "（" + Math.Max(0, (int)(record.NextRetryUtc.Value - DateTime.UtcNow).TotalSeconds) + " 秒）" : "不触发自动续接";
            DetailId.Text = record.ThreadId;
            ContinueButton.IsEnabled = !record.AwaitingConfirmation && record.FreshFailure && record.Attempts < service.Settings.MaxAttempts && (record.State == SessionState.NetworkFailure || record.State == SessionState.QuotaWait) && service.IsConnected;
            ContinueButton.Content = record.AwaitingConfirmation || record.State == SessionState.Pending ? "已发送 · 等待确认" : "立即继续";
            ContinueHint.Text = record.AwaitingConfirmation ? "消息已提交。只有观察到后续实际执行，才确认恢复；不会重复发送。" : !record.FreshFailure ? "历史状态不会触发续接。仅对启动后新确认的可恢复异常开放操作。" : "向原会话发送已保存消息，并等待真实执行证据。";
            EventList.ItemsSource = (record.Events ?? new List<MonitorEvent>()).OrderByDescending(e => e.TimeUtc).Take(12).Select(e => new EventItem { TimeLabel = e.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm:ss"), Message = e.Message }).ToList();
        }
        private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
        public void ExitApplication() { exiting = true; Close(); }
        private void OnClosing(object sender, CancelEventArgs e) { if (!exiting && service.Settings.CloseToTray) { e.Cancel = true; Hide(); } }
        private void TitleDrag(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) MaximizeClick(sender, e); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void MinimizeClick(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        private void MaximizeClick(object sender, RoutedEventArgs e) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        private void CloseClick(object sender, RoutedEventArgs e) { Close(); }
        private async void RefreshClick(object sender, RoutedEventArgs e) { await RefreshNowAsync(); }
        private void RefreshFilter()
        {
            view.Refresh();
            EmptyState.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = items.Count == 0 ? "尚未发现本地桌面会话" : "没有符合条件的会话";
            AllFilter.Background = new SolidColorBrush(ColorsEx.Parse(filter == "all" ? "#466249" : "#25382E"));
            ActiveFilter.Background = new SolidColorBrush(ColorsEx.Parse(filter == "active" ? "#466249" : "#25382E"));
            AttentionFilter.Background = new SolidColorBrush(ColorsEx.Parse(filter == "attention" ? "#466249" : "#25382E"));
            UpdateDetails();
        }
        private void SearchChanged(object sender, TextChangedEventArgs e) { if (view != null) RefreshFilter(); }
        private void SessionSelected(object sender, SelectionChangedEventArgs e) { UpdateDetails(); }
        private void AllClick(object sender, RoutedEventArgs e) { filter = "all"; RefreshFilter(); }
        private void ActiveClick(object sender, RoutedEventArgs e) { filter = "active"; RefreshFilter(); }
        private void AttentionClick(object sender, RoutedEventArgs e) { filter = "attention"; RefreshFilter(); }
        private void OverviewClick(object sender, RoutedEventArgs e) { filter = "all"; PageTitle.Text = "会话总览"; RefreshFilter(); }
        private void HistoryClick(object sender, RoutedEventArgs e)
        {
            var window = new EventHistoryWindow(service.Events) { Owner = this };
            window.ShowDialog();
        }
        private void PolicyClick(object sender, RoutedEventArgs e) { OpenSettings(0); }
        private void NotificationsClick(object sender, RoutedEventArgs e) { OpenSettings(1); }
        private void ConnectionClick(object sender, RoutedEventArgs e) { OpenSettings(2); }
        private void UpdateClick(object sender, RoutedEventArgs e) { OpenSettings(3); }
        private void OpenSettings(int tab) { var window = new SettingsWindow(service, tab, updates) { Owner = this }; window.ShowDialog(); UpdateView(); UpdateUpdateStatus(); }
        private void GlobalClick(object sender, RoutedEventArgs e) { service.SetGlobalMonitoring(!service.Settings.GlobalMonitoring); UpdateView(); }
        private void SessionMonitoringClick(object sender, RoutedEventArgs e)
        {
            var box = (CheckBox)sender;
            var item = box.DataContext as SessionItem;
            if (item != null) { service.SetSessionMonitoring(item.ThreadId, box.IsChecked == true); UpdateView(); }
        }
        private async void ContinueClick(object sender, RoutedEventArgs e)
        {
            var item = SessionList.SelectedItem as SessionItem;
            if (item == null) return;
            continuing = true;
            ContinueButton.IsEnabled = false;
            try { await service.ContinueAsync(item.ThreadId); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法续接", MessageBoxButton.OK, MessageBoxImage.Information); }
            finally { continuing = false; }
            UpdateView();
        }
        private void DataFolderClick(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = service.DataDirectory, UseShellExecute = true }); }
            catch { FooterStatus.Text = "无法打开数据目录：" + service.DataDirectory; }
        }
    }
    internal sealed class SessionItem : INotifyPropertyChanged
    {
        public string ThreadId { get; private set; }
        public string Title { get; private set; }
        public string Workspace { get; private set; }
        public string Subtitle { get; private set; }
        public SessionState State { get; private set; }
        public string StateLabel { get { return StatePresentation.Label(State); } }
        public Brush StateBrush { get { return new SolidColorBrush(StatePresentation.Color(State)); } }
        public string Timing { get; private set; }
        public DateTime LastActivityUtc { get; private set; }
        public bool Monitoring { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;
        public void Update(SessionRecord s)
        {
            ThreadId = s.ThreadId; Title = string.IsNullOrWhiteSpace(s.Title) ? "未命名会话" : s.Title; Workspace = s.Workspace; State = s.State; Monitoring = s.Monitoring; LastActivityUtc = s.LastActivityUtc;
            Subtitle = string.IsNullOrWhiteSpace(s.Workspace) ? "本地 Codex 会话" : s.Workspace;
            Timing = s.AwaitingConfirmation ? "不重复发送" : s.NextRetryUtc.HasValue && s.Monitoring ? "计划 " + s.NextRetryUtc.Value.ToLocalTime().ToString("HH:mm:ss") : s.LastActivityUtc == default(DateTime) ? "等待新事件" : s.LastActivityUtc.ToLocalTime().ToString("MM-dd HH:mm");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
    internal sealed class EventItem { public string TimeLabel { get; set; } public string Message { get; set; } }
}
