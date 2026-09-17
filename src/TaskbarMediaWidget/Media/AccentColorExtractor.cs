using System.Windows.Media.Imaging;
using TaskbarMediaWidget.Core;

namespace TaskbarMediaWidget.Media;

/// <summary>
/// Picks a single representative "vibrant" color out of a piece of cover art, for tinting the
/// widget. Deliberately not a full median-cut/octree quantizer: the source is the already-decoded
/// 96px-wide SMTC thumbnail (~9k pixels), so a weighted hue histogram is both accurate enough for
/// a background tint and cheap enough to run inline on the refresh path.
/// </summary>
internal static class AccentColorExtractor
{
    private const int HueBuckets = 24;          // 15° per bucket
    private const double MinSaturation = 0.18;  // below this it's effectively grey — no usable hue
    private const double MinValue = 0.15;       // near-black pixels carry no tint

    // Note there is deliberately no upper bound on value. An earlier version rejected value > 0.97
    // to skip near-white letterboxing, which silently threw away every fully-bright saturated
    // pixel too — #FF0000, #FF8C00 and #FF00FF all sit at value 1.0, so vivid cover art produced
    // no tint at all. White is already excluded by MinSaturation (it has no hue), which is the
    // correct test for it.

    // Clamps for the color we hand back. Raw cover-art colors are routinely either neon or muddy;
    // both look bad as a taskbar tint, so the hue is kept and the rest is normalized into a band
    // that stays legible against both a light and a dark taskbar.
    private const double OutputMinSaturation = 0.35;
    private const double OutputMaxSaturation = 0.85;
    private const double OutputMinValue = 0.55;
    private const double OutputMaxValue = 0.95;

    /// <summary>
    /// Returns the dominant vibrant color, or null when the art has no usable hue at all
    /// (greyscale covers, solid black/white) — callers should render untinted in that case rather
    /// than inventing a color.
    /// </summary>
    public static System.Windows.Media.Color? TryGetAccent(BitmapSource? source)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            var bgra = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            bgra.Freeze(); // this runs on a thread-pool thread off the SMTC refresh path
            var width = bgra.PixelWidth;
            var height = bgra.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var stride = width * 4;
            var pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);

            var weights = new double[HueBuckets];
            var sumR = new double[HueBuckets];
            var sumG = new double[HueBuckets];
            var sumB = new double[HueBuckets];

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a < 128)
                {
                    continue; // transparent padding around non-square art
                }

                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                var (hue, saturation, value) = ToHsv(r, g, b);

                if (saturation < MinSaturation || value < MinValue)
                {
                    continue;
                }

                // Weight by vibrancy so a small patch of saturated color beats a large dull wash —
                // which is what the eye picks out of a cover too.
                var weight = saturation * value;
                var bucket = (int)(hue / 360.0 * HueBuckets) % HueBuckets;

                weights[bucket] += weight;
                sumR[bucket] += r * weight;
                sumG[bucket] += g * weight;
                sumB[bucket] += b * weight;
            }

            var best = 0;
            for (var i = 1; i < HueBuckets; i++)
            {
                if (weights[i] > weights[best])
                {
                    best = i;
                }
            }

            if (weights[best] <= 0)
            {
                return null;
            }

            var avgR = sumR[best] / weights[best];
            var avgG = sumG[best] / weights[best];
            var avgB = sumB[best] / weights[best];

            var (h, s, v) = ToHsv(avgR, avgG, avgB);
            s = Math.Clamp(s, OutputMinSaturation, OutputMaxSaturation);
            v = Math.Clamp(v, OutputMinValue, OutputMaxValue);

            return FromHsv(h, s, v);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Failed to extract accent color from thumbnail: {ex.Message}");
            return null;
        }
    }

    private static (double Hue, double Saturation, double Value) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue;
        if (delta < double.Epsilon)
        {
            hue = 0;
        }
        else if (max == r)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            hue = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, max <= 0 ? 0 : delta / max, max / 255.0);
    }

    private static System.Windows.Media.Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
