#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
sh "$root/scripts/build.sh"
cc -std=c11 -Wall -Wextra -Werror -O2 -fPIC -shared "$root/tests/eis_fixture.c" \
    $(pkg-config --cflags --libs libeis-1.0) -o "$root/.build/libeis-fixture.so"
cd "$root"
python3 -m unittest discover -s tests -v
BATCAVE_TEST_PORTAL_BUS=1 dbus-run-session -- python3 -m unittest discover -s tests -p test_portal.py -v
python3 -m compileall -q batcave_linux
