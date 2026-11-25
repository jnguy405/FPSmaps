using UnityEngine;

// tileable noise for generated textures, every position is 0..1 across the tile and wraps at the edges
// integer hashing instead of UnityEngine.Random, so every platform draws the same texture for the same seed
public static class Noise
{
    // well mixed 32-bit hash of a lattice point
    static uint Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u;
            h ^= (uint)x * 0x85EBCA77u;
            h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0xC2B2AE3Du;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return h;
        }
    }

    // 0..1 random value for a lattice point
    public static float Rand(int x, int y, int seed) => (Hash(x, y, seed) & 0xFFFFFF) / 16777215f;

    static int Wrap(int i, int period) => (i % period + period) % period;

    // smooth value noise with period lattice cells across the tile, 0..1
    public static float Value(float u, float v, int period, int seed)
    {
        float x = u * period, y = v * period;
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);

        // smoothstep between lattice points, so there are no creases along the grid
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        int xa = Wrap(x0, period), xb = Wrap(x0 + 1, period);
        int ya = Wrap(y0, period), yb = Wrap(y0 + 1, period);
        float bottom = Mathf.Lerp(Rand(xa, ya, seed), Rand(xb, ya, seed), fx);
        float top = Mathf.Lerp(Rand(xa, yb, seed), Rand(xb, yb, seed), fx);
        return Mathf.Lerp(bottom, top, fy);
    }

    // layered value noise: each octave doubles the detail and halves the strength, 0..1
    public static float Fbm(float u, float v, int period, int octaves, int seed)
    {
        float sum = 0f, amp = 1f, total = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += Value(u, v, period << o, seed + o * 7919) * amp;
            total += amp;
            amp *= 0.5f;
        }
        return sum / total;
    }

    // cellular noise with cells across the tile: distance to the nearest (f1) and second nearest (f2)
    // jittered point, in cell units, plus an id for the nearest cell
    // f2 - f1 is close to 0 along the borders between cells, which is where grout lines go
    public static void Worley(float u, float v, int cells, int seed, out float f1, out float f2, out int id)
    {
        float x = u * cells, y = v * cells;
        int cx = Mathf.FloorToInt(x), cy = Mathf.FloorToInt(y);
        f1 = f2 = float.MaxValue;
        id = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int gx = cx + dx, gy = cy + dy;
                int wx = Wrap(gx, cells), wy = Wrap(gy, cells);

                // points kept away from their cell's edges, so stones come out roughly even in size
                float px = gx + 0.15f + 0.7f * Rand(wx, wy, seed);
                float py = gy + 0.15f + 0.7f * Rand(wx, wy, seed + 1);
                float d = Mathf.Sqrt((px - x) * (px - x) + (py - y) * (py - y));
                if (d < f1)
                {
                    f2 = f1;
                    f1 = d;
                    id = wx + wy * cells;
                }
                else if (d < f2) f2 = d;
            }
        }
    }
}
