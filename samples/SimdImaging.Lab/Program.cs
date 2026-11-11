using System.Collections.Concurrent;
using System.Diagnostics;
using SimdImaging;
using SimdImaging.Abstractions;
using SimdImaging.Lab;
using SimdImaging.Processing;
using OurImage = SimdImaging.Image;
using TheirImage = SixLabors.ImageSharp.Image;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Bench state lives in memory: this is a test bench, not a service — but it is a bench that holds 4K PNGs,
// and a 4K chain writes seven of them per run. Uncapped dictionaries grew the process past 400 MiB in one
// six-step run, which reads as "the library leaks" when it is the bench's own bookkeeping. Bounded FIFO
// keeps the last few results reachable and the process honest about what it retains.
var images = new ConcurrentDictionary<string, Entry>();
var imageOrder = new ConcurrentQueue<string>();
var outputs = new ConcurrentDictionary<string, byte[]>();
var outputOrder = new ConcurrentQueue<string>();

// Capped by BYTES, not by count. A count cap is meaningless when one entry can be 10 MiB: 48 retained 4K
// PNGs is half a gigabyte, while the same 48 of a 1080p JPEG is 4 MiB. One long[] because Interlocked needs
// a ref and captured locals cannot be passed by ref.
const long MaxImageBytes = 64L * 1024 * 1024;
const long MaxOutputBytes = 96L * 1024 * 1024;
var imageBytes = new long[1];
var outputBytes = new long[1];

void StoreImage(string id, Entry entry)
{
    images[id] = entry;
    Interlocked.Add(ref imageBytes[0], entry.Bytes.Length);
    imageOrder.Enqueue(id);
    while ((Volatile.Read(ref imageBytes[0]) > MaxImageBytes || imageOrder.Count > 64)
        && imageOrder.TryDequeue(out var old))
    {
        if (images.TryRemove(old, out var removed))
            Interlocked.Add(ref imageBytes[0], -removed.Bytes.Length);
    }
}

void StoreOutput(string id, byte[] bytes)
{
    outputs[id] = bytes;
    Interlocked.Add(ref outputBytes[0], bytes.Length);
    outputOrder.Enqueue(id);
    while ((Volatile.Read(ref outputBytes[0]) > MaxOutputBytes || outputOrder.Count > 96)
        && outputOrder.TryDequeue(out var old))
    {
        if (outputs.TryRemove(old, out var removed))
            Interlocked.Add(ref outputBytes[0], -removed.Length);
    }
}

