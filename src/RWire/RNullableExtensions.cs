namespace RWire;

/// <summary>
/// Optional, higher-level conversion from the raw sentinel-preserving
/// arrays in RValue to idiomatic nullable C# types. Deliberately kept
/// separate from decoding itself (docs/spec.md section 5.1) so the hot
/// decode path never pays for Nullable&lt;T&gt; boxing unless a caller
/// actually asks for it.
/// </summary>
public static class RNullableExtensions
{
    public static int?[] ToNullableArray(this int[] values)
    {
        var result = new int?[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = RNumeric.IsNaInteger(values[i]) ? null : values[i];
        }
        return result;
    }

    public static double?[] ToNullableArray(this double[] values)
    {
        var result = new double?[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = RNumeric.IsNaReal(values[i]) ? null : values[i];
        }
        return result;
    }

    public static bool?[] ToNullableArray(this byte[] logicalCodes)
    {
        var result = new bool?[logicalCodes.Length];
        for (int i = 0; i < logicalCodes.Length; i++)
        {
            result[i] = logicalCodes[i] switch
            {
                RNumeric.LogicalFalse => false,
                RNumeric.LogicalTrue => true,
                _ => null,
            };
        }
        return result;
    }
}
