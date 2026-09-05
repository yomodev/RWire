namespace RWire;

/// <summary>
/// Thrown when the R side responds to EVAL/CALL with an ERROR frame -
/// this represents a caught R condition (a non-fatal error inside the
/// evaluated expression/function), not a protocol or connection
/// failure. The connection remains usable after this exception and
/// the supervisor stays Ready - this deliberately does NOT put it into
/// Faulted (docs/spec.md section 12.5: this is the non-fatal path).
/// </summary>
public sealed class RErrorException : Exception
{
    public RErrorException(string message) : base(message)
    {
    }
}
