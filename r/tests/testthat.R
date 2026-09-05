# Run with: Rscript tests/testthat.R   (from the r/ directory)
# or:       testthat::test_dir("tests/testthat")
library(testthat)

options(rwire.testing = TRUE)
test_dir("tests/testthat", reporter = "summary")
