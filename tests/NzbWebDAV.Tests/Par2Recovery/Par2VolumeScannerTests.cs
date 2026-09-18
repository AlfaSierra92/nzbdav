using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

/// <summary>
/// The repair pipeline must know which recovery slices survive without downloading
/// their data (recovery volumes can be a large fraction of the release). The scanner
/// walks a volume, parses the small metadata packets, and records offset pointers to
/// each recovery slice's data so only the slices actually used get fetched later.
/// </summary>
public class Par2VolumeScannerTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Par2");

    [Theory]
    [InlineData(0UL)]
    [InlineData(63UL)]
    [InlineData(1024UL)]
    public async Task Scan_StopsAtInvalidPacketLength(ulong length)
    {
        var bytes = new byte[64];
        "PAR2\0PKT"u8.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), length);
        using var stream = new MemoryStream(bytes);
        var result = await Par2VolumeScanner.ScanAsync(stream, bytes.Length);
        Assert.Empty(result.MetadataPackets);
        Assert.Empty(result.RecoverySlices);
    }

    [Fact]
    public async Task Scan_DoesNotTurnCancellationIntoAnEmptyRecoverySet()
    {
        using var stream = new MemoryStream(new byte[64]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Par2VolumeScanner.ScanAsync(stream, stream.Length, cts.Token));
    }

    [Fact]
    public async Task Scan_CatalogsRecoverySlicePointers_WithoutReadingTheirData()
    {
        var volPath = Path.Combine(FixtureDir, "data.vol3+3.par2");

        await using var stream = File.OpenRead(volPath);
        var result = await Par2VolumeScanner.ScanAsync(stream, stream.Length, CancellationToken.None);

        // vol3+3 carries exponents 3, 4, 5 with 4096-byte slices
        Assert.Equal([3u, 4u, 5u], result.RecoverySlices.Select(x => x.Exponent).Order());
        Assert.All(result.RecoverySlices, p => Assert.Equal(4096, p.Length));

        // metadata packets are parsed in full
        Assert.Contains(result.MetadataPackets, p => p is Main);
        Assert.Contains(result.MetadataPackets, p => p is FileDesc);
        Assert.Contains(result.MetadataPackets, p => p is Ifsc);

        // pointers locate the same bytes the full parser returns as RecoveryData
        await using var reparse = File.OpenRead(volPath);
        var fullPackets = new List<Par2Packet>();
        await foreach (var packet in Par2.ReadAllPacketsAsync(reparse))
            fullPackets.Add(packet);
        var expectedByExponent = fullPackets.OfType<RecoverySlice>().ToDictionary(x => x.Exponent, x => x.RecoveryData);

        foreach (var pointer in result.RecoverySlices)
        {
            var data = new byte[pointer.Length];
            stream.Position = pointer.DataOffset;
            await stream.ReadExactlyAsync(data);
            Assert.Equal(expectedByExponent[pointer.Exponent], data);
        }
    }
}
