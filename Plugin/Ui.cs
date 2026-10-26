// Ui.cs — минимальный IMGUI-кит в стиле Creator Toolkit (кремовый лист, тёмно-синий текст, бирюза).
// Основан на проверенном в SR2 подходе SlimeLive/Creator Toolkit: скругления — runtime-текстуры,
// рисуются девятью GUI.DrawTexture; урезанный в игре DrawTextureWithTexCoords не используется.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR2ModManager.Plugin;

internal static class Ui
{
    internal static readonly Color Cream = Hex(0xF7F3EA, 0.98f);
    internal static readonly Color Paper = Hex(0xFFFFFF);
    internal static readonly Color Beige = Hex(0xEFE6D6);
    internal static readonly Color Line = Hex(0xE2D8C4);
    internal static readonly Color Navy = Hex(0x1F2A44);
    internal static readonly Color NavySoft = Hex(0x56607A);
    internal static readonly Color Hint = Hex(0x8A8F9C);
    internal static readonly Color Teal = Hex(0x22C3A6);
    internal static readonly Color Coral = Hex(0xFF7A6B);
    internal static readonly Color Orange = Hex(0xFFA445);
    internal static readonly Color Pink = Hex(0xFF6FAE);
    internal static readonly Color Blue = Hex(0x4C8DF6);
    internal static readonly Color BeigeDark = Hex(0xE5D9C3);
    internal static readonly Color LineDark = Hex(0xCBBDA3);
    internal static readonly Color TealDark = Hex(0x17A98F);
    internal static readonly Color TealTint = Hex(0xE2F6F1);
    internal static readonly Color CoralTint = Hex(0xFFEBE7);
    internal static readonly Color OrangeTint = Hex(0xFFF0DB);
    internal static readonly Color BlueTint = Hex(0xE7EFFE);
    internal static readonly Color TealText = Hex(0x0C7A66);
    internal static readonly Color CoralText = Hex(0xC23B2A);
    internal static readonly Color OrangeText = Hex(0xA45A05);
    internal static readonly Color BlueText = Hex(0x2B5FC4);

