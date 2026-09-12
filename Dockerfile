# Verification-only image: builds RWire and runs its test suite on
# Linux, so cross-platform status moves from "believed" to "verified"
# (see docs/progress.md and docs/phases/phase-8-plan.md Tier 3).
#
# This has NOT been built or run anywhere - this sandbox has no
# Docker/network access. Treat it as a well-informed first draft, not
# a confirmed-working image. If `docker build` fails on the R package
# install step or the R version resolution below, that's the most
# likely place - see the comment there.
#
# Usage (on a machine with Docker):
#   docker build -t rwire-verify .
#   docker run --rm rwire-verify
#
# What this actually verifies beyond "it compiles": every integration
# test that launches a real Rscript process runs for real here, on
# Linux, which is the part that can't be checked by reading the code -
# no Windows-specific API is used anywhere in src/RWire (verified by
# grep, not by running), but "no obviously wrong API" isn't the same
# claim as "actually works," which is what this image is for.

FROM mcr.microsoft.com/dotnet/sdk:10.0

# r-base-core is Debian/Ubuntu's standard R package. This pulls
# whatever R version the base image's package repo currently carries -
# not pinned to an exact version, since spec.md's only stated
# requirement is "R >= 4.4," and pinning here would need updating
# every time this image is rebuilt against a newer base. If a build
# ever fails specifically on an R-version-dependent test, check
# `R --version` output first before assuming the test itself is wrong.
RUN apt-get update \
    && apt-get install -y --no-install-recommends r-base-core \
    && rm -rf /var/lib/apt/lists/*

# data.table is the one R-side package dependency spec.md accepts
# (see docs/spec.md and README's Requirements section). Installed from
# CRAN at build time, so this step needs network access during
# `docker build` - if that's unavailable in a given environment,
# vendor a specific data.table version into the image instead of
# reaching out to CRAN each build.
RUN R -e "install.packages('data.table', repos='https://cloud.r-project.org')"

WORKDIR /src
COPY . .

RUN dotnet restore RWire.sln
RUN dotnet build RWire.sln --no-restore -c Release

# Runs the full suite, including the Rscript-launching integration
# tests - Rscript is now on PATH via r-base-core above, so
# ProcessSupervisor's default RScriptPath ("Rscript") resolves without
# any extra configuration, matching how it resolves on a normal
# Windows dev machine with R on PATH.
CMD ["dotnet", "test", "RWire.sln", "--no-restore", "-c", "Release", "--logger", "console;verbosity=normal"]
