using System.Collections;
using System.Reflection;

namespace RWire;

/// <summary>
/// A registry of converters between arbitrary .NET types and RValue
/// (in both directions), with automatic chaining through intermediate
/// types when no direct converter is registered - e.g. if A→C isn't
/// registered but A→B and B→C are, Convert&lt;A, C&gt; composes them
/// automatically via a breadth-first search over the registered edges.
///
/// <see cref="Default"/> comes pre-populated with converters for the
/// common basic types beyond RWire's core atomic set (long, uint,
/// decimal, DateTime, DateOnly, TimeOnly, Guid, and their nullable
/// counterparts), plus structural handling - not flat edges, since
/// these need type-parameterized logic - for arrays/List&lt;T&gt;/
/// IEnumerable&lt;T&gt;, Dictionary&lt;TKey,TValue&gt;, enums (as R
/// factors), and plain classes/structs (as named R lists) via
/// reflection over public readable/writable properties.
///
/// This is a convenience layer for ordinary POCO/collection interop,
/// not a replacement for constructing RValue directly. For the
/// genuinely performance-critical bulk-transfer path (docs/spec.md
/// section 6 - a 10M-row table), building the RValue directly via
/// RValue.OfDouble/OfTable etc. avoids the per-element boxing and
/// reflection this converter uses for anything beyond a directly
/// bulk-convertible element type.
/// </summary>
public sealed class RTypeConverter
{
    private readonly Dictionary<(Type From, Type To), Func<object?, object?>> _edges = new();

    /// <summary>
    /// Registers a converter from TFrom to TTo. Overwrites any
    /// existing direct edge for the same pair. Chaining (via
    /// <see cref="Convert{TFrom, TTo}"/>) automatically uses this edge
    /// as one hop in a longer path if no direct edge exists for a
    /// requested conversion.
    /// </summary>
    public void Register<TFrom, TTo>(Func<TFrom, TTo> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        _edges[(typeof(TFrom), typeof(TTo))] = value => converter((TFrom)value!);
    }

    /// <summary>Converts value from TFrom to TTo, via a direct edge, structural handling, or a chained path through registered edges.</summary>
    public TTo Convert<TFrom, TTo>(TFrom value) => (TTo)ConvertObject(value, typeof(TFrom), typeof(TTo))!;

    /// <summary>Non-generic entry point, used internally for recursive/structural conversions where the concrete types are only known at runtime.</summary>
    public object? ConvertObject(object? value, Type fromType, Type toType)
    {
        // A non-null Nullable<T> is boxed as a plain T at runtime (a
        // CLR boxing quirk), so the *static* fromType passed in for a
        // Nullable<T> source never matches value's actual boxed
        // representation - unwrap it here so edge lookups below see
        // the type that's actually there. A null Nullable<T> boxes to
        // a real null reference, which the null check just below
        // already handles regardless of fromType.
        Type? fromUnderlying = Nullable.GetUnderlyingType(fromType);
        if (fromUnderlying is not null)
        {
            fromType = fromUnderlying;
        }

        if (value is null)
        {
            if (toType == typeof(RValue))
            {
                return RValue.Null();
            }

            if (toType.IsValueType && Nullable.GetUnderlyingType(toType) is null)
            {
                throw new InvalidOperationException(
                    $"Cannot convert a null value to non-nullable value type {toType}.");
            }

            return null;
        }

        if (toType.IsInstanceOfType(value))
        {
            return value;
        }

        if (_edges.TryGetValue((fromType, toType), out Func<object?, object?>? direct))
        {
            return direct(value);
        }

        if (TryStructuralConvert(value, fromType, toType, out object? structuralResult))
        {
            return structuralResult;
        }

        List<Func<object?, object?>>? path = FindPath(fromType, toType);
        if (path is null)
        {
            throw new InvalidOperationException(
                $"No converter (direct, structural, or chained) is registered from {fromType} to {toType}. " +
                $"Use Register<{fromType.Name}, {toType.Name}>(...) to add one, or add an intermediate " +
                "type this converter already knows how to reach on both sides.");
        }

