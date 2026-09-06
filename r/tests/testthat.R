# Run with: Rscript tests/testthat.R   (from the r/ directory)
# or:       testthat::test_dir("tests/testthat")
#
# Also runnable from C# via tests/RWire.Tests/RTestthatSuiteTests.cs,
# which asserts on this script's process exit code - stop_on_failure
# is passed explicitly (rather than relying on testthat's own default,
# which has changed across versions) so a failing R-side test reliably
# produces a non-zero exit code here, not just a printed summary.
library(testthat)

options(rwire.testing = TRUE)
test_dir("tests/testthat", reporter = "summary", stop_on_failure = TRUE)
