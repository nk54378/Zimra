using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Raylib_cs;

namespace RhythmGameUI
{
    static class Theme
    {
        public static readonly Color Accent = new(250, 56, 52, 255);       // #FA3834
        public static readonly Color AccentSoft = new(250, 56, 52, 150);
        public static readonly Color ButtonFill = new(30, 30, 45, 200);    // dark blue-grey
        public static readonly Color PanelFill = new(30, 30, 45, 225);
        public static readonly Color Background = new(10, 14, 23, 255);
        public static readonly Color BackgroundLight = new(24, 26, 44, 255);
        public static readonly Color Text = new(244, 245, 250, 255);
        public static readonly Color TextDim = new(168, 172, 194, 255);
        public static readonly Color TextFaint = new(110, 114, 140, 255);

        public static readonly Color Perfect = new(80, 210, 255, 255);
        public static readonly Color Good = new(110, 235, 120, 255);
        public static readonly Color Okay = new(255, 172, 64, 255);
        public static readonly Color Miss = new(255, 64, 72, 255);
        public static readonly Color Gold = new(255, 208, 64, 255);

        public static Color For(Judgement j) => j switch
        {
            Judgement.Perfect => Perfect,
            Judgement.Good => Good,
            Judgement.Okay => Okay,
            _ => Miss
        };

        public static Color WithAlpha(Color c, float alpha) =>
            new(c.R, c.G, c.B, (int)Math.Clamp(c.A * alpha, 0, 255));
    }

    static class Ease
    {
        public static float OutCubic(float t) { t = Math.Clamp(t, 0, 1); return 1 - MathF.Pow(1 - t, 3); }
        public static float OutBack(float t) { t = Math.Clamp(t, 0, 1); const float c = 1.9f; return 1 + (c + 1) * MathF.Pow(t - 1, 3) + c * MathF.Pow(t - 1, 2); }
        public static float InOutCubic(float t) { t = Math.Clamp(t, 0, 1); return t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2; }
        /// <summary>Frame-rate independent smoothing towards a target.</summary>
        public static float Damp(float current, float target, float speed, float dt) => current + (target - current) * (1 - MathF.Exp(-speed * dt));
    }

    static class Ui
    {
        public static Font Display;   // Gantians: titles, buttons, numbers
        public static Font Body;      // Lilita One: small readable text
        static bool ownsDisplay, ownsBody;

        /// <summary>Set by the game so menu buttons ignore clicks that belong to the editor panel.</summary>
        public static Func<Vector2, bool>? MouseBlocked;

        /// <summary>0..1, peaks on every beat of whatever is playing. Drives button and logo pulsing.</summary>
        public static float BeatPulse;

        static readonly Dictionary<string, float> hoverAnim = new();

        public static void LoadFonts()
        {
            Display = LoadFontOrDefault("Assets/Fonts/Gantians-BLL2B.otf", 128, out ownsDisplay);
            Body = LoadFontOrDefault("Assets/Fonts/LilitaOne-Regular.ttf", 64, out ownsBody);
            if (!ownsBody) { Body = Display; }
            if (!ownsDisplay && ownsBody) Display = Body;
        }