// Deterministic synthetic images, shared by /api/sample and by the recipes that composite a demo layer.
// Returns an unencoded image so a chain can composite it directly without a PNG round-trip.
static OurImage RenderSample(int width, int height, string kind)
{
var image = OurImage.Create(width, height, WorkingFormat.Rgba8);
var span = image.WritablePixels;
for (var y = 0; y < height; y++)
{
    var row = y * width * 4;
    for (var x = 0; x < width; x++)
    {
        var i = row + x * 4;
        float r, g, b;
        var alpha = 1f;
        if (kind == "alpha")
        {
            // Six vertical colour bars under a radial alpha ramp. Generated rather than shipped as a file:
            // the lab must not depend on another repository's assets for its alpha test chart.
            (r, g, b) = (x * 6 / Math.Max(1, width)) switch
            {
                0 => (1f, 0f, 0f), 1 => (1f, 1f, 0f), 2 => (0f, 1f, 0f),
                3 => (0f, 1f, 1f), 4 => (0f, 0f, 1f), _ => (1f, 0f, 1f),
            };
            // The falloff has to reach 0 *inside* the card, or every Porter-Duff mode reads as "a solid
            // rectangle was erased". Normalised to the half-diagonal so the transparent band clears the corners.
            var adx = (x - width * 0.5f) / (width * 0.5f);
            var ady = (y - height * 0.5f) / (height * 0.5f);
            alpha = Math.Clamp(1.45f - 1.5f * MathF.Sqrt(adx * adx + ady * ady), 0f, 1f);
        }
        else if (kind == "portrait")
        {
            var dx = (x - width * 0.5f) / width;
            var dy = (y - height * 0.45f) / height;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            r = Math.Clamp(0.9f - d * 1.6f, 0, 1);
            g = Math.Clamp(0.7f - d * 1.2f, 0, 1);
            b = Math.Clamp(0.55f + d * 0.9f, 0, 1);
            if (y > height * 0.78f) { r *= 0.35f; g *= 0.4f; b *= 0.5f; }
        }
        else if (kind == "detail")
        {
            var checker = ((x / 8) + (y / 8)) % 2 == 0 ? 1f : 0.35f;
            var stripe = MathF.Sin(x * 0.25f) * 0.5f + 0.5f;
            r = checker * stripe;
            g = checker * (1 - stripe);
            b = ((x ^ y) & 0x3F) / 63f;
        }
        else if (kind == "glow")
        {
            // Black backdrop with soft coloured blobs. This is the overlay that makes Screen / Add /
            // ColorDodge / Lighten legible in one glance: black contributes nothing, only the light
            // survives — the opposite of what a photograph-on-photograph demo shows (nothing).
            var dx1 = (x - width * 0.26f) / (width * 0.22f);
            var dy1 = (y - height * 0.30f) / (height * 0.28f);
            var dx2 = (x - width * 0.70f) / (width * 0.20f);
            var dy2 = (y - height * 0.58f) / (height * 0.30f);
            var dx3 = (x - width * 0.46f) / (width * 0.26f);
            var dy3 = (y - height * 0.82f) / (height * 0.24f);
            var b1 = MathF.Exp(-(dx1 * dx1 + dy1 * dy1) * 1.4f);
            var b2 = MathF.Exp(-(dx2 * dx2 + dy2 * dy2) * 1.6f);
            var b3 = MathF.Exp(-(dx3 * dx3 + dy3 * dy3) * 2.0f);
            r = Math.Clamp(b1 * 1.0f + b2 * 0.35f + b3 * 0.75f, 0f, 1f);
            g = Math.Clamp(b1 * 0.55f + b2 * 0.90f + b3 * 0.35f, 0f, 1f);
            b = Math.Clamp(b1 * 0.20f + b2 * 1.00f + b3 * 0.90f, 0f, 1f);
        }
        else if (kind == "beam")
        {
            // A smooth whole-frame gradient, not blobs. A bright blob under Lighten/Screen flattens a disc of the
            // picture to a single value, and on a face that reads as a grey smudge — which is exactly how it
            // looked. Real light falls across the frame; 0..0.5 is the amplitude that brightens without
            // blowing the highlights.
            var u = width > 1 && height > 1 ? 0.5f * (x / (float)(width - 1) + y / (float)(height - 1)) : 0f;
            r = g = b = 0.35f * u;
        }
        else if (kind == "shadow")
        {
            // The mirror of beam: white at the top-left (identity for Darken / ColorBurn) falling to 0.55 at the
            // bottom-right. Same reason — a soft whole-frame gradient darkens the picture, a dark blob smears it.
            var u = width > 1 && height > 1 ? 0.5f * (x / (float)(width - 1) + y / (float)(height - 1)) : 0f;
            r = g = b = 1f - 0.30f * u;
        }
        else if (kind == "dim")
        {
            // Black base, low amplitude: Subtract is b - s, so a bright overlay crushes the whole picture to
            // black. 0.35 is enough to read as "darkened" without clipping.
            var dx1 = (x - width * 0.34f) / (width * 0.22f);
            var dy1 = (y - height * 0.36f) / (height * 0.28f);
            var dx2 = (x - width * 0.66f) / (width * 0.24f);
            var dy2 = (y - height * 0.64f) / (height * 0.30f);
            var d1 = MathF.Exp(-(dx1 * dx1 + dy1 * dy1) * 1.6f);
            var d2 = MathF.Exp(-(dx2 * dx2 + dy2 * dy2) * 1.9f) * 0.8f;
            r = g = b = Math.Clamp(0.35f * Math.Max(d1, d2), 0f, 1f);
        }
        else if (kind == "ramp")
        {
            // Neutral black-to-white gradient, no colour. Under Lighten the picture gets brighter toward the
            // right and stays untouched at the left edge — "the photo brightened", which is what the Chinese
            // name 变亮 promises. A coloured glow reads as "something was pasted on", which is right for Screen
            // (light leak) and wrong for the tonal modes.
            var t = width > 1 ? x / (float)(width - 1) : 0f;
            r = g = b = t;
        }
        else if (kind == "shade")
        {
            // White-to-black ramp: white is the identity for Darken (min(x, 1) = x), so the left edge is the
            // untouched photo and it darkens toward the right. The black-to-white ramp has the opposite
            // problem — under Darken its black end wipes a rectangle of the picture out.
            var t = width > 1 ? 1f - x / (float)(width - 1) : 1f;
            r = g = b = t;
        }
        else if (kind == "light")
        {
            // Mid-grey base: 0.5 is the identity for Overlay / HardLight / SoftLight, so the picture changes only
            // where a blob sits — bright blob lightens locally, dark one darkens. These modes were being shown
            // against the black-backed glow layer, and black under HardLight multiplies the backdrop to 0:
            // a black rectangle pasted over the photo, which is what it looked like.
            var dx1 = (x - width * 0.30f) / (width * 0.16f);
            var dy1 = (y - height * 0.36f) / (height * 0.22f);
            var dx2 = (x - width * 0.68f) / (width * 0.18f);
            var dy2 = (y - height * 0.62f) / (height * 0.24f);
            var bright = MathF.Exp(-(dx1 * dx1 + dy1 * dy1) * 1.6f);
            var dark = MathF.Exp(-(dx2 * dx2 + dy2 * dy2) * 1.6f);
            r = g = b = Math.Clamp(0.5f + 0.5f * bright - 0.42f * dark, 0f, 1f);
        }
        else if (kind == "mark")
        {
            // White card with dark strokes: under Multiply the white drops out and only the strokes
            // print onto the photo — exactly how a watermark or logo layer is actually used. Text is out
            // of scope for this library, so the mark is geometry.
            // No outer frame: a border along the overlay's own edges draws a rectangle around the picture as
            // soon as the layer is shown full-frame, and a blend mode demo must not look like a pasted card.
            var m = Math.Max(2, width / 140);
            var cx = x - width / 2f;
            var cy = y - height / 2f;
            var ring = Math.Abs(MathF.Sqrt(cx * cx + cy * cy) - Math.Min(width, height) * 0.22f) < m;
            var diag = Math.Abs(Math.Abs(cx) - Math.Abs(cy)) < m * 0.8f
                && Math.Abs(cx) < Math.Min(width, height) * 0.30f;
            var ink = ring || diag;
            r = g = b = ink ? 0.08f : 1f;
        }
        else
        {
            var checker = ((x / 24) + (y / 24)) % 2 == 0 ? 1f : 0.6f;
            r = x / (float)(width - 1) * checker;
            g = y / (float)(height - 1) * checker;
            b = ((x + y) % 256) / 255f * checker;
        }

        span[i] = (byte)(Math.Clamp(r, 0, 1) * 255);
        span[i + 1] = (byte)(Math.Clamp(g, 0, 1) * 255);
        span[i + 2] = (byte)(Math.Clamp(b, 0, 1) * 255);
        span[i + 3] = (byte)(alpha * 255);
    }
}


    return image;
}