        object? current = value;
        foreach (Func<object?, object?> edge in path)
        {
            current = edge(current);
        }
        return current;
    }

    private List<Func<object?, object?>>? FindPath(Type from, Type to)
    {
        // Breadth-first search over the registered direct edges only -
        // structural conversions (arrays, RValue shapes, etc.) aren't
        // graph nodes in the traditional sense and are tried before
        // this search runs (see ConvertObject), so this purely chains
        // scalar-to-scalar (or scalar-to-RValue/RValue-to-scalar)
        // conversions that were explicitly Register()ed.
        var visited = new HashSet<Type> { from };
        var queue = new Queue<(Type Node, List<Func<object?, object?>> Path)>();
        queue.Enqueue((from, new List<Func<object?, object?>>()));

        while (queue.Count > 0)
        {
            (Type node, List<Func<object?, object?>> path) = queue.Dequeue();

            foreach (((Type edgeFrom, Type edgeTo), Func<object?, object?> edge) in _edges)
            {
                if (edgeFrom != node || visited.Contains(edgeTo))
                {
                    continue;
                }

                var newPath = new List<Func<object?, object?>>(path) { edge };
                if (edgeTo == to)
                {
                    return newPath;
                }

                visited.Add(edgeTo);
                queue.Enqueue((edgeTo, newPath));
            }
        }

        return null;
    }

    // -----------------------------------------------------------------
    // Structural conversions - shapes that need type-parameterized
    // logic rather than a single flat edge per pair of types.
    // -----------------------------------------------------------------

    private bool TryStructuralConvert(object value, Type fromType, Type toType, out object? result)
    {
        Type? nullableUnderlying = Nullable.GetUnderlyingType(toType);
        if (nullableUnderlying is not null)
        {
            result = ConvertObject(value, fromType, nullableUnderlying);
            return true;
        }

        if (fromType == typeof(RValue))
        {
            result = ConvertFromRValue((RValue)value, toType);
            return true;
        }

        if (toType == typeof(RValue))
        {
            result = ConvertToRValue(value, fromType);
            return true;
        }

        result = null;
        return false;
    }

    // ---- .NET -> RValue ----

    private RValue ConvertToRValue(object value, Type fromType)
    {
        if (fromType.IsEnum)
        {
            return ConvertEnumToFactor(value, fromType);
        }

        if (TryGetDictionaryTypes(fromType, out Type? keyType, out Type? valueType))
        {
            return ConvertDictionaryToNamedList((IEnumerable)value, keyType!, valueType!);
        }

        if (fromType != typeof(string) && value is IEnumerable enumerable)
        {
            Type elementType = GetEnumerableElementType(fromType) ?? typeof(object);
            return ConvertEnumerableToRValue(enumerable, elementType);
        }

        if (IsPlainObjectType(fromType))
        {
            return ConvertObjectToNamedList(value, fromType);
        }

        throw new InvalidOperationException(
            $"No converter registered from {fromType} to RValue, and it doesn't match any structural " +
            "shape this converter handles (enum, dictionary, enumerable, or plain class/struct).");
    }

    private RValue ConvertEnumToFactor(object value, Type enumType)
    {
        string[] names = Enum.GetNames(enumType);
        int code = Array.IndexOf(names, value.ToString()) + 1; // R factor codes are 1-based
        return new RValue
        {
            TypeTag = RTypeTag.Integer,
            IntegerValues = new[] { code },
            Class = new[] { "factor" },
            Attributes = new Dictionary<string, RValue> { ["levels"] = RValue.OfCharacter(names.Cast<string?>().ToArray()) },
        };
    }

