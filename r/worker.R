# RWire worker script (Phase 2)
#
# Adds the RValue codec (atomic vectors, NA sentinels, names/dim/class
# + generic attributes, factor handling) and EVAL/CALL dispatch on top
# of Phase 1's frame protocol and heartbeat/shutdown handling.
#
# Usage:
#   Rscript worker.R --channel=socket --port=<port> --token=<token>
#
# Frame wire format (docs/spec.md section 4.1, little-endian):
#   [Length(4)][MsgType(1)][CorrelationId(4)][PayloadLen(4)][Payload(N)]
#
# RValue wire format (docs/spec.md section 5, matches
# src/RWire/RValueCodec.cs exactly - keep the two in sync by hand,
# there is no shared source of truth yet, see docs/progress.md):
#   [TypeTag(1)]
#   if TypeTag != NULL:
#     [ElementCount(4)]
#     <type-specific payload>
#     [HasNames(1)] [Names?]
#     [HasDim(1)]   [Dim?]
#     [HasClass(1)] [Class?]
#     [AttrCount(4)] { [Name][Value, recursive] }*

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

# ---- RValue type tag constants (must match src/RWire/RTypeTag.cs) --

RTAG_NULL      <- 0L
RTAG_LOGICAL   <- 1L
RTAG_INTEGER   <- 2L
RTAG_DOUBLE    <- 3L
RTAG_CHARACTER <- 4L
RTAG_RAW       <- 5L
RTAG_LIST      <- 6L

# ---- Low-level primitive read/write helpers ------------------------

write_byte_value <- function(con, b) {
  writeBin(as.raw(b), con)
}

write_int32 <- function(con, i) {
  writeBin(as.integer(i), con, size = 4L, endian = "little")
}

read_byte_value <- function(con) {
  as.integer(readBin(con, what = "raw", n = 1))
}

read_int32 <- function(con) {
  readBin(con, what = "integer", n = 1, size = 4, endian = "little")
}

# UTF-8 safe raw<->char helpers - rawToChar()/charToRaw() operate on
# bytes, but the Encoding needs to be marked explicitly so R treats
# the result as UTF-8 rather than the native/unknown encoding.
raw_to_utf8 <- function(bytes) {
  s <- rawToChar(bytes)
  Encoding(s) <- "UTF-8"
  s
}

utf8_to_raw <- function(s) {
  charToRaw(enc2utf8(s))
}

#' [Len(4)][UTF-8 bytes] - used for control-plane strings (HELLO,
#' ERROR messages, EVAL expressions, CALL function/attribute names).
#' NOT used for character *vector elements* - those go through
#' write_nullable_string/read_nullable_string, which has an NA
#' sentinel (length -1) that this plain form doesn't need.
write_length_prefixed_string <- function(con, s) {
  bytes <- utf8_to_raw(s)
  write_int32(con, length(bytes))
  if (length(bytes) > 0) writeBin(bytes, con)
}

read_length_prefixed_string <- function(con) {
  len <- read_int32(con)
  bytes <- if (len > 0) readBin(con, what = "raw", n = len) else raw(0)
  raw_to_utf8(bytes)
}

#' Character *vector element* encoding: same as above but length -1
#' means NA_character_, distinct from a zero-length string.
write_nullable_string <- function(con, s) {
  if (is.na(s)) {
    write_int32(con, -1L)
    return(invisible(NULL))
  }
  bytes <- utf8_to_raw(s)
  write_int32(con, length(bytes))
  if (length(bytes) > 0) writeBin(bytes, con)
}

read_nullable_string <- function(con) {
  len <- read_int32(con)
  if (len < 0) return(NA_character_)
  bytes <- if (len > 0) readBin(con, what = "raw", n = len) else raw(0)
  raw_to_utf8(bytes)
}

# ---- RValue attribute encode/decode --------------------------------
#
# Fast-pathed: names, dim, class (one flag byte + data each).
# Generic: everything else (currently just a factor's "levels"),
# recursively encoded as an RValue itself - docs/spec.md section 5.3.