// A demo overlay sized to fit the backdrop exactly, which is what makes a full-frame blend valid.
static OurImage RenderOverlay(Entry entry, string kind)
{
    using var probe = OurImage.Load(entry.Bytes);
    return RenderSample(probe.Width, probe.Height, kind);
}

app.MapGet("/api/health", () => Results.Text("ok"));

app.MapGet("/api/info", () => Results.Json(new
{
    library = "SimdImaging",
    version = typeof(OurImage).Assembly.GetName().Version?.ToString() ?? "?",
    runtime = Environment.Version.ToString(),
    simd = $"{VectorWidth()} 位 · AVX2={(System.Runtime.Intrinsics.X86.Avx2.IsSupported ? "是" : "否")} · " +
           $"AVX-512={(System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated ? "是" : "否")}",
    cores = Environment.ProcessorCount,
    machine = $"{Environment.OSVersion.VersionString} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
    oracle = typeof(TheirImage).Assembly.GetName().Version?.ToString() ?? "?",
    // The oracle runs on its own default configuration, exactly as an application would: pinning it to one
    // thread would flatter our numbers, and this bench exists to show what a user actually gets.
    oracleThreads = SixLabors.ImageSharp.Configuration.Default.MaxDegreeOfParallelism > 0
        ? SixLabors.ImageSharp.Configuration.Default.MaxDegreeOfParallelism
        : Environment.ProcessorCount,
    opCount = Ops.Catalog.Length,
}));

// Idle compaction. A 4K chain leaves several hundred MiB of collected-but-committed large-object heap behind:
// the buffers are dead, the pages are not returned to Windows until something compacts the LOH. Doing that on
// the request path would add hundreds of milliseconds to a half-second operation, so it waits for the bench
// to go quiet — a newer request bumps the token and the pending compaction gives up.
var activity = 0L;

void ScheduleIdleCompaction()
{
    var token = Interlocked.Increment(ref activity);
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (Interlocked.Read(ref activity) != token)
                return;

            ArrayPixelPool.Shared.Trim();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"idle compaction failed: {e.Message}");
        }
    });
}

