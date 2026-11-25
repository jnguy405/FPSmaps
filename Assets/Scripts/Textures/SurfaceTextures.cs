using System.Collections.Generic;
using UnityEngine;

// the numbers are fixed on purpose: the inspector stores kinds by number, and each texture's noise seed comes from it
// new kinds take an unused number
public enum SurfaceKind { Sandstone = 1, SandstoneTiles = 3 }

// Assigned: the floor and wall materials as they are, Fixed: the chosen noise textures,
// RandomFromSeed: picked from the seed, together with any extra materials passed in
public enum SurfaceMode { Assigned, Fixed, RandomFromSeed }

// noise textures for map surfaces, drawn in code so they need no image files
// sandstone tiles repeat every 2 world units at the default uvScale and chalky sandstone every 4,
// each cached per kind and variant so rebuilding a map doesn't redraw it
public static class SurfaceTextures
{
    public const int Variants = 8;

    // plaster tones for chalky sandstone, a darker and lighter shade each, one per variant (wrapping around)
    static readonly (Color dark, Color light)[] PlasterTones =
    {
        (Hex(0xBF, 0xA0, 0x74), Hex(0xE6, 0xD4, 0xB3)),   // cream
        (Hex(0xC9, 0x9A, 0x72), Hex(0xEE, 0xD3, 0xB8)),   // peach
        (Hex(0xB8, 0x8E, 0x55), Hex(0xE0, 0xC0, 0x8A)),   // ochre
        (Hex(0xB0, 0xA6, 0x94), Hex(0xDD, 0xD6, 0xC8)),   // pale grey beige
        (Hex(0xC4, 0x9A, 0x86), Hex(0xEA, 0xD0, 0xC2)),   // rose sand
        (Hex(0xC8, 0xA0, 0x60), Hex(0xEB, 0xD5, 0x9E)),   // golden
    };

    // chalky plaster is drawn twice as big and spread over twice the area, so its flaked spots repeat less
    // while the detail stays as sharp as the tiles
    public static int SizeOf(SurfaceKind kind) => kind == SurfaceKind.Sandstone ? 512 : 256;

    // texture repeats per mesh UV unit, set on the material so larger textures cover more of the wall
    public static float TileScale(SurfaceKind kind) => 256f / SizeOf(kind);

    // which kinds suit floors and walls when picking at random
    // no floor kinds yet: floors keep the plain assigned material until a ground texture is chosen
    // (the sandstone tiles read as wood planks from floor level, so they're only used when picked in Fixed mode)
    public static readonly SurfaceKind[] FloorKinds = { };
    public static readonly SurfaceKind[] WallKinds = { SurfaceKind.Sandstone };

    static readonly Dictionary<(SurfaceKind, int), Texture2D> cache = new Dictionary<(SurfaceKind, int), Texture2D>();

    public static string Name(SurfaceKind kind) => kind == SurfaceKind.Sandstone ? "Chalky sandstone" : "Sandstone tiles";

