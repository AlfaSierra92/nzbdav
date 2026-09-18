using System.Runtime.InteropServices;
using System.Text;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

/// <summary>
/// Fabricates PAR2 packets from spec-layout body bytes, for testing recovery-set
/// assembly with shapes the committed fixture doesn't cover (e.g. multiple files).
/// </summary>
public static class SyntheticPackets
{
    private static int HeaderSize => Marshal.SizeOf<Par2PacketHeader>();

    private static async Task<T> ParseAsync<T>(T packet, byte[] body) where T : Par2Packet
    {
        await packet.ReadAsync(new MemoryStream(body));
        return packet;
    }

    private static Par2PacketHeader MakeHeader(string packetType, int bodyLength) => new()
    {
        Magic = "PAR2\0PKT"u8.ToArray(),
        PacketLength = (ulong)(HeaderSize + bodyLength),
        PacketHash = new byte[16],
        RecoverySetID = new byte[16],
        PacketType = Encoding.ASCII.GetBytes(packetType),
    };

    public static Task<Main> MakeMainAsync(int sliceSize, params byte[][] recoverySetFileIds)
    {
        var body = new byte[8 + 4 + 16 * recoverySetFileIds.Length];
        BitConverter.TryWriteBytes(body.AsSpan(0), (ulong)sliceSize);
        BitConverter.TryWriteBytes(body.AsSpan(8), (uint)recoverySetFileIds.Length);
        for (var i = 0; i < recoverySetFileIds.Length; i++)
            recoverySetFileIds[i].CopyTo(body.AsSpan(12 + 16 * i));
        return ParseAsync(new Main(MakeHeader(Main.PacketType, body.Length)), body);
    }

    public static Task<FileDesc> MakeFileDescAsync(byte[] fileId, string fileName, long fileLength)
    {
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var paddedNameLength = (nameBytes.Length + 3) / 4 * 4;
        var body = new byte[16 + 16 + 16 + 8 + paddedNameLength];
        fileId.CopyTo(body.AsSpan(0));
        BitConverter.TryWriteBytes(body.AsSpan(48), (ulong)fileLength);
        nameBytes.CopyTo(body.AsSpan(56));
        return ParseAsync(new FileDesc(MakeHeader(FileDesc.PacketType, body.Length)), body);
    }
}
