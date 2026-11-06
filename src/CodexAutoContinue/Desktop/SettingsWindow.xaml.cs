using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.Desktop
{
    public partial class SettingsWindow : Window
    {
        private readonly IApplicationService service;
        private readonly AppSettings original;
        private bool loaded;
        public SettingsWindow(IApplicationService service, int tab = 0)
        {
            this.service = service;
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
    }
}
