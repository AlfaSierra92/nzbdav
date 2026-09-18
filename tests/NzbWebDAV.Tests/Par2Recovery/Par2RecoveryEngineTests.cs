using System.Security.Cryptography;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

/// <summary>
/// Validates the PAR2 GF(2^16) Reed-Solomon engine against a real par2cmdline-generated
/// recovery set (see Fixtures/Par2/README.md). The fixture has 16 input slices of 4096 bytes
/// and 6 recovery slices (exponents 0..5).
/// </summary>
public class Par2RecoveryEngineTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Par2");

    private sealed record Fixture(int SliceSize, int SliceCount, Ifsc Ifsc,
        IReadOnlyDictionary<uint, byte[]> Recovery, byte[] Data);

    private static async Task<List<Par2Packet>> ReadPacketsAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        var packets = new List<Par2Packet>();
        await foreach (var packet in Par2.ReadAllPacketsAsync(fs))
            packets.Add(packet);
        return packets;
    }

    private static async Task<Fixture> LoadFixtureAsync()
    {
        Main? main = null;
        Ifsc? ifsc = null;
        var recovery = new Dictionary<uint, byte[]>();

        foreach (var file in Directory.GetFiles(FixtureDir, "*.par2"))
        {
            foreach (var packet in await ReadPacketsAsync(file))
            {
                switch (packet)
                {
                    case Main m: main ??= m; break;
                    case Ifsc i: ifsc ??= i; break;
                    case RecoverySlice r: recovery.TryAdd(r.Exponent, r.RecoveryData); break;
                }
            }
        }

        Assert.NotNull(main);
        Assert.NotNull(ifsc);
        var data = await File.ReadAllBytesAsync(Path.Combine(FixtureDir, "data.bin"));
        var sliceSize = (int)main!.SliceSize;
        return new Fixture(sliceSize, data.Length / sliceSize, ifsc!, recovery, data);
    }

    private static byte[] SliceOf(byte[] data, int index, int sliceSize)
        => data.AsSpan(index * sliceSize, sliceSize).ToArray();

    [Fact]
    public void GaloisField_InverseAndPow_AreConsistent()
    {
        foreach (ushort a in new ushort[] { 1, 2, 3, 255, 4107, 0x8000, 0xFFFF })
        {
            Assert.Equal(1, GaloisField16.Mul(a, GaloisField16.Inv(a)));
            Assert.Equal(GaloisField16.Mul(a, a), GaloisField16.Pow(a, 2));
            Assert.Equal(1, GaloisField16.Pow(a, 0));
        }
    }

    [Fact]
    public void Constants_MatchSpecSequence()
    {
        // 2^n in GF(2^16) for n coprime to 65535, per the PAR2 spec.
        ushort[] expected = [2, 4, 16, 128, 256, 2048, 8192, 16384, 4107, 32856, 17132, 34264, 28396, 43963, 18301, 3583];
        Assert.Equal(expected, ReedSolomon.Constants(16));
    }

    [Fact]
    public async Task ParsedRecoverySlices_MatchEncodedInputs()
    {
        // Independent check of the encode direction: recompute each recovery slice from the
        // inputs and confirm it equals the parsed recovery data.
        var fx = await LoadFixtureAsync();
        var constants = ReedSolomon.Constants(fx.SliceCount);
        var wordsPerSlice = fx.SliceSize / 2;

        foreach (var (exponent, expected) in fx.Recovery)
        {
            var acc = new ushort[wordsPerSlice];
            for (var i = 0; i < fx.SliceCount; i++)
            {
                var coeff = GaloisField16.Pow(constants[i], exponent);
                var slice = SliceOf(fx.Data, i, fx.SliceSize);
                for (var w = 0; w < wordsPerSlice; w++)
                {
                    var word = (ushort)(slice[2 * w] | (slice[2 * w + 1] << 8));
                    acc[w] ^= GaloisField16.Mul(coeff, word);
                }
            }

            for (var w = 0; w < wordsPerSlice; w++)
            {
                var expectedWord = (ushort)(expected[2 * w] | (expected[2 * w + 1] << 8));
                Assert.Equal(expectedWord, acc[w]);
            }
        }
    }

    public static TheoryData<int[], uint[]> RepairCases() => new()
    {
        { [5], [0] },                            // single missing slice
        { [2, 9], [0, 1] },                      // two missing
        { [0, 7, 15], [3, 4, 5] },               // includes first and last slice
        { [1, 4, 8, 11, 14, 3], [0, 1, 2, 3, 4, 5] }, // |M| == recovery count (feasibility boundary)
    };

    [Theory]
    [MemberData(nameof(RepairCases))]
    public async Task Reconstruct_RebuildsMissingSlices_ByteExactAndIfscVerified(int[] missing, uint[] exponents)
    {
        var fx = await LoadFixtureAsync();

        var present = Enumerable.Range(0, fx.SliceCount)
            .Where(i => !missing.Contains(i))
            .ToDictionary(i => i, i => SliceOf(fx.Data, i, fx.SliceSize));
        var recoverySlices = exponents.Select(e => (e, fx.Recovery[e])).ToList();

        var rebuilt = ReedSolomon.Reconstruct(fx.SliceSize, fx.SliceCount, present, missing, recoverySlices);

        for (var c = 0; c < missing.Length; c++)
        {
            var expected = SliceOf(fx.Data, missing[c], fx.SliceSize);
            Assert.Equal(expected, rebuilt[c]);

            // the real trust signal: reconstructed slice matches its IFSC MD5
            var md5 = MD5.HashData(rebuilt[c]);
            Assert.Equal(fx.Ifsc.SliceChecksums[missing[c]].Md5, md5);
        }
    }

    [Theory]
    [MemberData(nameof(RepairCases))]
    public async Task StreamingReconstructor_FedOneSliceAtATime_MatchesInMemoryReconstruct(int[] missing, uint[] exponents)
    {
        // the repair pipeline can't hold a whole release in RAM: present slices are
        // streamed from usenet once and accumulated online. Feeding slices one at a
        // time (with a truncated final slice) must give the same answer as the
        // all-in-memory Reconstruct.
        var fx = await LoadFixtureAsync();
        var recoverySlices = exponents.Select(e => (e, fx.Recovery[e])).ToList();

        var reconstructor = new ReedSolomon.StreamingReconstructor(
            fx.SliceSize, fx.SliceCount, missing, recoverySlices);
        for (var i = 0; i < fx.SliceCount; i++)
        {
            if (missing.Contains(i)) continue;
            // feed the last present slice truncated by a byte to prove short (tail) slices get padded
            var slice = SliceOf(fx.Data, i, fx.SliceSize);
            reconstructor.FeedPresentSlice(i, i == fx.SliceCount - 1 ? slice.AsSpan(..^1) : slice);
        }

        var rebuilt = reconstructor.Solve();

        var present = Enumerable.Range(0, fx.SliceCount)
            .Where(i => !missing.Contains(i))
            .ToDictionary(i => i, i =>
            {
                var slice = SliceOf(fx.Data, i, fx.SliceSize);
                if (i == fx.SliceCount - 1) slice[^1] = 0; // match the truncation above
                return slice;
            });
        var expected = ReedSolomon.Reconstruct(fx.SliceSize, fx.SliceCount, present, missing, recoverySlices);
        Assert.Equal(expected, rebuilt);
    }

    [Fact]
    public async Task Reconstruct_Throws_WhenNotEnoughRecoverySlices()
    {
        var fx = await LoadFixtureAsync();
        int[] missing = [1, 2, 3];
        var present = Enumerable.Range(0, fx.SliceCount)
            .Where(i => !missing.Contains(i))
            .ToDictionary(i => i, i => SliceOf(fx.Data, i, fx.SliceSize));
        var tooFew = new List<(uint, byte[])> { (0u, fx.Recovery[0]) };

        Assert.Throws<InvalidOperationException>(() =>
            ReedSolomon.Reconstruct(fx.SliceSize, fx.SliceCount, present, missing, tooFew));
    }
}
