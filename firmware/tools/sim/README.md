# Face simulator

Runs the real faces (`main/ui`) and LVGL on the desktop with a pretend board and link. A script
drives them through every state (PC connecting, music, volume, Claude, Claude Code asking, DialKit,
a Teams call, focus, the toys, pairing) and saves what the glass would show:
`out/NN_name.png` for each, and `out/sheet.png` with all of them.

```sh
cd firmware && idf.py reconfigure        # once, so the component manager downloads LVGL
cd tools/sim && ./build.sh && ./build/sim.exe
```

On Windows, `build.sh` uses MSYS2's gcc (`C:\msys64\ucrt64\bin`, `pacman -S mingw-w64-ucrt-x86_64-gcc`)
from Git Bash. The script is `sim_main.cpp`; the pretend board and link are `sim_board.cpp` (it prints
what the faces send to the PC). Pass `-q` to keep it quiet.
