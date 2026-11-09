# Puck link protocol (v1)

The puck (ESP32-S3 firmware) and the Windows app talk over one of two transports carrying the
**same byte stream**: newline-delimited JSON (NDJSON), UTF-8, one message per line, `\n` terminated.

Every message is a JSON object with a short type key `"t"`. Unknown types and unknown fields
**must be ignored** by both sides (forward compatibility). Lines that don't start with `{` are
logs/noise and are ignored (the firmware may print boot logs on USB).

Max line length: 1024 bytes. Senders must truncate long text fields to stay under it, counting
UTF-8 bytes after JSON escaping (the app uses relaxed escaping and `PuckMessage.FitUtf8`).

## Transports

### USB (preferred when plugged in)
- ESP32-S3 native USB (the USB-Serial-JTAG controller), shows up as a COM port, VID `0x303A`, PID `0x1001`.
- Baud is irrelevant (native USB), the app opens it at 115200, DTR **off**, RTS **off**
  (toggling DTR/RTS on the ESP32-S3 USB-JTAG port can reset the chip).
- The app sends `hello` right after opening, and every 2 s until it gets a `hello` back.

### Bluetooth LE
- Advertised name: `Puck-XXXX` (last 2 bytes of the BT MAC, hex upper case).
- Advertises the Puck service UUID (128-bit) so the app can filter scans.

| What | UUID | Properties |
|---|---|---|
| Puck service | `60bd0001-b6ce-41b2-9911-a4a7e64e01d9` | |
| RX (app → puck) | `60bd0002-b6ce-41b2-9911-a4a7e64e01d9` | Write, Write Without Response |
| TX (puck → app) | `60bd0003-b6ce-41b2-9911-a4a7e64e01d9` | Notify |

- The byte stream is cut into chunks of at most `min(MTU - 3, 512)` bytes (MTU is negotiated; the
  puck requests 515, because NimBLE rejects attribute writes longer than 512 bytes). A chunk may
  contain part of a line or several lines; the receiver buffers bytes and splits on `\n`.
- The puck asks for a short connection interval (7.5–15 ms) after connecting so pointer motion is
  smooth. The app also requests `ThroughputOptimized` connection parameters on Windows 11.
- No BLE-level bonding is required. Trust is established by the app-level pairing below.

When both transports are up, the app uses USB and ignores BLE traffic (the puck sends to every
connected transport; the app must de-duplicate by only listening to the active one).

## Handshake and pairing

1. App → puck: `{"t":"hello","v":1,"app":"0.1.0","host":"OFFICE-PC","id":"9f3c0a7d5b1e2c44"}`
   - `id` is a random 16-hex-char host id, generated once and stored by the app.
2. Puck → app: `{"t":"hello","v":1,"fw":"0.1.0","id":"a1b2c3d4e5f60708","name":"Puck-1A2B","paired":true,"bat":87,"chg":false,"usb":true,"face":"home"}`
   - `id` is the puck's id (derived from its MAC). `paired` tells whether this host id is known.
