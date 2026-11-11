using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>Which distribution the noise samples come from.</summary>
public enum NoiseKind : byte
{
    /// <summary>Uniform in [-amount, amount).</summary>
    Uniform,

    /// <summary>Normal with standard deviation <c>amount</c>, from the sum of twelve uniforms.</summary>
    Gaussian,
}

/// <summary>
/// Additive noise from a fixed seed. The generator is xorshift128+ seeded through splitmix64: no
/// <see cref="Random"/>, whose sequence is a runtime implementation detail, and no transcendental functions, so
/// the same seed produces the same bytes on every platform.
/// </summary>
public static class NoiseOps
{
    /// <summary>Adds noise to the colour channels; alpha is untouched.</summary>
    /// <param name="source">The pixels.</param>
    /// <param name="amount">Amplitude in normalised units, 0..1.</param>
    /// <param name="kind">Uniform or Gaussian.</param>
    /// <param name="seed">Sequence seed; the same seed always yields the same image.</param>
    /// <param name="pool">Where the destination is rented.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative or above 1.</exception>
    public static WorkingBuffer Noise(PixelView source, float amount = 0.05f, NoiseKind kind = NoiseKind.Gaussian,
        uint seed = 1, IPixelPool? pool = null)
    {
        if (!(amount >= 0f) || amount > 1f)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "Amount must be in [0, 1].");

        var width = source.Width;
        var stride = width * 4;
        var destination = WorkingBuffer.Rent(width, source.Height, source.Format, pool);
        var random = new Rng(seed);
        var row = new float[stride];

        for (var y = 0; y < source.Height; y++)
        {
            Pixels.ReadRowRaw(source.Row(y), width, source.Format, row);

            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                for (var c = 0; c < 3; c++)
                {
                    var sample = kind == NoiseKind.Uniform ? random.NextSigned() : random.NextGaussian();
                    row[offset + c] = Math.Clamp(row[offset + c] + sample * amount, 0f, 1f);
                }
            }

            Pixels.WriteRowRaw(row, width, source.Format, destination.View.Row(y));
        }

        return destination;
    }

    /// <summary>xorshift128+ with a splitmix64-seeded state.</summary>
    private struct Rng
    {
        private ulong _s0;
        private ulong _s1;

        public Rng(uint seed)
        {
            var state = 0x9E3779B97F4A7C15UL ^ seed;
            _s0 = SplitMix(ref state);
            _s1 = SplitMix(ref state);
            if (_s0 == 0 && _s1 == 0)
                _s0 = 1;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public float NextUnit() => Next() * (1f / 4294967296f);

        /// <summary>Uniform in [-1, 1).</summary>
        public float NextSigned() => NextUnit() * 2f - 1f;

        /// <summary>Standard normal from twelve uniforms: each has variance 1/12, so the sum minus six has
        /// variance one. No logarithm or cosine, so the sequence cannot drift between C runtimes.</summary>
        public float NextGaussian()
        {
            var sum = 0f;
            for (var i = 0; i < 12; i++)
                sum += NextUnit();
            return sum - 6f;
        }

        private uint Next()
        {
            var s1 = _s0;
            var s0 = _s1;
            _s0 = s0;
            s1 ^= s1 << 23;
            _s1 = s1 ^ s0 ^ (s1 >> 17) ^ (s0 >> 26);
            return (uint)(_s1 + s0);
        }

        private static ulong SplitMix(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
