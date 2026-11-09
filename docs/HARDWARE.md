# Hardware notes

gyrodial runs on the **Waveshare ESP32-S3-Touch-AMOLED-1.75C**:

| Part | What it does here |
|---|---|
| ESP32-S3 with 8 MB PSRAM | Runs the firmware (ESP-IDF 5.5, LVGL 8.4, NimBLE) |
| CO5300 466×466 AMOLED over QSPI | The faces |
| CST9217 touch | Taps, swipes, the trackpad |
| QMI8658 IMU | Turning (gyro), lifted or flat, the air mouse, shake |
| AXP2101 PMU | Battery, charging, power off |
| ES8311 codec + speaker | Chimes and clicks |

The display init, pins and I2C addresses come from Waveshare's own board support package.

## If your board behaves differently

Each orientation-dependent behaviour is one named constant, in case a board needs it flipped:

- **Turn direction:** `TURN_SIGN` in `firmware/main/config.h` (or *Flip turn direction* in the app's settings).
- **Screen-up axis** (drives lifted/flat and the air mouse): `kScreenUpZ` in `firmware/main/hal/motion.cpp`.
- **Air-mouse directions:** `kPointerXSign` / `kPointerYSign` in `motion.cpp`.
- **Touch orientation:** `TOUCH_MIRROR_X/Y` in `config.h`.
- **Display:** `LCD_COL_OFFSET` (6, as in Waveshare's examples) in `config.h`, and the QSPI clock `kLcdClockHz` (40 MHz, as in Waveshare's BSP) in `hal/display.cpp`.

## Diagnostics

Send `{"t":"diag"}` over the dial's COM port (it's allowed before pairing). It answers with one line
containing:

- uptime and reset reason
- display on/off and brightness, and how many times the screen has toggled
- which chips answered
- battery
- per-type counts of the touch, turn and button events it has seen
- live motion readings and the learned gyro bias
- current face and pairing state

Please include that line in bug reports.

## Logs and crashes

The USB port carries only the app's protocol. ESP-IDF's console (boot log, `ESP_LOGx` lines,
panics) is on UART0: GPIO43 TX / GPIO44 RX, 115200 baud. It logs a line for each device that fails
to start (touch, IMU, PMU, codec), and the dial keeps running without it.

A crash is also saved to the `coredump` partition. Read it back over USB with
`idf.py -p COM5 coredump-info`, using the `gyrodial.elf` from the same release's firmware zip (or your own
build) to decode it.

## Backing up the board

Flashing replaces whatever is on the board. To keep a copy of the current firmware (all 32 MB of
flash), run this first, replacing `COM4` with your board's port:

```powershell
python -m esptool --chip esp32s3 --port COM4 read_flash 0 0x2000000 board-backup.bin
# and to put it back later:
python -m esptool --chip esp32s3 --port COM4 write_flash 0 board-backup.bin
```

If the board won't enter download mode, hold **BOOT** while plugging it in.

## Checked on a real board

- Every chip starts.
- Lying flat, it reads +0.96 g on z.
- Its gyro sits about 15 °/s off zero on X. The dial learns that and removes it within a second of lying still.
- The PMU's fuel gauge reads nonsense, so battery % comes from the voltage.
- Pairing starts, cancels and times out correctly over USB, and the app connects over Bluetooth too.
- Touch (trackpad) and turning work by hand, and the air mouse's axes were tuned on it.

Results from other boards (even "it all works") are welcome in an issue.
