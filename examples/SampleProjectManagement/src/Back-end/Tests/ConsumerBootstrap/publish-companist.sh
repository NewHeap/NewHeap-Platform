#!/bin/sh
set -eu
# Scope workstation GC to the compiler process in nested Linux build containers.
# Do not export this setting into the final application image.
DOTNET_gcServer=0 dotnet publish "$@"
