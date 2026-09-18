namespace NzbWebDAV.Par2Recovery.Packets
{
    /// <summary>
    /// A "recovery slice" packet. Holds one recovery slice's data together with the
    /// exponent that identifies its column in the Reed-Solomon recovery matrix.
    /// </summary>
    public class RecoverySlice : Par2Packet
    {
        public const string PacketType = "PAR 2.0\0RecvSlic";

        /// <summary>
        /// The exponent for this recovery slice. The matrix coefficient applied to input
        /// slice i when producing this recovery slice is base(i) ^ Exponent in GF(2^16).
        /// </summary>
        public uint Exponent { get; protected set; }

        /// <summary>The recovery data, exactly one slice (SliceSize bytes) long.</summary>
        public byte[] RecoveryData { get; protected set; } = [];

        public RecoverySlice(Par2PacketHeader header) : base(header)
        {
        }

        protected override void ParseBody(byte[] body)
        {
            // 4  uint32  Exponent (recovery-matrix column).
            Exponent = BitConverter.ToUInt32(body, 0);

            // remainder: the recovery slice data (SliceSize bytes).
            RecoveryData = body[4..];
        }

        public override string ToString()
        {
            return $"RecoverySlice(exponent={Exponent}, bytes={RecoveryData.Length})";
        }
    }
}
