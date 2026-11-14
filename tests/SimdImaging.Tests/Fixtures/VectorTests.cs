using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SimdImaging.Shared;

namespace SimdImaging.Tests;

/// <summary>
/// Guards the reference-vector package itself: the checked-in expected bytes must hash to the
/// checked-in SHA-256, the sidecar must agree, and the vectors must actually cover the cases
/// that P0's precision fix is about. No call into the library under test happens here.
/// </summary>
public class VectorTests
{
    [Fact]
    public void Vectors_AreWellFormedAndIndependentlyHashed()
    {
        var files = Directory.GetFiles(VectorsDirectory(), "*.json")
            .OrderBy(static p => p, StringComparer.Ordinal).ToArray();
        (files.Length >= 3).ShouldBeTrue($"expected at least 3 vectors, found {files.Length}");

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            var id = root.GetProperty("id").GetString()!;
            Path.GetFileNameWithoutExtension(file).ShouldBe(id);

            var determinism = root.GetProperty("determinism").GetString();
            determinism.ShouldBeOneOf("D0", "D1", "D2");
            var tolerance = root.GetProperty("tolerance").ValueKind;
            if (determinism == "D2")
                tolerance.ShouldNotBe(JsonValueKind.Null);
            else
                tolerance.ShouldBe(JsonValueKind.Null);

            foreach (var side in new[] { "input", "expected" })
            {
                var node = root.GetProperty(side);
                var bytes = Convert.FromHexString(node.GetProperty("bytes").GetString()!);
                Convert.ToHexString(SHA256.HashData(bytes))
                    .ShouldBe(node.GetProperty("sha256").GetString(), $"{id}: {side} SHA-256 drifted");
            }

            var sidecar = Path.ChangeExtension(file, ".sha256");
            File.Exists(sidecar).ShouldBeTrue($"{id}: missing .sha256 sidecar");
            File.ReadAllText(sidecar).Trim()
                .ShouldBe(root.GetProperty("expected").GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public void Vectors_IdentityWarpExpectsTheInputBytesInEveryFormat()
    {
        var directory = VectorsDirectory();
        foreach (var format in new[] { "Rgba8", "Rgba16", "Rgba32F" })
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, $"warp-identity-{format.ToLowerInvariant()}.json")));
            var root = document.RootElement;
            root.GetProperty("input").GetProperty("format").GetString().ShouldBe(format);
            root.GetProperty("expected").GetProperty("bytes").GetString()
                .ShouldBe(root.GetProperty("input").GetProperty("bytes").GetString());
        }
    }

    [Fact]
    public void Vectors_ReallyCoverSixteenBitLowBytesAndFloatHdr()
    {
        var directory = VectorsDirectory();

        using (var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "warp-identity-rgba16.json"))))
        {
            var bytes = Convert.FromHexString(
                document.RootElement.GetProperty("input").GetProperty("bytes").GetString()!);
            var samples = MemoryMarshal.Cast<byte, ushort>(bytes);
            samples.Length.ShouldBe(5 * 4 * 4);
            // A byte-only intermediate divides by 257 and destroys exactly these low bytes.
            var hasLowByte = false;
            foreach (var sample in samples)
                if ((sample & 0xFF) != 0) { hasLowByte = true; break; }
            hasLowByte.ShouldBeTrue();
        }

        using (var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "warp-identity-rgba32f.json"))))
        {
            var bytes = Convert.FromHexString(
                document.RootElement.GetProperty("input").GetProperty("bytes").GetString()!);
            var samples = MemoryMarshal.Cast<byte, float>(bytes);
            var hasHdr = false;
            var hasNegative = false;
            var hasOutOfRange = false;
            foreach (var sample in samples)
            {
                if (sample > 1f) hasHdr = true;
                if (sample < 0f) hasNegative = true;
                if (sample > 10f) hasOutOfRange = true;
            }

            hasHdr.ShouldBeTrue("HDR > 1 must be in the vector");
            hasNegative.ShouldBeTrue("negative floats must be in the vector");
            hasOutOfRange.ShouldBeTrue("out-of-range floats must be in the vector");
        }
    }

    [Fact]
    public void SyntheticCorpus_IsDeterministicAndCoversEveryQualityTag()
    {
        (SyntheticCorpus.Tags.Length >= 9).ShouldBeTrue();
        foreach (var tag in SyntheticCorpus.Tags)
        {
            var first = SyntheticCorpus.Generate(tag);
            var second = SyntheticCorpus.Generate(tag);
            first.Length.ShouldBe(SyntheticCorpus.Width * SyntheticCorpus.Height * 4);
            first.ShouldBe(second, $"synthetic fixture '{tag}' is not deterministic");
            PixelDiff.Sha256(first).ShouldBe(PixelDiff.Sha256(second));
        }
    }

    private static string VectorsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "vectors");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"tests/Fixtures/vectors not found above {AppContext.BaseDirectory}");
    }
}
