// etodpx - renk paleti
// Tek tema var: karanlik. Aydinlik mod bilerek kaldirildi.
using System.Windows.Media;

namespace Etodpx.Ui
{
    internal static class Palette
    {
        public static readonly Color Bg = Hex("#0B0F16");
        public static readonly Color Surface = Hex("#141B26");
        public static readonly Color Border = Hex("#1F2836");
        public static readonly Color Text = Hex("#E8EDF5");
        public static readonly Color SubText = Hex("#7D8798");
        public static readonly Color Accent = Hex("#2BE0A8");
        public static readonly Color Idle = Hex("#2A3444");
        public static readonly Color Danger = Hex("#FF6B6B");
        public static readonly Color Hover = Hex("#1C2534");

        private static Color Hex(string s)
        {
            return (Color)ColorConverter.ConvertFromString(s);
        }
    }
}
