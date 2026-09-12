using System.Buffers;
using System.Buffers.Binary;

namespace RWire;

/// <summary>
/// Pure wire-level encode/decode helpers for the EVAL/CALL/GET_OBJ/
/// SET_OBJ/CREATE_REF/RELEASE_REF request/response payloads (docs/
/// spec.md sections 4.2-4.4). Extracted out of ProcessSupervisor
/// (Phase 8) because every method here is a pure function with no
/// dependency on process/connection/session state - moving them here
/// doesn't change ProcessSupervisor's actual coupling problem (see
/// docs/phases/processsupervisor-decomposition.md), but does shrink
/// the file and make these encode/decode rules independently testable
/// without any of the process/connection machinery.
/// </summary>
internal static class ProcessSupervisorWireCodec
{
    internal static byte[] EncodeHandleId(long id)
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, id);
        return buffer;
    }

    internal static long DecodeHandleIdResult(Frame response)
    {
        if (response.MsgType == MsgType.Error)
        {
            throw DecodeError(response.Payload.Span);
        }

        if (response.MsgType != MsgType.Result || response.Payload.Length != 8)
        {
            throw new InvalidOperationException(
                $"Expected an 8-byte handle id in RESULT, got {response.MsgType} " +
                $"with {response.Payload.Length} payload bytes.");
        }

        return BinaryPrimitives.ReadInt64LittleEndian(response.Payload.Span);
    }

    internal static void EnsureSuccessAck(Frame response)
    {
        if (response.MsgType == MsgType.Error)
        {
            throw DecodeError(response.Payload.Span);
        }

        if (response.MsgType != MsgType.Result)
        {
            throw new InvalidOperationException($"Expected RESULT or ERROR, got {response.MsgType}.");
        }
    }

    internal static ArrayBufferWriter<byte> EncodeEvalPayload(string expression)
    {
        var writer = new ArrayBufferWriter<byte>();
        WireStrings.Write(writer, expression);
        return writer;
    }

    internal static ArrayBufferWriter<byte> EncodeCallPayload(string functionName, IReadOnlyList<RCallArgument> arguments)
    {
        var writer = new ArrayBufferWriter<byte>();
        WireStrings.Write(writer, functionName);

        Span<byte> countSpan = writer.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(countSpan, arguments.Count);
        writer.Advance(4);

        foreach (RCallArgument arg in arguments)
        {
            Span<byte> isHandleSpan = writer.GetSpan(1);
            isHandleSpan[0] = (byte)(arg.IsHandle ? 1 : 0);
            writer.Advance(1);

            if (arg.IsHandle)
            {
                long id = arg.Handle.Id; // throws ObjectDisposedException if released
                Span<byte> idSpan = writer.GetSpan(8);
                BinaryPrimitives.WriteInt64LittleEndian(idSpan, id);
                writer.Advance(8);
            }
            else
            {
                RValueCodec.Encode(writer, arg.Value);
            }
        }

        return writer;
    }

    internal static RValue DecodeResponse(Frame response)
    {
        if (response.MsgType == MsgType.Error)
        {
            throw DecodeError(response.Payload.Span);
        }

        if (response.MsgType != MsgType.Result)
        {
            throw new InvalidOperationException(
                $"Expected RESULT or ERROR, got {response.MsgType}.");
        }

        return RValueCodec.Decode(response.Payload.Span);
    }

    /// <summary>
    /// Decodes an ERROR frame's payload into an RErrorException.
    /// Wire shape (docs/spec.md section 4.2, ERROR):
    ///   [Message: Len(4)+UTF8] [ClassCount(4)] {Len(4)+UTF8}*ClassCount [HasCall(1)] [Call: Len(4)+UTF8]?
    /// This is a structured object sent over the protocol itself, not
    /// something inferred from stdout/stderr - the connection's data
    /// channel and the diagnostic-logging channel are deliberately
    /// separate (docs/spec.md section 2), and relying on stdout/stderr
    /// to communicate a specific request's failure back to the caller
    /// that made it would be fragile (ordering isn't guaranteed to
    /// line up with which request caused it, and there is nothing
    /// stopping arbitrary R code from writing to stdout/stderr itself).
    /// </summary>
    internal static RErrorException DecodeError(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        string message = WireStrings.Read(payload, ref offset);

        int classCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
        offset += 4;
        var classes = new string[classCount];
        for (int i = 0; i < classCount; i++)
        {
            classes[i] = WireStrings.Read(payload, ref offset);
        }

        byte hasCall = payload[offset];
        offset += 1;
        string? call = hasCall == 1 ? WireStrings.Read(payload, ref offset) : null;

        return new RErrorException(message, classes, call);
    }
}
