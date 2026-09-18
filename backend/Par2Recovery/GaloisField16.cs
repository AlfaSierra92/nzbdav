namespace NzbWebDAV.Par2Recovery
{
    /// <summary>
    /// Arithmetic over the Galois field GF(2^16) used by PAR2, with generator polynomial
    /// 0x1100B and primitive element 2. Multiplication, exponentiation and inversion are
    /// done via precomputed exp/log tables. Field elements are 16-bit values.
    /// </summary>
    /// <remarks>
    /// Validated byte-for-byte against par2cmdline-generated recovery sets: the constants,
    /// the generator polynomial and the little-endian 16-bit word interpretation all match
    /// the Parity Volume Set Specification 2.0.
    /// </remarks>
    public static class GaloisField16
    {
        /// <summary>Generator polynomial x^16 + x^12 + x^3 + x + 1.</summary>
        public const int GeneratorPolynomial = 0x1100B;

        private const int Order = 65535; // 2^16 - 1, size of the multiplicative group

        // Exp is double-length (2*Order) so Mul can index Log[a]+Log[b] without a modulo.
        private static readonly ushort[] Exp = new ushort[2 * Order];
        private static readonly ushort[] Log = new ushort[Order + 1];

        static GaloisField16()
        {
            var x = 1;
            for (var i = 0; i < Order; i++)
            {
                Exp[i] = (ushort)x;
                Exp[i + Order] = (ushort)x;
                Log[x] = (ushort)i;
                x <<= 1;
                if ((x & 0x10000) != 0) x ^= GeneratorPolynomial;
            }
        }

        /// <summary>Multiply two field elements.</summary>
        public static ushort Mul(ushort a, ushort b)
        {
            if (a == 0 || b == 0) return 0;
            return Exp[Log[a] + Log[b]];
        }

        /// <summary>Raise a field element to a (non-negative) power.</summary>
        public static ushort Pow(ushort a, uint exponent)
        {
            if (exponent == 0) return 1;
            if (a == 0) return 0;
            // Log[a] and exponent can each approach 2^16, so multiply in 64-bit before the modulo.
            return Exp[(long)Log[a] * exponent % Order];
        }

        /// <summary>Multiplicative inverse: a^(Order-1), i.e. 2^(Order - Log[a]).</summary>
        public static ushort Inv(ushort a)
        {
            if (a == 0) throw new DivideByZeroException("GF(2^16): zero has no inverse.");
            return Exp[(Order - Log[a]) % Order];
        }
    }
}
