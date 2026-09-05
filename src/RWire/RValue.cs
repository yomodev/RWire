namespace RWire;

/// <summary>
/// A decoded (or to-be-encoded) R value: the type tag, the raw
/// sentinel-preserving data for that type, and R's attributes
/// (names/dim/class fast-pathed, everything else in a generic
/// dictionary) - docs/spec.md sections 5 and 5.3.
///
/// Exactly one of the *Values/*Codes properties is populated,
/// matching TypeTag. NA sentinels are preserved as-is in the raw
/// arrays (int.MinValue for integer NA, R's specific NaN payload for
/// double NA, a null string entry for character NA, 2 for logical NA)
/// - conversion to idiomatic nullable C# types is a separate, optional
/// step (see RNullableExtensions), deliberately kept out of the hot
/// decode path (docs/spec.md section 5.1).
/// </summary>
public sealed class RValue
{
    public required RTypeTag TypeTag { get; init; }

    /// <summary>Logical vector, one byte per element: 0 = FALSE, 1 = TRUE, 2 = NA.</summary>
    public byte[]? LogicalCodes { get; init; }

    /// <summary>Integer vector; NA is int.MinValue (R's NA_INTEGER) - see RNumeric.</summary>
    public int[]? IntegerValues { get; init; }

    /// <summary>
    /// Double vector; NA is R's specific NaN payload (see
    /// RNumeric.IsNaReal) - a computed NaN is preserved as
    /// double.NaN, never collapsed into NA.
    /// </summary>
    public double[]? DoubleValues { get; init; }

    /// <summary>Character vector; a null entry is NA_character_, distinct from "".</summary>
    public string?[]? CharacterValues { get; init; }

    /// <summary>Raw (byte) vector; no NA concept.</summary>
    public byte[]? RawValues { get; init; }

    /// <summary>List: an arbitrary sequence of independently-typed RValues.</summary>
    public RValue[]? ListValues { get; init; }

    /// <summary>
    /// Row count for a Table (TypeTag == Table). Every entry in
    /// ListValues (the table's columns) must have exactly this many
    /// elements - docs/spec.md section 6.
    /// </summary>
    public int? RowCount { get; init; }

    /// <summary>Element names, if any (an empty string means "unnamed", per R's own convention).</summary>
    public string?[]? Names { get; init; }

    /// <summary>The `dim` attribute, if any.</summary>
    public int[]? Dim { get; init; }

    /// <summary>The `class` attribute, if any (e.g. ["factor"], ["data.frame"]).</summary>
    public string[]? Class { get; init; }

    /// <summary>Any other attribute (e.g. a factor's "levels"), encoded recursively.</summary>
    public Dictionary<string, RValue>? Attributes { get; init; }

    public int Length => TypeTag switch
    {
        RTypeTag.Null => 0,
        RTypeTag.Logical => LogicalCodes?.Length ?? 0,
        RTypeTag.Integer => IntegerValues?.Length ?? 0,
        RTypeTag.Double => DoubleValues?.Length ?? 0,
        RTypeTag.Character => CharacterValues?.Length ?? 0,
        RTypeTag.Raw => RawValues?.Length ?? 0,
        RTypeTag.List => ListValues?.Length ?? 0,
        RTypeTag.Table => ListValues?.Length ?? 0, // column count, matching Names' column-name count
        _ => 0,
    };

    public static RValue Null() => new() { TypeTag = RTypeTag.Null };
    public static RValue OfLogical(byte[] codes) => new() { TypeTag = RTypeTag.Logical, LogicalCodes = codes };
    public static RValue OfInteger(int[] values) => new() { TypeTag = RTypeTag.Integer, IntegerValues = values };
    public static RValue OfDouble(double[] values) => new() { TypeTag = RTypeTag.Double, DoubleValues = values };
    public static RValue OfCharacter(string?[] values) => new() { TypeTag = RTypeTag.Character, CharacterValues = values };
    public static RValue OfRaw(byte[] values) => new() { TypeTag = RTypeTag.Raw, RawValues = values };
    public static RValue OfList(RValue[] values) => new() { TypeTag = RTypeTag.List, ListValues = values };

    /// <summary>
    /// Builds a Table RValue from named columns - the row count is
    /// taken from the first column and every other column is
    /// validated to match (throws ArgumentException on mismatch, a
    /// programmer error on the encode side; the decode side has its
    /// own InvalidDataException check for the equivalent wire-level
    /// corruption case - see RValueCodec).
    /// </summary>
    public static RValue OfTable(IReadOnlyList<(string Name, RValue Column)> columns, string[]? classNames = null)
    {
        int rowCount = columns.Count > 0 ? columns[0].Column.Length : 0;
        var values = new RValue[columns.Count];
        var names = new string?[columns.Count];

        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].Column.Length != rowCount)
            {
                throw new ArgumentException(
                    $"Column '{columns[i].Name}' has length {columns[i].Column.Length}, " +
                    $"expected {rowCount} (taken from the first column).",
                    nameof(columns));
            }

            values[i] = columns[i].Column;
            names[i] = columns[i].Name;
        }

        return new RValue
        {
            TypeTag = RTypeTag.Table,
            RowCount = rowCount,
            ListValues = values,
            Names = names,
            Class = classNames ?? new[] { "data.frame" },
        };
    }

    /// <summary>
    /// Decode-side table construction: validates each column's length
    /// against the declared row count and throws InvalidDataException
    /// (not ArgumentException) on mismatch, since a failure here means
    /// the wire data itself is corrupt or desynced, not a caller
    /// mistake. Names/Class are attached afterward by
    /// RValueCodec.ReadAttributes, same as every other type.
    /// </summary>
    internal static RValue FromWireTable(int rowCount, RValue[] columns)
    {
        foreach (RValue column in columns)
        {
            if (column.Length != rowCount)
            {
                throw new InvalidDataException(
                    $"Table column length {column.Length} does not match declared RowCount " +
                    $"{rowCount} - stream is desynced or corrupt.");
            }
        }

        return new RValue { TypeTag = RTypeTag.Table, RowCount = rowCount, ListValues = columns };
    }

    /// <summary>Convenience accessor for a Table's columns by name (falls back to "V1", "V2", ... for unnamed columns).</summary>
    public IReadOnlyDictionary<string, RValue> GetTableColumns()
    {
        if (TypeTag != RTypeTag.Table)
        {
            throw new InvalidOperationException($"GetTableColumns() requires TypeTag.Table, this value is {TypeTag}.");
        }

        var result = new Dictionary<string, RValue>(ListValues!.Length);
        for (int i = 0; i < ListValues.Length; i++)
        {
            string name = Names?[i] ?? $"V{i + 1}";
            result[name] = ListValues[i];
        }

        return result;
    }
}
