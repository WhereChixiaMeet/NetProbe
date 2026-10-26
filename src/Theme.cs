using System;
using System.Drawing;

using static NetProbe.Tr;

namespace NetProbe
{
    public enum Level { Off, Wait, Good, Fair, Slow, Down }
    public enum ThemeKind { Dark = 0, Light = 1, Classic = 2 }

    // Монохромные палитры: Dark / Light и «классика» (что-то между Windows 95 и 11).
    public static class Theme
    {
        public static float Scale = 1f;
        public static int S(double v) { return (int)Math.Round(v * Scale); }

        public static ThemeKind Kind = ThemeKind.Dark;
        public static bool Classic { get { return Kind == ThemeKind.Classic; } }
        public static bool IsDark { get { return Kind == ThemeKind.Dark; } }

        public static Color Bg, Side, Card, CardHi, Border, Input, Text, Muted, Dim, Accent, AccentFg, Hilite, Shadow;
        public static Color Green, Yellow, Orange, Red, Gray, Vpn, Direct, Dpi, Grid, Widget;

        public static Font Body = new Font("Segoe UI", 9.5f);
        public static Font Small = new Font("Segoe UI", 8.5f);
        public static Font Bold = new Font("Segoe UI Semibold", 9.5f);
        public static Font H1 = new Font("Segoe UI Semibold", 15f);
        public static Font H2 = new Font("Segoe UI Semibold", 11.5f);
        public static Font Big = new Font("Segoe UI Light", 30f);
        public static Font Mono = new Font("Consolas", 9.5f);
        public static Font Icons = new Font("Segoe MDL2 Assets", 11f);

        // Цвет акцента выбирается в настройках; по умолчанию — синий, как в NetProbe 1.x
        public static readonly Color DefaultAccent = Color.FromArgb(79, 140, 255);
        public static Color AccentColor = DefaultAccent;
        public static readonly Tuple<string, Color>[] Presets = {
            Tuple.Create("Blue", Color.FromArgb(79, 140, 255)), Tuple.Create("Indigo", Color.FromArgb(99, 102, 241)), Tuple.Create("Purple", Color.FromArgb(139, 92, 246)),
            Tuple.Create("Pink", Color.FromArgb(236, 72, 153)), Tuple.Create("Red", Color.FromArgb(239, 68, 68)), Tuple.Create("Orange", Color.FromArgb(249, 115, 22)),
            Tuple.Create("Amber", Color.FromArgb(234, 179, 8)), Tuple.Create("Green", Color.FromArgb(34, 197, 94)), Tuple.Create("Teal", Color.FromArgb(20, 184, 166)),
            Tuple.Create("Cyan", Color.FromArgb(6, 182, 212)), Tuple.Create("Graphite", Color.FromArgb(140, 140, 140)) };

        static Theme() { Apply(ThemeKind.Dark); }

        public static Color ParseColor(string s)
        {
            try { var p = s.Split(','); return Color.FromArgb(int.Parse(p[0].Trim()), int.Parse(p[1].Trim()), int.Parse(p[2].Trim())); } catch { return DefaultAccent; }
        }
        public static string ColorStr(Color c) { return c.R + "," + c.G + "," + c.B; }