write_attributes <- function(con, x, is_factor_value) {
  nm <- names(x)
  if (!is.null(nm)) {
    write_byte_value(con, 1L)
    for (n in nm) {
      write_length_prefixed_string(con, if (is.na(n)) "" else n)
    }
  } else {
    write_byte_value(con, 0L)
  }

  d <- dim(x)
  if (!is.null(d)) {
    write_byte_value(con, 1L)
    write_int32(con, length(d))
    writeBin(as.integer(d), con, size = 4L, endian = "little")
  } else {
    write_byte_value(con, 0L)
  }

  cls <- if (is_factor_value) "factor" else if (is.object(x)) class(x) else NULL
  if (!is.null(cls)) {
    write_byte_value(con, 1L)
    write_int32(con, length(cls))
    for (c in cls) write_length_prefixed_string(con, c)
  } else {
    write_byte_value(con, 0L)
  }

  generic_attrs <- list()
  if (is_factor_value) {
    generic_attrs[["levels"]] <- levels(x)
  }
  write_int32(con, length(generic_attrs))
  for (attr_name in names(generic_attrs)) {
    write_length_prefixed_string(con, attr_name)
    write_r_value(con, generic_attrs[[attr_name]])
  }
}

read_attributes <- function(con, value) {
  nm <- NULL
  if (read_byte_value(con) == 1L) {
    n <- length(value)
    nm <- character(n)
    for (i in seq_len(n)) {
      nm[i] <- read_length_prefixed_string(con)
    }
  }

  d <- NULL
  if (read_byte_value(con) == 1L) {
    dim_count <- read_int32(con)
    d <- read_int32_vec(con, dim_count)
  }

  cls <- NULL
  if (read_byte_value(con) == 1L) {
    class_count <- read_int32(con)
    cls <- character(class_count)
    for (i in seq_len(class_count)) {
      cls[i] <- read_length_prefixed_string(con)
    }
  }

  attr_count <- read_int32(con)
  generic_attrs <- list()
  if (attr_count > 0) {
    for (i in seq_len(attr_count)) {
      attr_name <- read_length_prefixed_string(con)
      generic_attrs[[attr_name]] <- read_r_value(con)
    }
  }

  if (!is.null(nm)) {
    names(value) <- nm
  }
  if (!is.null(d)) {
    dim(value) <- d
  }

  if (!is.null(cls) && "factor" %in% cls) {
    # Low-level factor construction: levels must be set before class.
    attr(value, "levels") <- generic_attrs[["levels"]]
    class(value) <- "factor"
  } else {
    for (attr_name in names(generic_attrs)) {
      attr(value, attr_name) <- generic_attrs[[attr_name]]
    }
    if (!is.null(cls)) {
      class(value) <- cls
    }
  }

  value
}

# ---- RValue type-specific encode/decode ----------------------------

read_int32_vec <- function(con, n) {
  if (n == 0) return(integer(0))
  readBin(con, what = "integer", n = n, size = 4, endian = "little")
}

read_double_vec <- function(con, n) {
  if (n == 0) return(double(0))
  readBin(con, what = "double", n = n, size = 8, endian = "little")
}

