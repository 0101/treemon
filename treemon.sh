#!/bin/sh
# Thin wrapper around the cross-platform lifecycle script, so nobody has to remember the dotnet fsi
# invocation. All of the behaviour lives in treemon.fsx.
exec dotnet fsi "$(dirname "$0")/treemon.fsx" "$@"
