namespace RWire;

/// <summary>
/// Extension-method sugar over RTypeConverter for the two directions
/// that come up at call sites: decoding a received RValue into a .NET
/// type, and encoding a .NET value into an RValue to send. Both
/// default to RTypeConverter.Default; pass an explicit converter if
/// you've registered custom conversions on your own instance instead.
/// </summary>
public static class RValueConversionExtensions
{
    /// <summary>
    /// Converts this RValue to TDest - a basic type (int, DateTime,
    /// decimal, ...), an enum (from a factor), an array/List&lt;T&gt;/
    /// IEnumerable&lt;T&gt; (from an atomic vector, a generic list, or
    /// a Table - one element per row), a Dictionary&lt;TKey,TValue&gt;
    /// (from a named list), or a plain class/struct (from a named
    /// list, or the first row of a Table).
    /// </summary>
    public static TDest To<TDest>(this RValue value) => RTypeConverter.Default.Convert<RValue, TDest>(value);

    /// <summary>Same as <see cref="To{TDest}(RValue)"/>, using a specific converter instead of RTypeConverter.Default.</summary>
    public static TDest To<TDest>(this RValue value, RTypeConverter converter) => converter.Convert<RValue, TDest>(value);

    /// <summary>
    /// Converts an arbitrary .NET value to an RValue - the mirror of
    /// <see cref="To{TDest}(RValue)"/>. An IEnumerable&lt;TRecord&gt;
    /// where TRecord looks like a data record becomes a TABLE; an
    /// IEnumerable of a directly bulk-convertible scalar type (int,
    /// double, string, ...) becomes a proper atomic vector, not a
    /// generic list of boxed scalars.
    /// </summary>
    public static RValue ToRValue<TSource>(this TSource value) =>
        RTypeConverter.Default.Convert<TSource, RValue>(value);

    /// <summary>Same as <see cref="ToRValue{TSource}(TSource)"/>, using a specific converter instead of RTypeConverter.Default.</summary>
    public static RValue ToRValue<TSource>(this TSource value, RTypeConverter converter) =>
        converter.Convert<TSource, RValue>(value);
}