    internal static Color Hex(int rgb, float a = 1f) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a);
    internal static Color Fade(Color c, float k) { c.a *= k; return c; }
    internal static Color Mix(Color a, Color b, float t) => Color.Lerp(a, b, t);

    internal static float S => Mathf.Clamp(Screen.height / 1080f, 0.6f, 2.5f);
    internal static bool Repaint => Event.current != null && Event.current.type == EventType.Repaint;

    private const int N = 64;
    private static Texture2D _round, _shadow, _circle;
    private static bool Alive(Texture2D t) => t != null && !t.WasCollected;

    private static Texture2D Make(string name, Func<float, float, float> alpha)
    {
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false);
        t.name = "SR2ModManager_UI_" + name;
        t.hideFlags = HideFlags.HideAndDontSave;
        t.filterMode = FilterMode.Bilinear;
        t.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(alpha(x + 0.5f, y + 0.5f))));
        t.Apply(false, true);
        return t;
    }

    private static Texture2D RoundTex() => Alive(_round) ? _round : _round = Make("Round", (x, y) =>
    {
        const float R = 20f;
        float cx = Mathf.Clamp(x, R, N - R), cy = Mathf.Clamp(y, R, N - R);
        return R - Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) + 0.5f;
    });

    private static Texture2D ShadowTex() => Alive(_shadow) ? _shadow : _shadow = Make("Shadow", (x, y) =>
    {
        const float M = 14f, R = 6f;
        float cx = Mathf.Clamp(x, M + R, N - M - R), cy = Mathf.Clamp(y, M + R, N - M - R);
        float d = Mathf.Max(0f, Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - R);
        float a = 1f - Mathf.Clamp01(d / M);
        return a * a;
    });

    private static Texture2D CircleTex() => Alive(_circle) ? _circle : _circle = Make("Circle", (x, y) =>
        31.5f - Mathf.Sqrt((x - 32f) * (x - 32f) + (y - 32f) * (y - 32f)) + 0.5f);

    internal static void Fill(Rect r, Color c)
    {
        if (!Repaint || r.width <= 0f || r.height <= 0f || c.a <= 0.003f) return;
        var prev = GUI.color; GUI.color = c;
        GUI.DrawTexture(Snap(r), Texture2D.whiteTexture);
        GUI.color = prev;
    }

    internal static void Round(Rect r, Color c, float radius)
    {
        if (!Repaint || r.width < 1f || r.height < 1f || c.a <= 0.003f) return;
        r = Snap(r);
        float rad = Mathf.Min(Mathf.Round(radius), Mathf.Floor(Mathf.Min(r.width, r.height) * 0.5f));
        if (rad < 1f) { Fill(r, c); return; }
        Slice(r, RoundTex(), rad, c, true);
    }

    internal static void Shadow(Rect r, float radius, float strength)
    {
        if (!Repaint) return;
        float g = Mathf.Round(12f * S);
        var sr = Snap(new Rect(r.x - g, r.y - g * 0.55f, r.width + 2f * g, r.height + 2f * g));
        float rad = Mathf.Min(Mathf.Round(radius + g), Mathf.Floor(Mathf.Min(sr.width, sr.height) * 0.5f));
        if (rad >= 2f) Slice(sr, ShadowTex(), rad, new Color(0f, 0f, 0f, strength), false);
    }

    internal static void Sheet(Rect r)
    {
        float rad = 22f * S;
        Shadow(r, rad, 0.32f);
        float w = Mathf.Max(1f, Mathf.Round(1.5f * S));
        Round(r, Line, rad);
        Round(new Rect(r.x + w, r.y + w, r.width - 2f * w, r.height - 2f * w), Cream, Mathf.Max(0f, rad - w));
    }

    internal static void Circle(Vector2 c, float radius, Color col)
    {
        if (!Repaint || radius < 0.5f) return;
        var prev = GUI.color; GUI.color = col;
        GUI.DrawTexture(Snap(new Rect(c.x - radius, c.y - radius, radius * 2f, radius * 2f)), CircleTex());
        GUI.color = prev;
    }

    internal static void Gradient(Rect r, Color a, Color b, int n = 32)
    {
        if (!Repaint || r.width < 1f) return;
        float sw = r.width / n;
        for (int i = 0; i < n; i++) Fill(new Rect(r.x + i * sw, r.y, sw + 1f, r.height), Color.Lerp(a, b, (i + 0.5f) / n));
    }

    /// Вертикальный градиент полосами (для мягкого затухания краёв списка).
    internal static void VGradient(Rect r, Color top, Color bottom, int n = 12)
    {
        if (!Repaint || r.height < 1f) return;
        float sh = r.height / n;
        for (int i = 0; i < n; i++)
        {
            float y0 = Mathf.Round(r.y + i * sh), y1 = Mathf.Round(r.y + (i + 1) * sh);
            if (y1 > y0) Fill(new Rect(r.x, y0, r.width, y1 - y0), Color.Lerp(top, bottom, (i + 0.5f) / n));
        }
    }

    /// Карточка с рамкой: внешний скруглённый прямоугольник цвета рамки и внутренний — заливки.
    internal static void Card(Rect r, Color fill, Color border, float radius, float bw = 0f)
    {
        if (bw <= 0f) bw = Mathf.Max(1f, Mathf.Round(1.2f * S));
        Round(r, border, radius);
        Round(new Rect(r.x + bw, r.y + bw, r.width - 2f * bw, r.height - 2f * bw), fill, Mathf.Max(0f, radius - bw));
    }

    /// Переключатель-«таблетка». t — положение ручки 0..1 (для плавной анимации).
    internal static void Toggle(Rect sw, float t, bool hover)
    {
        t = Mathf.Clamp01(t);
        var off = hover ? LineDark : Line;
        var on = hover ? TealDark : Teal;
        Round(sw, Color.Lerp(off, on, t), sw.height * 0.5f);
        float k = sw.height * 0.5f - Mathf.Max(2f, 3f * S);
        float cx = Mathf.Lerp(sw.x + sw.height * 0.5f, sw.xMax - sw.height * 0.5f, t);
        Circle(new Vector2(cx, sw.center.y + Mathf.Max(1f, S)), k, Fade(Navy, 0.16f));
        Circle(new Vector2(cx, sw.center.y), k, Color.white);
    }

    /// Мини-логотип: розовый слайм (как в лаунчере): полукруглая «шапка» + тело со скруглённым низом.
    internal static void Slime(Rect r)
    {
        if (!Repaint) return;
        float w = r.width, h = r.height, x = r.x, y = r.y;
        float R = w * 0.5f, bottom = y + h * 0.9f, rad = Mathf.Max(2f, h * 0.2f);
        Round(new Rect(x + w * 0.12f, bottom - h * 0.04f, w * 0.76f, h * 0.12f), Fade(Navy, 0.13f), h * 0.06f);
        Round(new Rect(x, y + R, w, bottom - (y + R)), Pink, rad);
        Fill(new Rect(x, y + R - 1f, w, Mathf.Max(0f, bottom - rad - (y + R) + 1f)), Pink);
        GUI.BeginGroup(new Rect(x, y, w, R));
        try { Circle(new Vector2(R, R), R, Pink); }
        finally { GUI.EndGroup(); }
        Circle(new Vector2(x + w * 0.3f, y + h * 0.24f), w * 0.1f, Fade(Color.white, 0.55f));
        Circle(new Vector2(x + w * 0.46f, y + h * 0.14f), w * 0.04f, Fade(Color.white, 0.55f));
        float ey = y + h * 0.52f, er = w * 0.08f;
        Circle(new Vector2(x + w * 0.36f, ey), er, Navy);
        Circle(new Vector2(x + w * 0.64f, ey), er, Navy);
        Circle(new Vector2(x + w * 0.345f, ey - er * 0.35f), er * 0.4f, Color.white);
        Circle(new Vector2(x + w * 0.625f, ey - er * 0.35f), er * 0.4f, Color.white);
        Circle(new Vector2(x + w * 0.2f, y + h * 0.68f), w * 0.07f, Fade(Color.white, 0.3f));
        Circle(new Vector2(x + w * 0.8f, y + h * 0.68f), w * 0.07f, Fade(Color.white, 0.3f));
    }

    /// Значок «!» в круге.
    internal static void Bang(Vector2 c, float radius, Color bg)
    {
        Circle(c, radius, bg);
        Label(new Rect(c.x - radius, c.y - radius - 1f * S, radius * 2f, radius * 2f), "!", Mathf.RoundToInt(radius * 1.35f / S), TextAnchor.MiddleCenter, Color.white, true);
    }

    /// Текст в капсуле, возвращает прямоугольник капсулы.
    internal static Rect Pill(float x, float y, string text, int size, Color bg, Color fg, float h = 0f)
    {
        float ph = h > 0f ? h : Mathf.Round((size + 12f) * S);
        var r = new Rect(x, y, TextW(text, size) + 22f * S, ph);
        Round(r, bg, ph * 0.5f);
        Label(r, text, size, TextAnchor.MiddleCenter, fg);
        return r;
    }

    private static void Slice(Rect r, Texture2D tex, float rad, Color c, bool solidCenter)
    {
        var prev = GUI.color; GUI.color = c;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                float sx = col == 0 ? 0f : col == 1 ? 20f : 44f, sy = row == 0 ? 0f : row == 1 ? 20f : 44f;
                float sw = col == 1 ? 24f : 20f, sh = row == 1 ? 24f : 20f;
                float dx = col == 0 ? 0f : col == 1 ? rad : r.width - rad;
                float dy = row == 0 ? 0f : row == 1 ? rad : r.height - rad;
                float dw = col == 1 ? r.width - 2f * rad : rad;
                float dh = row == 1 ? r.height - 2f * rad : rad;
                if (dw <= 0.01f || dh <= 0.01f) continue;
                float left = Mathf.Round(r.x + dx), top = Mathf.Round(r.y + dy);
                float right = Mathf.Round(r.x + dx + dw), bottom = Mathf.Round(r.y + dy + dh);
                if (right - left < 0.5f || bottom - top < 0.5f) continue;
                var dst = new Rect(left, top, right - left, bottom - top);
                if (solidCenter && row == 1 && col == 1) { GUI.DrawTexture(dst, Texture2D.whiteTexture); continue; }
                float kx = dst.width / sw, ky = dst.height / sh;
                GUI.BeginGroup(dst);
                try { GUI.DrawTexture(new Rect(-sx * kx, -sy * ky, N * kx, N * ky), tex); }
                finally { GUI.EndGroup(); }
            }
        GUI.color = prev;
    }

    private static Rect Snap(Rect r) => Rect.MinMaxRect(Mathf.Round(r.x), Mathf.Round(r.y), Mathf.Round(r.xMax), Mathf.Round(r.yMax));

    // ───── текст ─────
    private static Font _font;
    private static bool _fontResolved;
    internal static string FontName = "";
    private static readonly Dictionary<int, GUIStyle> _styles = new();
    private static readonly GUIContent _gc = new();

    private static void ResolveFont()
    {
        if (_fontResolved && (_font == null || !_font.WasCollected)) return;
        _fontResolved = true; _font = null; _styles.Clear(); _ell.Clear(); _wrap.Clear();
        int best = -1;
        try
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f == null || f.WasCollected) continue;
                string n = (f.name ?? "").ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
                int rank = n.Contains("opensans") ? (n.Contains("semibold") ? 900 : n.Contains("bold") ? 950 : 600)
                         : n.Contains("nunito") || n.Contains("rubik") ? 800 : n.Contains("montserrat") ? 700 : -1;
                if (n.Contains("italic") || n.Contains("mono")) rank = -1;
                if (rank > best && HasCyrillic(f)) { best = rank; _font = f; FontName = f.name; }
            }
        }
        catch { }
        if (_font == null)
            foreach (var n in new[] { "Segoe UI", "Arial" })
            {
                try
                {
                    var f = Font.CreateDynamicFontFromOSFont(n, 32);
                    if (f != null && HasCyrillic(f)) { f.hideFlags = HideFlags.HideAndDontSave; _font = f; FontName = n + " (Windows)"; break; }
                }
                catch { }
            }
    }

    private static bool HasCyrillic(Font f)
    {
        try
        {
            if (f.dynamic) f.RequestCharactersInTexture("АБВЖЯжяЩщЮюЁё", 24, FontStyle.Normal);
            foreach (char c in "ЖжЯяЩщЮю") if (!f.HasCharacter(c)) return false;
            return true;
        }
        catch { return false; }
    }

    internal static GUIStyle Style(int size, TextAnchor anchor, bool bold = false)
    {
        ResolveFont();
        int px = Mathf.Clamp(Mathf.RoundToInt(size * S), 6, 200);
        int key = (px << 6) | ((int)anchor << 1) | (bold ? 1 : 0);
        if (_styles.TryGetValue(key, out var st) && st != null) return st;
        st = new GUIStyle
        {
            font = _font,
            fontSize = px,
            alignment = anchor,
            fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
            richText = false,
            wordWrap = false,
            clipping = TextClipping.Clip,
        };
        st.normal.textColor = Color.white;
        _styles[key] = st;
        return st;
    }

    internal static float TextW(string text, int size, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        _gc.text = text;
        return Style(size, TextAnchor.MiddleLeft, bold).CalcSize(_gc).x;
    }

    internal static void Label(Rect r, string text, int size, TextAnchor a, Color col, bool bold = false)
    {
        if (!Repaint || string.IsNullOrEmpty(text)) return;
        var st = Style(size, a, bold);
        var prev = GUI.color; GUI.color = col;
        GUI.Label(r, text, st);
        GUI.color = prev;
    }

    // ───── обрезка и перенос строк (кэшируются: CalcSize в IMGUI недешёвый) ─────
    private static readonly Dictionary<string, string> _ell = new();
    private static float _ellScale = -1f;

    /// Обрезает строку многоточием, чтобы она влезла в maxW пикселей.
    internal static string Ellipsize(string text, int size, float maxW, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (maxW <= 4f) return "";
        if (_ellScale != S || _ell.Count > 600) { _ell.Clear(); _ellScale = S; }
        string key = string.Concat(size.ToString(), bold ? "b" : "n", Mathf.RoundToInt(maxW).ToString(), "|", text);
        if (_ell.TryGetValue(key, out var hit)) return hit;
        string res = text;
        if (TextW(text, size, bold) > maxW)
        {
            int lo = 0, hi = text.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (TextW(text.Substring(0, mid).TrimEnd() + "…", size, bold) <= maxW) lo = mid; else hi = mid - 1;
            }
            res = lo <= 0 ? "…" : text.Substring(0, lo).TrimEnd(' ', ',', ';', ':', '·', '—', '-', '(') + "…";
        }
        _ell[key] = res;
        return res;
    }

    private static readonly Dictionary<string, List<string>> _wrap = new();

    /// Перенос по словам (не больше maxLines строк, последняя — с многоточием).
    internal static List<string> Wrap(string text, int size, float maxW, int maxLines, bool bold = false)
    {
        string key = string.Concat(size.ToString(), bold ? "b" : "n", Mathf.RoundToInt(maxW).ToString(), "/", maxLines.ToString(), "/", S.ToString(), "|", text ?? "");
        if (_wrap.TryGetValue(key, out var cached)) return cached;
        if (_wrap.Count > 64) _wrap.Clear();
        var lines = new List<string>();
        var words = (text ?? "").Split(' ');
        string cur = "";
        int i = 0;
        for (; i < words.Length; i++)
        {
            string wd = words[i];
            if (wd.Length == 0) continue;
            string next = cur.Length == 0 ? wd : cur + " " + wd;
            if (cur.Length > 0 && TextW(next, size, bold) > maxW)
            {
                if (lines.Count == maxLines - 1) break;
                lines.Add(cur);
                cur = wd;
            }
            else cur = next;
        }
        if (i < words.Length) cur = cur + " " + string.Join(" ", words, i, words.Length - i);
        if (cur.Length > 0 || lines.Count == 0) lines.Add(Ellipsize(cur, size, maxW, bold));
        _wrap[key] = lines;
        return lines;
    }

    // ───── русские числительные ─────
    internal static string Plural(int n, string one, string few, string many)
    {
        int a = Math.Abs(n) % 100, b = a % 10;
        if (a > 10 && a < 20) return many;
        if (b == 1) return one;
        if (b >= 2 && b <= 4) return few;
        return many;
    }

    internal static string Count(int n, string one, string few, string many) => n.ToString() + " " + Plural(n, one, few, many);
}

