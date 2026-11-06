using System;
using System.Windows;
using System.Windows.Media;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.Desktop
{
    public sealed class StateGlyph : FrameworkElement
    {
        public static readonly DependencyProperty StateProperty = DependencyProperty.Register("State", typeof(SessionState), typeof(StateGlyph), new FrameworkPropertyMetadata(SessionState.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));
        public SessionState State { get { return (SessionState)GetValue(StateProperty); } set { SetValue(StateProperty, value); } }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double scale = Math.Max(0.01, Math.Min(ActualWidth, ActualHeight) / 100);
            dc.PushTransform(new TranslateTransform((ActualWidth - 100 * scale) / 2, (ActualHeight - 100 * scale) / 2));
            dc.PushTransform(new ScaleTransform(scale, scale));
            var color = StatePresentation.Color(State);
            var line = new Pen(new SolidColorBrush(color), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var dim = new Pen(new SolidColorBrush(Color.FromArgb(74, color.R, color.G, color.B)), 1.2);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(16, color.R, color.G, color.B)), null, new Point(50, 50), 46, 46);
            dc.DrawEllipse(null, dim, new Point(50, 50), 37, 37);
            dc.DrawLine(dim, new Point(5, 50), new Point(17, 50));
            dc.DrawLine(dim, new Point(83, 50), new Point(95, 50));
            if (State == SessionState.Running || State == SessionState.InternalRetry)
            {
                dc.DrawGeometry(null, line, Geometry.Parse("M24,45 C29,18 71,18 77,46 M76,39 L77,47 L69,44 M77,57 C70,83 30,83 23,57 M24,64 L23,56 L31,59"));
                dc.DrawEllipse(new SolidColorBrush(color), null, new Point(50, 50), 7, 7);
                if (State == SessionState.InternalRetry) dc.DrawGeometry(null, line, Geometry.Parse("M43,47 L50,41 L57,47 M50,42 L50,59"));
                else dc.DrawEllipse(null, dim, new Point(50, 50), 16, 16);
            }
            else if (State == SessionState.NetworkFailure)
            {
                dc.DrawGeometry(null, line, Geometry.Parse("M24,32 C38,19 62,19 76,32 M31,44 C38,38 45,36 50,37 M59,40 L68,45 M40,57 L47,53 M56,56 L61,59 M29,76 L72,25"));
                dc.DrawEllipse(new SolidColorBrush(color), null, new Point(50, 70), 3, 3);
            }
            else if (State == SessionState.QuotaWait)
            {
                dc.DrawGeometry(null, line, Geometry.Parse("M34,23 L66,23 M34,77 L66,77 M38,24 C38,41 46,43 50,50 C54,57 62,59 62,76 M62,24 C62,41 54,43 50,50 C46,57 38,59 38,76 M42,67 L50,58 L58,67 Z"));
            }
            else if (State == SessionState.Pending)
            {
                dc.DrawGeometry(null, line, Geometry.Parse("M25,47 L75,28 L60,76 L48,57 Z M48,57 L75,28 M29,64 L21,72 M39,72 L33,80"));
            }
            else if (State == SessionState.Completed)
            {
                dc.DrawEllipse(null, line, new Point(50, 50), 26, 26);
                dc.DrawGeometry(null, line, Geometry.Parse("M37,50 L46,59 L65,40"));
            }
            else if (State == SessionState.WaitingApproval || State == SessionState.Stopped)
            {
                dc.DrawRoundedRectangle(null, line, new Rect(28, 26, 44, 48), 12, 12);
                dc.DrawLine(new Pen(new SolidColorBrush(color), 5), new Point(43, 40), new Point(43, 60));
                dc.DrawLine(new Pen(new SolidColorBrush(color), 5), new Point(57, 40), new Point(57, 60));
            }
            else if (State == SessionState.Failed)
            {
                dc.DrawGeometry(null, line, Geometry.Parse("M50,22 L80,74 L20,74 Z M50,39 L50,55"));
                dc.DrawEllipse(new SolidColorBrush(color), null, new Point(50, 64), 2, 2);
            }
            else
            {
                dc.DrawGeometry(null, line, Geometry.Parse("M30,35 L43,35 M57,35 L70,35 M30,35 L30,65 L43,65 M70,35 L70,65 L57,65 M45,50 L55,50"));
                dc.DrawEllipse(null, dim, new Point(50, 50), 15, 15);
            }
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(84, 27), 2.6, 2.6);
            dc.Pop(); dc.Pop();
        }
    }

    public static class StatePresentation
    {
        public static string Label(SessionState state)
        {
            switch (state)
            {
                case SessionState.Running: return "执行中";
                case SessionState.InternalRetry: return "内部重试";
                case SessionState.WaitingApproval: return "等待用户确认";
                case SessionState.NetworkFailure: return "网络异常";
                case SessionState.QuotaWait: return "等待额度恢复";
                case SessionState.Pending: return "等待恢复确认";
                case SessionState.Completed: return "已完成";
                case SessionState.Stopped: return "已停止";
                case SessionState.Failed: return "需要处理";
                default: return "状态待确认";
            }
        }
        public static Color Color(SessionState state)
        {
            switch (state)
            {
                case SessionState.Running: return ColorsEx.Parse("#B5D49D");
                case SessionState.InternalRetry: return ColorsEx.Parse("#8BBDBF");
                case SessionState.WaitingApproval: return ColorsEx.Parse("#A1B8D1");
                case SessionState.NetworkFailure: return ColorsEx.Parse("#E3A77D");
                case SessionState.QuotaWait: return ColorsEx.Parse("#D7C88E");
                case SessionState.Pending: return ColorsEx.Parse("#8DC8B1");
                case SessionState.Completed: return ColorsEx.Parse("#9AB195");
                case SessionState.Stopped: return ColorsEx.Parse("#9AA9A4");
                case SessionState.Failed: return ColorsEx.Parse("#DF9B8B");
                default: return ColorsEx.Parse("#91A9A1");
            }
        }
        public static bool NeedsAttention(SessionState state)
        {
            return state == SessionState.NetworkFailure || state == SessionState.QuotaWait || state == SessionState.Failed || state == SessionState.Pending || state == SessionState.WaitingApproval;
        }
    }
    internal static class ColorsEx { internal static Color Parse(string value) { return (Color)ColorConverter.ConvertFromString(value); } }
}
