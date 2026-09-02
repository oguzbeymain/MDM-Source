using System.Windows.Media;

namespace DownloadMuck
{
    /// <summary>
    /// Açık temada yüzey renklerini grimsi tona çeker; yazı ve vurgu renkleri etkilenmez.
    /// </summary>
    public static class LightSurfaceDimmer
    {
        private static readonly Color Floor = Color.FromRgb(0xB6, 0xB6, 0xBC);

        public static int EffectiveBrightness
        {
            get
            {
                int v = ThemeService.PreviewLightBrightness ?? AppSettingsStore.Load().LightThemeBrightness;
                if (v <= 0) v = 100;
                return Math.Clamp(v, 70, 100);
            }
        }

        public static Color DimSurface(Color c)
        {
            if (EffectiveBrightness >= 100)
                return c;

            double amount = (100 - EffectiveBrightness) / 30.0;
            double mix = amount * 0.55;
            return Blend(c, Floor, mix);
        }

        public static Color DimSurface(byte r, byte g, byte b) => DimSurface(Color.FromRgb(r, g, b));

        public static Color DimSurface(byte a, byte r, byte g, byte b)
        {
            var rgb = DimSurface(r, g, b);
            return Color.FromArgb(a, rgb.R, rgb.G, rgb.B);
        }

        private static Color Blend(Color from, Color to, double t) =>
            Color.FromRgb(
                (byte)(from.R + (to.R - from.R) * t),
                (byte)(from.G + (to.G - from.G) * t),
                (byte)(from.B + (to.B - from.B) * t));
    }
}
