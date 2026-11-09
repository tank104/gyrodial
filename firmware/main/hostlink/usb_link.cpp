// USB: the ESP32-S3's own USB-Serial-JTAG port (a COM port on Windows, VID 303A PID 1001), through
// ESP-IDF's usb_serial_jtag driver. The console is on UART0, so nothing else writes to this port.
#include <esp_log.h>
#include <driver/usb_serial_jtag.h>
#include "../config.h"
#include "internal.h"

namespace hostlink {
namespace detail {

namespace {
// Big enough for the app's longest lines (Claude replies, permission asks) and its burst of
// messages right after connecting; the driver drops what doesn't fit.
constexpr uint32_t kRxBytes = 4096;
constexpr uint32_t kTxBytes = 2048;
// The driver takes a write whole or not at all, so offer it small pieces: a nearly full buffer
// still takes some, and a line is never held back because it's longer than the free space.
constexpr size_t kWritePiece = 64;

bool s_ok = false;
char s_line[LINK_MAX_LINE + 1];
size_t s_len = 0;
bool s_overflow = false;  // the current line is too long: skip to its end
}  // namespace

void usbBegin() {
  usb_serial_jtag_driver_config_t cfg = {};
  cfg.rx_buffer_size = kRxBytes;
  cfg.tx_buffer_size = kTxBytes;
  esp_err_t err = usb_serial_jtag_driver_install(&cfg);
  s_ok = err == ESP_OK || err == ESP_ERR_INVALID_STATE;  // already installed is fine
  if (!s_ok) ESP_LOGE("link", "USB serial driver failed: %s", esp_err_to_name(err));
}

void usbPoll() {
  if (!s_ok) return;
  uint8_t chunk[128];
  // Bounded per call so a flood can't starve the faces.
  for (int budget = 4096; budget > 0;) {
    const int got = usb_serial_jtag_read_bytes(chunk, sizeof(chunk), 0);
    if (got <= 0) break;
    budget -= got;
    for (int i = 0; i < got; i++) {
      char c = (char)chunk[i];
      if (c == '\n') {
        if (!s_overflow && s_len) {
          s_line[s_len] = 0;
          onLine(TP_USB, s_line, s_len);
        }
        s_len = 0;
        s_overflow = false;
      } else if (c == '\r') {
        // CRLF from a terminal: ignore
      } else if (s_len < LINK_MAX_LINE) {
        s_line[s_len++] = c;
      } else {
        s_overflow = true;
      }
    }
  }
}

// Never blocks. While nobody reads the port the buffer fills and this returns 0; the TX task keeps
// whatever didn't fit and tries again on its next round.
size_t usbWrite(const char* data, size_t len) {
  if (!s_ok) return 0;
  size_t done = 0;
  while (done < len) {
    const size_t n = len - done < kWritePiece ? len - done : kWritePiece;
    if (usb_serial_jtag_write_bytes(data + done, n, 0) != (int)n) break;
    done += n;
  }
  return done;
}

}  // namespace detail
}  // namespace hostlink