    private RValue ConvertDictionaryToNamedList(IEnumerable dictionary, Type keyType, Type valueType)
    {
        // Reflection over KeyValuePair<,> rather than `dynamic` - the
        // latter needs an explicit Microsoft.CSharp reference that
        // this project doesn't otherwise require.
        Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(keyType, valueType);
        PropertyInfo keyProperty = kvpType.GetProperty("Key")!;
        PropertyInfo valueProperty = kvpType.GetProperty("Value")!;

        var names = new List<string?>();
        var values = new List<RValue>();

        foreach (object entry in dictionary)
        {
            object key = keyProperty.GetValue(entry)!;
            object? val = valueProperty.GetValue(entry);
            names.Add(key.ToString());
            values.Add(val is null ? RValue.Null() : (RValue)ConvertObject(val, valueType, typeof(RValue))!);
        }

        return new RValue { TypeTag = RTypeTag.List, ListValues = values.ToArray(), Names = names.ToArray() };
    }

    /// <summary>
    /// Converts a sequence to an RValue: a proper atomic vector when
    /// every element bulk-converts to the same length-1 atomic
    /// RTypeTag (e.g. IEnumerable&lt;long&gt; -> a Double vector, not
    /// a List of length-1 Doubles), a TABLE when the element type
    /// looks like a data record (docs/spec.md section 6 - this is the
    /// flagship case this converter exists for), or a generic List of
    /// individually-converted elements otherwise.
    /// </summary>
    internal RValue ConvertEnumerableToRValue(IEnumerable source, Type elementType)
    {
        var elements = source.Cast<object?>().ToList();

        if (TryBulkConvertToAtomicVector(elements, elementType, out RValue? atomicVector))
        {
            return atomicVector!;
        }

        if (IsPlainObjectType(elementType))
        {
            return ConvertRecordSequenceToTable(elements, elementType);
        }

        var listValues = new RValue[elements.Count];
        for (int i = 0; i < elements.Count; i++)
        {
            listValues[i] = elements[i] is null
                ? RValue.Null()
                : (RValue)ConvertObject(elements[i], elementType, typeof(RValue))!;
        }
        return RValue.OfList(listValues);
    }

    private bool TryBulkConvertToAtomicVector(List<object?> elements, Type elementType, out RValue? result)
    {
        if (elements.Count == 0)
        {
            // Nothing to infer a shape from - fall through to the
            // generic empty List rather than guessing an atomic type.
            result = null;
            return false;
        }

        RValue[] converted = new RValue[elements.Count];
        RTypeTag? consistentTag = null;

        for (int i = 0; i < elements.Count; i++)
        {
            RValue elementValue;
            try
            {
                elementValue = elements[i] is null
                    ? RValue.Null()
                    : (RValue)ConvertObject(elements[i], elementType, typeof(RValue))!;
            }
            catch (InvalidOperationException)
            {
                result = null;
                return false;
            }

            if (elementValue.TypeTag == RTypeTag.Null || elementValue.Length != 1 ||
                !IsAtomicScalarTag(elementValue.TypeTag))
            {
                result = null;
                return false;
            }

            consistentTag ??= elementValue.TypeTag;
            if (elementValue.TypeTag != consistentTag)
            {
                result = null;
                return false;
            }

            converted[i] = elementValue;
        }

        result = PackAtomicVector(converted, consistentTag!.Value);
        return true;
    }

    private static bool IsAtomicScalarTag(RTypeTag tag) =>
        tag is RTypeTag.Logical or RTypeTag.Integer or RTypeTag.Double or RTypeTag.Character or RTypeTag.Raw;

    private static RValue PackAtomicVector(RValue[] scalars, RTypeTag tag) => tag switch
    {
        RTypeTag.Logical => RValue.OfLogical(scalars.Select(s => s.LogicalCodes![0]).ToArray()),
        RTypeTag.Integer => RValue.OfInteger(scalars.Select(s => s.IntegerValues![0]).ToArray()),
        RTypeTag.Double => RValue.OfDouble(scalars.Select(s => s.DoubleValues![0]).ToArray()),
        RTypeTag.Character => RValue.OfCharacter(scalars.Select(s => s.CharacterValues![0]).ToArray()),
        RTypeTag.Raw => RValue.OfRaw(scalars.Select(s => s.RawValues![0]).ToArray()),
        _ => throw new NotSupportedException($"Unsupported atomic tag for bulk packing: {tag}"),
    };

