using System;

namespace WFInfo
{
    /// <summary>
    /// Cross-platform color struct replacing System.Drawing.Color.
    /// Provides HSL methods needed by the OCR theme detection.
    /// </summary>
    public readonly struct WFColor : IEquatable<WFColor>
    {
        public byte A { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public WFColor(byte a, byte r, byte g, byte b) { A = a; R = r; G = g; B = b; }

        public static WFColor FromArgb(int a, int r, int g, int b) => new WFColor((byte)a, (byte)r, (byte)g, (byte)b);
        public static WFColor FromArgb(int r, int g, int b) => new WFColor(255, (byte)r, (byte)g, (byte)b);
        public static WFColor FromRgb(int r, int g, int b) => new WFColor(255, (byte)r, (byte)g, (byte)b);

        public float GetHue()
        {
            float r = R / 255f, g = G / 255f, b = B / 255f;
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            if (max == min) return 0f;
            float delta = max - min;
            float hue;
            if (max == r) hue = ((g - b) / delta) % 6f;
            else if (max == g) hue = (b - r) / delta + 2f;
            else hue = (r - g) / delta + 4f;
            hue *= 60f;
            if (hue < 0) hue += 360f;
            return hue;
        }

        public float GetSaturation()
        {
            float r = R / 255f, g = G / 255f, b = B / 255f;
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            if (max == min) return 0f;
            float l = (max + min) / 2f;
            return l <= 0.5f ? (max - min) / (max + min) : (max - min) / (2f - max - min);
        }

        public float GetBrightness()
        {
            float r = R / 255f, g = G / 255f, b = B / 255f;
            return (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2f;
        }

        /// <summary>Compute hue, saturation and brightness in a single pass.</summary>
        public void GetHSB(out float hue, out float saturation, out float brightness)
        {
            float r = R / 255f, g = G / 255f, b = B / 255f;
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float delta = max - min;
            brightness = (max + min) / 2f;
            if (delta == 0f) { hue = 0f; saturation = 0f; return; }
            saturation = brightness <= 0.5f ? delta / (max + min) : delta / (2f - max - min);
            if (max == r) hue = ((g - b) / delta) % 6f;
            else if (max == g) hue = (b - r) / delta + 2f;
            else hue = (r - g) / delta + 4f;
            hue *= 60f;
            if (hue < 0) hue += 360f;
        }

        public bool Equals(WFColor other) => A == other.A && R == other.R && G == other.G && B == other.B;
        public override bool Equals(object obj) => obj is WFColor c && Equals(c);
        public override int GetHashCode() => (A << 24) | (R << 16) | (G << 8) | B;
        public static bool operator ==(WFColor a, WFColor b) => a.Equals(b);
        public static bool operator !=(WFColor a, WFColor b) => !a.Equals(b);
        public override string ToString() => $"Color [A={A}, R={R}, G={G}, B={B}]";
    }
}