// What the bench is actually holding. WorkingSet includes the .NET runtime itself (~60-80 MiB) and the
// shared ArrayPool's retained arrays, which are bucketed to powers of two and never shrink on their own —
// a 33 MiB 4K buffer occupies a 64 MiB bucket. The two dictionary totals are the bench's own contribution.
app.MapGet("/api/memory", (bool? compact) =>
{
    // opt-in: a compacting gen2 with LOH compaction is a full blocking collection, hundreds of ms at this
    // heap size. Worth it when the process is idle and Windows is showing a number someone will ask about;
    // never worth it on the request path.
    if (compact == true)
    {
        ArrayPixelPool.Shared.Trim();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    return Results.Json(new
{
    workingSetMiB = Math.Round(Environment.WorkingSet / 1048576.0, 1),
    privateMiB = Math.Round(Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0, 1),
    // Two numbers on purpose. The first includes everything not yet collected, which is what the process
    // is actually holding; the second is what is still reachable after a full collection. A large gap means
    // garbage waiting for a GC, not a leak — those have completely different fixes.
    managedHeapMiB = Math.Round(GC.GetTotalMemory(forceFullCollection: false) / 1048576.0, 1),
    liveAfterFullGcMiB = Math.Round(GC.GetTotalMemory(forceFullCollection: true) / 1048576.0, 1),
    gen0 = GC.CollectionCount(0),
    gen1 = GC.CollectionCount(1),
    gen2 = GC.CollectionCount(2),
    images = new { count = images.Count, mib = Math.Round(imageBytes[0] / 1048576.0, 1) },
    outputs = new { count = outputs.Count, mib = Math.Round(outputBytes[0] / 1048576.0, 1) },
    limits = new { imagesMiB = MaxImageBytes / 1048576, outputsMiB = MaxOutputBytes / 1048576 },
});
});

app.MapGet("/api/ops", () => Results.Json(Ops.Catalog.Select(o => new
{
    o.Id, o.Group, o.Title, o.Note, o.Params, hasOracle = o.Theirs is not null, o.OracleNote,
})));

app.MapPost("/api/open", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "需要 multipart/form-data" });

    var form = await request.ReadFormAsync();
    if (form.Files.Count == 0)
        return Results.BadRequest(new { error = "没有文件" });

    var file = form.Files[0];
    using var buffer = new MemoryStream();
    await file.CopyToAsync(buffer);
    var bytes = buffer.ToArray();

    try
    {
        using var image = OurImage.Load(bytes);
        var id = Guid.NewGuid().ToString("N");
        StoreImage(id, new Entry(bytes, file.ContentType ?? "application/octet-stream", image.Width, image.Height,
            image.Format.ToString()));
        return Results.Json(new
        {
            id, width = image.Width, height = image.Height, format = image.Format.ToString(),
            bytes = bytes.Length, name = file.FileName,
        });
    }
    catch (Exception e)
    {
        return Results.BadRequest(new { error = $"解码失败：{e.Message}" });
    }
});

// Built-in samples so the bench is usable without hunting for a big picture. Deterministic, and each kind
// targets something different: checker shows resize/blur, detail shows edge operators, portrait shows falloff.
app.MapPost("/api/sample", (SampleRequest request) =>
{
    var width = Math.Clamp(request.Width, 32, 8192);
    var height = Math.Clamp(request.Height, 32, 8192);
    var kind = request.Kind ?? "checker";

    using var image = RenderSample(width, height, kind);

    var encoded = OurImage.Encode(image, Sdcb.SimdCodecs.Abstractions.ImageFormat.Png);
    var sampleId = Guid.NewGuid().ToString("N");
    StoreImage(sampleId, new Entry(encoded, "image/png", width, height, "Rgba8"));
    return Results.Json(new
    {
        id = sampleId, width, height, format = "Rgba8", bytes = encoded.Length,
        name = $"合成图 · {kind switch
        {
            "glow" => "光斑层", "mark" => "水印层", "alpha" => "透明彩条",
            "ramp" => "灰度渐变（暗→亮）", "shade" => "灰度渐变（亮→暗）",
            "light" => "中性灰打光", "beam" => "中性光斑（黑底）",
            "shadow" => "中性暗斑（白底）", "dim" => "微光斑（黑底）",
            "portrait" => "柔光人像", "detail" => "细节图", _ => "棋盘",
        }} · {width}×{height}",
    });
});

app.MapGet("/api/source/{id}", (string id) =>
    images.TryGetValue(id, out var entry) ? Results.Bytes(entry.Bytes, entry.ContentType) : Results.NotFound());

