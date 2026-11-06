using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexAutoContinue.Desktop
{
    internal static class WindowAppearance
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void ApplyDarkCaption(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                int dark = 1;
                var handle = new WindowInteropHelper(window).Handle;
                if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            };
        }
    }
}
