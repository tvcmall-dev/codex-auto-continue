using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using CodexAutoContinue.Core;
using CodexAutoContinue.Updating;

namespace CodexAutoContinue.Desktop
{
    public partial class SettingsWindow : Window
    {
        private readonly IApplicationService service;
        private readonly AppSettings original;
        private readonly IUpdateService updates;
        private bool loaded, closed;
        public SettingsWindow(IApplicationService service, int tab = 0, IUpdateService updates = null)
        {
            this.service = service;
            this.updates = updates;
            var json = new JavaScriptSerializer();
            original = json.Deserialize<AppSettings>(json.Serialize(service.Settings));
            InitializeComponent();
            Icon = Application.Current?.MainWindow?.Icon;
            WindowAppearance.ApplyDarkCaption(this);
            SettingsTabs.SelectedIndex = tab;
            ChineseReply.IsChecked = original.ReplyMode == "zh";
            EnglishReply.IsChecked = original.ReplyMode == "en";
            CustomReply.IsChecked = original.ReplyMode == "custom";
            CustomText.Text = original.CustomMessage ?? "";
            NetworkDelay.Text = original.NetworkDelaySeconds.ToString(CultureInfo.InvariantCulture);
            MaxAttempts.Text = original.MaxAttempts.ToString(CultureInfo.InvariantCulture);
            CodexHome.Text = original.CodexHome ?? "";
            CliPath.Text = original.CliPath ?? "";
            PollSeconds.Text = original.PollSeconds.ToString(CultureInfo.InvariantCulture);
            StartWithWindows.IsChecked = original.StartWithWindows;
            CloseToTray.IsChecked = original.CloseToTray;
            AutoCheckUpdates.IsChecked = original.AutoCheckUpdates;
            AutoInstallUpdates.IsChecked = original.AutoInstallUpdates;
            IncludePrereleaseUpdates.IsChecked = original.IncludePrereleaseUpdates;
            var n = original.Notifications ?? new NotificationSettings();
            DingTalkProvider.IsChecked = n.Provider == "DingTalk";
            FeishuProvider.IsChecked = n.Provider == "Feishu";
            WeComProvider.IsChecked = n.Provider == "WeCom";
            GenericProvider.IsChecked = n.Provider == "Generic";
            if (DingTalkProvider.IsChecked != true && FeishuProvider.IsChecked != true && WeComProvider.IsChecked != true && GenericProvider.IsChecked != true) DingTalkProvider.IsChecked = true;
            ChannelName.Text = n.Name ?? "";
            WebhookUrl.Password = n.WebhookUrl ?? "";
            SigningSecret.Password = n.Secret ?? "";
            EnableNotifications.IsChecked = n.Enabled;
            FailureEvent.IsChecked = n.OnFailure;
            RecoveryEvent.IsChecked = n.OnRecovery;
            ExhaustedEvent.IsChecked = n.OnExhausted;
            ConnectionDetails.Text = service.ConnectionStatus;
            DataPath.Text = "应用数据目录：" + service.DataDirectory;
            loaded = true;
            UpdateReplyPreview();
            UpdateProviderFields();
            if (updates != null) updates.Changed += OnUpdateChanged;
            Closed += (s, e) => { closed = true; if (updates != null) updates.Changed -= OnUpdateChanged; };
            UpdateUpdateStatus();
        }
        private void ReplyChanged(object sender, RoutedEventArgs e) { if (loaded) UpdateReplyPreview(); }
        private void CustomChanged(object sender, TextChangedEventArgs e) { if (loaded) UpdateReplyPreview(); }
        private void UpdateReplyPreview()
        {
            bool custom = CustomReply.IsChecked == true;
            CustomGroup.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            MessagePreview.Text = custom ? CustomText.Text.Trim() : EnglishReply.IsChecked == true ? "continue" : "继续";
            if (custom && MessagePreview.Text.Length == 0) MessagePreview.Text = "请输入自定义内容";
        }
        private void ProviderChanged(object sender, RoutedEventArgs e)
        {
            if (!loaded) return;
            SigningSecret.Password = "";
            UpdateProviderFields();
            TestResult.Text = "已切换平台，签名密钥已清空，请确认接收地址。";
        }
        private void UpdateProviderFields() { SecretGroup.Visibility = DingTalkProvider.IsChecked == true || FeishuProvider.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        private static int ParseInteger(TextBox input, string name, int minimum, int maximum)
        {
            int value;
            if (!int.TryParse(input.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum) throw new ArgumentException(name + "应为 " + minimum + " 到 " + maximum + " 之间的整数。");
            return value;
        }
        private AppSettings ReadSettings(bool validatePolicy)
        {
            var json = new JavaScriptSerializer();
            var result = json.Deserialize<AppSettings>(json.Serialize(original));
            result.GlobalMonitoring = service.Settings.GlobalMonitoring;
            if (validatePolicy)
            {
                result.ReplyMode = CustomReply.IsChecked == true ? "custom" : EnglishReply.IsChecked == true ? "en" : "zh";
                result.CustomMessage = CustomText.Text.Trim();
                result.EffectiveMessage();
                result.NetworkDelaySeconds = ParseInteger(NetworkDelay, "首次重试等待", 1, 300);
                result.MaxAttempts = ParseInteger(MaxAttempts, "最多续接次数", 1, 20);
                result.PollSeconds = ParseInteger(PollSeconds, "刷新间隔", 1, 60);
                result.CodexHome = CodexHome.Text.Trim();
                if (result.CodexHome.Length == 0) throw new ArgumentException("请填写 Codex 数据目录。");
                result.CliPath = CliPath.Text.Trim();
                result.CloseToTray = CloseToTray.IsChecked == true;
                result.StartWithWindows = StartWithWindows.IsChecked == true;
                result.AutoCheckUpdates = AutoCheckUpdates.IsChecked == true;
                result.AutoInstallUpdates = AutoInstallUpdates.IsChecked == true;
                result.IncludePrereleaseUpdates = IncludePrereleaseUpdates.IsChecked == true;
            }
            result.Notifications = new NotificationSettings
            {
                Enabled = EnableNotifications.IsChecked == true,
                Provider = FeishuProvider.IsChecked == true ? "Feishu" : WeComProvider.IsChecked == true ? "WeCom" : GenericProvider.IsChecked == true ? "Generic" : "DingTalk",
                Name = ChannelName.Text.Trim(), WebhookUrl = WebhookUrl.Password.Trim(),
                Secret = SecretGroup.Visibility == Visibility.Visible ? SigningSecret.Password.Trim() : "",
                OnFailure = FailureEvent.IsChecked == true, OnRecovery = RecoveryEvent.IsChecked == true, OnExhausted = ExhaustedEvent.IsChecked == true
            };
            return result;
        }
        private async void SaveClick(object sender, RoutedEventArgs e)
        {
            SaveButton.IsEnabled = false;
            try
            {
                var settings = ReadSettings(true);
                await service.SaveSettingsAsync(settings);
                DialogResult = true;
            }
            catch (Exception error) { Feedback.Text = error.Message; }
            finally { SaveButton.IsEnabled = true; }
        }
        private async void TestClick(object sender, RoutedEventArgs e)
        {
            TestButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            TestResult.Text = "正在发送真实测试通知…";
            try { TestResult.Text = await service.TestWebhookAsync(ReadSettings(false)); }
            catch (Exception error) { TestResult.Text = error.Message; }
            finally { TestButton.IsEnabled = true; SaveButton.IsEnabled = true; }
        }
        private void CancelClick(object sender, RoutedEventArgs e) { DialogResult = false; }
        private void OnUpdateChanged()
        {
            if (!closed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (!closed) UpdateUpdateStatus(); }));
        }
        private void UpdateUpdateStatus()
        {
            CurrentVersion.Text = updates == null ? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3) : updates.CurrentVersion;
            AvailableVersion.Text = string.IsNullOrWhiteSpace(updates?.AvailableVersion) ? "尚未发现新版本" : updates.AvailableVersion;
            UpdateStatus.Text = updates?.StatusText ?? "启动验证模式未运行自动升级服务。";
            UpdateProgress.Value = Math.Max(0, Math.Min(100, updates?.Progress ?? 0));
            UpdateProgress.Visibility = updates != null && (updates.IsBusy || updates.IsReady) ? Visibility.Visible : Visibility.Collapsed;
            CheckUpdateButton.IsEnabled = updates != null && !updates.IsBusy;
            InstallUpdateButton.IsEnabled = updates != null && updates.IsReady && !updates.IsBusy;
        }
        private async void CheckUpdatesClick(object sender, RoutedEventArgs e)
        {
            if (updates == null) return;
            CheckUpdateButton.IsEnabled = false;
            try { await updates.CheckAsync(true); }
            catch { if (!closed) Feedback.Text = "检查更新暂不可用，请稍后重试。"; }
            finally { if (!closed) UpdateUpdateStatus(); }
        }
        private async void InstallUpdateClick(object sender, RoutedEventArgs e)
        {
            if (updates == null || !updates.IsReady) return;
            InstallUpdateButton.IsEnabled = false; SaveButton.IsEnabled = false;
            try
            {
                await service.SaveSettingsAsync(ReadSettings(true));
                var dispatcher = Dispatcher;
                var owner = Owner;
                DialogResult = true;
                _ = dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await updates.InstallAsync(); }
                    catch { if (!dispatcher.HasShutdownStarted) MessageBox.Show(owner, "升级未能启动，请在应用升级中查看状态后重试。", "应用升级", MessageBoxButton.OK, MessageBoxImage.Information); }
                }));
            }
            catch (Exception error) { Feedback.Text = error.Message; }
            finally { if (!closed) { SaveButton.IsEnabled = true; UpdateUpdateStatus(); } }
        }
    }
}
