using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

public class Par2ReaderTests
{
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Par2", "data.par2");

    [Fact]
    public async Task ReadFileDescriptions_PreservesNamesAndLengths()
    {
        await using var stream = File.OpenRead(FixturePath);
        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadFileDescriptions(stream))
            descriptions.Add(description);

        Assert.NotEmpty(descriptions);
        Assert.All(descriptions, description =>
        {
            Assert.Equal("data.bin", description.FileName);
            Assert.Equal(65536UL, description.FileLength);
        });
    }

    [Fact]
    public async Task ReadAllPackets_StopsAtMalformedPacket_KeepingEarlierPackets()
    {
        var data = await File.ReadAllBytesAsync(FixturePath);
        using var stream = new MemoryStream();
        stream.Write(data);
        stream.Write(new byte[64]); // Invalid header between two valid sequences.
        stream.Write(data);
        stream.Position = 0;

        var actual = new List<Par2Packet>();
        await foreach (var packet in Par2.ReadAllPacketsAsync(stream))
            actual.Add(packet);

        using var original = new MemoryStream(data);
        var expected = new List<Par2Packet>();
        await foreach (var packet in Par2.ReadAllPacketsAsync(original))
            expected.Add(packet);

        Assert.NotEmpty(expected);
        Assert.Equal(expected.Select(x => x.GetType()), actual.Select(x => x.GetType()));
    }

    [Fact]
    public async Task ReadAllPackets_PreCancelled_DoesNotReadStream()
    {
        await using var stream = File.OpenRead(FixturePath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await foreach (var packet in Par2.ReadAllPacketsAsync(stream, cts.Token))
            Assert.Fail("A cancelled reader must not yield packets.");

        Assert.Equal(0L, stream.Position);
    }
}
