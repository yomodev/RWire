namespace RWire;

/// <summary>
/// Wire message types (docs/spec.md section 4.2).
///
/// NOTE: the original spec table listed PING/PONG sharing a single
/// value (0x02) as documentation shorthand for "the heartbeat pair" -
/// they need distinct codes since they're different frames travelling
/// in opposite directions. Split here into 0x02/0x03, shifting every
/// subsequent value up by one relative to the original table. This is
/// recorded in docs/progress.md under "Decisions changed since
/// spec.md" and spec.md's table has been corrected to match - this
/// enum is the single source of truth for the numbering.
/// </summary>
public enum MsgType : byte
{
    /// <summary>R -> C#. Sent once on connect; payload is [token][R version], both length-prefixed UTF-8 strings.</summary>
    Hello = 0x01,

    /// <summary>C# -> R. Heartbeat request.</summary>
    Ping = 0x02,

    /// <summary>R -> C#. Heartbeat response.</summary>
    Pong = 0x03,

    /// <summary>C# -> R. Evaluate a script/expression, no return value expected. Not implemented before Phase 2.</summary>
    Exec = 0x04,

    /// <summary>C# -> R. Evaluate an arbitrary expression, return the value. Not implemented before Phase 2.</summary>
    Eval = 0x05,

    /// <summary>C# -> R. Invoke a named function with typed/handle arguments. Not implemented before Phase 2.</summary>
    Call = 0x06,

    /// <summary>C# -> R. Fetch value referenced by handle. Not implemented before Phase 3.</summary>
    GetObj = 0x07,

    /// <summary>C# -> R. Store a value, get back a new handle. Not implemented before Phase 3.</summary>
    SetObj = 0x08,

    /// <summary>C# -> R. Increment refcount on an existing handle. Not implemented before Phase 3.</summary>
    CreateRef = 0x09,

    /// <summary>C# -> R. Decrement refcount / free. Not implemented before Phase 3.</summary>
    ReleaseRef = 0x0A,

    /// <summary>C# -> R. Graceful stop.</summary>
    Shutdown = 0x0B,

    /// <summary>R -> C#. Successful response.</summary>
    Result = 0x0C,

    /// <summary>R -> C#. Failure response; connection stays alive.</summary>
    Error = 0x0D,
}
