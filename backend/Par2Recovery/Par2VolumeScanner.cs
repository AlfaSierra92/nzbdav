using System.Runtime.InteropServices;
using System.Text;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Par2Recovery;

/// <summary>
/// Walks a par2 volume parsing only the small metadata packets (Main, FileDesc, IFSC) in
/// full, while recovery-slice packets are skipped over and recorded as offset pointers.
/// This lets the repair pipeline count surviving recovery slices — and later fetch just
/// the ones it uses — without downloading every volume's recovery data. A read error
/// mid-volume (e.g. a dead article) ends the scan and returns what was found so far.
/// </summary>
public static class Par2VolumeScanner
{
    /// <param name="Exponent">The recovery slice's Reed-Solomon matrix exponent.</param>
    /// <param name="DataOffset">Absolute offset of the slice data within the volume file.</param>
    /// <param name="Length">Length of the slice data in bytes (equals the slice size).</param>
    public sealed record RecoverySlicePointer(uint Exponent, long DataOffset, int Length);

    public sealed record ScanResult(List<Par2Packet> MetadataPackets, List<RecoverySlicePointer> RecoverySlices);

    private static readonly int HeaderSize = Marshal.SizeOf<Par2PacketHeader>();

    public static async Task<ScanResult> ScanAsync(Stream stream, long streamLength, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var metadataPackets = new List<Par2Packet>();
        var recoverySlices = new List<RecoverySlicePointer>();
        var position = 0L;

        try
        {
            var headerBuffer = new byte[HeaderSize];
            while (position + HeaderSize <= streamLength)
            {
                ct.ThrowIfCancellationRequested();
                if (stream.Position != position) stream.Seek(position, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(headerBuffer, ct).ConfigureAwait(false);
                if (!"PAR2\0PKT"u8.SequenceEqual(headerBuffer.AsSpan(0, 8))) break;
                var header = ParseHeader(headerBuffer);

                // A malformed length must not loop forever or point outside the volume.
                if (header.PacketLength < (ulong)HeaderSize
                    || header.PacketLength > (ulong)(streamLength - position)) break;

                var bodyLength = (long)header.PacketLength - HeaderSize;
                var packetType = Encoding.ASCII.GetString(header.PacketType);
                if (packetType == RecoverySlice.PacketType)
                {
                    if (bodyLength < 4 || bodyLength - 4 > int.MaxValue) break;
                    // exponent is the first 4 bytes of the body; the rest is slice data we skip
                    var exponentBuffer = new byte[4];
                    await stream.ReadExactlyAsync(exponentBuffer, ct).ConfigureAwait(false);
                    var exponent = BitConverter.ToUInt32(exponentBuffer);
                    recoverySlices.Add(new RecoverySlicePointer(
                        exponent, position + HeaderSize + 4, (int)(bodyLength - 4)));
                }
                else if (packetType is Main.PacketType or Ifsc.PacketType or FileDesc.PacketType)
                {
                    Par2Packet packet = packetType switch
                    {
                        Main.PacketType => new Main(header),
                        Ifsc.PacketType => new Ifsc(header),
                        _ => new FileDesc(header),
                    };
                    await packet.ReadAsync(stream).ConfigureAwait(false);
                    metadataPackets.Add(packet);
                }

                position += (long)header.PacketLength;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            // a dead article (or short volume) ends the scan; surviving packets still count
        }

        return new ScanResult(metadataPackets, recoverySlices);
    }

    /// <summary>Layout per spec: 8B magic, 8B packet length, 16B hash, 16B set id, 16B type.</summary>
    private static Par2PacketHeader ParseHeader(byte[] buffer) => new()
    {
        Magic = buffer[..8],
        PacketLength = BitConverter.ToUInt64(buffer, 8),
        PacketHash = buffer[16..32],
        RecoverySetID = buffer[32..48],
        PacketType = buffer[48..64],
    };
}