app.MapPost("/api/run", (RunRequest request) =>
{
    if (!images.TryGetValue(request.Id, out var entry))
        return Results.NotFound(new { error = "图片已失效，请重新打开" });

    var spec = Ops.Catalog.FirstOrDefault(o => o.Id == request.Op);
    if (spec is null)
        return Results.BadRequest(new { error = $"未知算子 {request.Op}" });

    var parameters = Ops.Resolve(spec, request.Params);

    var our = MeasureOurs(entry.Bytes, spec, parameters);
    if (our.Png is null)
        return Results.Json(new { error = our.Error ?? "我方执行失败" });

    var their = spec.Theirs is null ? null : MeasureTheirs(entry.Bytes, spec, parameters);

    var outputId = Guid.NewGuid().ToString("N");
    StoreOutput(outputId, our.Png);
    ScheduleIdleCompaction();

    return Results.Json(new
    {
        outputId,
        width = our.Width,
        height = our.Height,
        oursMs = Math.Round(our.Ms, 3),
        theirsMs = their?.Ms is null ? (double?)null : Math.Round(their.Ms, 3),
        speedup = their?.Ms is null || our.Ms <= 0 ? (double?)null : Math.Round(their.Ms / our.Ms, 2),
        oursAllocKiB = Math.Round(our.AllocBytes / 1024.0, 1),
        theirsAllocKiB = their is null ? (double?)null : Math.Round(their.AllocBytes / 1024.0, 1),
        allocRatio = their is null || their.AllocBytes <= 0 ? (double?)null
            : Math.Round((double)their.AllocBytes / Math.Max(1, our.AllocBytes), 2),
        oursWorkingSetMiB = Math.Round(our.WorkingSet / 1048576.0, 1),
        theirsWorkingSetMiB = their is null ? (double?)null : Math.Round(their.WorkingSet / 1048576.0, 1),
        oursBytes = our.Png.Length,
        theirsBytes = their?.EncodedBytes ?? 0,
        theirsError = their?.Error,
        oracleNote = spec.OracleNote,
        parameters,
    });
});

// ---------- 配方（多步骤链） ----------
// Same primitives as /api/run, several of them in one ProcessingPlan. The bench view exists because a
// single-operator number cannot show what a seven-step chain does to the picture or to the peak footprint.

app.MapGet("/api/chains", () => Results.Json(Chains.Catalog.Select(c => new
{
    c.Id, c.Group, c.Title, c.Note, c.Params, c.Caveat,
    steps = c.Steps.Select(s => new { s.Name, category = s.Category.ToString(), s.HaloX, s.HaloY }),
})));

app.MapPost("/api/chain", (ChainRequest request) =>
{
    if (!images.TryGetValue(request.Id, out var entry))
        return Results.NotFound(new { error = "图片已失效，请重新打开" });

    var spec = Chains.Catalog.FirstOrDefault(c => c.Id == request.Chain);
    if (spec is null)
        return Results.BadRequest(new { error = $"未知配方 {request.Chain}" });

    var parameters = Chains.Resolve(spec, request.Params);

    // Recipes that composite a second image generate their demo layer at the backdrop's own size, so the
    // blend is a straight full-frame composite with no resampling in the middle. Disposed at the end of the
    // request — the plan closes over the view, so it has to outlive every Execute inside MeasureChain.
    using var overlay = spec.OverlayDemo is null ? null : RenderOverlay(entry, spec.OverlayDemo);
    var plan = spec.Build(parameters, overlay);

    var measured = MeasureChain(entry.Bytes, plan);
    if (measured.Error is not null)
        return Results.Json(new { error = measured.Error });

    // Second pass, observer on: every intermediate is encoded so the page can show where the picture went
    // wrong. The per-step milliseconds here are single-shot and sit next to an encoder, so they are a rough
    // "where did the time go" hint — `totalMs` from the seven-sample pass above is the number to quote.
    var snapshots = new List<object>(plan.Steps.Count);
    try
    {
        using var decoded = OurImage.Load(entry.Bytes);
        var watch = Stopwatch.StartNew();
        var previous = 0.0;

        using var final = decoded.Apply(plan, null, (index, name, result) =>
        {
            var stepMs = watch.Elapsed.TotalMilliseconds - previous;
            var outputId = Guid.NewGuid().ToString("N");
            StoreOutput(outputId, EncodeView(result, snapshot: true));

            // After the encode, not before: encoding one 1080p PNG costs 100 ms or more, which would be
            // charged to the *next* step if `previous` moved first. That mistake made every step look
            // 130–200 ms while the whole six-step chain reported 67 ms.
            previous = watch.Elapsed.TotalMilliseconds;

            snapshots.Add(new
            {
                index,
                name,
                category = plan.Steps[index].Category.ToString(),
                ms = Math.Round(stepMs, 3),
                outputId,
            });
        });

        var finalId = Guid.NewGuid().ToString("N");
        StoreOutput(finalId, EncodeView(final[0].Pixels.View));
        snapshots.Add(new
        {
            index = plan.Steps.Count,
            name = "结果",
            category = "Output",
            ms = 0.0,
            outputId = finalId,
        });
    }
    catch (Exception e)
    {
        return Results.Json(new { error = $"快照生成失败：{e.Message}" });
    }

    ScheduleIdleCompaction();

    return Results.Json(new
    {
        chain = spec.Id,
        title = spec.Title,
        width = measured.Width,
        height = measured.Height,
        totalMs = Math.Round(measured.Ms, 3),
        allocKiB = Math.Round(measured.AllocBytes / 1024.0, 1),
        workingSetMiB = Math.Round(Environment.WorkingSet / 1048576.0, 1),
        steps = snapshots,
        parameters,
        caveat = spec.Caveat,
    });
});

