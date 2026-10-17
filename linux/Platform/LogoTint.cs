using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Dingus.Services;

/// <summary>A dark shade of a logo's main colour, for the tile behind it. White, grey or black logos get none.</summary>
public static class LogoTint
{
    static readonly Dictionary<string, IBrush> Cache = new();

    public static IBrush For(string url, Bitmap bmp)
    {
        if (url == null || bmp == null) return null;
        lock (Cache) if (Cache.TryGetValue(url, out var hit)) return hit;
        IBrush brush = null;
        try { brush = Compute(bmp); } catch { }
        lock (Cache) { if (Cache.Count > 4000) Cache.Clear(); Cache[url] = brush; }
        return brush;
    }

    static IBrush Compute(Bitmap b)
    {
        int w = b.PixelSize.Width, h = b.PixelSize.Height;
        if (w < 2 || h < 2) return null;
        int stride = w * 4;
        var px = new byte[stride * h];
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try { b.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, stride); }
        finally { handle.Free(); }
        bool bgra = b.Format == null || b.Format.Value == Avalonia.Platform.PixelFormat.Bgra8888;

        var bins = new double[36];
        var sumR = new double[36]; var sumG = new double[36]; var sumB = new double[36];
        bool transparentBits = false;
        int counted = 0, total = 0;
        int step = Math.Max(1, (w * h) / 6000);
        for (int i = 0; i < w * h; i += step)
        {
            int o = i * 4;
            byte c0 = px[o], g = px[o + 1], c2 = px[o + 2], a = px[o + 3];
            byte r = bgra ? c2 : c0, bl = bgra ? c0 : c2;
            if (a < 160) { transparentBits = true; continue; }
            total++;
            double max = Math.Max(r, Math.Max(g, bl)) / 255.0, min = Math.Min(r, Math.Min(g, bl)) / 255.0;
            double l = (max + min) / 2, d = max - min;
            if (d < 0.18 || l < 0.12 || l > 0.92) continue;
            double s = d / (1 - Math.Abs(2 * l - 1));
            if (s < 0.3) continue;
            int bin = (int)(Hue(r / 255.0, g / 255.0, bl / 255.0, max, d) / 10) % 36;
            bins[bin] += s; sumR[bin] += r; sumG[bin] += g; sumB[bin] += bl;
            counted++;
        }
        if (total == 0 || counted < Math.Max(8, total * (transparentBits ? 0.06 : 0.15))) return null;
        double Score(int i) => bins[i] + 0.5 * (bins[(i + 35) % 36] + bins[(i + 1) % 36]);
        int best = 0;
        for (int i = 1; i < 36; i++) if (Score(i) > Score(best)) best = i;
        double R = 0, G = 0, B = 0;
        foreach (var k in new[] { (best + 35) % 36, best, (best + 1) % 36 }) { R += sumR[k]; G += sumG[k]; B += sumB[k]; }
        var (hue, sat) = HueSat(R, G, B);
        sat = Math.Clamp(sat, 0.35, 0.62);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(FromHsl(hue, sat, 0.25), 0), new GradientStop(FromHsl(hue, sat * 0.9, 0.17), 1) },
        }.ToImmutable();
    }

    static double Hue(double r, double g, double b, double max, double d)
    {
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    static (double H, double S) HueSat(double r, double g, double b)
    {
        double m = Math.Max(r, Math.Max(g, b));
        if (m <= 0) return (0, 0);
        r /= m; g /= m; b /= m;
        double min = Math.Min(r, Math.Min(g, b)), d = 1 - min, l = (1 + min) / 2;
        if (d <= 0) return (0, 0);
        return (Hue(r, g, b, 1, d), d / (1 - Math.Abs(2 * l - 1)));
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x) : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
