#!/usr/bin/env bash
# Regenerates the LVGL fonts in src/ui/fonts/ from Nunito (SIL Open Font License, see OFL.txt).
#
#   cd firmware/tools/fonts && ./build_fonts.sh
#
# Needs node (npx fetches lv_font_conv) and python. The static weights here were cut from Nunito's
# variable font (github.com/googlefonts/nunito) with:
#   python -m fontTools.varLib.instancer "Nunito[wght].ttf" wght=800 -o Nunito-w800.ttf
set -euo pipefail
cd "$(dirname "$0")"
OUT=../../src/ui/fonts
mkdir -p "$OUT"
CONV="${LV_FONT_CONV:-npx --yes lv_font_conv@1.5.2}"

# Printable ASCII, plus • … ° – ‹ › × ·
TEXT="0x20-0x7E,0x2022,0x2026,0x00B0,0x2013,0x2039,0x203A,0x00D7,0x00B7"

# name  ttf  size  range  montserrat-fallback-size
gen() {
  local name=$1 ttf=$2 size=$3 range=$4 fb=$5
  $CONV --no-compress --no-prefilter --bpp 4 --size "$size" --font "$ttf" -r "$range" \
    --format lvgl --lv-include lvgl.h -o "$OUT/$name.c" >/dev/null
  # LV_SYMBOL_* icons aren't in Nunito: let them fall through to the built-in Montserrat
  python - "$OUT/$name.c" "$fb" <<'PY'
import sys
path, fb = sys.argv[1], sys.argv[2]
s = open(path, encoding="utf-8").read()
s = s.replace("#ifdef LV_LVGL_H_INCLUDE_SIMPLE", "#ifdef LV_LVGL_H_INCLUDE_SIMPLE", 1)
s = s.replace("\n#endif\n\n#ifndef ", f"\n#endif\n\nLV_FONT_DECLARE(lv_font_montserrat_{fb})\n\n#ifndef ", 1)
s = s.replace("    .dsc = &font_dsc", f"    .fallback = &lv_font_montserrat_{fb},\n    .dsc = &font_dsc", 1)
open(path, "w", encoding="utf-8", newline="\n").write(s)
PY
  echo "  $name"
}

gen nunito_digits_150 Nunito-w900.ttf 150 "0x30-0x3A,0x2D,0x25" 48
gen nunito_digits_110 Nunito-w900.ttf 110 "0x30-0x3A,0x2D,0x25" 48
gen nunito_64 Nunito-w900.ttf 64 "$TEXT" 48
gen nunito_40 Nunito-w800.ttf 40 "$TEXT" 40
gen nunito_28 Nunito-w800.ttf 28 "$TEXT" 28
gen nunito_22 Nunito-w800.ttf 22 "$TEXT" 20
gen nunito_18 Nunito-w700.ttf 18 "$TEXT" 16
