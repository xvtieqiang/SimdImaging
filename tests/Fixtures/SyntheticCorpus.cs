using System.Security.Cryptography;

namespace SimdImaging.Tests;

/// <summary>
/// Offline substitute for the CC0 photo corpus. Every fixture is generated from integer arithmetic
/// with a fixed tag — no Random, no clock, no locale — so its SHA-256 is reproducible on every
/// machine and under every SIMD tier. Images are never committed (AGENTS.md clean-room rules).
/// </summary>
internal static class SyntheticCorpus
{
    public const int Width = 64;
    public const int Height = 48;

    public static readonly string[] Tags =
    [
        "high-frequency",
        "thin-diagonal-lines",
        "portrait-hair",
        "transparent-edge",
        "hdr-highlight",
        "texture-repeat",
        "perspective-rotation",
        "noise-blur",
        "downscale-upscale",
    ];

    public static byte[] Generate(string tag)
    {
        var data = new byte[checked(Width * Height * 4)];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * Width + x) * 4;
                var (r, g, b, a) = tag switch
                {
                    "high-frequency" => ((x + y) % 2 == 0 ? 255 : 0, (x + y) % 2 == 0 ? 0 : 255, 90, 255),
                    "thin-diagonal-lines" => ((x + y) % 17 == 0 ? 255 : 8, 12, 20, 255),
                    "portrait-hair" => ((x * 7 + y * 3) % 11 < 2 ? 240 : 30, 60, 45, 255),
                    // Associated-alpha edge: RGB stays coloured while alpha fades, the exact case
                    // where straight-alpha interpolation produces a black fringe.
                    "transparent-edge" => (240, 90, 20, Math.Clamp(x * 4, 0, 255)),
                    "hdr-highlight" => (Math.Min(255, 128 + (x * y) % 200), 200, 255, 255),
                    "texture-repeat" => (((x % 8) ^ (y % 8)) * 16, (x % 8) * 31, (y % 8) * 31, 255),
                    "perspective-rotation" => ((x * 3 + y * 2) % 31 * 8, 100, 220, 255),
                    "noise-blur" => ((x / 6 + y / 8) * 17 % 256, 128, 64, 255),
                    "downscale-upscale" => (((x / 4 + y / 4) % 2) * 200 + 20, 40, 180, 255),
                    _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "Unknown quality tag."),
                };

                data[offset] = (byte)r;
                data[offset + 1] = (byte)g;
                data[offset + 2] = (byte)b;
                data[offset + 3] = (byte)a;
            }
        }

        return data;
    }

    /// <summary>Full SHA-256 of a fixture, for pinning quality-test inputs in reports.</summary>
    public static string Sha256(string tag) => Convert.ToHexString(SHA256.HashData(Generate(tag)));
}
