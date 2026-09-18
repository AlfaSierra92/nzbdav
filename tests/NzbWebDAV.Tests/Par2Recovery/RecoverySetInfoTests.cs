using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

/// <summary>
/// RecoverySetInfo assembles the parsed par2 packets into the structure the repair
/// pipeline works with: slice size, the recovery-set files in Main-packet order (which
/// defines global slice numbering), each file's global slice base, and its per-slice
/// IFSC checksums.
/// </summary>
public class RecoverySetInfoTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Par2");

    private static async Task<List<Par2Packet>> ReadAllFixturePacketsAsync()
    {
        var packets = new List<Par2Packet>();
        foreach (var file in Directory.GetFiles(FixtureDir, "*.par2"))
        {
            await using var fs = File.OpenRead(file);
            await foreach (var packet in Par2.ReadAllPacketsAsync(fs))
                packets.Add(packet);
        }

        return packets;
    }

    [Fact]
    public async Task Build_FromFixturePackets_ExposesFilesWithSliceBasesAndChecksums()
    {
        // fixture: one file (data.bin, 65536 bytes) with 4096-byte slices → 16 slices.
        var packets = await ReadAllFixturePacketsAsync();

        var info = RecoverySetInfo.Build(packets);

        Assert.Equal(4096, info.SliceSize);
        Assert.Equal(16, info.TotalSlices);
        var file = Assert.Single(info.Files);
        Assert.Equal("data.bin", file.FileName);
        Assert.Equal(65536, file.Length);
        Assert.Equal(0, file.SliceBase);
        Assert.Equal(16, file.SliceCount);
        Assert.Equal(16, file.SliceChecksums.Length);
        Assert.Equal(16, file.Hash16k.Length);
    }

    [Fact]
    public async Task Build_NumbersSlicesFileByFile_InMainPacketOrder()
    {
        // Main lists file B before file A: B gets slices [0,1), A gets [1,4).
        byte[] idA = Enumerable.Repeat((byte)0xAA, 16).ToArray();
        byte[] idB = Enumerable.Repeat((byte)0xBB, 16).ToArray();
        var packets = new List<Par2Packet>
        {
            await SyntheticPackets.MakeMainAsync(sliceSize: 4096, idB, idA),
            await SyntheticPackets.MakeFileDescAsync(idA, "a.bin", 10000),
            await SyntheticPackets.MakeFileDescAsync(idB, "b.bin", 4096),
        };

        var info = RecoverySetInfo.Build(packets);

        Assert.Equal(4, info.TotalSlices);
        Assert.Equal(["b.bin", "a.bin"], info.Files.Select(x => x.FileName));
        Assert.Equal(0, info.Files[0].SliceBase);
        Assert.Equal(1, info.Files[0].SliceCount);
        Assert.Equal(1, info.Files[1].SliceBase);
        Assert.Equal(3, info.Files[1].SliceCount);
        Assert.Empty(info.Files[0].SliceChecksums);
    }
}