        static Color G(int v) { return Color.FromArgb(v, v, v); }
        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static void Apply(ThemeKind k)
        {
            Kind = k;
            switch (k)
            {
                case ThemeKind.Light:
                    Bg = C(243, 245, 249); Side = C(234, 237, 243); Card = G(255); CardHi = C(234, 238, 245); Border = C(214, 219, 228); Input = C(250, 251, 253);
                    Text = C(20, 24, 33); Muted = C(96, 104, 122); Dim = C(152, 160, 176); Accent = G(28); AccentFg = G(250); Hilite = G(255); Shadow = G(190);
                    Green = C(22, 140, 64); Yellow = C(176, 132, 0); Orange = C(196, 96, 20); Red = C(200, 40, 40); Gray = G(170);
                    Vpn = G(24); Direct = G(140); Dpi = G(90); Grid = G(232); Widget = G(250);
                    Body = new Font("Segoe UI", 9.5f); Small = new Font("Segoe UI", 8.5f); Bold = new Font("Segoe UI Semibold", 9.5f);
                    H1 = new Font("Segoe UI Semibold", 15f); H2 = new Font("Segoe UI Semibold", 11.5f); Big = new Font("Segoe UI Light", 30f);
                    break;
                case ThemeKind.Classic:
                    // «классика» поближе к Windows 7/10: белые карточки, светло-серый фон, тонкие рамки
                    Bg = G(240); Side = G(231); Card = G(255); CardHi = C(229, 241, 251); Border = G(172); Input = G(255);
                    Text = G(0); Muted = G(80); Dim = G(140); Accent = C(0, 120, 215); AccentFg = G(255); Hilite = G(255); Shadow = G(200);
                    Green = C(16, 124, 16); Yellow = C(157, 93, 0); Orange = C(202, 80, 16); Red = C(196, 43, 28); Gray = G(150);
                    Vpn = C(0, 120, 215); Direct = C(0, 153, 153); Dpi = C(157, 93, 0); Grid = G(228); Widget = G(248);
                    Body = new Font("Segoe UI", 9f); Small = new Font("Segoe UI", 8.25f); Bold = new Font("Segoe UI Semibold", 9f);
                    H1 = new Font("Segoe UI Semibold", 14f); H2 = new Font("Segoe UI Semibold", 10.5f); Big = new Font("Segoe UI Light", 28f);
                    break;
                default:
                    // палитра NetProbe 1.x
                    Bg = C(13, 15, 19); Side = C(17, 19, 25); Card = C(24, 27, 34); CardHi = C(31, 35, 44); Border = C(40, 45, 56); Input = C(18, 21, 27);
                    Text = C(230, 232, 238); Muted = C(132, 140, 158); Dim = C(84, 91, 107); Accent = C(79, 140, 255); AccentFg = G(255); Hilite = C(60, 66, 80); Shadow = G(8);
                    Green = C(34, 197, 94); Yellow = C(234, 179, 8); Orange = C(249, 115, 22); Red = C(239, 68, 68); Gray = C(75, 85, 99);
                    Vpn = C(139, 92, 246); Direct = C(6, 182, 212); Dpi = C(234, 179, 8); Grid = C(34, 39, 49); Widget = C(18, 20, 26);
                    Body = new Font("Segoe UI", 9.5f); Small = new Font("Segoe UI", 8.5f); Bold = new Font("Segoe UI Semibold", 9.5f);
                    H1 = new Font("Segoe UI Semibold", 15f); H2 = new Font("Segoe UI Semibold", 11.5f); Big = new Font("Segoe UI Light", 30f);
                    break;
            }
            Accent = AccentColor;
            AccentFg = (Accent.R * 0.299 + Accent.G * 0.587 + Accent.B * 0.114) > 170 ? G(16) : G(255);
            Vpn = k == ThemeKind.Classic ? C(0, 120, 215) : C(139, 92, 246);
            Direct = k == ThemeKind.Classic ? C(0, 153, 153) : C(6, 182, 212);
        }

        public static float R(double r) { return Classic ? Math.Min(S(r), S(2)) : S(r); }

        // Полупрозрачные карточки, когда задан свой фон (MainForm выставляет Translucent и CardAlpha)
        public static bool Translucent;
        public static int CardAlpha = 255;
        public static Color CardFill { get { return Translucent ? Color.FromArgb(CardAlpha, Card) : Card; } }

        public static Color LevelColor(Level l)
        {
            switch (l)
            {
                case Level.Good: return Green;
                case Level.Fair: return Yellow;
                case Level.Slow: return Orange;
                case Level.Down: return Red;
                case Level.Wait: return Accent;
                default: return Gray;
            }
        }

        public static string LevelText(Level l)
        {
            switch (l)
            {
                case Level.Good: return Tx("ОТЛИЧНО");
                case Level.Fair: return Tx("НОРМА");
                case Level.Slow: return Tx("МЕДЛЕННО");
                case Level.Down: return Tx("НЕТ СВЯЗИ");
                case Level.Wait: return Tx("ПРОВЕРКА");
                default: return Tx("ОСТАНОВЛЕН");
            }
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }
}
