# RWire worker script (Phase 1)
#
# Implements the real frame protocol: connects back to the C# host,
# sends a proper HELLO frame, then runs a read-dispatch-write message
# loop handling PING and SHUTDOWN. EXEC/EVAL/CALL/GET_OBJ/SET_OBJ/
# CREATE_REF/RELEASE_REF are not implemented yet (Phases 2-3) - they
# get an ERROR response rather than being silently ignored or crashing
# the loop.
#
# Usage:
#   Rscript worker.R --channel=socket --port=<port> --token=<token>
#
# Wire format (docs/spec.md section 4.1, little-endian throughout):
#   [Length(4)][MsgType(1)][CorrelationId(4)][PayloadLen(4)][Payload(N)]
# Length = 9 + PayloadLen (i.e. everything after the Length field).

# ---- Message type constants (must match src/RWire/MsgType.cs) -----

MSG_HELLO       <- 0x01L
MSG_PING        <- 0x02L
MSG_PONG        <- 0x03L
MSG_EXEC        <- 0x04L
MSG_EVAL        <- 0x05L
MSG_CALL        <- 0x06L
MSG_GET_OBJ     <- 0x07L
MSG_SET_OBJ     <- 0x08L
MSG_CREATE_REF  <- 0x09L
MSG_RELEASE_REF <- 0x0AL
MSG_SHUTDOWN    <- 0x0BL
MSG_RESULT      <- 0x0CL
MSG_ERROR       <- 0x0DL

# ---- Frame I/O -------------------------------------------------------

#' Reads one frame from `con`. Returns NULL on a clean EOF (host closed
#' the connection), or a list(msg_type, correlation_id, payload).
read_frame <- function(con) {
  length_prefix <- readBin(con, what = "integer", n = 1, size = 4, endian = "little")
  if (length(length_prefix) == 0) {
    return(NULL)
  }

  msg_type_raw <- readBin(con, what = "raw", n = 1)
  correlation_id <- readBin(con, what = "integer", n = 1, size = 4, endian = "little")
  payload_len <- readBin(con, what = "integer", n = 1, size = 4, endian = "little")

  payload <- if (payload_len > 0) {
    readBin(con, what = "raw", n = payload_len)
  } else {
    raw(0)
  }

  list(
    msg_type = as.integer(msg_type_raw),
    correlation_id = correlation_id,
    payload = payload
  )
}

#' Writes one frame to `con` and flushes immediately - request/response
#' semantics mean the C# side is blocked waiting for exactly this.
write_frame <- function(con, msg_type, correlation_id, payload = raw(0)) {
  writeBin(as.integer(9L + length(payload)), con, size = 4L, endian = "little")
  writeBin(as.raw(msg_type), con)
  writeBin(as.integer(correlation_id), con, size = 4L, endian = "little")
  writeBin(as.integer(length(payload)), con, size = 4L, endian = "little")
  if (length(payload) > 0) {
    writeBin(payload, con)
  }
  flush(con)
}

# ---- Payload helpers --------------------------------------------------

#' Builds a [Len(4)][UTF-8 bytes] block for a single string and appends
#' it to `con` (a raw connection opened for writing).
write_length_prefixed_string <- function(con, s) {
  string_bytes <- charToRaw(enc2utf8(s))
  writeBin(as.integer(length(string_bytes)), con, size = 4L, endian = "little")
  writeBin(string_bytes, con)
}

build_hello_payload <- function(token, r_version) {
  con <- rawConnection(raw(0), "w")
  on.exit(close(con))
  write_length_prefixed_string(con, token)
  write_length_prefixed_string(con, r_version)
  rawConnectionValue(con)
}

build_error_payload <- function(message_text) {
  con <- rawConnection(raw(0), "w")
  on.exit(close(con))
  write_length_prefixed_string(con, message_text)
  rawConnectionValue(con)
}

# ---- Argument parsing --------------------------------------------------

parse_arg <- function(args, name) {
  prefix <- paste0("--", name, "=")
  matched <- args[startsWith(args, prefix)]
  if (length(matched) == 0) {
    stop(sprintf("Missing required argument: --%s", name))
  }
  sub(prefix, "", matched[[1]])
}

# ---- Dispatch -----------------------------------------------------------

#' Handles one incoming frame. Returns "shutdown" to signal the main
#' loop to exit after this frame, or NULL otherwise. Any error thrown
#' here is caught by the caller and turned into an ERROR response
#' without killing the loop - only PING and SHUTDOWN are implemented in
#' Phase 1.
dispatch_frame <- function(con, frame) {
  if (frame$msg_type == MSG_PING) {
    write_frame(con, MSG_PONG, frame$correlation_id)
    return(invisible(NULL))
  }

  if (frame$msg_type == MSG_SHUTDOWN) {
    write_frame(con, MSG_RESULT, frame$correlation_id)
    return("shutdown")
  }

  # EXEC/EVAL/CALL/GET_OBJ/SET_OBJ/CREATE_REF/RELEASE_REF arrive in
  # Phases 2-3. Respond with a clear, well-formed ERROR rather than
  # silently dropping the frame or crashing the loop.
  error_payload <- build_error_payload(sprintf(
    "Message type %d is not implemented yet (Phase 1 only handles PING and SHUTDOWN).",
    frame$msg_type
  ))
  write_frame(con, MSG_ERROR, frame$correlation_id, error_payload)
  invisible(NULL)
}

# ---- Main ---------------------------------------------------------------

main <- function(args) {
  channel <- parse_arg(args, "channel")
  port <- as.integer(parse_arg(args, "port"))
  token <- parse_arg(args, "token")

  if (channel != "socket") {
    stop(sprintf(
      "Unsupported channel type: %s (only 'socket' exists in Phase 1)",
      channel
    ))
  }

  con <- socketConnection(
    host = "127.0.0.1",
    port = port,
    open = "a+b",
    blocking = TRUE
  )
  on.exit(close(con), add = TRUE)

  hello_payload <- build_hello_payload(token, R.version.string)
  write_frame(con, MSG_HELLO, 0L, hello_payload)

  repeat {
    frame <- read_frame(con)
    if (is.null(frame)) {
      break # host closed the connection
    }

    outcome <- tryCatch(
      dispatch_frame(con, frame),
      error = function(e) {
        # Non-fatal per-request error: report it, keep the loop alive.
        # This is the path spec.md section 12.5 calls out as
        # deliberately NOT triggering a supervisor restart.
        error_payload <- build_error_payload(conditionMessage(e))
        write_frame(con, MSG_ERROR, frame$correlation_id, error_payload)
        invisible(NULL)
      }
    )

    if (identical(outcome, "shutdown")) {
      break
    }
  }
}

tryCatch(
  main(commandArgs(trailingOnly = TRUE)),
  error = function(e) {
    message(sprintf("worker.R fatal error: %s", conditionMessage(e)))
    quit(status = 1, save = "no")
  }
)
