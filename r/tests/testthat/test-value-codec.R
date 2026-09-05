# Mirrors the round-trip coverage in tests/RWire.Tests/RValueCodecTests.cs
# on the R side, exercising write_r_value/read_r_value directly over a
# rawConnection (no socket, no C# process involved) - see docs/spec.md
# section 12.2.

round_trip <- function(x) {
  con <- rawConnection(raw(0), "w")
  write_r_value(con, x)
  bytes <- rawConnectionValue(con)
  close(con)

  read_con <- rawConnection(bytes, "r")
  on.exit(close(read_con))
  read_r_value(read_con)
}

test_that("NULL round-trips", {
  expect_null(round_trip(NULL))
})

test_that("double vector with NA round-trips, distinct from computed NaN", {
  computed_nan <- 0 / 0
  input <- c(1.5, NA_real_, computed_nan, -2.25)

  result <- round_trip(input)

  expect_equal(result[1], 1.5)
  expect_true(is.na(result[2]))
  expect_true(is.nan(result[3]))
  # is.na() is TRUE for both NA_real_ and NaN in R itself - identical()
  # is what actually distinguishes the two bit patterns, and is the
  # right check here for "did the round trip preserve which one this
  # was" (the property RValueCodecTests.Double_NaReal_And_ComputedNaN_
  # StayDistinct_AfterRoundTrip checks on the C# side).
  expect_false(identical(result[2], result[3]))
  expect_true(identical(result[2], NA_real_))
  expect_equal(result[4], -2.25)
})

test_that("integer vector with NA round-trips", {
  input <- c(1L, NA_integer_, -5L, 0L)
  expect_equal(round_trip(input), input)
})

test_that("logical vector with NA round-trips", {
  input <- c(TRUE, NA, FALSE)
  expect_equal(round_trip(input), input)
})

test_that("character vector with NA and empty string round-trip as distinct values", {
  input <- c("hello", NA_character_, "", "world")
  expect_equal(round_trip(input), input)
})

test_that("character vector with UTF-8 multi-byte content round-trips", {
  input <- c("héllo", "日本語", "🎉")
  expect_equal(round_trip(input), input)
})

test_that("raw vector round-trips", {
  input <- as.raw(c(0x00, 0xFF, 0x7F, 0x01))
  expect_equal(round_trip(input), input)
})

test_that("named vector round-trips with names intact", {
  input <- c(a = 10L, b = 20L, 30L)
  result <- round_trip(input)
  expect_equal(as.integer(result), c(10L, 20L, 30L))
  expect_equal(names(result), c("a", "b", ""))
})

test_that("factor round-trips with levels and class intact", {
  input <- factor(c("low", "high", "low"), levels = c("low", "medium", "high"))
  result <- round_trip(input)
  expect_true(is.factor(result))
  expect_equal(levels(result), c("low", "medium", "high"))
  expect_equal(as.character(result), c("low", "high", "low"))
})

test_that("list of mixed types round-trips", {
  input <- list(1:2, c("x", NA), NULL)
  result <- round_trip(input)
  expect_equal(result[[1]], 1:2)
  expect_equal(result[[2]], c("x", NA))
  expect_null(result[[3]])
})

test_that("data.frame round-trips as a Table with row.names set", {
  input <- data.frame(id = 1:3, name = c("a", NA, "c"), stringsAsFactors = FALSE)
  result <- round_trip(input)

  expect_true(is.data.frame(result))
  expect_equal(nrow(result), 3)
  expect_equal(result$id, 1:3)
  expect_equal(result$name, c("a", NA, "c"))
  expect_equal(row.names(result), as.character(1:3))
})

test_that("data.table round-trips and setDT is applied", {
  skip_if_not_installed("data.table")
  input <- data.table::data.table(x = 1:3, y = c(1.5, 2.5, 3.5))
  result <- round_trip(input)

  expect_true(data.table::is.data.table(result))
  expect_equal(result$x, 1:3)
  expect_equal(result$y, c(1.5, 2.5, 3.5))
})

test_that("zero-row data.frame round-trips", {
  input <- data.frame(a = integer(0), b = character(0), stringsAsFactors = FALSE)
  result <- round_trip(input)

  expect_true(is.data.frame(result))
  expect_equal(nrow(result), 0)
  expect_equal(ncol(result), 2)
})