// Every recipe against one image, in order, single pass each. The chain view shows one chain at a time and a
// seven-sample protocol — right for a number, wrong for a survey: five recipes at 4K would take twenty
// seconds. This answers "what do these look like next to each other", so it runs each once.
app.MapPost("/api/compare", (CompareRequest request) =>
{
    if (!images.TryGetValue(request.Id, out var entry))
        return Results.NotFound(new { error = "图片已失效，请重新打开" });

    var results = new List<object>(Chains.Catalog.Length + 2);

    foreach (var spec in Chains.Catalog)
    {
        try
        {
            using var overlay = spec.OverlayDemo is null ? null : RenderOverlay(entry, spec.OverlayDemo);
            var plan = spec.Build(Chains.Resolve(spec, null), overlay);
            using var decoded = OurImage.Load(entry.Bytes);
            var watch = Stopwatch.StartNew();
            using var result = decoded.Apply(plan);
            watch.Stop();

            var outputId = Guid.NewGuid().ToString("N");
            StoreOutput(outputId, EncodeView(result[0].Pixels.View, snapshot: true));

            results.Add(new
            {
                id = spec.Id, spec.Group, spec.Title,
                ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                steps = plan.Steps.Count,
                outputId,
            });
        }
        catch (Exception e)
        {
            results.Add(new { id = spec.Id, spec.Group, spec.Title, error = e.Message });
        }
    }

    ScheduleIdleCompaction();

    return Results.Json(new
    {
        width = entry.Width,
        height = entry.Height,
        results,
    });
});

// Batch: many images, one operator or one recipe. `concurrency` is exposed rather than fixed because the
// right value is not obvious — every Execute already parallelises its own rows, so N concurrent Executes can
// oversubscribe the pool. Measure it instead of guessing; 1 means plain serial, which is often the fastest.
app.MapPost("/api/batch", (BatchRequest request) =>
{
    if (request.Ids is null || request.Ids.Length == 0)
        return Results.BadRequest(new { error = "没有要处理的图片" });

    var opSpec = request.Op is null ? null : Ops.Catalog.FirstOrDefault(o => o.Id == request.Op);
    var chainSpec = request.Chain is null ? null : Chains.Catalog.FirstOrDefault(c => c.Id == request.Chain);
    if (opSpec is null && chainSpec is null)
        return Results.BadRequest(new { error = "需要 op 或 chain 之一" });

    // Overlay-bearing work is deliberately out: the overlay would have to be regenerated per backdrop size,
    // and a batch of mixed sizes is exactly where that assumption breaks.
    if (chainSpec?.OverlayDemo is not null)
        return Results.BadRequest(new { error = "需要叠加图的算子/配方暂不支持批量" });

    var parameters = opSpec is not null ? Ops.Resolve(opSpec, request.Params) : Chains.Resolve(chainSpec!, request.Params);

    var payloads = new byte[request.Ids.Length][];
    var names = new string[request.Ids.Length];
    for (var i = 0; i < request.Ids.Length; i++)
    {
        if (!images.TryGetValue(request.Ids[i], out var entry))
            return Results.BadRequest(new { error = $"第 {i + 1} 张图已失效，请重新打开" });
        payloads[i] = entry.Bytes;
        names[i] = request.Ids[i];
    }

    var concurrency = Math.Clamp(request.Concurrency ?? 1, 1, 32);
    var encode = request.Encode ?? true;
    var ms = new double[payloads.Length];
    var outputIds = new string?[payloads.Length];
    var errors = new string?[payloads.Length];

    void RunOne(int i)
    {
        try
        {
            using var image = OurImage.Load(payloads[i]);
            var watch = Stopwatch.StartNew();
            string? outputId = null;

            if (opSpec is not null)
            {
                using var result = opSpec.Ours!(image, parameters);
                watch.Stop();
                if (encode)
                {
                    outputId = Guid.NewGuid().ToString("N");
                    StoreOutput(outputId, EncodeView(result[0].Pixels.View, snapshot: true));
                }
            }
            else
            {
                var plan = chainSpec!.Build(parameters);
                using var result = image.Apply(plan);
                watch.Stop();
                if (encode)
                {
                    outputId = Guid.NewGuid().ToString("N");
                    StoreOutput(outputId, EncodeView(result[0].Pixels.View, snapshot: true));
                }
            }

            ms[i] = watch.Elapsed.TotalMilliseconds;
            outputIds[i] = outputId;
        }
        catch (Exception e)
        {
            errors[i] = e.Message;
        }
    }

    var total = Stopwatch.StartNew();
    if (concurrency <= 1)
    {
        for (var i = 0; i < payloads.Length; i++)
            RunOne(i);
    }
    else
    {
        Parallel.For(0, payloads.Length,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency }, RunOne);
    }
    total.Stop();

    ScheduleIdleCompaction();

    return Results.Json(new
    {
        count = payloads.Length,
        concurrency,
        encoded = encode,
        totalMs = Math.Round(total.Elapsed.TotalMilliseconds, 1),
        throughputPerSec = Math.Round(payloads.Length / Math.Max(1e-6, total.Elapsed.TotalSeconds), 2),
        items = Enumerable.Range(0, payloads.Length).Select(i => new
        {
            index = i,
            ms = Math.Round(ms[i], 2),
            outputId = outputIds[i],
            error = errors[i],
        }),
    });
});