#' Encodes an arbitrary supported R value onto `con` (docs/spec.md
#' section 5). Vectorized writeBin is used wherever possible (integer/
#' double/raw/logical) rather than an R-level per-element loop - R's
#' own NA_integer_/NA_real_ bit patterns already match what the C#
#' side checks for, so no per-element NA translation is needed for
#' those types; writeBin just needs to write the vector's existing
#' bytes with the right endianness.
write_r_value <- function(con, x) {
  if (is.null(x)) {
    write_byte_value(con, RTAG_NULL)
    return(invisible(NULL))
  }

  is_factor_value <- is.factor(x)

  if (is_factor_value) {
    codes <- unclass(x)
    write_byte_value(con, RTAG_INTEGER)
    write_int32(con, length(codes))
    if (length(codes) > 0) writeBin(as.integer(codes), con, size = 4L, endian = "little")
  } else if (is.list(x)) {
    write_byte_value(con, RTAG_LIST)
    write_int32(con, length(x))
    for (elem in x) write_r_value(con, elem)
  } else if (is.character(x)) {
    write_byte_value(con, RTAG_CHARACTER)
    write_int32(con, length(x))
    for (s in x) write_nullable_string(con, s)
  } else if (is.double(x)) {
    write_byte_value(con, RTAG_DOUBLE)
    write_int32(con, length(x))
    if (length(x) > 0) writeBin(as.double(x), con, size = 8L, endian = "little")
  } else if (is.integer(x)) {
    write_byte_value(con, RTAG_INTEGER)
    write_int32(con, length(x))
    if (length(x) > 0) writeBin(as.integer(x), con, size = 4L, endian = "little")
  } else if (is.logical(x)) {
    write_byte_value(con, RTAG_LOGICAL)
    write_int32(con, length(x))
    if (length(x) > 0) {
      codes <- ifelse(is.na(x), 2L, as.integer(x))
      writeBin(as.integer(codes), con, size = 1L, endian = "little")
    }
  } else if (is.raw(x)) {
    write_byte_value(con, RTAG_RAW)
    write_int32(con, length(x))
    if (length(x) > 0) writeBin(x, con)
  } else {
    stop(sprintf("write_r_value: unsupported R type '%s'", typeof(x)))
  }

  write_attributes(con, x, is_factor_value)
}

#' Decodes one RValue from `con`.
read_r_value <- function(con) {
  tag <- read_byte_value(con)

  if (tag == RTAG_NULL) {
    return(NULL)
  }

  n <- read_int32(con)

  value <- if (tag == RTAG_LOGICAL) {
    raw_codes <- if (n > 0) {
      readBin(con, what = "integer", n = n, size = 1, signed = FALSE, endian = "little")
    } else {
      integer(0)
    }
    ifelse(raw_codes == 2L, NA, as.logical(raw_codes))
  } else if (tag == RTAG_INTEGER) {
    read_int32_vec(con, n)
  } else if (tag == RTAG_DOUBLE) {
    read_double_vec(con, n)
  } else if (tag == RTAG_CHARACTER) {
    if (n == 0) character(0) else vapply(seq_len(n), function(i) read_nullable_string(con), character(1))
  } else if (tag == RTAG_RAW) {
    if (n > 0) readBin(con, what = "raw", n = n) else raw(0)
  } else if (tag == RTAG_LIST) {
    if (n == 0) list() else lapply(seq_len(n), function(i) read_r_value(con))
  } else {
    stop(sprintf("read_r_value: unknown type tag %d", tag))
  }

  read_attributes(con, value)
}

# ---- Frame I/O (from Phase 1) ---------------------------------------

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
  write_int32(con, 9L + length(payload))
  write_byte_value(con, msg_type)
  write_int32(con, correlation_id)
  write_int32(con, length(payload))
  if (length(payload) > 0) {
    writeBin(payload, con)
  }
  flush(con)
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

build_result_payload <- function(value) {
  con <- rawConnection(raw(0), "w")
  on.exit(close(con))
  write_r_value(con, value)
  rawConnectionValue(con)
}

# ---- Argument parsing ------------------------------------------------

parse_arg <- function(args, name) {
  prefix <- paste0("--", name, "=")
  matched <- args[startsWith(args, prefix)]
  if (length(matched) == 0) {
    stop(sprintf("Missing required argument: --%s", name))
  }
  sub(prefix, "", matched[[1]])
}

# ---- EVAL / CALL handlers --------------------------------------------

# ---- Object registry (docs/spec.md section 8) ------------------------
#
# Handle IDs: the wire slot is 8 bytes (int64) to match
# src/RWire/ProcessSupervisor.cs, but IDs are allocated here as plain
# 32-bit R integers - base R has no native 64-bit integer type without
# the bit64 package, and a 32-bit range (~2 billion objects per R
# session) is far more than any realistic session needs. The high
# 4 bytes are always written as zero and are required to be zero on
# read; see docs/progress.md's "Decisions changed since spec.md" for
# this trade-off.

