using Sdcb.SimdCodecs.Abstractions;
using SimdImaging.Abstractions;
using SimdImaging.Processing;
using SimdImaging.Processing.Compilation;
using SimdImaging.Processing.Contracts;
using SimdImaging.Processing.Fusion;
using SimdImaging.Processing.Geometry;
using Shouldly;
using Xunit;

namespace SimdImaging.Tests;

/// <summary>
/// P2's closing loop: a representative operator, compiled once, executed by every backend that accepts it, compared
/// byte for byte with the others; and where a backend does not accept it, the refusal must be the reason the design
/// says it is. The pixels here come from the compiled sections, not from a hand-written plan.
/// </summary>
public sealed class CompiledBackendsTests
{
    public static TheoryData<WorkingFormat> Formats => new()
    {
        WorkingFormat.Rgba8,
        WorkingFormat.Rgba16,
        WorkingFormat.Rgba32F,
    };

    public static TheoryData<WorkingFormat, int> FormatsAndBands => new()
    {
        { WorkingFormat.Rgba8, 3 },
        { WorkingFormat.Rgba8, 17 },
        { WorkingFormat.Rgba8, 128 },
        { WorkingFormat.Rgba16, 3 },
        { WorkingFormat.Rgba16, 17 },
        { WorkingFormat.Rgba16, 128 },
        { WorkingFormat.Rgba32F, 3 },
        { WorkingFormat.Rgba32F, 17 },
        { WorkingFormat.Rgba32F, 128 },
    };

    [Theory]
    [MemberData(nameof(FormatsAndBands))]
    public void PointSteps_MatchBetweenFrameAndStream(WorkingFormat format, int bandRows)
    {
        var recipe = Recipe(PlanOps.Invert(), PlanOps.Contrast(1.1f));
        using var image = TestImages.Gradient(64, 48, format);

        var frame = Compile(recipe, 64, 48, format, stream: false, bandRows);
        var stream = Compile(recipe, 64, 48, format, stream: true, bandRows);
        stream.Backend.ShouldBe(ExecutionBackend.Stream);

        Frame(frame, image[0].Pixels.View).ShouldBe(StreamBytes(stream, image[0].Pixels.View));
    }

