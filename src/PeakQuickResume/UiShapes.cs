using System.Collections.Generic;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// Tiny runtime sprite generator for <see cref="IslandToggleButton"/>: rounded
    /// rectangles (panel background, switch track) and a circle (switch knob), each
    /// baked once per distinct size/color/border combo and cached for the rest of the
    /// session. No art assets are bundled, everything here is a plain analytic
    /// distance-field rasterization (same trick as signed-distance-field shape
    /// rendering, just baked to a small texture instead of drawn per-frame in a shader)
    ///
    /// Baked with a WHITE fill and a light-gray border rather than the actual desired
    /// colors: <see cref="IslandToggleButton"/> then tints the sprite via Image.color,
    /// which scales the border proportionally darker along with the fill, same trick
    /// <see cref="PauseMenuPatch"/> uses (derive the border shade FROM the tint) but
    /// baked into the texture instead of computed per-button
    /// </summary>
    internal static class UiShapes
    {
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        public static Sprite RoundedRect(int width, int height, float radius, Color32 fill, Color32 border, float borderThickness)
        {
            string key = $"rr:{width}:{height}:{radius}:{fill}:{border}:{borderThickness}";
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            float halfW = width / 2f, halfH = height / 2f;
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float px = x + 0.5f - halfW;
                    float py = y + 0.5f - halfH;
                    float qx = Mathf.Abs(px) - (halfW - radius);
                    float qy = Mathf.Abs(py) - (halfH - radius);
                    float d = Mathf.Sqrt(Mathf.Pow(Mathf.Max(qx, 0f), 2f) + Mathf.Pow(Mathf.Max(qy, 0f), 2f))
                              + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;

                    float alpha = Mathf.Clamp01(1f - d);
                    Color32 baseColor = d > -borderThickness ? border : fill;
                    pixels[y * width + x] = new Color32(baseColor.r, baseColor.g, baseColor.b,
                        (byte)Mathf.RoundToInt(baseColor.a * alpha));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _cache[key] = sprite;
            return sprite;
        }

        public static Sprite Circle(int diameter, Color32 fill, Color32 border, float borderThickness)
        {
            string key = $"c:{diameter}:{fill}:{border}:{borderThickness}";
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var tex = new Texture2D(diameter, diameter, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            float radius = diameter / 2f;
            var pixels = new Color32[diameter * diameter];
            for (int y = 0; y < diameter; y++)
            {
                for (int x = 0; x < diameter; x++)
                {
                    float px = x + 0.5f - radius;
                    float py = y + 0.5f - radius;
                    float d = Mathf.Sqrt(px * px + py * py) - radius;

                    float alpha = Mathf.Clamp01(1f - d);
                    Color32 baseColor = d > -borderThickness ? border : fill;
                    pixels[y * diameter + x] = new Color32(baseColor.r, baseColor.g, baseColor.b,
                        (byte)Mathf.RoundToInt(baseColor.a * alpha));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, diameter, diameter), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _cache[key] = sprite;
            return sprite;
        }
    }
}