RWIRE_HANDLE_MAX_AGE_SECONDS <- 300

.rwire_registry <- new.env(parent = emptyenv())
.rwire_next_handle_id <- 0L

write_handle_id <- function(con, id) {
  write_int32(con, id)
  write_int32(con, 0L) # high word - always zero, see note above
}

read_handle_id <- function(con) {
  low <- read_int32(con)
  high <- read_int32(con)
  if (high != 0L) {
    stop("Handle ID exceeds the 32-bit range this R worker supports.")
  }
  low
}

rwire_registry_allocate_id <- function() {
  .rwire_next_handle_id <<- .rwire_next_handle_id + 1L
  if (.rwire_next_handle_id < 0L) {
    stop("Handle ID counter overflowed the 32-bit range.")
  }
  .rwire_next_handle_id
}

rwire_registry_set <- function(value) {
  id <- rwire_registry_allocate_id()
  assign(
    as.character(id),
    list(value = value, refcount = 1L, last_touched = Sys.time()),
    envir = .rwire_registry
  )
  id
}

rwire_registry_lookup_entry <- function(id) {
  key <- as.character(id)
  if (!exists(key, envir = .rwire_registry, inherits = FALSE)) {
    stop(sprintf("Unknown or already-released handle: %d", id))
  }
  get(key, envir = .rwire_registry, inherits = FALSE)
}

rwire_registry_get <- function(id) {
  key <- as.character(id)
  entry <- rwire_registry_lookup_entry(id)
  entry$last_touched <- Sys.time()
  assign(key, entry, envir = .rwire_registry)
  entry$value
}

rwire_registry_create_ref <- function(id) {
  key <- as.character(id)
  entry <- rwire_registry_lookup_entry(id)
  entry$refcount <- entry$refcount + 1L
  entry$last_touched <- Sys.time()
  assign(key, entry, envir = .rwire_registry)
  invisible(NULL)
}

#' Decrements the refcount for `id`, removing the entry once it
#' reaches zero. Releasing an already-gone handle is a NO-OP, not an
#' error - a client-side double-release (e.g. Dispose() racing the
#' finalizer) is a normal, harmless occurrence and shouldn't surface as
#' a protocol error. See docs/progress.md's "Decisions changed since
#' spec.md".
rwire_registry_release <- function(id) {
  key <- as.character(id)
  if (!exists(key, envir = .rwire_registry, inherits = FALSE)) {
    return(invisible(NULL))
  }
  entry <- get(key, envir = .rwire_registry, inherits = FALSE)
  entry$refcount <- entry$refcount - 1L
  if (entry$refcount <= 0L) {
    rm(list = key, envir = .rwire_registry)
  } else {
    entry$last_touched <- Sys.time()
    assign(key, entry, envir = .rwire_registry)
  }
  invisible(NULL)
}

#' Leak-guard sweep: removes entries untouched for longer than
#' RWIRE_HANDLE_MAX_AGE_SECONDS - a backstop against a client that
#' crashed without releasing its handles (docs/spec.md section 8).
#' Piggybacks on the existing PING heartbeat cadence rather than
#' needing a separate timer.
rwire_registry_sweep <- function() {
  now <- Sys.time()
  keys <- ls(envir = .rwire_registry)
  if (length(keys) == 0) {
    return(invisible(NULL))
  }
  stale <- vapply(keys, function(key) {
    entry <- get(key, envir = .rwire_registry, inherits = FALSE)
    as.numeric(difftime(now, entry$last_touched, units = "secs")) > RWIRE_HANDLE_MAX_AGE_SECONDS
  }, logical(1))
  if (any(stale)) {
    rm(list = keys[stale], envir = .rwire_registry)
  }
  invisible(NULL)
}

handle_eval <- function(con, frame) {
  payload_con <- rawConnection(frame$payload, "r")
  on.exit(close(payload_con))
  expr_text <- read_length_prefixed_string(payload_con)

  result_value <- eval(parse(text = expr_text), envir = .GlobalEnv)
  write_frame(con, MSG_RESULT, frame$correlation_id, build_result_payload(result_value))
}

