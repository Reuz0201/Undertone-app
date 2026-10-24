using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Undertone.UI
{
    /// <summary>
    /// Акцентный цвет из обложки: самый «живой» оттенок (насыщенный и не тёмный), приведённый к яркости,
    /// которая читается на тёмном фоне. Серые обложки дают нейтральный светлый графит.
    /// </summary>
    public static class CoverPalette
    {
        public static readonly Color Neutral = Color.FromRgb(0xBF, 0xC3, 0xD0);

        /// <summary>Цвет из картинки (уже загруженной). null — картинку не прочитать.</summary>
        public static Color? FromImage(BitmapSource source)
        {
            try
            {
                // уменьшаем до ~32 px — для оттенка больше не нужно
                var scale = 32.0 / Math.Max(source.PixelWidth, source.PixelHeight);
                BitmapSource small = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
                var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                var stride = bgra.PixelWidth * 4;
                var pixels = new byte[stride * bgra.PixelHeight];
                bgra.CopyPixels(pixels, stride, 0);
                var (r, g, b) = Pick(pixels);
                return Color.FromRgb(r, g, b);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }

        private const int Bins = 24;

        /// <summary>Пиксели BGRA → акцент (r, g, b). Отдельно от WPF — для тестов.</summary>
        internal static (byte R, byte G, byte B) Pick(ReadOnlySpan<byte> bgra)
        {
            Span<double> weight = stackalloc double[Bins];
            Span<double> sumR = stackalloc double[Bins];
            Span<double> sumG = stackalloc double[Bins];
            Span<double> sumB = stackalloc double[Bins];
            double total = 0;
            int count = 0;

            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                if (bgra[i + 3] < 128) continue;
                double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
                count++;
                var (h, s, v) = ToHsv(r, g, b);
                if (v < 0.18 || s < 0.12) continue; // тени и серое оттенка не задают
                var w = s * s * (0.35 + v);
                var bin = (int)(h / 360.0 * Bins) % Bins;
                weight[bin] += w;
                sumR[bin] += r * w;
                sumG[bin] += g * w;
                sumB[bin] += b * w;
                total += w;
            }

            // почти нет цвета (ч/б, графит) — спокойный нейтральный
            if (count == 0 || total / count < 0.025) return (Neutral.R, Neutral.G, Neutral.B);

            // соседние оттенки считаем вместе, чтобы градиент не дробился на мелкие корзины
            int best = 0;
            double bestW = -1;
            for (int i = 0; i < Bins; i++)
            {
                var w = weight[i] + 0.5 * (weight[(i + Bins - 1) % Bins] + weight[(i + 1) % Bins]);
                if (w > bestW) { bestW = w; best = i; }
            }
            double wr = 0, wg = 0, wb = 0, ww = 0;
            for (int d = -1; d <= 1; d++)
            {
                var k = (best + d + Bins) % Bins;
                var f = d == 0 ? 1.0 : 0.5;
                wr += sumR[k] * f; wg += sumG[k] * f; wb += sumB[k] * f; ww += weight[k] * f;
            }
            var (hue, sat, _) = ToHsl(wr / ww, wg / ww, wb / ww);
            // приглушаем кислотное и вытягиваем тёмное: акцент должен быть мягким и читаться на #0D0E12
            sat = Math.Clamp(sat, 0.32, 0.72);
            const double light = 0.70;
            var (rr, gg, bb) = FromHsl(hue, sat, light);
            return (ToByte(rr), ToByte(gg), ToByte(bb));
        }

        private static byte ToByte(double x) => (byte)Math.Clamp(Math.Round(x * 255), 0, 255);

        private static (double H, double S, double V) ToHsv(double r, double g, double b)
        {
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var d = max - min;
            return (Hue(r, g, b, max, d), max <= 0 ? 0 : d / max, max);
        }

        private static (double H, double S, double L) ToHsl(double r, double g, double b)
        {
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var d = max - min;
            var l = (max + min) / 2;
            var s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
            return (Hue(r, g, b, max, d), s, l);
        }

        private static double Hue(double r, double g, double b, double max, double d)
        {
            if (d <= 0) return 0;
            double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
            return h < 0 ? h + 360 : h;
        }

        private static (double R, double G, double B) FromHsl(double h, double s, double l)
        {
            var c = (1 - Math.Abs(2 * l - 1)) * s;
            var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            var m = l - c / 2;
            var (r, g, b) = h switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            return (r + m, g + m, b + m);
        }
    }
}