    private RValue ConvertRecordSequenceToTable(List<object?> elements, Type elementType)
    {
        PropertyInfo[] properties = GetReadableProperties(elementType);
        var columns = new (string Name, RValue Column)[properties.Length];

        for (int c = 0; c < properties.Length; c++)
        {
            PropertyInfo property = properties[c];
            var columnValues = new object?[elements.Count];
            for (int r = 0; r < elements.Count; r++)
            {
                columnValues[r] = elements[r] is null ? null : property.GetValue(elements[r]);
            }

            RValue columnValue = ConvertEnumerableToRValue(columnValues, property.PropertyType);
            columns[c] = (property.Name, columnValue);
        }

        return RValue.OfTable(columns);
    }

    private RValue ConvertObjectToNamedList(object value, Type type)
    {
        PropertyInfo[] properties = GetReadableProperties(type);
        var values = new RValue[properties.Length];
        var names = new string?[properties.Length];

        for (int i = 0; i < properties.Length; i++)
        {
            object? propertyValue = properties[i].GetValue(value);
            values[i] = propertyValue is null
                ? RValue.Null()
                : (RValue)ConvertObject(propertyValue, properties[i].PropertyType, typeof(RValue))!;
            names[i] = properties[i].Name;
        }

        return new RValue { TypeTag = RTypeTag.List, ListValues = values, Names = names };
    }

    // ---- RValue -> .NET ----

    private object? ConvertFromRValue(RValue rv, Type toType)
    {
        if (rv.TypeTag == RTypeTag.Null)
        {
            return toType.IsValueType ? Activator.CreateInstance(toType) : null;
        }

        if (toType.IsEnum)
        {
            return ConvertFactorToEnum(rv, toType);
        }

        if (toType.IsArray)
        {
            Type elementType = toType.GetElementType()!;
            IList list = BuildListFromRValue(rv, elementType);
            Array array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }

        if (TryGetDictionaryTypes(toType, out Type? keyType, out Type? valueType))
        {
            return BuildDictionaryFromNamedList(rv, keyType!, valueType!);
        }

        Type? enumerableElementType = GetGenericEnumerableElementType(toType);
        if (enumerableElementType is not null)
        {
            return BuildListFromRValue(rv, enumerableElementType);
        }

        if (rv.TypeTag == RTypeTag.List && rv.Names is not null)
        {
            return BuildObjectFromNamedList(rv, toType);
        }

        if (rv.TypeTag == RTypeTag.Table)
        {
            return BuildObjectFromTableFirstRow(rv, toType);
        }

        throw new InvalidOperationException(
            $"No structural conversion from RValue (TypeTag={rv.TypeTag}) to {toType}. " +
            $"Register a converter explicitly if this shape is intentional.");
    }

    private static object ConvertFactorToEnum(RValue rv, Type enumType)
    {
        if (rv.Attributes is null || !rv.Attributes.TryGetValue("levels", out RValue? levelsValue))
        {
            throw new InvalidOperationException(
                $"RValue has no 'levels' attribute - cannot convert to enum {enumType}. Expected a factor.");
        }

        int code = rv.IntegerValues![0];
        string levelName = levelsValue.CharacterValues![code - 1]!; // factor codes are 1-based
        return Enum.Parse(enumType, levelName);
    }