handle_call <- function(con, frame) {
  payload_con <- rawConnection(frame$payload, "r")
  on.exit(close(payload_con))

  function_name <- read_length_prefixed_string(payload_con)
  arg_count <- read_int32(payload_con)

  args_list <- vector("list", arg_count)
  if (arg_count > 0) {
    for (i in seq_len(arg_count)) {
      is_handle <- read_byte_value(payload_con)
      if (is_handle == 1L) {
        handle_id <- read_handle_id(payload_con)
        args_list[[i]] <- rwire_registry_get(handle_id)
      } else {
        args_list[[i]] <- read_r_value(payload_con)
      }
    }
  }

  result_value <- do.call(function_name, args_list)
  write_frame(con, MSG_RESULT, frame$correlation_id, build_result_payload(result_value))
}

handle_set_obj <- function(con, frame) {
  payload_con <- rawConnection(frame$payload, "r")
  on.exit(close(payload_con))
  value <- read_r_value(payload_con)

  id <- rwire_registry_set(value)

  result_con <- rawConnection(raw(0), "w")
  on.exit(close(result_con), add = TRUE)
  write_handle_id(result_con, id)
  write_frame(con, MSG_RESULT, frame$correlation_id, rawConnectionValue(result_con))
}

handle_get_obj <- function(con, frame) {
  payload_con <- rawConnection(frame$payload, "r")
  on.exit(close(payload_con))
  id <- read_handle_id(payload_con)

  value <- rwire_registry_get(id)
  write_frame(con, MSG_RESULT, frame$correlation_id, build_result_payload(value))
}

handle_create_ref <- function(con, frame) {
  payload_con <- rawConnection(frame$payload, "r")
  on.exit(close(payload_con))
  id <- read_handle_id(payload_con)

  rwire_registry_create_ref(id)
  write_frame(con, MSG_RESULT, frame$correlation_id)
}

handle_release_ref <- function(con, frame) {
  payload_con <- rawConnection(frame$payload, "r")
  on.exit(close(payload_con))
  id <- read_handle_id(payload_con)

  rwire_registry_release(id)
  write_frame(con, MSG_RESULT, frame$correlation_id)
}

# ---- Dispatch ---------------------------------------------------------

#' Handles one incoming frame. Returns "shutdown" to signal the main
#' loop to exit after this frame, or NULL otherwise. Any error thrown
#' here is caught by the caller and turned into an ERROR response
#' without killing the loop.
dispatch_frame <- function(con, frame) {
  if (frame$msg_type == MSG_PING) {
    rwire_registry_sweep()
    write_frame(con, MSG_PONG, frame$correlation_id)
    return(invisible(NULL))
  }

  if (frame$msg_type == MSG_SHUTDOWN) {
    write_frame(con, MSG_RESULT, frame$correlation_id)
    return("shutdown")
  }

  if (frame$msg_type == MSG_EVAL) {
    handle_eval(con, frame)
    return(invisible(NULL))
  }

  if (frame$msg_type == MSG_CALL) {
    handle_call(con, frame)
    return(invisible(NULL))
  }

  if (frame$msg_type == MSG_SET_OBJ) {
    handle_set_obj(con, frame)
    return(invisible(NULL))
  }

  if (frame$msg_type == MSG_GET_OBJ) {
    handle_get_obj(con, frame)
    return(invisible(NULL))
  }

  if (frame$msg_type == MSG_CREATE_REF) {
    handle_create_ref(con, frame)
    return(invisible(NULL))
  }

  if (frame$msg_type == MSG_RELEASE_REF) {
    handle_release_ref(con, frame)
    return(invisible(NULL))
  }

  # EXEC arrives in a later phase (not part of Phase 2's or Phase 3's
  # scope - EVAL/CALL cover the tested use cases so far).
  error_payload <- build_error_payload(sprintf(
    "Message type %d is not implemented yet.",
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
      "Unsupported channel type: %s (only 'socket' exists in Phase 2)",
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

  write_frame(con, MSG_HELLO, 0L, build_hello_payload(token, R.version.string))

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
