# Mirrors tests/RWire.Tests/FrameCodecTests.cs on the R side - frame
# write/read over a rawConnection, no socket involved.

test_that("write_frame then read_frame round-trips a payload exactly", {
  payload <- as.raw(1:20)

  con <- rawConnection(raw(0), "w")
  write_frame(con, MSG_EVAL, 7L, payload)
  bytes <- rawConnectionValue(con)
  close(con)

  read_con <- rawConnection(bytes, "r")
  on.exit(close(read_con))
  frame <- read_frame(read_con)

  expect_equal(frame$msg_type, MSG_EVAL)
  expect_equal(frame$correlation_id, 7L)
  expect_equal(frame$payload, payload)
})

test_that("write_frame then read_frame round-trips a zero-length payload", {
  con <- rawConnection(raw(0), "w")
  write_frame(con, MSG_PING, 1L)
  bytes <- rawConnectionValue(con)
  close(con)

  read_con <- rawConnection(bytes, "r")
  on.exit(close(read_con))
  frame <- read_frame(read_con)

  expect_equal(frame$msg_type, MSG_PING)
  expect_equal(length(frame$payload), 0)
})

test_that("read_frame returns NULL on a clean EOF", {
  con <- rawConnection(raw(0), "r")
  on.exit(close(con))
  expect_null(read_frame(con))
})

test_that("handle id round-trips through write_handle_id/read_handle_id", {
  con <- rawConnection(raw(0), "w")
  write_handle_id(con, 42L)
  bytes <- rawConnectionValue(con)
  close(con)

  read_con <- rawConnection(bytes, "r")
  on.exit(close(read_con))
  expect_equal(read_handle_id(read_con), 42L)
})

test_that("read_handle_id rejects a non-zero high word", {
  con <- rawConnection(raw(0), "w")
  write_int32(con, 1L)
  write_int32(con, 1L) # high word non-zero - out of the supported 32-bit range
  bytes <- rawConnectionValue(con)
  close(con)

  read_con <- rawConnection(bytes, "r")
  on.exit(close(read_con))
  expect_error(read_handle_id(read_con))
})

test_that("build_error_payload wraps a plain string as a condition-shaped payload", {
  payload <- build_error_payload("something went wrong")

  con <- rawConnection(payload, "r")
  on.exit(close(con))
  message_text <- read_length_prefixed_string(con)
  class_count <- read_int32(con)

  expect_equal(message_text, "something went wrong")
  expect_gte(class_count, 1) # simpleError() always has at least "simpleError","error","condition"
})

test_that("build_error_payload carries a real condition's classes and call", {
  raise_boom <- function() stop("boom")
  cond <- tryCatch(raise_boom(), error = function(e) e)
  payload <- build_error_payload(cond)

  con <- rawConnection(payload, "r")
  on.exit(close(con))
  message_text <- read_length_prefixed_string(con)
  class_count <- read_int32(con)
  classes <- vapply(seq_len(class_count), function(i) read_length_prefixed_string(con), character(1))
  has_call <- read_byte_value(con)

  expect_equal(message_text, "boom")
  expect_true("simpleError" %in% classes)
  expect_equal(has_call, 1L) # raise_boom()'s call frame gives stop() a non-NULL call to attach
})
