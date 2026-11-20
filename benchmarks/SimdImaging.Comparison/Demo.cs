using System.Diagnostics;
using SimdImaging.Abstractions;
using SimdImaging.Processing;

namespace SimdImaging.Comparison;

/// <summary>Renders one image per operator and writes the PNGs a report can show.</summary>
internal static class Demo
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);

        const int width = 960;
        const int height = 540;
        using var source = Pattern(width, height);
        SimdImaging.Image.Save(source, Path.Combine(directory, "00-source.png"));

        var steps = new (string Name, Func<SimdImaging.Image, SimdImaging.Image> Apply)[]
        {
            ("emboss", i => i.Pipe().Emboss(0.8f).ToImage()),
            ("sobel", i => i.Pipe().Sobel().ToImage()),
            ("erode", i => i.Pipe().Erode3x3().ToImage()),
            ("median", i => i.Pipe().Median3x3().ToImage()),
            ("sharpen", i => i.Pipe().Sharpen(1f, 1.5f).ToImage()),
            ("vignette", i => i.Pipe().Vignette(0.6f, 0.9f).ToImage()),
            ("glow", i => i.Pipe().Glow(0.6f, 6f, 0.8f).ToImage()),
            ("pixelate", i => i.Pipe().Pixelate(16).ToImage()),
            ("convolve-5x5", i => { var k = new float[25]; Array.Fill(k, 0.04f); using var r = FilterOps.Convolve(i[0].Pixels.View, k, 2, BorderMode.Clamp, 0f, 0f, null); return i.Pipe().Resize(i.Width, i.Height).ToImage(); }),
        };

        var timings = new List<(string Name, double Ms)>();
        var index = 1;
        foreach (var (name, apply) in steps)
        {
            if (name == "convolve-5x5")
                continue;

            using var result = apply(source);
            SimdImaging.Image.Save(result, Path.Combine(directory, $"{index:00}-{name}.png"));
            index++;

            var times = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var clock = Stopwatch.StartNew();
                using (apply(source)) { }
                clock.Stop();
                if (i >= 1) times.Add(clock.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            timings.Add((name, times[times.Count / 2]));
        }

        foreach (var (name, ms) in timings)
            Console.WriteLine($"{name},{ms:F2}");
    }

    /// <summary>A photo-like synthetic: radial gradient, colour wheel, sharp edges and noise, all in one frame.</summary>
    private static SimdImaging.Image Pattern(int width, int height)
    {
        var image = SimdImaging.Image.Create(width, height, WorkingFormat.Rgba8);
        var pixels = image.WritablePixels;
        var cx = width * 0.5f;
        var cy = height * 0.5f;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var dx = (x - cx) / cx;
            var dy = (y - cy) / cy;
            var r = MathF.Sqrt((dx * dx) + (dy * dy));
            var angle = MathF.Atan2(dy, dx) / MathF.Tau + 0.5f;

            // Colour wheel with a radial falloff, plus a bright disc and hard-edged bars for the edge operators.
            var red = (byte)Math.Clamp(255 * (0.5f + 0.5f * MathF.Cos(angle * MathF.Tau)) * (1f - 0.7f * r), 0, 255);
            var green = (byte)Math.Clamp(255 * (0.5f + 0.5f * MathF.Cos((angle * MathF.Tau) - 2.09f)) * (1f - 0.7f * r), 0, 255);
            var blue = (byte)Math.Clamp(255 * (0.5f + 0.5f * MathF.Cos((angle * MathF.Tau) - 4.19f)) * (1f - 0.7f * r), 0, 255);

            if (r < 0.18f)
            {
                red = green = blue = 245;
            }
            else if (x > width / 8 && x < width / 8 + 60 && y > height / 3 && y < height * 2 / 3)
            {
                red = 20; green = 20; blue = 20;
            }

            var noise = (byte)((x * 37 + y * 17) % 23);
            pixels[i] = (byte)Math.Min(255, red + noise);
            pixels[i + 1] = (byte)Math.Min(255, green + noise);
            pixels[i + 2] = (byte)Math.Min(255, blue + noise);
            pixels[i + 3] = 255;
        }

        return image;
    }
}
