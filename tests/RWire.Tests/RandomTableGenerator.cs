namespace RWire.Tests;

/// <summary>
/// Generates random RValue tables and mixed-type lists for transfer
/// tests - one column of every supported atomic type (logical,
/// integer, double, character, raw), each with a realistic sprinkling
/// of NAs where the type supports one, plus helpers for building lists
/// that mix tables with other value types. Not a production type -
/// test-only, hence living under tests/ rather than src/.
/// </summary>
internal static class RandomTableGenerator
{
    /// <summary>
    /// Builds a table with one column per atomic type RWire supports
    /// (docs/spec.md section 5's mapping table) - the "all possible
    /// combinations of vector types" table. NA probability applies to
    /// the types that have an NA concept (logical, integer, double,
    /// character); raw has none.
    /// </summary>
    public static RValue GenerateTable(int rowCount, Random random, double naProbability = 0.1)
    {
        var columns = new (string Name, RValue Column)[]
        {
            ("logical_col", RValue.OfLogical(GenerateLogicalCodes(rowCount, random, naProbability))),
            ("integer_col", RValue.OfInteger(GenerateIntegers(rowCount, random, naProbability))),
            ("double_col", RValue.OfDouble(GenerateDoubles(rowCount, random, naProbability))),
            ("character_col", RValue.OfCharacter(GenerateStrings(rowCount, random, naProbability))),
            ("raw_col", RValue.OfRaw(GenerateRawBytes(rowCount, random))),
        };

        return RValue.OfTable(columns);
    }

    /// <summary>
    /// Builds a List whose elements cycle through: a table (from
    /// GenerateTable), a plain double vector, and a plain character
    /// vector - the "list with multiple object types (and tables)"
    /// scenario.
    /// </summary>
    public static RValue GenerateMixedList(int elementCount, int rowsPerElement, Random random)
    {
        var elements = new RValue[elementCount];
        for (int i = 0; i < elementCount; i++)
        {
            elements[i] = (i % 3) switch
            {
                0 => GenerateTable(rowsPerElement, random),
                1 => RValue.OfDouble(GenerateDoubles(rowsPerElement, random, naProbability: 0.05)),
                _ => RValue.OfCharacter(GenerateStrings(rowsPerElement, random, naProbability: 0.05)),
            };
        }

        return RValue.OfList(elements);
    }

    private static byte[] GenerateLogicalCodes(int n, Random random, double naProbability)
    {
        var result = new byte[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = random.NextDouble() < naProbability
                ? RNumeric.LogicalNa
                : (byte)(random.Next(2) == 0 ? RNumeric.LogicalFalse : RNumeric.LogicalTrue);
        }
        return result;
    }

    private static int[] GenerateIntegers(int n, Random random, double naProbability)
    {
        var result = new int[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = random.NextDouble() < naProbability
                ? RNumeric.NaInteger
                : random.Next(-1_000_000, 1_000_000);
        }
        return result;
    }

    private static double[] GenerateDoubles(int n, Random random, double naProbability)
    {
        var result = new double[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = random.NextDouble() < naProbability
                ? RNumeric.NaReal
                : (random.NextDouble() - 0.5) * 2_000_000.0;
        }
        return result;
    }

    private static readonly string[] SamplePool =
    {
        "alpha", "beta", "gamma", "delta", "epsilon", "héllo", "日本語", "🎉", "",
    };

    private static string?[] GenerateStrings(int n, Random random, double naProbability)
    {
        var result = new string?[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = random.NextDouble() < naProbability
                ? null
                : SamplePool[random.Next(SamplePool.Length)];
        }
        return result;
    }

    private static byte[] GenerateRawBytes(int n, Random random)
    {
        var result = new byte[n];
        random.NextBytes(result);
        return result;
    }
}
