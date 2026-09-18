namespace NzbWebDAV.Par2Recovery.Packets
{
    /// <summary>
    /// The "Input File Slice Checksum" (IFSC) packet. Carries, for one file, the MD5 hash
    /// and CRC32 of every input slice of that file. Used to verify reconstructed slices.
    /// </summary>
    public class Ifsc : Par2Packet
    {
        public const string PacketType = "PAR 2.0\0IFSC\0\0\0\0";

        /// <summary>The File ID these slice checksums belong to (matches a FileDesc's FileID).</summary>
        public byte[] FileId { get; protected set; } = [];

        /// <summary>One entry per input slice of the file, in order.</summary>
        public SliceChecksum[] SliceChecksums { get; protected set; } = [];

        public Ifsc(Par2PacketHeader header) : base(header)
        {
        }

        protected override void ParseBody(byte[] body)
        {
            // 16  MD5  The File ID.
            FileId = body[0..16];

            // (16 MD5 + 4 CRC32) per slice, repeated for the remainder of the body.
            var count = (body.Length - 16) / 20;
            SliceChecksums = new SliceChecksum[count];
            var offset = 16;
            for (var i = 0; i < count; i++)
            {
                var md5 = body[offset..(offset + 16)];
                var crc32 = BitConverter.ToUInt32(body, offset + 16);
                SliceChecksums[i] = new SliceChecksum(md5, crc32);
                offset += 20;
            }
        }

        public readonly record struct SliceChecksum(byte[] Md5, uint Crc32);

        public override string ToString()
        {
            return $"Ifsc(slices={SliceChecksums.Length})";
        }
    }
}
