# Contributing to gyrodial

Thanks for helping. Bug reports, results from other boards, fixes, new faces and new integrations
are all welcome.

## Before you start

- **Bugs:** open an issue with the bug template. If the puck is involved, include the line it sends
  back for `{"t":"diag"}` ([how](docs/HARDWARE.md#diagnostics)).
- **Anything bigger than a fix** (a new face, an integration, a protocol change): open an issue
  first, so we can agree on the shape before you spend time on it.
- **Security problems:** please don't open a public issue. See [SECURITY.md](SECURITY.md).

## Setting up

| For | You need |
|---|---|
| Firmware | [ESP-IDF v5.5.3](https://docs.espressif.com/projects/esp-idf/en/v5.5.3/esp32s3/get-started/windows-setup.html) (CI uses exactly this version) |
| Windows app | The [.NET 10 SDK](https://dotnet.microsoft.com/download), on Windows 10 2004 or later |
| Face simulator | gcc from [MSYS2](https://www.msys2.org/) (`pacman -S mingw-w64-ucrt-x86_64-gcc`) |

You don't need a board to work on the app, the integrations or the faces.

## Building and testing

Everything at once (firmware, then the app with that firmware bundled in):

```powershell
./scripts/build.ps1                  # → dist/Puck/ and dist/Puck-win-x64.zip
./scripts/build.ps1 -SkipFirmware    # app only, reusing the last firmware build
```

`build.ps1` finds ESP-IDF in `-IdfPath`, `$env:IDF_PATH` or `~/esp/esp-idf`. It copies the firmware
images into the app (`windows/src/Puck.App/firmware`) and the web installer (`web/firmware`), along
with an ESP Web Tools `manifest.json`.

Or piece by piece:

```powershell
cd firmware; idf.py build                    # firmware, in an ESP-IDF shell
idf.py -p COM5 flash                         # flash it (use your board's port)
dotnet build windows/Puck.sln                # app, integrations, puck-hook
dotnet test windows/Puck.sln                 # integration tests
windows/src/Puck.App/bin/Debug/net10.0-windows10.0.22621.0/Puck.exe --self-test   # link logic against a fake puck
```

**The face simulator** compiles the real face code (`firmware/main/ui`) and LVGL against a pretend
board, drives it through every state, and writes a PNG of each face to `firmware/tools/sim/out/`:

```sh
cd firmware && idf.py reconfigure            # once, so the component manager downloads LVGL
cd tools/sim && ./build.sh && ./build/sim.exe
```

If you change how a face looks, regenerate `docs/faces.png` (copy `out/sheet.png` over it) and put
before/after images in your pull request.

**Firmware logs** go to UART0, not USB. See [docs/HARDWARE.md](docs/HARDWARE.md#logs-and-crashes).

## Repository layout

| Path | What |
|---|---|
| `VERSION` | The version of both the firmware and the app |
| `docs/PROTOCOL.md` | The wire protocol between the puck and the app (NDJSON over USB serial or BLE) |
| `docs/HARDWARE.md` | The board, orientation constants, diagnostics, logs, backups |
| `firmware/` | The ESP-IDF project (`sdkconfig.defaults`, `partitions.csv`, LVGL from the component registry, cJSON built into IDF) |
| `firmware/main/hal` | The board: CO5300 QSPI display and LVGL port, CST9217 touch, QMI8658 motion, AXP2101 power, ES8311 sound, on IDF's own drivers |
| `firmware/main/hostlink` | The USB and BLE (NimBLE) links, pairing, and the protocol |
| `firmware/main/ui` | The faces |
| `firmware/tools/sim` | The desktop face simulator |
| `windows/src/Puck.Core` | Shared contracts: protocol messages, settings, the integration interfaces |
| `windows/src/Puck.App` | The tray app: transports, pairing, volume/media/mouse, flyout, settings, firmware installer |
| `windows/src/Puck.Integrations` | Claude voice chat, Claude Code approvals, the DialKit/game bridge, Teams |
| `windows/src/PuckHook` | `puck-hook.exe`, the Claude Code hook command |
| `web/` | The browser firmware installer (ESP Web Tools), published to GitHub Pages |
| `scripts/build.ps1` | Builds and packages everything |

## Guidelines

- **The firmware is plain ESP-IDF:** no Arduino core and no PlatformIO. Use IDF's drivers and
  components.
- **The protocol has two sides.** If you change a message, update the firmware, the app and
  `docs/PROTOCOL.md` in the same pull request. The puck must ignore what it doesn't understand,
  because anything in Bluetooth range can send it a line.
- **Match the code around you.** Keep changes focused: one topic per pull request.
- **Add or update tests** in `windows/tests` for app and integration logic.
- **Keep personal data out:** no API keys, tokens, flash backups, real PC names, MAC addresses or
  paths from your machine, in code, tests or screenshots.

## Pull requests

1. Fork the repository and make a branch.
2. Make sure `dotnet test windows/Puck.sln` passes and the firmware builds.
3. Open a pull request and fill in the template. Say how you tested it, and on what hardware if any.

Every change to `main` goes through a pull request, the maintainer's own included: nobody pushes to
`main` directly. On every pull request, CI:

- builds the firmware with ESP-IDF
- builds the face simulator from the same UI code and renders every face (download the `faces`
  image from the run to see them)
- builds the app and runs the unit tests
- runs `Puck.exe --self-test` (the connection logic against a fake puck)

A pull request can be merged only when all of that passes and the branch is up to date with `main`
(GitHub's *Update branch* button does that). For contributors from outside the project, a
maintainer approves the CI run first. The maintainer reviews every pull request and merges it,
squashed into one commit.

By contributing, you agree that your contribution is licensed under the project's
[MIT license](LICENSE).

## Releasing (maintainers)

1. Set the new version in `VERSION` (for example `0.3.0`) in a pull request, and merge it.
2. Tag the merged commit on `main` and push the tag:
   ```powershell
   git switch main; git pull
   git tag v0.3.0
   git push origin v0.3.0
   ```
3. The **release** workflow checks that the tag matches `VERSION`, builds the firmware and the app,
   and creates a **draft** release with:
   - `Puck-<version>-win-x64.zip`: the app, with this firmware bundled
   - `puck-firmware-<version>.zip`: the flash images and the ESP Web Tools manifest
   - `puck-firmware-<version>-merged.bin`: one image to write at `0x0` with any flashing tool
   - `puck-<version>.elf`: for decoding crash dumps
   - `SHA256SUMS.txt`
4. Check the draft, edit the notes, and click **Publish**. Publishing a release (not a pre-release)
   also updates the web installer on GitHub Pages to that firmware.

To try the release build without releasing anything, run the **release** workflow by hand from the
Actions tab: it builds everything and attaches the files to the run, but doesn't create a release.
