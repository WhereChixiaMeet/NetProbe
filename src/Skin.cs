using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NetProbe
{
    // Скины Personal-тем: только отрисовка поверх стандартных контролов (порядок элементов и поведение не меняются).
    //  Chisa  — острые формы (срезы — в Gfx.Round), алая кромка-«лазер», тонкие диагонали.
    //  Kurumi — мягкие формы, золотая внутренняя линия, мотив часов (Zafkiel): циферблат, стрелка.
    // В Dark / Light / Classic все методы ничего не рисуют.
    public static class Skin
    {
        public static void Button(Graphics g, RectangleF r, bool primary, bool hover, bool down, bool enabled)
        {
            if (!Theme.Personal || !enabled) return;
            if (Theme.Chisa)
            {
                // левая алая кромка у основной кнопки, «лазер» по нижней кромке при наведении
                if (primary) using (var b = new SolidBrush(Theme.Mix(Theme.Accent, Color.White, 0.25))) g.FillRectangle(b, r.X + Theme.S(1), r.Y + Theme.S(7), Theme.S(2), r.Height - Theme.S(14));
                if (hover)
                    using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Bottom - 2, r.Width, 2), Color.FromArgb(0, Theme.Accent), Color.FromArgb(230, Theme.Accent), 0f))
                    {
                        var cb = new ColorBlend { Colors = new[] { Color.FromArgb(0, Theme.Accent), Color.FromArgb(230, Theme.Accent), Color.FromArgb(0, Theme.Accent) }, Positions = new[] { 0f, 0.5f, 1f } };
                        lg.InterpolationColors = cb;
                        g.FillRectangle(lg, r.X + Theme.S(6), r.Bottom - Theme.S(2), r.Width - Theme.S(12), Theme.S(1.6));
                    }
            }
            else if (Theme.Kurumi)
            {
                // тонкая золотая внутренняя линия у основной; мягкое crimson-свечение при наведении
                if (primary) Gfx.DrawRound(g, Color.FromArgb(150, Theme.Accent2), new RectangleF(r.X + Theme.S(2), r.Y + Theme.S(2), r.Width - Theme.S(4), r.Height - Theme.S(4)), Theme.R(6) - Theme.S(2), 1f);
                else if (hover) Gfx.DrawRound(g, Color.FromArgb(110, Theme.Accent), r, Theme.R(6), 1.6f);
            }
        }

        public static void NavMarker(Graphics g, RectangleF r, int h)
        {
            if (Theme.Kurumi)
            {
                float cx = r.Right - Theme.S(10), cy = h / 2f, rr = Theme.S(2.5);
                using (var b = new SolidBrush(Theme.Accent2)) g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
            }
            else if (Theme.Chisa)
            {
                // срезанный алый маркер справа
                float x = r.Right - Theme.S(14), y = h / 2f;
                using (var b = new SolidBrush(Color.FromArgb(200, Theme.Accent)))
                    g.FillPolygon(b, new[] { new PointF(x, y - Theme.S(5)), new PointF(x + Theme.S(6), y - Theme.S(5)), new PointF(x + Theme.S(2), y + Theme.S(5)), new PointF(x - Theme.S(4), y + Theme.S(5)) });
            }
        }

        // декор в правом верхнем углу карточки: едва заметный (≤ 8 % непрозрачности), не мешает тексту
        public static void CardDecor(Graphics g, RectangleF r)
        {
            if (!Theme.Personal || r.Width < Theme.S(120) || r.Height < Theme.S(60)) return;
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Theme.Chisa)
            {
                // тонкая алая диагональ-«осколок» в углу
                using (var p = new Pen(Color.FromArgb(70, Theme.Accent), Theme.S(1)))
                {
                    g.DrawLine(p, r.Right - Theme.S(46), r.Y + Theme.S(1), r.Right - Theme.S(1), r.Y + Theme.S(28));
                    g.DrawLine(p, r.Right - Theme.S(30), r.Y + Theme.S(1), r.Right - Theme.S(1), r.Y + Theme.S(18));
                }
                using (var b = new SolidBrush(Color.FromArgb(40, Theme.Accent)))
                    g.FillPolygon(b, new[] { new PointF(r.Right - Theme.S(12), r.Y + Theme.S(1)), new PointF(r.Right - Theme.S(1), r.Y + Theme.S(1)), new PointF(r.Right - Theme.S(1), r.Y + Theme.S(8)) });
            }
            else
            {
                // циферблат Zafkiel: окружность, 12 рисок, стрелка — золотом, очень бледно
                float R = Theme.S(16), cx = r.Right - Theme.S(26), cy = r.Bottom - Theme.S(24); // внизу справа — там нет кнопок
                using (var p = new Pen(Color.FromArgb(20, Theme.Accent2), Theme.S(1)))
                {
                    g.DrawEllipse(p, cx - R, cy - R, R * 2, R * 2);
                    for (int i = 0; i < 12; i++)
                    {
                        double a = i * Math.PI / 6; float r1 = R - Theme.S(i % 3 == 0 ? 4 : 2);
                        g.DrawLine(p, cx + (float)Math.Sin(a) * r1, cy - (float)Math.Cos(a) * r1, cx + (float)Math.Sin(a) * R, cy - (float)Math.Cos(a) * R);
                    }
                }
                using (var p = new Pen(Color.FromArgb(26, Theme.Accent), Theme.S(1.4)) { EndCap = LineCap.Round })
                { g.DrawLine(p, cx, cy, cx + R * 0.45f, cy - R * 0.45f); g.DrawLine(p, cx, cy, cx, cy - R * 0.7f); }
            }
            g.SmoothingMode = sm;
        }

        // бегунок переключателя: у Kurumi — маленькая часовая стрелка
        public static void Knob(Graphics g, RectangleF k, bool on)
        {
            if (!Theme.Kurumi || !on) return;
            float cx = k.X + k.Width / 2, cy = k.Y + k.Height / 2;
            using (var p = new Pen(Theme.Accent, Theme.S(1.5)) { EndCap = LineCap.Round, StartCap = LineCap.Round })
            { g.DrawLine(p, cx, cy, cx, cy - k.Height * 0.32f); g.DrawLine(p, cx, cy, cx + k.Width * 0.24f, cy); }
        }
    }
}
