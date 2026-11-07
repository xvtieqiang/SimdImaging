using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

// C1 evidence: can the pinned 4.1.3 oracle actually be published as NativeAOT and run a full
// decode -> process -> encode pipeline? The generic Image<TPixel> x format x codec combinations are
// exactly where AOT trimming bites, so the probe must include a real codec round trip, not just an
// in-memory image. Only public APIs are used; no source is read.
using var image = new Image<Rgba32>(64, 64);
image.Mutate(x => x.Resize(32, 32).Grayscale().GaussianBlur(1f));

using var encoded = new MemoryStream();
image.SaveAsPng(encoded);
encoded.Position = 0;
using var decoded = Image.Load<Rgba32>(encoded);

Console.WriteLine($"aot-oracle-ok png={encoded.Length}B decoded={decoded.Width}x{decoded.Height} " +
                  $"dynamic-code={RuntimeFeature.IsDynamicCodeSupported}");