    /// <summary>
    /// Builds a List&lt;elementType&gt; from an RValue - the shared
    /// core behind array/List&lt;T&gt;/IEnumerable&lt;T&gt; decoding.
    /// A Table converts to a sequence of elementType records (one per
    /// row, via BuildObjectFromTableRow per row); an atomic vector or
    /// generic List converts element-by-element.
    /// </summary>
    private IList BuildListFromRValue(RValue rv, Type elementType)
    {
        Type listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;

        if (rv.TypeTag == RTypeTag.Table)
        {
            int rowCount = rv.RowCount ?? 0;
            IReadOnlyDictionary<string, RValue> columns = rv.GetTableColumns();
            for (int row = 0; row < rowCount; row++)
            {
                list.Add(BuildObjectFromTableRow(columns, row, elementType));
            }
            return list;
        }

        switch (rv.TypeTag)
        {
            case RTypeTag.Logical:
                foreach (byte code in rv.LogicalCodes!)
                {
                    list.Add(ConvertObject(RValue.OfLogical(new[] { code }), typeof(RValue), elementType));
                }
                break;
            case RTypeTag.Integer:
                foreach (int v in rv.IntegerValues!)
                {
                    list.Add(ConvertObject(RValue.OfInteger(new[] { v }), typeof(RValue), elementType));
                }
                break;
            case RTypeTag.Double:
                foreach (double v in rv.DoubleValues!)
                {
                    list.Add(ConvertObject(RValue.OfDouble(new[] { v }), typeof(RValue), elementType));
                }
                break;
            case RTypeTag.Character:
                foreach (string? v in rv.CharacterValues!)
                {
                    list.Add(v is null
                        ? ConvertObject(RValue.Null(), typeof(RValue), elementType)
                        : ConvertObject(RValue.OfCharacter(new[] { v }), typeof(RValue), elementType));
                }
                break;
            case RTypeTag.Raw:
                foreach (byte v in rv.RawValues!)
                {
                    list.Add(ConvertObject(RValue.OfRaw(new[] { v }), typeof(RValue), elementType));
                }
                break;
            case RTypeTag.List:
                foreach (RValue element in rv.ListValues!)
                {
                    list.Add(ConvertObject(element, typeof(RValue), elementType));
                }
                break;
            default:
                throw new InvalidOperationException($"Cannot build a sequence of {elementType} from RValue TypeTag {rv.TypeTag}.");
        }

        return list;
    }

    private object BuildObjectFromTableRow(IReadOnlyDictionary<string, RValue> columns, int row, Type type)
    {
        object instance = Activator.CreateInstance(type)!;
        foreach (PropertyInfo property in GetWritableProperties(type))
        {
            if (!columns.TryGetValue(property.Name, out RValue? column))
            {
                continue;
            }

            RValue cell = ExtractScalar(column, row);
            property.SetValue(instance, ConvertObject(cell, typeof(RValue), property.PropertyType));
        }
        return instance;
    }

    private object BuildObjectFromTableFirstRow(RValue rv, Type toType)
    {
        IReadOnlyDictionary<string, RValue> columns = rv.GetTableColumns();
        return BuildObjectFromTableRow(columns, 0, toType);
    }

    private object BuildObjectFromNamedList(RValue rv, Type toType)
    {
        object instance = Activator.CreateInstance(toType)!;
        var columns = new Dictionary<string, RValue>();
        for (int i = 0; i < rv.ListValues!.Length; i++)
        {
            string name = rv.Names![i] ?? $"V{i + 1}";
            columns[name] = rv.ListValues[i];
        }

        foreach (PropertyInfo property in GetWritableProperties(toType))
        {
            if (columns.TryGetValue(property.Name, out RValue? value))
            {
                property.SetValue(instance, ConvertObject(value, typeof(RValue), property.PropertyType));
            }
        }
        return instance;
    }

    private object BuildDictionaryFromNamedList(RValue rv, Type keyType, Type valueType)
    {
        Type dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var dict = (IDictionary)Activator.CreateInstance(dictType)!;

        if (rv.TypeTag != RTypeTag.List || rv.Names is null)
        {
            throw new InvalidOperationException("Expected a named List RValue to build a Dictionary.");
        }

        for (int i = 0; i < rv.ListValues!.Length; i++)
        {
            object key = keyType == typeof(string)
                ? rv.Names[i] ?? $"V{i + 1}"
                : ConvertObject(rv.Names[i], typeof(string), keyType)!;
            object? value = ConvertObject(rv.ListValues[i], typeof(RValue), valueType);
            dict.Add(key, value!);
        }

