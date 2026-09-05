namespace RWire;

/// <summary>
/// Thrown when the R side responds to EVAL/CALL/GET_OBJ/SET_OBJ/
/// CREATE_REF/RELEASE_REF with an ERROR frame - this represents a
/// caught R condition (a non-fatal error), not a protocol or
/// connection failure. The connection remains usable after this
/// exception and the supervisor stays Ready - this deliberately does
/// NOT put it into Faulted (docs/spec.md section 12.5: this is the
/// non-fatal path).
///
/// The error is sent as a structured object over the wire protocol
/// itself (message + condition classes + deparsed call, all inside
/// the frame payload) - it never relies on stdout/stderr, which
/// carries only diagnostic logging and would be an unreliable,
/// unstructured way to communicate a specific request's failure back
/// to the caller that made it.
/// </summary>
public sealed class RErrorException : Exception
{
    /// <summary>
    /// R's condition class hierarchy for the error, e.g.
    /// ["simpleError", "error", "condition"], or a custom condition
    /// class if the R code raised one.
    /// </summary>
    public IReadOnlyList<string> Classes { get; }

    /// <summary>The deparsed R call that raised the condition, if R attached one (conditionCall() can be NULL).</summary>
    public string? Call { get; }

    public RErrorException(string message, IReadOnlyList<string> classes, string? call)
        : base(message)
    {
        Classes = classes;
        Call = call;
    }
}