3. If `paired` is false, the puck starts pairing on its own and sends `{"t":"pair","s":"start"}`.
   The app shows "Turn and tap on the puck to pair" **only once that `pair start` arrives**, and sends
   nothing but `hello`/`ping`/`pair` until pairing finishes. The puck shows the pairing face: three
   numbers to dial in, one after the other (turn to the number, tap). While pairing:
   - Puck → app: `{"t":"pair","s":"progress","n":1}` after each correct number (n = 0..3; 0 after
     a wrong one), so the app can mirror the progress.
   - Puck → app: `{"t":"pair","s":"ok"}` — the puck stored the host id (up to 4 hosts, oldest
     evicted). The app stores the puck id (and BLE address) as "my puck".
   - Puck → app: `{"t":"pair","s":"fail"}` — cancelled with the side button. The puck then refuses
     that host for 60 s, except that an explicit `pair a:start` more than 5 s after the cancel
     starts over (a person clicked "Pair" in the app).
   - Puck → app: `{"t":"pair","s":"refused","retry":42}` — answer to a hello or `pair a:start`
     while refused: seconds until it will pair again.
   - Puck → app: `{"t":"pair","s":"timeout"}` — nobody finished within 3 minutes. The puck leaves
     the pairing face (it doesn't refuse the host); `pair a:start` starts over.
   - `{"t":"pair","a":"start"}` from the app is idempotent: while pairing it just re-sends
     `pair start`.
   - App → puck: `{"t":"pair","a":"cancel"}` — the user cancelled on the PC. The puck leaves the
     pairing face (showing "Pairing stopped") and does not refuse that host.
4. App → puck: `{"t":"unpair"}` removes this host from the puck. The puck replies
   `{"t":"pair","s":"none"}`.
5. **Unprompted hellos.** The puck also sends a hello nobody asked for: on USB about 0.6 s after
   boot, and on BLE when the app subscribes to TX. These carry `"new":true`, and their `paired` is
   meaningless (that transport's session doesn't know the host yet). The app must answer one with
   its own `hello` (at most once every 2 s per link), and must also re-send `hello` before any
   `pair a:start` it sends, in case the puck restarted while the port stayed open.

Until `paired` is true the puck ignores every other message type, and sends only `hello`, `pair`,
`bat`, `pong`, `log`.

## Keepalive
- App → puck `{"t":"ping"}` every 3 s; puck → app `{"t":"pong"}`.
- Puck considers the host gone after 10 s without any message (USB) or on BLE disconnect.
- App considers the transport dead after 10 s without any message.

## App → puck

| t | Fields | Meaning |
|---|---|---|
| `hello` | `v`, `app`, `host`, `id` | see handshake |
| `ping` | | keepalive |
| `diag` | | ask for a `diag` line (debugging; allowed before pairing) |
| `pair` | `a:"start"` / `a:"cancel"` | (re)start pairing on the puck / stop it from the PC |
| `unpair` | | forget this host |
| `state` | any of: `vol` 0–100, `mute` bool, `play` bool, `title` str, `artist` str, `src` str (player app name) | system audio + now playing. Partial updates allowed: only the fields present change. |
| `time` | `epoch` (unix s, UTC), `tz` (minutes east of UTC) | sent after hello and every 10 min |
| `cfg` | any of: `bright` 10–100, `sound` bool, `dim` s, `off` s, `sens` 0.25–4.0, `flip` bool | device settings. `dim`/`off` 0 = never. `flip` swaps turn direction. |
| `face` | `f` face id | ask the puck to show a face (e.g. `"claude"` when the app's hotkey is pressed) |
| `claude` | `s` one of `idle`/`listening`/`thinking`/`reply`/`error`; optional `text` (≤ 600 chars), `lvl` 0..1 mic level | Claude face state. `listening` + `text` = live transcript. `reply` + `text` = the answer. |
| `ask` | `id` str, `tool` str, `text` str (≤ 240 chars), `proj` str | Claude Code permission request. The puck switches to the `allow` face. |
| `ask_end` | `id` | request answered elsewhere / timed out: leave the allow face |
| `notify` | `text` (≤ 120), `kind` (`claude`/`info`) | short banner on the puck for ~4 s (e.g. "Claude needs your input") |
| `dial` | `on` bool; when on: `panel`, `name`, `kind` (`num`/`bool`/`action`/`select`/`color`/`text`), `val` number (num), `on2` bool (bool kind, value), `txt` display string, `min`, `max`, `step`, `idx`, `cnt` | DialKit control currently selected. `on:false` = no DialKit page in focus. |
| `game` | `on` bool; `label` str; `angle` deg (clockwise from 12) | a dialkit-watt game page wants the puck as a paddle |
| `call` | `on` bool; `muted` bool; `video` bool; `app` str | in a Teams meeting |

## Puck → app

| t | Fields | Meaning |
|---|---|---|
| `hello` | see handshake | also sent unprompted after boot on every transport |
| `pong` | | |
| `pair` | `s:"start"/"progress"/"ok"/"fail"/"refused"/"timeout"/"none"`; `n` (progress), `retry` (refused) | pairing (see the handshake) |
| `diag` | `m` str | answer to app → puck `{"t":"diag"}` (allowed before pairing): one line of board state for debugging |
| `bat` | `p` 0–100, `chg` bool, `usb` bool, `mv` battery mV | on change of ≥1 % / charge state, and every 60 s |
| `face` | `f` | the face now showing |
| `vol` | `v` 0–100 | set the system volume (absolute; the puck owns the number while turning) |
| `mute` | `m` bool | set mute |
| `media` | `a`: `toggle`/`next`/`prev` | media keys (tap on home = toggle) |
| `mv` | `s`: `t` (trackpad) or `p` (pointer); `x`, `y` floats | move the mouse. `t`: touch pixels (466 across the glass), already rotated so up on the puck is up on the screen. `p`: degrees of hand rotation (yaw → x, pitch → y). The app applies sensitivity + acceleration. Sent at up to 100 Hz, coalesced. |
| `scroll` | `y` float, optional `x` float | scroll, in notches (1.0 = one wheel notch, positive = up / right) |
| `click` | `b`: `l`/`r`/`m`; `n` 1 or 2 | click (n=2: double click) |
| `btn` | `b`: `l`; `s`: `down`/`up` | press / release (drag on the trackpad) |
| `claude` | `a`: `listen`/`send`/`cancel` | tap on the Claude face starts listening; tap again sends; hold cancels |
| `ans` | `id`, `ok` bool | answer to an `ask` |
| `dial` | `a`: `turn` (`d` signed detents) / `next` / `prev` / `tap` | DialKit input |
| `game` | `in`: `turn` (`v` degrees, clockwise +) / `tap` / `hold` / `swipe` (`v` `l`/`r`/`u`/`d`) / `press` | game input |
| `call` | `a`: `mute`/`video`/`leave` | Teams control |
| `focus` | `s`: `start`/`pause`/`done`/`reset`; `min` | focus timer events (the app shows a toast on `done`) |
| `log` | `m` str | debug text, shown in the app's log |

## Face ids

`home`, `trackpad`, `pointer` (trackpad face while lifted), `claude`, `allow`, `dialkit`, `game`,
`call`, `focus`, `spin`, `dice`, `pong`, `pair`, `picker`, `offline`.

## Example session

```
app  → {"t":"hello","v":1,"app":"0.1.0","host":"DESK","id":"9f3c0a7d5b1e2c44"}
puck → {"t":"hello","v":1,"fw":"0.1.0","id":"a1b2c3d4e5f60708","name":"Puck-1A2B","paired":true,"bat":87,"chg":false,"usb":true,"face":"home"}
app  → {"t":"time","epoch":1760700000,"tz":-420}
app  → {"t":"state","vol":42,"mute":false,"play":true,"title":"Teardrop","artist":"Massive Attack","src":"Spotify"}
puck → {"t":"vol","v":45}
puck → {"t":"media","a":"toggle"}
app  → {"t":"state","play":false}
app  → {"t":"ask","id":"r7","tool":"Bash","text":"npm run build","proj":"gyrodial"}
puck → {"t":"face","f":"allow"}
puck → {"t":"ans","id":"r7","ok":true}
```