        return dict;
    }

    private static RValue ExtractScalar(RValue column, int index) => column.TypeTag switch
    {
        RTypeTag.Logical => RValue.OfLogical(new[] { column.LogicalCodes![index] }),
        RTypeTag.Integer => RValue.OfInteger(new[] { column.IntegerValues![index] }),
        RTypeTag.Double => RValue.OfDouble(new[] { column.DoubleValues![index] }),
        RTypeTag.Character => column.CharacterValues![index] is { } s
            ? RValue.OfCharacter(new[] { s })
            : RValue.Null(),
        RTypeTag.Raw => RValue.OfRaw(new[] { column.RawValues![index] }),
        RTypeTag.List => column.ListValues![index],
        _ => throw new NotSupportedException($"Cannot extract a scalar from column TypeTag {column.TypeTag}."),
    };

    // ---- reflection/type-shape helpers ----

    private static PropertyInfo[] GetReadableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

    private static PropertyInfo[] GetWritableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToArray();

    private static bool IsPlainObjectType(Type type) =>
        !type.IsPrimitive && type != typeof(string) && type != typeof(RValue) &&
        !typeof(IEnumerable).IsAssignableFrom(type) && !type.IsEnum &&
        GetReadableProperties(type).Length > 0;

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        Type? genericEnumerable = type.GetInterfaces().Prepend(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return genericEnumerable?.GetGenericArguments()[0];
    }

