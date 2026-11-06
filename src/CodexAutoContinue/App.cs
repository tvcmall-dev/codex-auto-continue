using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexAutoContinue.Core;
using CodexAutoContinue.Desktop;

namespace CodexAutoContinue
{
    public sealed class App : Application
    {
        [STAThread]
        public static int Main(string[] args)
        {
            args = args ?? new string[0];
            bool automated = args.Contains("--smoke-test") || args.Contains("--screenshot") || args.Contains("--settings-screenshot");
            string temporaryDirectory = null;
            bool created;
            string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            using (var instance = new Mutex(true, "Local\\CodexAutoContinue-" + user, out created))
            {
                if (!created)
                {
                    if (automated) { Console.Error.WriteLine("Desktop verification cannot run while another instance is open."); return 2; }
                    MessageBox.Show("Codex 续航已在运行，请从系统托盘打开面板。", "Codex 续航"); return 0;
                }
                IApplicationService service = null;
                try
                {
                    var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodexAutoContinue;component/Desktop/Theme.xaml", UriKind.Relative) });
                    string capture = ArgumentValue(args, "--screenshot");
                    string settingsCapture = ArgumentValue(args, "--settings-screenshot");
                    string directory = ArgumentValue(args, "--data-dir");
                    if (directory != null) directory = Path.GetFullPath(directory);
                    else if (automated)
                    {
                        temporaryDirectory = Path.Combine(Path.GetTempPath(), "CodexAutoContinue-Verify-" + Guid.NewGuid().ToString("N"));
                        directory = temporaryDirectory;
                    }
                    service = new ApplicationService(directory);
                    var window = new MainWindow(service);
                    app.MainWindow = window;
                    int exitCode = 0;
                    app.DispatcherUnhandledException += (s, e) =>
                    {
                        e.Handled = true;
                        if (automated) { exitCode = 1; Console.Error.WriteLine("Desktop smoke test failed: " + e.Exception.GetType().Name); window.ExitApplication(); }
                        else MessageBox.Show(window, "操作未能完成，请检查连接与设置后重试。", "Codex 续航", MessageBoxButton.OK, MessageBoxImage.Information);
                    };
                    if (automated)
                    {
                        window.Loaded += async (s, e) =>
                        {
                            await window.RefreshNowAsync();
                            await Task.Delay(1200);
                            if (capture != null) Capture(window, capture);
                            if (settingsCapture != null)
                            {
                                var settings = new SettingsWindow(service) { Owner = window };
                                settings.Show();
                                await Task.Delay(500);
                                Capture(settings, settingsCapture);
                                settings.Close();
                            }
                            window.ExitApplication();
                        };
                    }
                    else if (args.Contains("--tray")) window.Loaded += (s, e) => window.Hide();
                    app.Run(window);
                    return exitCode;
                }
                catch (Exception error)
                {
                    if (automated) { Console.Error.WriteLine("Desktop verification failed: " + error.GetType().Name); return 1; }
                    MessageBox.Show("Codex 续航无法启动，请确认已安装 .NET Framework 4.8，并检查应用数据目录权限。\n\n错误类型：" + error.GetType().Name, "Codex 续航", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                finally
                {
                    service?.Dispose();
                    if (temporaryDirectory != null)
                    {
                        try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }
        private static string ArgumentValue(string[] args, string key)
        {
            int i = Array.IndexOf(args, key);
            if (i < 0) return null;
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Missing value for " + key + ".");
            return args[i + 1];
        }
        private static void Capture(Window window, string path)
        {
            window.UpdateLayout();
            var bounds = VisualTreeHelper.GetDescendantBounds(window);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Window has no rendered content.");
            var target = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                var area = new Rect(0, 0, bounds.Width, bounds.Height);
                context.DrawRectangle(window.Background, null, area);
                context.DrawRectangle(new VisualBrush(window) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds, Stretch = Stretch.Fill }, null, area);
            }
            target.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            string fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            using (var stream = File.Create(fullPath)) encoder.Save(stream);
        }
    }
}
