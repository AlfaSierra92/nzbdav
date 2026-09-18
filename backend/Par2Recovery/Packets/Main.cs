namespace NzbWebDAV.Par2Recovery.Packets
{
    /// <summary>
    /// The "main" packet. Defines the slice size and the ordered list of files in the
    /// recovery set, which together establish the global input-slice numbering used by
    /// the recovery math.
    /// </summary>
    public class Main : Par2Packet
    {
        public const string PacketType = "PAR 2.0\0Main\0\0\0\0";

        /// <summary>Size of each input slice in bytes. Always a multiple of 4.</summary>
        public ulong SliceSize { get; protected set; }

        /// <summary>
        /// File IDs of the files in the recovery set, in the order that defines global
        /// slice numbering (slices are numbered file-by-file in this order).
        /// </summary>
        public byte[][] RecoverySetFileIds { get; protected set; } = [];

        /// <summary>File IDs of files present in the set but not protected by recovery data.</summary>
        public byte[][] NonRecoverySetFileIds { get; protected set; } = [];

        public Main(Par2PacketHeader header) : base(header)
        {
        }

        protected override void ParseBody(byte[] body)
        {
            // 8  uint64  Slice size. Must be a multiple of 4.
            SliceSize = BitConverter.ToUInt64(body, 0);

            // 4  uint32  Number of files in the recovery set.
            var numRecoveryFiles = BitConverter.ToUInt32(body, 8);
            var offset = 12;

            // 16*N  File IDs of the files in the recovery set (sorted, defines slice order).
            RecoverySetFileIds = ReadFileIds(body, ref offset, (int)numRecoveryFiles);

            // 16*M  File IDs of the non-recovery-set files (whatever remains).
            var remaining = (body.Length - offset) / 16;
            NonRecoverySetFileIds = ReadFileIds(body, ref offset, remaining);
        }

        private static byte[][] ReadFileIds(byte[] body, ref int offset, int count)
        {
            var ids = new byte[count][];
            for (var i = 0; i < count; i++)
            {
                ids[i] = body[offset..(offset + 16)];
                offset += 16;
            }

            return ids;
        }

        public override string ToString()
        {
            return $"Main(sliceSize={SliceSize}, recoveryFiles={RecoverySetFileIds.Length})";
        }
    }
}
