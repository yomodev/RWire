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
        _ => 0,
    };

    public static RValue Null() => new() { TypeTag = RTypeTag.Null };
    public static RValue OfLogical(byte[] codes) => new() { TypeTag = RTypeTag.Logical, LogicalCodes = codes };
    public static RValue OfInteger(int[] values) => new() { TypeTag = RTypeTag.Integer, IntegerValues = values };
    public static RValue OfDouble(double[] values) => new() { TypeTag = RTypeTag.Double, DoubleValues = values };
    public static RValue OfCharacter(string?[] values) => new() { TypeTag = RTypeTag.Character, CharacterValues = values };
    public static RValue OfRaw(byte[] values) => new() { TypeTag = RTypeTag.Raw, RawValues = values };
    public static RValue OfList(RValue[] values) => new() { TypeTag = RTypeTag.List, ListValues = values };
}
