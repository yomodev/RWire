# Mirrors the R-side half of tests/RWire.Tests/HandleLifecycleTests.cs -
# exercises the registry functions directly (no socket, no C# process).
# Each test resets .rwire_registry first so tests don't leak state into
# each other via the shared package-level environment.

reset_registry <- function() {
  rm(list = ls(envir = .rwire_registry), envir = .rwire_registry)
  # rwire_registry_allocate_id() increments the module-level counter
  # via <<- against .rwire_next_handle_id in the sourced worker.R's
  # environment - not reset here, since ever-increasing IDs across
  # tests is fine and actually more realistic than resetting to 0.
}

test_that("set then get round-trips the value", {
  reset_registry()
  id <- rwire_registry_set(c(1, 2, 3))
  expect_equal(rwire_registry_get(id), c(1, 2, 3))
})

test_that("get on an unknown id errors", {
  reset_registry()
  expect_error(rwire_registry_get(999999L), "Unknown or already-released handle")
})

test_that("release removes the entry", {
  reset_registry()
  id <- rwire_registry_set("hello")
  rwire_registry_release(id)
  expect_error(rwire_registry_get(id))
})

test_that("release on an already-released id is a no-op, not an error", {
  reset_registry()
  id <- rwire_registry_set("hello")
  rwire_registry_release(id)
  expect_no_error(rwire_registry_release(id))
})

test_that("create_ref increments refcount; object survives one release but not two", {
  reset_registry()
  id <- rwire_registry_set(42)
  rwire_registry_create_ref(id)

  rwire_registry_release(id)
  expect_equal(rwire_registry_get(id), 42) # still alive - second reference remains

  rwire_registry_release(id)
  expect_error(rwire_registry_get(id)) # now actually gone
})

test_that("create_ref on an unknown id errors", {
  reset_registry()
  expect_error(rwire_registry_create_ref(999999L), "Unknown or already-released handle")
})

test_that("sweep removes only entries older than the max age", {
  reset_registry()
  fresh_id <- rwire_registry_set("fresh")
  stale_id <- rwire_registry_set("stale")

  # Backdate the stale entry's last_touched past the sweep threshold.
  stale_key <- as.character(stale_id)
  entry <- get(stale_key, envir = .rwire_registry, inherits = FALSE)
  entry$last_touched <- Sys.time() - (RWIRE_HANDLE_MAX_AGE_SECONDS + 10)
  assign(stale_key, entry, envir = .rwire_registry)

  rwire_registry_sweep()

  expect_equal(rwire_registry_get(fresh_id), "fresh")
  expect_error(rwire_registry_get(stale_id))
})

test_that("get touches last_touched, protecting an object from the sweep", {
  reset_registry()
  id <- rwire_registry_set("keep me")

  key <- as.character(id)
  entry <- get(key, envir = .rwire_registry, inherits = FALSE)
  entry$last_touched <- Sys.time() - (RWIRE_HANDLE_MAX_AGE_SECONDS + 10)
  assign(key, entry, envir = .rwire_registry)

  rwire_registry_get(id) # should refresh last_touched to now
  rwire_registry_sweep()

  expect_equal(rwire_registry_get(id), "keep me")
})