    [Theory]
    [MemberData(nameof(FormatsAndBands))]
    public void GaussianBlurAndPoint_MatchBetweenFrameAndStream(WorkingFormat format, int bandRows)
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f), PlanOps.Invert());
        using var image = TestImages.Gradient(72, 56, format);

        var frame = Compile(recipe, 72, 56, format, stream: false, bandRows);
        var stream = Compile(recipe, 72, 56, format, stream: true, bandRows);
        stream.Backend.ShouldBe(ExecutionBackend.Stream);

        Frame(frame, image[0].Pixels.View).ShouldBe(StreamBytes(stream, image[0].Pixels.View));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void CropBlurResize_MatchBetweenFrameAndStream(WorkingFormat format)
    {
        var recipe = Recipe(GeometrySteps.Crop(new PixelRect(8, 6, 64, 48)), PlanOps.GaussianBlur(1f),
            GeometrySteps.Resize(40, 30, Resampler.Bicubic), PlanOps.Invert());
        using var image = TestImages.Gradient(96, 72, format);

        var frame = Compile(recipe, 96, 72, format, stream: false, bandRows: 9);
        var stream = Compile(recipe, 96, 72, format, stream: true, bandRows: 9);
        stream.Backend.ShouldBe(ExecutionBackend.Stream);
        stream.Width.ShouldBe(40);
        stream.Height.ShouldBe(30);

        Frame(frame, image[0].Pixels.View).ShouldBe(StreamBytes(stream, image[0].Pixels.View));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void PadAndFlip_MatchBetweenFrameAndStream(WorkingFormat format)
    {
        var recipe = Recipe(GeometrySteps.Pad(3, 4, 5, 6, new RgbaColor(9, 8, 7, 255)),
            GeometrySteps.FlipHorizontal(), PlanOps.Contrast(1.1f));
        using var image = TestImages.Gradient(48, 40, format);

        var frame = Compile(recipe, 48, 40, format, stream: false);
        var stream = Compile(recipe, 48, 40, format, stream: true);
        stream.Backend.ShouldBe(ExecutionBackend.Stream);
        stream.Width.ShouldBe(56);
        stream.Height.ShouldBe(50);

        Frame(frame, image[0].Pixels.View).ShouldBe(StreamBytes(stream, image[0].Pixels.View));
    }

    [Fact]
    public void Equalize_IsRefusedByStream_AndFrameMatchesTheDirectCall()
    {
        var recipe = Recipe(PlanOps.Equalize());
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);

        var plan = Compile(recipe, 64, 48, WorkingFormat.Rgba8, stream: true);
        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.StreamRefusal.ShouldBe(StreamRefusal.GlobalStep);

        using var direct = HistogramOps.Equalize(image[0].Pixels.View);
        Frame(plan, image[0].Pixels.View).ShouldBe(Rows(direct.View));
    }

    [Fact]
    public void Pixelate_IsBlockAligned_RefusedByStream_AndMatchesTheDirectCall()
    {
        var recipe = Recipe(PlanOps.Pixelate(8));
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);

        var plan = Compile(recipe, 64, 48, WorkingFormat.Rgba8, stream: true);
        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.StreamRefusal.ShouldBe(StreamRefusal.GlobalStep);
        plan.Steps[0].Definition.Contract.Footprint.Kind.ShouldBe(InputFootprintKind.BlockAligned);

        using var direct = FilterOps.Pixelate(image[0].Pixels.View, 8);
        Frame(plan, image[0].Pixels.View).ShouldBe(Rows(direct.View));
    }

    [Fact]
    public void Warp_IsFrameOnly_AndMatchesTheDirectCall()
    {
        PixelWarp map = (float x, float y, out float sx, out float sy) =>
        {
            sx = x - 2f;
            sy = y - 1f;
        };
        var recipe = Recipe(GeometrySteps.Warp(64, 48, map, Resampler.Bilinear, default));
        using var image = TestImages.Gradient(80, 60, WorkingFormat.Rgba8);

        var stream = Compile(recipe, 80, 60, WorkingFormat.Rgba8, stream: true);
        stream.Backend.ShouldBe(ExecutionBackend.Frame);
        stream.StreamRefusal.ShouldBe(StreamRefusal.GeometricStep);
        stream.Width.ShouldBe(64);
        stream.Height.ShouldBe(48);

        using var direct = Sampling.Warp(image[0].Pixels.View, 64, 48, map);
        Frame(stream, image[0].Pixels.View).ShouldBe(Rows(direct.View));
    }

    [Fact]
    public void ConvolveWithMirror_RefusesStream_ButAllFrameBandSizesAgree()
    {
        var kernel = new float[] { 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f };
        var recipe = Recipe(PlanOps.Convolve(kernel, 1, BorderMode.Mirror));
        using var image = TestImages.Gradient(56, 44, WorkingFormat.Rgba8);

        var stream = Compile(recipe, 56, 44, WorkingFormat.Rgba8, stream: true);
        stream.Backend.ShouldBe(ExecutionBackend.Frame);
        stream.StreamRefusal.ShouldBe(StreamRefusal.StepNeedsWholeSource);

        var one = Frame(Compile(recipe, 56, 44, WorkingFormat.Rgba8, stream: false, bandRows: 1),
            image[0].Pixels.View);
        var seven = Frame(Compile(recipe, 56, 44, WorkingFormat.Rgba8, stream: false, bandRows: 7),
            image[0].Pixels.View);
        var whole = Frame(Compile(recipe, 56, 44, WorkingFormat.Rgba8, stream: false, bandRows: 128),
            image[0].Pixels.View);

        one.ShouldBe(seven);
        seven.ShouldBe(whole);
    }

    [Fact]
    public void Noise_IsDeterministic_AndRefusedByStream()
    {
        var recipe = Recipe(PlanOps.Noise(0.05f, NoiseKind.Gaussian, seed: 7));
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);

        var plan = Compile(recipe, 64, 48, WorkingFormat.Rgba8, stream: true);
        plan.StreamRefusal.ShouldBe(StreamRefusal.GlobalStep);

        Frame(plan, image[0].Pixels.View).ShouldBe(Frame(plan, image[0].Pixels.View));
    }

    [Fact]
    public void SeamCarve_ReportsItsTargetSize_RefusesStream_AndMatchesTheDirectCall()
    {
        var recipe = Recipe(GeometrySteps.SeamCarve(40, 34));
        using var image = TestImages.Gradient(48, 40, WorkingFormat.Rgba8);

        var plan = Compile(recipe, 48, 40, WorkingFormat.Rgba8, stream: true);
        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        plan.StreamRefusal.ShouldBe(StreamRefusal.GeometricStep);
        plan.Width.ShouldBe(40);
        plan.Height.ShouldBe(34);

        using var direct = SeamCarving.CarveTo(image[0].Pixels.View, 40, 34);
        Frame(plan, image[0].Pixels.View).ShouldBe(Rows(direct.View));
    }

    [Fact]
    public void CompositeSteps_AreRefusedByStreamAndRegion()
    {
        using var overlay = TestImages.Gradient(8, 8, WorkingFormat.Rgba8);
        var recipe = Recipe(PlanOps.Composite(overlay[0].Pixels.View, 2, 2));

        var stream = Compile(recipe, 32, 32, WorkingFormat.Rgba8, stream: true);
        stream.StreamRefusal.ShouldBe(StreamRefusal.CompositeStep);
        stream.Backend.ShouldBe(ExecutionBackend.Frame);

        var region = Compile(recipe, new CompileOptions
        {
            SourceWidth = 32,
            SourceHeight = 32,
            SourceFormat = WorkingFormat.Rgba8,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
            Region = new PixelRect(0, 0, 16, 16),
        });
        region.RegionRefusal.ShouldBe(RegionRefusal.CompositeStep);
    }

    [Fact]
    public void CompiledFusion_MatchesCompiledStepwise()
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f), PlanOps.Invert(), PlanOps.Contrast(1.1f));
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);

        var fused = Compile(recipe, 64, 48, WorkingFormat.Rgba8, stream: false,
            execute: new ExecuteOptions { ForceFusion = true });
        var stepwise = Compile(recipe, 64, 48, WorkingFormat.Rgba8, stream: false,
            execute: new ExecuteOptions { AllowFusion = false });

        Frame(fused, image[0].Pixels.View).ShouldBe(Frame(stepwise, image[0].Pixels.View));
    }

    [Fact]
    public void ACompiledFrame_InvokesTheObserverForEveryStep_WithGlobalIndices()
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f), PlanOps.Invert(), PlanOps.Contrast(1.1f));
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 48,
            SourceFormat = WorkingFormat.Rgba8,
            ObserveSteps = true,
        });
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);

        var seen = new List<(int Index, string Name)>();
        using var result = CompiledPlanExecutor.ExecuteFrame(plan, image[0].Pixels.View,
            observe: (index, name, _) => seen.Add((index, name)));

        seen.Select(entry => entry.Index).ShouldBe([0, 1, 2]);
        seen.Select(entry => entry.Name).ShouldBe(["GaussianBlur", "Invert", "Contrast"]);
    }

    [Fact]
    public void AnObserverForcesStepwiseFrame()
    {
        var recipe = Recipe(PlanOps.GaussianBlur(1f), PlanOps.Invert());
        var plan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 48,
            SourceFormat = WorkingFormat.Rgba8,
            Source = SourceCapabilities.ForwardOnly,
            Sink = SinkCapabilities.RowWritable,
            ObserveSteps = true,
        });

        plan.Backend.ShouldBe(ExecutionBackend.Frame);
        foreach (var section in plan.Sections)
        {
            if (section.Kind == CompiledSectionKind.OperatorRun)
                section.Fused.ShouldBeFalse("an observer must see every step's own output");
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void FrameWorkspace_CoversTheRentedPeak_ForARepresentativeChain(WorkingFormat format)
    {
        var recipe = Recipe(PlanOps.Invert(), PlanOps.GaussianBlur(1f), PlanOps.Contrast(1.1f));
        using var image = TestImages.Gradient(96, 72, format);
        var plan = Compile(recipe, 96, 72, format, stream: false, bandRows: 9);

        var pool = new SimdImaging.Processing.Fusion.CountingPool(ArrayPixelPool.Shared);
        using var result = CompiledPlanExecutor.ExecuteFrame(plan, image[0].Pixels.View, pool);

        plan.Workspace.BoundedBytes.ShouldBeGreaterThanOrEqualTo(pool.PeakBytes);
    }

    [Fact]
    public void RegionExecution_MatchesTheFrameCrop_ForAPointAndNeighbourhoodChain()
    {
        var recipe = Recipe(PlanOps.Invert(), PlanOps.GaussianBlur(1f));
        var region = new PixelRect(8, 6, 32, 24);
        using var image = TestImages.Gradient(64, 48, WorkingFormat.Rgba8);

        var regionPlan = Compile(recipe, new CompileOptions
        {
            SourceWidth = 64,
            SourceHeight = 48,
            SourceFormat = WorkingFormat.Rgba8,
            Source = SourceCapabilities.Seekable | SourceCapabilities.RandomRegion,
            Region = region,
        });
        regionPlan.Backend.ShouldBe(ExecutionBackend.Region);

        using var destination = WorkingBuffer.Rent(region.Width, region.Height, WorkingFormat.Rgba8);
        var result = RegionExecutor.Execute(
            ProcessingPlan.FromCompiled(regionPlan.Steps, regionPlan.BandRows),
            new ViewRegionSource(image[0].Pixels.View), region, destination.View, options: null, pool: null);
        result.Refusal.ShouldBe(RegionRefusal.None);

        var framePlan = Compile(recipe, 64, 48, WorkingFormat.Rgba8, stream: false);
        using var frame = CompiledPlanExecutor.ExecuteFrame(framePlan, image[0].Pixels.View);
        using var cropped = Transforms.Crop(frame.View, region);

        Rows(destination.View).ShouldBe(Rows(cropped.View));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static PipelineRecipe Recipe(params object[] steps)
    {
        var builder = new PipelineRecipeBuilder();
        foreach (var step in steps)
        {
            if (step is ImageOp op)
                builder.Append(op);
            else
                builder.Append((GeometryDescriptor)step);
        }

        return builder.Build();
    }

    private static CompiledExecutionPlan Compile(PipelineRecipe recipe, int width, int height, WorkingFormat format,
        bool stream, int bandRows = 0, ExecuteOptions? execute = null) =>
        Compile(recipe, new CompileOptions
        {
            SourceWidth = width,
            SourceHeight = height,
            SourceFormat = format,
            Source = stream ? SourceCapabilities.ForwardOnly : SourceCapabilities.Seekable,
            Sink = stream ? SinkCapabilities.RowWritable : SinkCapabilities.None,
            BandRows = bandRows,
            Execute = execute ?? new ExecuteOptions(),
        });

    private static CompiledExecutionPlan Compile(PipelineRecipe recipe, CompileOptions options) =>
        PipelineCompiler.Compile(recipe, options);

    private static byte[] Frame(CompiledExecutionPlan plan, PixelView source)
    {
        using var buffer = CompiledPlanExecutor.ExecuteFrame(plan, source);
        return Rows(buffer.View);
    }

    private static byte[] StreamBytes(CompiledExecutionPlan plan, PixelView source)
    {
        var chain = CompiledPlanExecutor.CreateStreamChain(plan, new ViewSource(source), new ExecuteOptions());
        using var owned = chain as IDisposable;
        var sink = new CollectSink(plan.Width, plan.Height, source.Format);
        Pipeline.Copy(chain, sink, default);
        return sink.Bytes;
    }

    private static byte[] Rows(PixelView view)
    {
        var bytes = new byte[view.Height * view.RowBytes];
        for (var y = 0; y < view.Height; y++)
            view.Row(y).CopyTo(bytes.AsSpan(y * view.RowBytes, view.RowBytes));
        return bytes;
    }

    private sealed class ViewSource(PixelView view) : IRowSource
    {
        private int _row;

        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;

        public int ReadRows(Span<byte> destination, int stride, int maxRows)
        {
            if (stride == 0)
                stride = view.RowBytes;
            var rows = Math.Min(maxRows, view.Height - _row);
            for (var y = 0; y < rows; y++)
                view.Row(_row + y).CopyTo(destination.Slice(y * stride, view.RowBytes));
            _row += rows;
            return rows;
        }
    }

    private sealed class CollectSink(int width, int height, WorkingFormat format) : IRowSink
    {
        private readonly int _rowBytes = width * format.BytesPerPixel();
        private int _written;

        public byte[] Bytes { get; } = new byte[width * height * format.BytesPerPixel()];

        public void WriteRows(ReadOnlySpan<byte> rows, int stride, int rowCount)
        {
            if (stride == 0)
                stride = _rowBytes;
            if (_written + rowCount > height || (long)stride * rowCount > rows.Length)
                throw new InvalidOperationException($"sink overflow: {_written}+{rowCount} of {height}");

            for (var y = 0; y < rowCount; y++)
                rows.Slice(y * stride, _rowBytes).CopyTo(Bytes.AsSpan((_written + y) * _rowBytes));
            _written += rowCount;
        }
    }

    private sealed class ViewRegionSource(PixelView view) : IRegionSource
    {
        public int Width => view.Width;
        public int Height => view.Height;
        public WorkingFormat Format => view.Format;

        public void ReadRegion(PixelRect region, PixelView destination)
        {
            var bytes = view.Format.BytesPerPixel();
            for (var y = 0; y < region.Height; y++)
            {
                view.Row(region.Y + y).Slice(region.X * bytes, region.Width * bytes)
                    .CopyTo(destination.Row(y));
            }
        }
    }
}
