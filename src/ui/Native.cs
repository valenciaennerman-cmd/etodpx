// dpetoos - pencere gorunumu icin kucuk Win32 dokunuslari
using System;
using System.Runtime.InteropServices;

namespace Dpetoos.Ui
{
    internal static class Native
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;

        private const int CornerRound = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Win11 yuvarlak kose + tema uyumlu pencere kenarligi.</summary>
        public static void ApplyChrome(IntPtr hwnd, bool dark, uint borderBgr)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int corner = CornerRound;
                DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

                int d = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref d, sizeof(int));

                int bc = unchecked((int)borderBgr);
                DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref bc, sizeof(int));
            }
            catch { /* eski Windows: sessizce gecilir */ }
        }

        /// <summary>System.Windows.Media.Color -> DWM icin 0x00BBGGRR.</summary>
        public static uint ToBgr(System.Windows.Media.Color c)
        {
            return (uint)(c.R | (c.G << 8) | (c.B << 16));
        }

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        /// <summary>
        /// Pencere tepsiye indiginde WPF cizim kaynaklarini bellekte tutmanin
        /// anlami kalmaz. Sayfalar geri cagrilabilir; motor tamponlari unmanaged
        /// oldugu icin paket yolu bundan etkilenmez.
        /// </summary>
        public static void TrimWorkingSet()
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                    EmptyWorkingSet(p.Handle);
            }
            catch { }
        }
    }
}
