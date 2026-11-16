using System.Diagnostics;
using SimdImaging;
using SimdImaging.Abstractions;

// The smallest workload that touches the library: decode nothing, build one image, run one operator, print a byte.
// Process wall time is measured outside; this only proves the work happened and reports the in-process elapsed time
// so the JIT and AOT runs can be compared on the same number.
var watch = Stopwatch.StartNew();
using var image = Image.Create(64, 64, WorkingFormat.Rgba8);
var span = image.WritablePixels;
for (var i = 0; i < span.Length; i++)
    span[i] = (byte)(i * 7);

using var blurred = image.GaussianBlur(1f);
watch.Stop();

Console.WriteLine($"op={blurred.WritablePixels[123]} elapsed={watch.Elapsed.TotalMilliseconds:F1}ms");
