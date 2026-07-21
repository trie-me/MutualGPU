#!/bin/sh
set -eu
sdk_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cmp "$sdk_dir/Sources/MutualGPUProvider/Provider.proto" "$sdk_dir/../../../src/MutualGPU.Protocol/provider.proto"
