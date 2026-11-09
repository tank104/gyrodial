# Puck for Windows

A palm-sized round dial for your Windows PC, built on the **Waveshare ESP32-S3-Touch-AMOLED-1.75C**
(about $40, nothing to solder). Turn it for volume. Flip its face to a trackpad, a focus timer, or
Claude. Approve Claude Code's permission prompts from your desk.

It's a Windows take on [Watt, the puck](https://watt.sourabhdaroji.com/) by Sourabh Daroji, which is
Mac-only. This is a separate implementation (firmware and app written from scratch). It does speak
the same page protocol as [`dialkit-watt`](https://github.com/gizmofalcon/dialkit-watt), so that
npm package works with Puck on Windows unchanged.

```
 ┌──────────── the puck (ESP32-S3) ────────────┐        ┌──────────── Windows ─────────────┐
 │ 466×466 AMOLED · touch · IMU · PMU · speaker │  USB   │ Puck.exe (tray)                  │
 │ faces: home/volume, trackpad, pointer,       │◄──────►│  volume · media · mouse          │
 │ Claude, approvals, DialKit, game, call,      │  or    │  Claude voice chat               │
 │ focus, spin, dice, pong, pairing             │  BLE   │  Claude Code hook (puck-hook.exe)│
 └──────────────────────────────────────────────┘ NDJSON │  DialKit / games (ws :47824)     │
                                                         │  Teams meeting mute              │
                                                         └──────────────────────────────────┘
```

![The faces, rendered by the desktop simulator](docs/faces.png)

## What it does

| On the puck | What happens |
|---|---|
| **Turn** it (on the home face) | System volume, with a big number and a ring. Tap: play/pause. Swipe left/right: previous/next track. Hold: mute. |
| **Side button** | Opens the face picker: turn to choose, tap to pick, hold to go back. On the faces the PC brings up (DialKit, game, Teams) it goes back. While pairing, it cancels. Held (on battery): power off. |
| **Trackpad** face, flat on the desk | The glass moves the pointer, and "up" stays up even after you turn the puck. Tap clicks. Hold or two-finger tap right-clicks. Two fingers scroll. Double-tap-and-drag drags. Turning also scrolls. |
| Trackpad face, **lifted** | Air mouse: aim at the screen with your hand's rotation, tap to click. |
| **Claude** face | Tap, talk, tap to send. Your PC turns your voice into text (Windows speech recognition), and only the text goes to Claude. Turn to scroll the reply. |
| **Claude Code** asks for permission | The puck switches to the approval face. Nothing is selected at first: turn clockwise for Allow or anticlockwise for Deny, then tap. Swipe up/down to read a long command. |
| **DialKit** page in focus (localhost) | Turn to tune the selected knob, swipe for the next one, tap to toggle/run. |
| **Teams** meeting | Tap to mute or unmute. Turn for volume. |
| **Focus** | Turn to set minutes, tap to start. Chimes when it's done, and Windows shows a notification. |
| **Spin**, **Dice** | Tap to spin or roll (or shake for dice). |
| No PC connected | It plays circular **Pong**. |

## Build one

1. **Get the board:** [Waveshare ESP32-S3-Touch-AMOLED-1.75C](https://www.waveshare.com/esp32-s3-touch-amoled-1.75c.htm). A LiPo battery is optional.
2. **Install the firmware:** any one of these works.
   - **From the app:** Settings → Firmware (it fetches `esptool` if needed).
   - **From the browser:** run `scripts/build.ps1` first, then serve `web/` over https or localhost (`python -m http.server -d web`), open it in Chrome or Edge, and click Install.
   - **From source:** `cd firmware && idf.py -p COM5 flash` (in an ESP-IDF v5.5 shell; use your board's port).
3. **Run Puck.exe:** it finds the puck over USB or Bluetooth. To pair, turn the puck to each number it shows and tap.

If the board won't enter download mode, hold **BOOT** while plugging it in.

Flashing replaces whatever is on the board. To keep a copy of the current firmware (all 32 MB of
flash), run this first, replacing `COM4` with your board's port:

```powershell
python -m esptool --chip esp32s3 --port COM4 read_flash 0 0x2000000 board-backup.bin
# and to put it back later:
python -m esptool --chip esp32s3 --port COM4 write_flash 0 board-backup.bin
```

## Building from source

Prerequisites: [ESP-IDF v5.5](https://docs.espressif.com/projects/esp-idf/en/v5.5.3/esp32s3/get-started/windows-setup.html)
(the build script finds it in `~/esp/esp-idf`, `$env:IDF_PATH`, or `-IdfPath`), the .NET 10 SDK,
and Windows 10 2004 or later.

```powershell
./scripts/build.ps1          # firmware + app → dist/Puck-win-x64.zip (self-contained, no .NET needed to run it)
```

Or piece by piece:

```powershell
cd firmware; idf.py build                    # firmware (ESP-IDF v5.5, LVGL 8.4, NimBLE), in an ESP-IDF shell
dotnet build windows/Puck.sln                # app, integrations, puck-hook
dotnet test windows/Puck.sln                 # integration tests
windows/src/Puck.App/bin/Debug/net10.0-windows10.0.22621.0/Puck.exe --self-test   # link logic against a fake puck
```

`scripts/build.ps1` copies the firmware images into the app (`windows/src/Puck.App/firmware`) and
the web installer (`web/firmware`), along with an ESP Web Tools `manifest.json`.

**Seeing the faces without a board:** `firmware/tools/sim` compiles the real face code against a
pretend board with MSYS2 gcc, and writes a PNG of every face to `tools/sim/out/`
(`cd firmware/tools/sim && ./build.sh && ./build/sim.exe`).

**Firmware logs and crashes:** the USB port carries only the app's protocol. ESP-IDF's console
(boot log, `ESP_LOGx` lines, panics) is on UART0 (GPIO43 TX / GPIO44 RX, 115200 baud). A crash is
also saved to the `coredump` partition: `idf.py -p COM5 coredump-info` reads it back over USB.

## Setting up the integrations

All of them are in **Settings → Integrations** in the tray app.

- **Claude:** paste your Anthropic API key in Settings → Claude (it's stored encrypted with Windows
  DPAPI). Puck also uses `ANTHROPIC_API_KEY` if that's set. The model defaults to
  `claude-opus-5-5` and can be changed there. For the best dictation, turn on
  *Settings → Privacy & security → Speech → Online speech recognition* in Windows. Without it, Puck
  falls back to offline recognition.
- **Claude Code approvals:** click *Install Claude Code hook*. This adds a `PermissionRequest` hook
  (and a `Notification` hook) to `%USERPROFILE%\.claude\settings.json` (or `CLAUDE_CONFIG_DIR`), after
  taking a backup, so that they run `puck-hook.exe`. The installer refuses to edit a file with
  comments or anything else it doesn't expect. When the puck isn't connected, the hook stays silent
  and Claude Code asks in the terminal as usual.

  The hook only trusts the Puck app. Each answer is signed with a per-user key in
  `%AppData%\Puck\hook.key`, and proxies are bypassed. The app's local endpoint refuses requests from
  web pages.
- **DialKit:** in your dev app, `npm install dialkit-watt` and add `<WattBridge />` next to
  `<DialRoot />`. Puck listens on `ws://127.0.0.1:47824/bridge`, and only accepts pages served from
  localhost.
- **Microsoft Teams:** turn it on, then in Teams go to *Settings → Privacy → Manage API* and enable
  the third-party app API. The first time you press mute on the puck during a meeting, Teams asks you
  to allow Puck.

## Repository layout

| Path | What |
|---|---|
| `docs/PROTOCOL.md` | The wire protocol between the puck and the app (NDJSON over USB serial or a BLE UART-style service) |
| `firmware/` | ESP-IDF project (`sdkconfig.defaults`, `partitions.csv`, LVGL from the component registry, cJSON built into IDF). `main/hal` = board (CO5300 QSPI display + LVGL port, CST9217 touch, QMI8658 motion, AXP2101 power, ES8311 sound, on IDF's native drivers). `main/hostlink` = BLE (NimBLE) + USB link, pairing. `main/ui` = the faces. `tools/sim` = a desktop renderer that writes PNGs of the faces. |
| `windows/src/Puck.Core` | Shared contracts: protocol messages, settings, the integration interfaces |
| `windows/src/Puck.App` | The tray app: transports, pairing, volume/media/mouse, flyout, settings, firmware installer |
| `windows/src/Puck.Integrations` | Claude voice chat, Claude Code approvals, DialKit/game bridge, Teams |
| `windows/src/PuckHook` | `puck-hook.exe`, the Claude Code hook command |
| `web/` | Browser firmware installer (ESP Web Tools) |
| `scripts/build.ps1` | Builds and packages everything |

## Hardware notes

The display init, pins and I2C addresses come from Waveshare's own board support package for this
board. Orientation-dependent behaviour is one named constant each, if a board needs it flipped:

- **Turn direction:** `TURN_SIGN` in `firmware/main/config.h` (or *Flip turn direction* in the app's settings).
- **Screen-up axis** (drives lifted/flat and the air mouse): `kScreenUpZ` in `firmware/main/hal/motion.cpp`.
- **Air-mouse directions:** `kPointerXSign` / `kPointerYSign` in `motion.cpp`.
- **Touch orientation:** `TOUCH_MIRROR_X/Y` in `config.h`.
- **Display:** `LCD_COL_OFFSET` (6, as in Waveshare's examples) in `config.h`, and the QSPI clock `kLcdClockHz` (40 MHz, as in Waveshare's BSP) in `hal/display.cpp`.

The UART0 console logs a line for each device that fails to start (touch, IMU, PMU, codec). The puck
keeps running without it; `diag` (below) shows the same over USB.

**Diagnostics:** send `{"t":"diag"}` over the puck's COM port (allowed before pairing). It answers
with one line containing:
- uptime and reset reason
- display on/off and brightness, and how many times the screen has toggled
- which chips answered
- battery
- per-type counts of the touch, turn and button events it has seen
- live motion readings and the learned gyro bias
- current face and pairing state

**Checked on a real 1.75C (Puck-1D4D):**
- Every chip starts.
- Lying flat it reads +0.96 g on z.
- Its gyro sits about 15 °/s off zero on X; the puck learns and removes that within a second of lying still.
- The PMU's fuel gauge reads nonsense, so battery % comes from the voltage.
- Pairing starts, cancels and times out correctly over USB; the app connects over Bluetooth too.
- Touch (trackpad) and turning work by hand; the air mouse's axes were tuned on it.

## Credits

- [Watt, the puck](https://watt.sourabhdaroji.com/) by Sourabh Daroji, for the idea and the interaction design this follows.
- [DialKit](https://www.npmjs.com/package/dialkit) by Josh Puckett, and [`dialkit-watt`](https://github.com/gizmofalcon/dialkit-watt) (MIT).
- Waveshare's board examples, and [ESP-IDF](https://github.com/espressif/esp-idf) (its NimBLE host, and the ES8311 register sequence). [LVGL](https://lvgl.io), [cJSON](https://github.com/DaveGamble/cJSON) (as shipped in ESP-IDF), [NAudio](https://github.com/naudio/NAudio), and [ESP Web Tools](https://esphome.github.io/esp-web-tools/). The touch, IMU and PMU register sequences are ported from [SensorLib and XPowersLib](https://github.com/lewisxhe) (MIT).
- The [Nunito](https://github.com/googlefonts/nunito) typeface (SIL Open Font License).

## License

MIT, see [LICENSE](LICENSE). The Nunito font files keep their own license (`firmware/tools/fonts/OFL.txt`), and the ported driver files credit SensorLib and XPowersLib in their headers.
