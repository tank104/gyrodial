#!/usr/bin/env bash
# Builds the face simulator: the real main/ui code and LVGL on the desktop, with a pretend board
# and link. It renders every face into PNGs in tools/sim/out/ so they can be looked at without
# the hardware.
#
#   cd firmware/tools/sim && ./build.sh && ./build/sim.exe
#
# Needs gcc (MSYS2 ucrt64 on Windows: pacman -S mingw-w64-ucrt-x86_64-gcc) and the LVGL that
# ESP-IDF's component manager downloads (run `idf.py reconfigure` in firmware/ once first).
set -euo pipefail
cd "$(dirname "$0")"
FW=../..
LVGL="$FW/managed_components/lvgl__lvgl"
[ -d "$LVGL/src" ] || { echo "LVGL not found: run 'idf.py reconfigure' in firmware/ first"; exit 1; }
[ -d /c/msys64/ucrt64/bin ] && export PATH=/c/msys64/ucrt64/bin:$PATH
mkdir -p build/lvgl out

INC="-I$FW/include -I$LVGL -I$FW/main"
DEFS="-DLV_CONF_INCLUDE_SIMPLE -DLV_LVGL_H_INCLUDE_SIMPLE"

# LVGL changes rarely: build it once into a library
if [ ! -f build/liblvgl.a ]; then
  echo "building LVGL..."
  objs=()
  while IFS= read -r c; do
    o="build/lvgl/$(echo "${c#$LVGL/src/}" | tr '/' '_').o"
    gcc -O2 -w $INC $DEFS -c "$c" -o "$o" &
    objs+=("$o")
    while [ "$(jobs -r | wc -l)" -ge 8 ]; do sleep 0.1; done
  done < <(find "$LVGL/src" -name '*.c')
  wait
  ar rcs build/liblvgl.a "${objs[@]}"
fi

echo "building the faces..."
for f in $FW/main/ui/fonts/*.c; do gcc -O1 -w $INC $DEFS -c "$f" -o "build/$(basename "$f" .c).o"; done
g++ -std=gnu++17 -O1 -Wall -Wextra -Wno-unused-parameter -Wno-missing-field-initializers $INC $DEFS \
  -I. $FW/main/ui/*.cpp $FW/main/model.cpp sim_board.cpp sim_main.cpp build/nunito_*.o build/liblvgl.a -o build/sim.exe
echo "built tools/sim/build/sim.exe"