app.MapGet("/api/output/{id}", (string id) =>
    outputs.TryGetValue(id, out var bytes) ? Results.Bytes(bytes, "image/png") : Results.NotFound());

app.Lifetime.ApplicationStarted.Register(() =>
{
    foreach (var url in app.Urls)
        Console.WriteLine($"实验台已启动：{url}/   （直接用浏览器打开这个地址；预览面板的代理端口不转发 POST 接口）");
});

app.Run();

static string VectorWidth() =>
    System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated ? "256"
    : System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated ? "128"
    : "标量";

// Copies a step's view into a fresh image and encodes it. Copying is unavoidable: the observer's view is the
// plan's ping-pong slot and is recycled on the next step.
//
// `snapshot` caps the width at 1600. Intermediate steps exist to show *where* the chain went wrong, not to
// pixel-peep: encoding seven full-size 4K PNGs per run cost 75 MiB of large-object heap, and because the LOH
// never compacts the process grew past a gigabyte while live data sat at 160 MiB. At 1600px a step stays
// perfectly legible for 1/6th the bytes. The final result is always encoded at full size.
const int SnapshotMaxWidth = 1600;

static byte[] EncodeView(PixelView view, bool snapshot = false)
{
    using var copy = ToImage(view);

    if (!snapshot || view.Width <= SnapshotMaxWidth)
        return OurImage.Encode(copy, Sdcb.SimdCodecs.Abstractions.ImageFormat.Png);

    var height = Math.Max(1, (int)Math.Round(view.Height * (SnapshotMaxWidth / (double)view.Width)));
    using var scaled = copy.Resize(SnapshotMaxWidth, height, Resampler.Bilinear);
    return OurImage.Encode(scaled, Sdcb.SimdCodecs.Abstractions.ImageFormat.Png);
}

// PNG, not JPEG, even for snapshots: the chain view has to work on alpha-bearing images and JPEG has no alpha.
static OurImage ToImage(PixelView view)
{
    var copy = OurImage.Create(view.Width, view.Height, view.Format);
    var destination = copy.WritablePixels;
    var rowBytes = view.RowBytes;

    for (var y = 0; y < view.Height; y++)
        view.Row(y)[..rowBytes].CopyTo(destination.Slice(y * rowBytes, rowBytes));

    return copy;
}

// Same warm-up protocol as the single-operator path: two passes, then best of seven. Spatial cost here is
// the interesting number — the plan keeps two full images alive whatever the step count, and the chain's
// allocations show whether that held.
static (double Ms, long AllocBytes, int Width, int Height, string? Error) MeasureChain(
    byte[] source, ProcessingPlan plan)
{
    try
    {
        var width = 0;
        var height = 0;
        for (var w = 0; w < 2; w++)
        {
            using var warm = OurImage.Load(source);
            using var warmResult = warm.Apply(plan);
            width = warmResult.Width;
            height = warmResult.Height;
        }

        var samples = new List<(double Ms, long Alloc)>(7);
        for (var i = 0; i < 7; i++)
        {
            using var decoded = OurImage.Load(source);
            var before = GC.GetTotalAllocatedBytes(precise: false);
            var watch = Stopwatch.StartNew();
            using var result = decoded.Apply(plan);
            watch.Stop();
            samples.Add((watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: false) - before));
        }

        samples.Sort((a, b) => a.Ms.CompareTo(b.Ms));
        return (samples[0].Ms, samples[0].Alloc, width, height, null);
    }
    catch (Exception e)
    {
        return (0, 0, 0, 0, e.Message);
    }
}