        static Font LoadFontOrDefault(string path, int baseSize, out bool owns)
        {
            owns = false;
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Font] Missing {path}, using raylib default.");
                return Raylib.GetFontDefault();
            }
            // Loading at a large base size keeps big titles crisp instead of blurry.
            Font f = Raylib.LoadFontEx(path, baseSize, null!, 0);
            if (f.Texture.Id == 0 || f.Texture.Id == Raylib.GetFontDefault().Texture.Id) return Raylib.GetFontDefault();
            Raylib.GenTextureMipmaps(ref f.Texture);
            Raylib.SetTextureFilter(f.Texture, TextureFilter.Trilinear);
            owns = true;
            return f;
        }

        public static void UnloadFonts()
        {
            if (ownsDisplay) Raylib.UnloadFont(Display);
            if (ownsBody && Body.Texture.Id != Display.Texture.Id) Raylib.UnloadFont(Body);
            ownsDisplay = ownsBody = false;
        }

        // ---------------- Text ----------------
        static float Spacing(float size) => size * 0.04f;

        public static Vector2 Measure(string text, float size, Font? font = null) =>
            Raylib.MeasureTextEx(font ?? Display, text, size, Spacing(size));

        public static void Text(string text, float x, float y, float size, Color color, Font? font = null) =>
            Raylib.DrawTextEx(font ?? Display, text, new Vector2(MathF.Round(x), MathF.Round(y)), size, Spacing(size), color);

        public static void TextCentered(string text, float cx, float y, float size, Color color, Font? font = null)
        {
            var m = Measure(text, size, font);
            Text(text, cx - m.X / 2, y, size, color, font);
        }

        public static void TextRight(string text, float right, float y, float size, Color color, Font? font = null)
        {
            var m = Measure(text, size, font);
            Text(text, right - m.X, y, size, color, font);
        }

        /// <summary>Text drawn rotated/scaled around its centre (used for popping judgements and the logo).</summary>
        public static void TextPop(string text, Vector2 centre, float size, float scale, float rotation, Color color, Font? font = null)
        {
            var f = font ?? Display;
            var m = Measure(text, size * scale, f);
            Raylib.DrawTextPro(f, text, centre, m / 2, rotation, size * scale, Spacing(size * scale), color);
        }

        public static void TextOutlined(string text, float x, float y, float size, Color color, Color outline, int thickness, Font? font = null)
        {
            if (thickness > 0)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            Text(text, x + dx * thickness, y + dy * thickness, size, outline, font);
            }
            Text(text, x, y, size, color, font);
        }

        public static void TextGlow(string text, Vector2 centre, float size, float scale, Color color, Color glow, float glowStrength, Font? font = null)
        {
            for (int i = 3; i >= 1; i--)
            {
                float s = scale * (1f + i * 0.008f * glowStrength);
                TextPop(text, centre, size, s, 0, Theme.WithAlpha(glow, 0.12f * Math.Min(glowStrength, 1.5f)), font);
            }
            TextPop(text, centre, size, scale, 0, color, font);
        }

        /// <summary>Cuts text with "..." so it fits a width.</summary>
        public static string Fit(string text, float width, float size, Font? font = null)
        {
            if (width <= 0) return "";
            if (Measure(text, size, font).X <= width) return text;
            while (text.Length > 0 && Measure(text + "...", size, font).X > width) text = text[..^1];
            return text + "...";
        }

        // ---------------- Panels & buttons ----------------
        public static void Panel(Rectangle r, float roundness = 0.1f, float border = 4f)
        {
            Raylib.DrawRectangleRounded(new Rectangle(r.X + 6, r.Y + 10, r.Width, r.Height), roundness, 12, new Color(0, 0, 0, 90));
            Raylib.DrawRectangleRounded(r, roundness, 12, Theme.Accent);
            Raylib.DrawRectangleRounded(Inset(r, border), roundness, 12, Theme.PanelFill);
        }

        public static Rectangle Inset(Rectangle r, float by) => new(r.X + by, r.Y + by, r.Width - by * 2, r.Height - by * 2);

        public static bool MouseOver(Rectangle r) => Raylib.CheckCollisionPointRec(Raylib.GetMousePosition(), r);

        /// <summary>
        /// The game's main button: #FA3834 border with a dark blue-grey fill (same look as before)
        /// plus a hover grow, glow and beat pulse.
        /// </summary>
        public static bool Button(Rectangle rect, string label, bool enabled = true, float fontSize = 0, string? id = null)
        {
            string key = id ?? $"{label}|{(int)rect.Width}x{(int)rect.Height}";
            Vector2 mouse = Raylib.GetMousePosition();
            bool blocked = MouseBlocked?.Invoke(mouse) ?? false;
            bool hover = enabled && !blocked && Raylib.CheckCollisionPointRec(mouse, rect);

            hoverAnim.TryGetValue(key, out float h);
            h = Ease.Damp(h, hover ? 1f : 0f, 18f, Raylib.GetFrameTime());
            hoverAnim[key] = h;

            float grow = h * 6f + (enabled ? Ui.BeatPulse * 2f : 0f);
            var r = new Rectangle(rect.X - grow, rect.Y - grow * 0.6f, rect.Width + grow * 2, rect.Height + grow * 1.2f);
            float round = Math.Min(0.5f, 24f / Math.Max(1f, Math.Min(r.Width, r.Height)) * 1.4f);

            if (h > 0.01f)
                Raylib.DrawRectangleRounded(new Rectangle(r.X - 6, r.Y - 6, r.Width + 12, r.Height + 12), round, 12, Theme.WithAlpha(Theme.Accent, 0.22f * h));

            Color border = enabled ? Theme.Accent : new Color(120, 60, 64, 255);
            Color idle = Theme.ButtonFill;
            Color fill = ColorTracker.Lerp(idle, Theme.AccentSoft, h);
            if (hover && Raylib.IsMouseButtonDown(MouseButton.Left)) fill = Theme.Accent;

            Raylib.DrawRectangleRounded(r, round, 12, border);
            Raylib.DrawRectangleRounded(Inset(r, 4), round, 12, fill);

            float size = fontSize > 0 ? fontSize : Math.Clamp(rect.Height * 0.5f, 16f, 40f);
            string text = Fit(label, rect.Width - 20, size);
            var m = Measure(text, size);
            Color textCol = enabled ? Theme.Text : Theme.TextFaint;
            Text(text, r.X + r.Width / 2 - m.X / 2, r.Y + r.Height / 2 - m.Y / 2, size, textCol);

            return hover && Raylib.IsMouseButtonPressed(MouseButton.Left);
        }

        // ---------------- Helpers ----------------
        public static Color ParseHex(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6 && hex.Length != 8) return fallback;
            try
            {
                int r = Convert.ToInt32(hex[..2], 16);
                int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                int b = Convert.ToInt32(hex.Substring(4, 2), 16);
                int a = hex.Length == 8 ? Convert.ToInt32(hex.Substring(6, 2), 16) : 255;
                return new Color(r, g, b, a);
            }
            catch (FormatException) { return fallback; }
        }

        public static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

        public static void TextureCentered(Texture2D tex, float x, float y, float scale, Color tint, float rotation = 0f)
        {
            if (tex.Id == 0 || tex.Width == 0) return;
            var dest = new Rectangle(x, y, tex.Width * scale, tex.Height * scale);
            Raylib.DrawTexturePro(tex, new Rectangle(0, 0, tex.Width, tex.Height), dest, new Vector2(dest.Width / 2, dest.Height / 2), rotation, tint);
        }

        /// <summary>Filled quad from four corners in clockwise screen order.</summary>
        public static void Quad(Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl, Color c)
        {
            Raylib.DrawTriangle(tl, bl, br, c);
            Raylib.DrawTriangle(tl, br, tr, c);
        }
    }

    /// <summary>Particles, expanding rings and screen shake.</summary>
    static class Fx
    {
        static readonly List<Particle> particles = new();
        static readonly List<(Vector2 pos, float life, float max, float r0, float r1, float thick, Color col)> rings = new();
        static readonly Random rng = new();
        static float shake;
        public static Vector2 ShakeOffset { get; private set; }

        public static float Rand(float min, float max) => min + (float)rng.NextDouble() * (max - min);

        public static void Burst(Vector2 pos, Color col, int count, float speed, float size = 6f, float life = 0.55f, float gravity = 0f)
        {
            for (int i = 0; i < count; i++)
            {
                float a = Rand(0, MathF.Tau);
                float s = speed * Rand(0.35f, 1f);
                particles.Add(new Particle
                {
                    Pos = pos, Vel = new Vector2(MathF.Cos(a), MathF.Sin(a)) * s,
                    Life = life * Rand(0.6f, 1f), MaxLife = life, Size = size * Rand(0.5f, 1.2f),
                    Rot = Rand(0, 360), RotVel = Rand(-540, 540), Col = col, Square = rng.Next(2) == 0, Gravity = gravity
                });
            }
        }

        public static void Ring(Vector2 pos, Color col, float r0, float r1, float life = 0.35f, float thick = 6f) =>
            rings.Add((pos, life, life, r0, r1, thick, col));

        public static void Shake(float amount) => shake = Math.Max(shake, amount);

        public static void Clear() { particles.Clear(); rings.Clear(); shake = 0; ShakeOffset = Vector2.Zero; }

        public static void Update(float dt)
        {
            for (int i = particles.Count - 1; i >= 0; i--)
            {
                var p = particles[i];
                p.Life -= dt;
                if (p.Life <= 0) { particles.RemoveAt(i); continue; }
                p.Vel *= MathF.Exp(-p.Drag * dt);
                p.Vel.Y += p.Gravity * dt;
                p.Pos += p.Vel * dt;
                p.Rot += p.RotVel * dt;
            }
            for (int i = rings.Count - 1; i >= 0; i--)
            {
                var r = rings[i];
                r.life -= dt;
                if (r.life <= 0) rings.RemoveAt(i); else rings[i] = r;
            }
            shake = Math.Max(0, shake - dt * 60f);
            ShakeOffset = shake > 0.1f ? new Vector2(Rand(-shake, shake), Rand(-shake, shake)) : Vector2.Zero;
        }

        public static void Draw()
        {
            Raylib.BeginBlendMode(BlendMode.Additive);
            foreach (var r in rings)
            {
                float t = 1 - r.life / r.max;
                float rad = r.r0 + (r.r1 - r.r0) * Ease.OutCubic(t);
                float th = r.thick * (1 - t) + 1;
                Raylib.DrawRing(r.pos, Math.Max(0, rad - th), rad, 0, 360, 48, Theme.WithAlpha(r.col, 1 - t));
            }
            foreach (var p in particles)
            {
                float a = p.Life / p.MaxLife;
                float s = p.Size * (0.4f + 0.6f * a);
                var c = Theme.WithAlpha(p.Col, a);
                if (p.Square) Raylib.DrawRectanglePro(new Rectangle(p.Pos.X, p.Pos.Y, s, s), new Vector2(s / 2, s / 2), p.Rot, c);
                else Raylib.DrawPoly(p.Pos, 3, s, p.Rot, c);
            }
            Raylib.EndBlendMode();
        }
    }

    /// <summary>osu!-style drifting triangles that kick on every beat.</summary>
    class TriangleField
    {
        struct Tri { public Vector2 Pos; public float Size, Speed, Rot, Shade; public bool Filled; }
        readonly List<Tri> tris = new();
        readonly Random rng = new(7);

        public void Update(float dt, float beatKick, int w, int h)
        {
            while (tris.Count < 46) tris.Add(NewTri(w, h, true));
            for (int i = tris.Count - 1; i >= 0; i--)
            {
                var t = tris[i];
                t.Pos.Y -= t.Speed * dt * (1f + beatKick * 3.5f);
                t.Rot += dt * 6f;
                if (t.Pos.Y < -t.Size * 2) t = NewTri(w, h, false);
                tris[i] = t;
            }
        }

        Tri NewTri(int w, int h, bool anywhere) => new()
        {
            Pos = new Vector2((float)rng.NextDouble() * w, anywhere ? (float)rng.NextDouble() * h : h + 80),
            Size = 20 + (float)rng.NextDouble() * 90,
            Speed = 20 + (float)rng.NextDouble() * 50,
            Rot = (float)rng.NextDouble() * 360,
            Shade = (float)rng.NextDouble(),
            Filled = rng.Next(3) == 0
        };

        public void Draw(float beatKick)
        {
            foreach (var t in tris)
            {
                Color c = t.Shade > 0.7f
                    ? Theme.WithAlpha(Theme.Accent, 0.10f + beatKick * 0.10f)
                    : new Color(70, 80, 130, (int)(28 + beatKick * 30));
                if (t.Filled) Raylib.DrawPoly(t.Pos, 3, t.Size, t.Rot, c);
                else Raylib.DrawPolyLinesEx(t.Pos, 3, t.Size, t.Rot, 3, c);
            }
        }
    }
}
