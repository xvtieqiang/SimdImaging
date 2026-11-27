using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Processing;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// A rented buffer's contents are undefined, and nothing in the engine may depend on them being zero. This is the
/// test that holds that line: the same plan runs twice against pools that differ only in what they hand out, and
/// any byte an operator failed to write shows up as a difference.
/// </summary>
/// <remarks>
/// The failure this catches is silent by construction — a kernel that skips the alpha channel, or stops one row
/// early, produces a plausible image whose missing bytes are the previous tenant's pixels. It also means an image
/// can carry another image's data across calls, which is why the guard is a property and not a code comment.
/// Both sizes matter: 96x64 is under the fusion gate (the chain runs in band windows, so a half-written window is
/// read by the next step), 320x240 is over it (the chain runs stepwise, so a half-written frame is read).
/// </remarks>
public sealed class PoolContentsTests
{
    public static TheoryData<string, int, int> Chains => new()
    {
        { "point", 96, 64 },
        { "point", 320, 240 },
        { "blur-point", 96, 64 },
        { "blur-point", 320, 240 },
        { "global", 96, 64 },
        { "lut", 96, 64 },
        { "composite", 96, 64 },
        { "convolve", 96, 64 },
    };

    [Theory]
    [MemberData(nameof(Chains))]
    public void PoolContents_DoNotChangeTheResult(string chain, int width, int height)
    {
        using var source = TestImages.Gradient(width, height, WorkingFormat.Rgba8);
        using var overlay = TestImages.Gradient(16, 12, WorkingFormat.Rgba8);
        var plan = Build(chain, overlay);

        using var poisoned = plan.Execute(source[0].Pixels.View, new FillPool(0xA5));
        using var zeroed = plan.Execute(source[0].Pixels.View, new FillPool(0x00));

        var first = -1;
        for (var i = 0; i < poisoned.View.Span.Length && first < 0; i++)
        {
            if (poisoned.View.Span[i] != zeroed.View.Span[i])
                first = i;
        }

        first.ShouldBe(-1,
            $"{chain} at {width}x{height}: byte {first} depends on what the pool handed out, so some step left " +
            "part of its destination unwritten");
    }

    /// <summary>
    /// The plan records the buffer, not only the view, so a second input that went back to the pool is reported
    /// instead of read. A view alone cannot be checked — this is the reason the <c>WorkingBuffer</c> overload exists.
    /// </summary>
    [Fact]
    public void DisposedSecondInput_IsRefusedBeforeTheFirstStep()
    {
        using var background = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        var overlay = TestImages.Gradient(8, 8, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Composite(4, 4), overlay[0].Pixels);
        overlay.Dispose();

        var error = Should.Throw<ObjectDisposedException>(() => plan.Execute(background[0].Pixels.View));
        error.Message.ShouldContain("second input");
    }

    /// <summary>The plan does not take ownership: the buffer is the caller's and survives the run.</summary>
    [Fact]
    public void SecondInputBuffer_IsNotDisposedByThePlan()
    {
        using var background = TestImages.Gradient(32, 24, WorkingFormat.Rgba8);
        using var overlay = TestImages.Gradient(8, 8, WorkingFormat.Rgba8);
        var plan = new ProcessingPlan().Add(PlanOps.Composite(4, 4), overlay[0].Pixels);

        using var result = plan.Execute(background[0].Pixels.View);

        overlay[0].Pixels.IsDisposed.ShouldBeFalse();
        Should.NotThrow(() => overlay[0].Pixels.View);
    }

    private static ProcessingPlan Build(string chain, Image overlay) => chain switch
    {
        "point" => new ProcessingPlan().Add(PlanOps.Invert()).Add(PlanOps.Contrast(1.2f)),
        "blur-point" => new ProcessingPlan().Add(PlanOps.GaussianBlur(2f)).Add(PlanOps.Invert()),
        "global" => new ProcessingPlan().Add(PlanOps.Equalize()).Add(PlanOps.Invert()),
        "lut" => new ProcessingPlan().Add(PlanOps.Invert()).AddPointLut("tone", ToneTable()),
        "composite" => new ProcessingPlan().Add(PlanOps.Invert()).Add(PlanOps.Composite(4, 4), overlay[0].Pixels),
        _ => new ProcessingPlan().Add(PlanOps.Convolve([0f, 0.25f, 0f, 0.25f, 0f, 0.25f, 0f, 0.25f, 0f], 1)),
    };

    private static byte[] ToneTable()
    {
        var table = new byte[4 * 256];
        for (var i = 0; i < table.Length; i++)
            table[i] = (byte)(255 - (i & 0xFF));
        return table;
    }

    /// <summary>
    /// Hands out pool memory pre-filled with one value. Thread-safe because a fused segment rents its windows from
    /// band workers; the fill is on the rent the caller owns, so it needs no lock of its own.
    /// </summary>
    private sealed class FillPool(byte value) : IPixelPool
    {
        private readonly ArrayPixelPool _inner = ArrayPixelPool.Shared;

        public string Name => $"fill-{value:x2}";

        public IMemoryOwner<byte> Rent(int length)
        {
            var owner = _inner.Rent(length);
            owner.Memory.Span.Slice(0, length).Fill(value);
            return owner;
        }
    }
}