    private static Type? GetGenericEnumerableElementType(Type type)
    {
        if (!type.IsGenericType)
        {
            return null;
        }

        Type definition = type.GetGenericTypeDefinition();
        if (definition == typeof(List<>) || definition == typeof(IList<>) ||
            definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>) ||
            definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>))
        {
            return type.GetGenericArguments()[0];
        }

        return GetEnumerableElementType(type);
    }

    private static bool TryGetDictionaryTypes(Type type, out Type? keyType, out Type? valueType)
    {
        Type? dictInterface = type.GetInterfaces().Prepend(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (dictInterface is null)
        {
            keyType = null;
            valueType = null;
            return false;
        }

        Type[] args = dictInterface.GetGenericArguments();
        keyType = args[0];
        valueType = args[1];
        return true;
    }

    // -----------------------------------------------------------------
    // Default instance
    // -----------------------------------------------------------------

    /// <summary>
    /// A ready-to-use converter pre-populated with the common basic
    /// types beyond RWire's core atomic set. Shared/mutable - call
    /// Register on it directly, or construct your own RTypeConverter
    /// for an isolated registry.
    /// </summary>
    public static RTypeConverter Default { get; } = CreateDefault();

    private static RTypeConverter CreateDefault()
    {
        var converter = new RTypeConverter();

        // Direct RWire atomic types - thin wrappers, mostly for
        // uniformity so every basic type can go through Convert<>/To<>
        // the same way, even ones RValue already has a factory for.
        converter.Register<bool, RValue>(b => new RValue { TypeTag = RTypeTag.Logical, LogicalCodes = new[] { b ? RNumeric.LogicalTrue : RNumeric.LogicalFalse } });
        converter.Register<RValue, bool>(v => v.LogicalCodes![0] == RNumeric.LogicalTrue);

        converter.Register<int, RValue>(i => RValue.OfInteger(new[] { i }));
        converter.Register<RValue, int>(v => v.IntegerValues![0]);

        converter.Register<double, RValue>(d => RValue.OfDouble(new[] { d }));
        converter.Register<RValue, double>(v => v.DoubleValues![0]);

        converter.Register<string, RValue>(s => RValue.OfCharacter(new[] { s }));
        converter.Register<RValue, string>(v => v.CharacterValues![0]!);

        converter.Register<byte, RValue>(b => RValue.OfRaw(new[] { b }));
        converter.Register<RValue, byte>(v => v.RawValues![0]);

        // Widened integer types - R has no native 8/16-bit or unsigned
        // integer type; these ride the existing Integer vector.
        converter.Register<sbyte, RValue>(v => RValue.OfInteger(new[] { (int)v }));
        converter.Register<RValue, sbyte>(v => checked((sbyte)v.IntegerValues![0]));
        converter.Register<short, RValue>(v => RValue.OfInteger(new[] { (int)v }));
        converter.Register<RValue, short>(v => checked((short)v.IntegerValues![0]));
        converter.Register<ushort, RValue>(v => RValue.OfInteger(new[] { (int)v }));
        converter.Register<RValue, ushort>(v => checked((ushort)v.IntegerValues![0]));
        converter.Register<char, RValue>(v => RValue.OfCharacter(new string?[] { v.ToString() }));
        converter.Register<RValue, char>(v => v.CharacterValues![0]![0]);

        // R has no native 64-bit or unsigned-32-bit integer type
        // either (see docs/progress.md's handle-ID discussion) - these
        // ride the Double vector. Values beyond 2^53 lose precision;
        // that's a real, documented limitation, not an oversight.
        converter.Register<long, RValue>(v => RValue.OfDouble(new[] { (double)v }));
        converter.Register<RValue, long>(v => checked((long)v.DoubleValues![0]));
        converter.Register<uint, RValue>(v => RValue.OfDouble(new[] { (double)v }));
        converter.Register<RValue, uint>(v => checked((uint)v.DoubleValues![0]));
        converter.Register<ulong, RValue>(v => RValue.OfDouble(new[] { (double)v }));
        converter.Register<RValue, ulong>(v => checked((ulong)v.DoubleValues![0]));
        converter.Register<float, RValue>(v => RValue.OfDouble(new[] { (double)v }));
        converter.Register<RValue, float>(v => (float)v.DoubleValues![0]);
        converter.Register<decimal, RValue>(v => RValue.OfDouble(new[] { (double)v }));
        converter.Register<RValue, decimal>(v => (decimal)v.DoubleValues![0]);

        // Dates/times - mapped onto R's actual native representations
        // (a Date is a double day-count with class "Date"; POSIXct is
        // a double second-count with class c("POSIXct","POSIXt")) so a
        // value produced here is directly usable as a real R date/time
        // object, not just an opaque number.
        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        converter.Register<DateTime, RValue>(dt => new RValue
        {
            TypeTag = RTypeTag.Double,
            DoubleValues = new[] { (dt.ToUniversalTime() - unixEpoch).TotalSeconds },
            Class = new[] { "POSIXct", "POSIXt" },
        });
        converter.Register<RValue, DateTime>(v => unixEpoch.AddSeconds(v.DoubleValues![0]));

        DateOnly epochDate = DateOnly.FromDateTime(unixEpoch);
        converter.Register<DateOnly, RValue>(d => new RValue
        {
            TypeTag = RTypeTag.Double,
            DoubleValues = new[] { (double)(d.DayNumber - epochDate.DayNumber) },
            Class = new[] { "Date" },
        });
        converter.Register<RValue, DateOnly>(v => epochDate.AddDays((int)v.DoubleValues![0]));

        converter.Register<TimeOnly, RValue>(t => new RValue
        {
            TypeTag = RTypeTag.Double,
            DoubleValues = new[] { t.ToTimeSpan().TotalSeconds },
            Class = new[] { "difftime" },
            Attributes = new Dictionary<string, RValue> { ["units"] = RValue.OfCharacter(new string?[] { "secs" }) },
        });
        converter.Register<RValue, TimeOnly>(v => TimeOnly.FromTimeSpan(TimeSpan.FromSeconds(v.DoubleValues![0])));

        converter.Register<Guid, RValue>(g => RValue.OfCharacter(new string?[] { g.ToString() }));
        converter.Register<RValue, Guid>(v => Guid.Parse(v.CharacterValues![0]!));

        return converter;
    }
}
