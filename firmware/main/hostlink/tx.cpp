// The outgoing side: one queue of lines from every task, drained by a TX task to USB and BLE.
//
// Mouse traffic doesn't go through the queue line by line: moves and scrolls are summed into
// accumulators and the TX task turns them into at most one "mv" per source and one "scroll" every
// 10 ms. That keeps BLE from drowning in tiny packets while the input task samples at 200+ Hz.
// A click or button flushes the accumulators first, so it lands after the moves that preceded it.
#include <freertos/FreeRTOS.h>
#include <freertos/ringbuf.h>
#include <freertos/semphr.h>
#include <string.h>
#include <atomic>
#include <initializer_list>
#include <math.h>
#include "puck_clock.h"
#include "../config.h"
#include "internal.h"

namespace hostlink {
namespace detail {

namespace {

constexpr size_t kQueueBytes = 8 * 1024;
constexpr size_t kItemMax = LINK_MAX_LINE + 2;  // header + the longest line (a diag reply)
constexpr uint32_t kMouseEveryMs = 10;
constexpr uint32_t kVolumeEveryMs = 33;  // 30 Hz

// A transport's pending output: whole lines go in, the pumps take bytes out from the front.
struct Fifo {
  char buf[4096];
  size_t len = 0;

  bool appendLine(const char* line, size_t n) {
    if (len + n + 1 > sizeof(buf)) return false;  // never half a line: drop the whole thing
    memcpy(buf + len, line, n);
    buf[len + n] = '\n';
    len += n + 1;
    return true;
  }
  void consume(size_t n) {
    if (n >= len) {
      len = 0;
      return;
    }
    memmove(buf, buf + n, len - n);
    len -= n;
  }
};

RingbufHandle_t s_queue = nullptr;
TaskHandle_t s_task = nullptr;
SemaphoreHandle_t s_mouseLock = nullptr;
portMUX_TYPE s_accMux = portMUX_INITIALIZER_UNLOCKED;

std::atomic<uint8_t> s_alive{0};
std::atomic<uint8_t> s_trusted{0};
std::atomic<int> s_volPending{-1};

// [0] trackpad, [1] pointer; x, y
float s_move[2][2] = {};
float s_scroll[2] = {};  // y, x

Fifo s_usbOut;
Fifo s_bleOut;
uint32_t s_dropped = 0;

// Rounds to 2 decimals and leaves the remainder in the accumulator, so slow motion isn't lost.
float takeRounded(float& acc) {
  float r = roundf(acc * 100.0f) / 100.0f;
  acc -= r;
  return r;
}

void route(const uint8_t* item, size_t size) {
  if (size < 2) return;
  uint8_t flags = item[0];
  uint8_t targets = item[1] ? item[1] : (uint8_t)TP_ALL;
  const char* line = (const char*)item + 2;
  size_t len = size - 2;
  uint8_t alive = s_alive.load();
  uint8_t trusted = s_trusted.load();

  for (uint8_t tp : {TP_USB, TP_BLE}) {
    if (!(targets & tp)) continue;
    if (!(flags & F_FORCE) && !(alive & tp)) continue;
    if ((flags & F_TRUSTED) && !(trusted & tp)) continue;
    Fifo& out = tp == TP_USB ? s_usbOut : s_bleOut;
    if (!out.appendLine(line, len)) s_dropped++;
  }
}

void pumpUsb() {
  while (s_usbOut.len) {
    size_t n = usbWrite(s_usbOut.buf, s_usbOut.len);
    if (!n) break;
    s_usbOut.consume(n);
  }
}

void pumpBle() {
  if (!bleConnected()) {
    s_bleOut.len = 0;
    return;
  }
  if (!bleSubscribed()) return;  // hold it until the app subscribes (the hello reply may be waiting)
  while (s_bleOut.len) {
    size_t chunk = bleMaxChunk();
    if (chunk > s_bleOut.len) chunk = s_bleOut.len;
    int rc = bleNotify((const uint8_t*)s_bleOut.buf, chunk);
    if (rc == 0) {
      s_bleOut.consume(chunk);
    } else {
      if (rc < 0) s_bleOut.len = 0;
      break;  // out of buffers: the same bytes go next round
    }
  }
}

void sendVolumeIfDue(uint32_t now) {
  static uint32_t last = 0;
  if (s_volPending.load() < 0 || now - last < kVolumeEveryMs) return;
  int v = s_volPending.exchange(-1);
  if (v < 0) return;
  last = now;
  char line[32];
  int n = snprintf(line, sizeof(line), "{\"t\":\"vol\",\"v\":%d}", v);
  txLine(line, (size_t)n, 0, F_TRUSTED);
}

void txTask(void*) {
  uint32_t lastMouse = 0;
  for (;;) {
    ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(5));
    uint32_t now = nowMs();
    if (now - lastMouse >= kMouseEveryMs) {
      txFlushMouse();
      lastMouse = now;
    }
    sendVolumeIfDue(now);

    size_t size = 0;
    while (void* item = xRingbufferReceive(s_queue, &size, 0)) {
      route((const uint8_t*)item, size);
      vRingbufferReturnItem(s_queue, item);
    }
    pumpUsb();
    pumpBle();
  }
}

}  // namespace

void txBegin() {
  if (s_queue) return;
  s_queue = xRingbufferCreate(kQueueBytes, RINGBUF_TYPE_NOSPLIT);
  s_mouseLock = xSemaphoreCreateMutex();
  // Core 0, next to the input task and the NimBLE host; the main loop (LVGL) has core 1.
  xTaskCreatePinnedToCore(txTask, "link_tx", 6144, nullptr, 3, &s_task, 0);
}

bool txLine(const char* line, size_t len, uint8_t targets, uint8_t flags) {
  if (!s_queue || !line || !len || len + 2 > kItemMax) return false;
  uint8_t item[kItemMax];
  item[0] = flags;
  item[1] = targets;
  memcpy(item + 2, line, len);
  if (xRingbufferSend(s_queue, item, len + 2, 0) != pdTRUE) {
    s_dropped++;
    return false;
  }
  if (s_task) xTaskNotifyGive(s_task);
  return true;
}

void txSetAlive(uint8_t tpMask) { s_alive.store(tpMask); }
void txSetTrusted(uint8_t tpMask) { s_trusted.store(tpMask); }
uint8_t txTrusted() { return s_trusted.load(); }

void txAddMove(char src, float x, float y) {
  int i = src == 'p' ? 1 : 0;
  portENTER_CRITICAL(&s_accMux);
  s_move[i][0] += x;
  s_move[i][1] += y;
  portEXIT_CRITICAL(&s_accMux);
}

void txAddScroll(float y, float x) {
  portENTER_CRITICAL(&s_accMux);
  s_scroll[0] += y;
  s_scroll[1] += x;
  portEXIT_CRITICAL(&s_accMux);
}

void txSetVolume(int v) {
  s_volPending.store(v);
  if (s_task) xTaskNotifyGive(s_task);
}

void txFlushMouse() {
  if (!s_mouseLock) return;
  // The lock keeps "take the sums, queue them" atomic between the TX task and a click from the
  // input task, so a click can't overtake the moves that came before it.
  xSemaphoreTake(s_mouseLock, portMAX_DELAY);
  float mv[2][2], sc[2];
  bool trusted = s_trusted.load() != 0;
  portENTER_CRITICAL(&s_accMux);
  for (int i = 0; i < 2; i++) {
    mv[i][0] = trusted ? takeRounded(s_move[i][0]) : 0;
    mv[i][1] = trusted ? takeRounded(s_move[i][1]) : 0;
  }
  sc[0] = trusted ? takeRounded(s_scroll[0]) : 0;
  sc[1] = trusted ? takeRounded(s_scroll[1]) : 0;
  if (!trusted) {  // nobody to send it to: forget it rather than replay it later
    memset(s_move, 0, sizeof(s_move));
    memset(s_scroll, 0, sizeof(s_scroll));
  }
  portEXIT_CRITICAL(&s_accMux);

  char line[96];
  for (int i = 0; i < 2; i++) {
    if (mv[i][0] == 0 && mv[i][1] == 0) continue;
    int n = snprintf(line, sizeof(line), "{\"t\":\"mv\",\"s\":\"%c\",\"x\":%.2f,\"y\":%.2f}", i ? 'p' : 't',
                     (double)mv[i][0], (double)mv[i][1]);
    txLine(line, (size_t)n, 0, F_TRUSTED);
  }
  if (sc[0] != 0 || sc[1] != 0) {
    int n = sc[1] != 0
                ? snprintf(line, sizeof(line), "{\"t\":\"scroll\",\"y\":%.2f,\"x\":%.2f}", (double)sc[0], (double)sc[1])
                : snprintf(line, sizeof(line), "{\"t\":\"scroll\",\"y\":%.2f}", (double)sc[0]);
    txLine(line, (size_t)n, 0, F_TRUSTED);
  }
  xSemaphoreGive(s_mouseLock);
}

}  // namespace detail
}  // namespace hostlink