    // the texture for a kind and variant, drawn the first time it's asked for
    public static Texture2D Get(SurfaceKind kind, int variant)
    {
        if (cache.TryGetValue((kind, variant), out var tex) && tex != null) return tex;

        int seed = (int)kind * 1000 + variant, size = SizeOf(kind);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                pixels[y * size + x] = kind == SurfaceKind.Sandstone ? ChalkySandstone(u, v, seed) : SandstoneTiles(u, v, seed);
            }
        }

        tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = $"{Name(kind)} {variant}",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4
        };
        tex.SetPixels32(pixels);
        tex.Apply(true);
        cache[(kind, variant)] = tex;
        return tex;
    }

    // SURFACES

    // sun-baked plaster over sandstone, the chalky look of desert tactical shooter walls:
    // low contrast plaster with swirled blotches, powdery light patches and sand grain,
    // plus spots where the plaster has flaked off the darker stone and hairline cracks around them
    // each variant is its own wall: a different tone, more or less wear, and stone courses shown or not
    static Color ChalkySandstone(float u, float v, int seed)
    {
        int variant = seed % 1000;
        var tone = PlasterTones[variant % PlasterTones.Length];
        float flakeLevel = Mathf.Lerp(0.7f, 0.8f, Noise.Rand(variant, 0, 4242));   // lower means more flaked spots
        float chalkAmount = Mathf.Lerp(0.45f, 0.9f, Noise.Rand(variant, 1, 4242));
        bool courses = Noise.Rand(variant, 2, 4242) > 0.4f;

        // warping the blotch noise by more noise gives the soft swirls of hand troweled plaster
        float wu = u + (Noise.Fbm(u, v, 3, 3, seed + 1) - 0.5f) * 0.2f;
        float wv = v + (Noise.Fbm(u, v, 3, 3, seed + 2) - 0.5f) * 0.2f;
        float blotch = Noise.Fbm(wu, wv, 3, 5, seed + 3);
        var c = Color.Lerp(tone.dark, tone.light, Mathf.SmoothStep(0.2f, 0.8f, blotch));

        // powdery chalk patches, lighter and a touch less saturated
        float chalk = Noise.Fbm(wu, wv, 4, 4, seed + 5);
        c = Color.Lerp(c, Color.Lerp(tone.light, Color.white, 0.4f), Mathf.SmoothStep(0.56f, 0.7f, chalk) * chalkAmount);

        // flaked spots: plaster missing, darker stone showing, with a crisp pale lip around the edge
        float flake = Noise.Fbm(u, v, 8, 4, seed + 7);
        float grain = Noise.Fbm(u, v, 96, 2, seed + 9) - 0.5f;
        if (flake > flakeLevel)
        {
            float rough = Noise.Fbm(u, v, 48, 2, seed + 11) - 0.5f;
            return Shade(Color.Lerp(tone.dark, Hex(0x8E, 0x62, 0x38), 0.55f), 1f + rough * 0.3f + grain * 0.2f);
        }
        if (flake > flakeLevel - 0.015f) return Shade(Color.Lerp(tone.light, Color.white, 0.5f), 1f + grain * 0.1f);

        // hairline cracks, only right around the flaked spots where the plaster is already failing
        Noise.Worley(u, v, 10, seed + 13, out float f1, out float f2, out _);
        if (flake > flakeLevel - 0.08f && f2 - f1 < 0.02f) c = Shade(c, 0.8f);

        // small scale dappling, faint stone courses showing through every half unit on some variants,
        // then sand grain and scattered light and dark specks
        float dapple = Noise.Fbm(u, v, 16, 3, seed + 17) - 0.5f;
        float speck = Noise.Value(u, v, 256, seed + 15);
        float k = 1f + dapple * 0.16f + grain * 0.12f;
        if (courses)
        {
            float course = Mathf.Repeat(v * 8f + (Noise.Fbm(u, v, 4, 2, seed + 19) - 0.5f) * 0.4f, 1f);
            if (course < 0.022f && Noise.Value(u, v, 6, seed + 23) > 0.5f) k *= 0.94f;   // patchy, so it reads as stone under plaster
        }
        k += speck > 0.9f ? -0.07f : speck < 0.06f ? 0.05f : 0f;
        return Shade(c, k);
    }

    // sandstone slabs in a running bond (1 x 0.5 units, every other row shifted by half a slab),
    // each cut from a different part of the rock so its strata bands sit at their own height
    static Color SandstoneTiles(float u, float v, int seed)
    {
        const int cols = 2, rows = 4;
        int row = Mathf.FloorToInt(v * rows);
        float su = u * cols + (row % 2) * 0.5f;
        int col = Mathf.FloorToInt(su);
        float fu = su - col, fv = v * rows - row;
        int slab = col % cols + row * cols;

        // thin dark seams between slabs, about 2 pixels wide
        if (fu < 0.015f || fv < 0.03f) return Hex(0x6E, 0x4C, 0x2E);

        // strata run along the slab, bent a little by noise
        // stepped into a few distinct layers, so the bands read as crisp rock layers rather than smudges
        float warp = Noise.Fbm(u, v, 4, 3, seed + 3) - 0.5f;
        float strataV = v + warp * 0.06f + Noise.Rand(slab, 1, seed);
        float band = Noise.Value(0.5f, strataV, 24, seed + 5);
        band = Mathf.Lerp(band, Mathf.Floor(band * 5f) / 4f, 0.65f);
        var dark = Hex(0xB0, 0x7E, 0x4E);
        var mid = Hex(0xCF, 0xA3, 0x6B);
        var light = Hex(0xE2, 0xC3, 0x94);
        var c = band < 0.5f ? Color.Lerp(dark, mid, band * 2f) : Color.Lerp(mid, light, band * 2f - 1f);

        // grain, dark specks, a shade per slab, and darkening toward the seams
        float grain = Noise.Fbm(u, v, 32, 3, seed + 9) - 0.5f;
        float specks = Noise.Value(u, v, 128, seed + 17) > 0.88f ? -0.12f : 0f;
        float slabShade = (Noise.Rand(slab, 2, seed) - 0.5f) * 0.1f;
        if (Noise.Value(0.5f, strataV, 96, seed + 21) > 0.82f) slabShade -= 0.08f;   // fine darker strata lines
        float seamDist = Mathf.Min(Mathf.Min(fu, 1f - fu) / cols, Mathf.Min(fv, 1f - fv) / rows);
        float bevel = 0.86f + 0.14f * Mathf.SmoothStep(0f, 0.02f, seamDist);
        return Shade(c, (1f + grain * 0.25f + specks + slabShade) * bevel);
    }

    // HELPERS

    static Color Hex(byte r, byte g, byte b) => new Color32(r, g, b, 255);

    // scales a color's brightness, keeping it opaque
    static Color Shade(Color c, float k) => new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), 1f);
}

// one pick for a surface: a noise texture kind and variant, a material passed in,
// or plain (nothing to pick from, so the assigned material stays as it is)
public struct SurfaceChoice
{
    public SurfaceKind kind;
    public int variant;
    public Material material;
    public bool plain;

    public string Name => plain ? "Plain" : material != null ? material.name : SurfaceTextures.Name(kind);

    public SurfaceChoice(SurfaceKind kind, int variant = 0)
    {
        this.kind = kind;
        this.variant = variant;
        material = null;
        plain = false;
    }

    // one of the noise kinds or one of the extra materials, all equally likely, or plain if there are none
    // always draws twice from rng, so the next pick doesn't shift depending on what this one was
    public static SurfaceChoice Pick(System.Random rng, SurfaceKind[] kinds, Material[] extras)
    {
        var pool = new List<Material>();
        if (extras != null)
            foreach (var m in extras)
                if (m != null) pool.Add(m);

        int index = rng.Next(kinds.Length + pool.Count);
        int variant = rng.Next(SurfaceTextures.Variants);
        if (index < kinds.Length) return new SurfaceChoice(kinds[index], variant);
        if (index - kinds.Length < pool.Count) return new SurfaceChoice { material = pool[index - kinds.Length] };
        return new SurfaceChoice { plain = true };
    }
}
