# testthat automatically sources every helper-*.R file before running
# tests. options(rwire.testing = TRUE) is also set in tests/testthat.R
# for the Rscript entry point, but set again here so this helper works
# equally when tests are run via testthat::test_dir() directly without
# going through that script.
options(rwire.testing = TRUE)
source(file.path("..", "..", "worker.R"))
