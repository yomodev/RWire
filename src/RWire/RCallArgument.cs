namespace RWire;

/// <summary>
/// One argument to a CALL - either an inline value or a reference to
/// an object already held via SET_OBJ, resolved on the R side without
/// its data crossing the wire (docs/spec.md section 4.4). Implicitly
/// convertible from both RValue and RHandle, so most call sites don't
/// need to construct this explicitly.
/// </summary>
public readonly struct RCallArgument
{
    private readonly RValue? _value;
    private readonly RHandle? _handle;

    private RCallArgument(RValue? value, RHandle? handle)
    {
        _value = value;
        _handle = handle;
    }

    public static RCallArgument FromValue(RValue value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), null);

    public static RCallArgument FromHandle(RHandle handle) =>
        new(null, handle ?? throw new ArgumentNullException(nameof(handle)));

    public static implicit operator RCallArgument(RValue value) => FromValue(value);
    public static implicit operator RCallArgument(RHandle handle) => FromHandle(handle);

    internal bool IsHandle => _handle is not null;

    internal RValue Value => _value
        ?? throw new InvalidOperationException("This argument is a handle, not an inline value.");

    internal RHandle Handle => _handle
        ?? throw new InvalidOperationException("This argument is an inline value, not a handle.");
}
