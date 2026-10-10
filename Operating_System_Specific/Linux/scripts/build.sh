#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
mkdir -p "$root/.build"
cc -std=c11 -D_POSIX_C_SOURCE=200809L -Wall -Wextra -Werror -O2 -fPIC \
    -D_FORTIFY_SOURCE=3 -fstack-protector-strong -Wl,-z,relro,-z,now,-z,noexecstack \
    -shared "$root/native/ei_backend.c" "$root/native/ei_capture.c" $(pkg-config --cflags --libs libei-1.0) -lm \
    -ffile-prefix-map="$root"=/src/batcave-linux -o "$root/.build/libbatcave-ei.so"
printf 'Built Linux input backend.\n'
