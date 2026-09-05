namespace RWire;

/// <summary>
/// Bit-level NA detection matching R's actual sentinel representations
/// (docs/spec.md section 5). These are low-level, allocation-free
/// checks meant for the hot decode/encode path - see
/// RNullableExtensions for the higher-level nullable-type conversion
/// built on top of these.
/// </summary>
public static class RNumeric
{
    /// <summary>R's NA_INTEGER: the C INT_MIN value.</summary>
    public const int NaInteger = int.MinValue;

    /// <summary>
    /// The low 32 bits of R's NA_REAL bit pattern. R represents
    /// NA_real_ as a quiet NaN whose low-order word equals this value;
    /// any other NaN payload (e.g. from 0.0/0.0) is a genuine NaN, not
    /// NA, and must NOT be treated as NA.
    /// </summary>
    private const uint NaRealLowWord = 1954;

    public static bool IsNaInteger(int value) => value == NaInteger;

    /// <summary>
    /// True only for R's specific NA_real_ bit pattern. A computed NaN
    /// (double.NaN from 0.0/0.0, Infinity-Infinity, etc.) returns
    /// false here even though double.IsNaN(value) is also true for it
    /// - the two are different values in R and must stay distinguished
    /// through a round trip.
    /// </summary>
    public static bool IsNaReal(double value)
    {
        if (!double.IsNaN(value))
        {
            return false;
        }

        long bits = BitConverter.DoubleToInt64Bits(value);
        uint lowWord = unchecked((uint)bits);
        return lowWord == NaRealLowWord;
    }

    /// <summary>Produces R's exact NA_real_ bit pattern.</summary>
    public static double NaReal
    {
        get
        {
            long bits = unchecked((long)0x7FF0000000000000UL | NaRealLowWord);
            return BitConverter.Int64BitsToDouble(bits);
        }
    }

    /// <summary>Logical codes used in RValue.LogicalCodes.</summary>
    public const byte LogicalFalse = 0;
    public const byte LogicalTrue = 1;
    public const byte LogicalNa = 2;
}