// Two warm-up passes then best of seven. Best-of rather than median because the band scratch is
// [ThreadStatic] and a web request lands on whatever pool thread is free: a median pays for cold pools
// (sigma 3 measured 47 ms that way against 16 ms warm). This is a demo bench; the authoritative numbers come
// from benchmarks/SimdImaging.Comparison, which shares one warm-up protocol across both libraries.
//
// Space is measured too, and it has to be measured the same way on both sides: GC.GetTotalAllocatedBytes
// covers the whole process (pool threads included), sampled around the timed region only. WorkingSet is read
// right after the fastest run — it is a footprint snapshot, not a peak-over-time.
static MeasureResult MeasureOurs(byte[] source, OpSpec spec, IReadOnlyDictionary<string, double> parameters)
{
    try
    {
        OurImage Run(OurImage bg) => spec.Ours!(bg, parameters);

        // Warm up for a wall-clock budget, not a fixed iteration count. A 640x480 grayscale takes ~0.3 ms, so
        // two passes leave the tiered JIT cold and the first sweep after a server restart read 5-20x slow —
        // which is exactly how a bench stops meaning anything. The comparison harness warms >=300 ms for the
        // same reason; this is the same protocol at a smaller budget.
        var width = 0;
        var height = 0;
        var warmup = Stopwatch.StartNew();
        var warmups = 0;
        do
        {
            using var warm = OurImage.Load(source);
            using var warmResult = Run(warm);
            width = warmResult.Width;
            height = warmResult.Height;
            warmups++;
        }
        while (warmup.ElapsedMilliseconds < 300 || warmups < 2);

        var samples = new List<(double Ms, long Alloc)>(7);
        for (var i = 0; i < 7; i++)
        {
            using var decoded = OurImage.Load(source);
            var before = GC.GetTotalAllocatedBytes(precise: false);
            var watch = Stopwatch.StartNew();
            using var result = Run(decoded);
            watch.Stop();
            samples.Add((watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: false) - before));
        }

        samples.Sort((a, b) => a.Ms.CompareTo(b.Ms));
        var best = samples[0];

        using var encodeSource = OurImage.Load(source);
        using var final = Run(encodeSource);
        var png = OurImage.Encode(final, Sdcb.SimdCodecs.Abstractions.ImageFormat.Png);

        return new MeasureResult(best.Ms, best.Alloc, Environment.WorkingSet, width, height, png.Length, png, null);
    }
    catch (Exception e)
    {
        return new MeasureResult(0, 0, 0, 0, 0, 0, null, e.Message);
    }
}

static MeasureResult MeasureTheirs(byte[] source, OpSpec spec, IReadOnlyDictionary<string, double> parameters)
{
    try
    {
        // Same wall-clock warm-up as MeasureOurs: two fixed passes are not enough for a sub-millisecond op.
        var width = 0;
        var height = 0;
        var warmup = Stopwatch.StartNew();
        var warmups = 0;
        do
        {
            using var warm = TheirImage.Load(source);
            using var warmResult = spec.Theirs!(warm, parameters);
            width = warmResult.Width;
            height = warmResult.Height;
            warmups++;
        }
        while (warmup.ElapsedMilliseconds < 300 || warmups < 2);

        var samples = new List<(double Ms, long Alloc)>(7);
        for (var i = 0; i < 7; i++)
        {
            using var decoded = TheirImage.Load(source);
            var before = GC.GetTotalAllocatedBytes(precise: false);
            var watch = Stopwatch.StartNew();
            using var result = spec.Theirs!(decoded, parameters);
            watch.Stop();
            samples.Add((watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: false) - before));
        }

        samples.Sort((a, b) => a.Ms.CompareTo(b.Ms));
        var best = samples[0];

        using var encodeSource = TheirImage.Load(source);
        using var final = spec.Theirs!(encodeSource, parameters);
        using var buffer = new MemoryStream();
        final.Save(buffer, new SixLabors.ImageSharp.Formats.Png.PngEncoder());

        return new MeasureResult(best.Ms, best.Alloc, Environment.WorkingSet, width, height,
            (int)buffer.Length, null, null);
    }
    catch (Exception e)
    {
        return new MeasureResult(0, 0, 0, 0, 0, 0, null, e.Message);
    }
}

internal sealed record MeasureResult(
    double Ms, long AllocBytes, long WorkingSet, int Width, int Height, int EncodedBytes, byte[]? Png, string? Error);

internal sealed record Entry(byte[] Bytes, string ContentType, int Width, int Height, string Format);

internal sealed record RunRequest(string Id, string Op, Dictionary<string, double>? Params,
    string? OverlayId = null, double? Scale = null);

internal sealed record ChainRequest(string Id, string Chain, Dictionary<string, double>? Params);

internal sealed record CompareRequest(string Id);

internal sealed record BatchRequest(string[]? Ids, string? Op, string? Chain,
    Dictionary<string, double>? Params, int? Concurrency, bool? Encode);

internal sealed record SampleRequest(int Width, int Height, string? Kind);
