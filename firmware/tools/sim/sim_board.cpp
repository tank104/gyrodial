// A pretend board and link for the simulator: the display is a framebuffer, the link prints what
// the faces would send to the PC.
#include <lvgl.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include "config.h"
#include "hal.h"
#include "hostlink.h"
#include "sim.h"

uint32_t g_now = 1000;
uint8_t g_fb[LCD_W * LCD_H * 3];
bool g_lifted = false;
bool g_usb = false;
bool g_quiet = false;
static bool s_displayOn = true;
static int s_bright = 80;

extern "C" uint32_t nowMs(void) { return g_now; }  // gyrodial_clock.h, as the board would

static void flush(lv_disp_drv_t* d, const lv_area_t* a, lv_color_t* px) {
  for (int y = a->y1; y <= a->y2; y++)
    for (int x = a->x1; x <= a->x2; x++) {
      lv_color32_t c;
      c.full = lv_color_to32(*px++);
      uint8_t* o = &g_fb[(y * LCD_W + x) * 3];
      o[0] = c.ch.red;
      o[1] = c.ch.green;
      o[2] = c.ch.blue;
    }
  lv_disp_flush_ready(d);
}

static void log(const char* fmt, ...) {
  if (g_quiet) return;
  va_list ap;
  va_start(ap, fmt);
  printf("  [%6u] ", (unsigned)g_now);
  vprintf(fmt, ap);
  printf("\n");
  va_end(ap);
}

namespace hal {
static lv_disp_draw_buf_t s_buf;
static lv_disp_drv_t s_drv;
static InputMode s_mode = InputMode::Ui;

void begin() {
  lv_init();
  static lv_color_t px[LCD_W * LCD_H];
  lv_disp_draw_buf_init(&s_buf, px, nullptr, LCD_W * LCD_H);
  lv_disp_drv_init(&s_drv);
  s_drv.hor_res = LCD_W;
  s_drv.ver_res = LCD_H;
  s_drv.flush_cb = flush;
  s_drv.draw_buf = &s_buf;
  lv_disp_drv_register(&s_drv);
}
void setBrightness(uint8_t p) {
  if (p != s_bright) log("hal brightness %d", p);
  s_bright = p;
}
void setDisplayOn(bool on) {
  if (on != s_displayOn) log("hal display %s", on ? "on" : "off");
  s_displayOn = on;
}
bool displayOn() { return s_displayOn; }
bool pollEvent(InputEvent&) { return false; }
void setInputMode(InputMode m) {
  if (m != s_mode) log("hal input mode %s", m == InputMode::Trackpad ? "trackpad" : "ui");
  s_mode = m;
}
InputMode inputMode() { return s_mode; }
float heading() { return 0; }
bool lifted() { return g_lifted; }
uint32_t lastActivityMs() { return g_now; }
void setTurnSensitivity(float) {}
void setTurnFlip(bool) {}
int batteryPercent() { return 72; }
int batteryMv() { return 3900; }
bool charging() { return false; }
bool usbPowered() { return g_usb; }
void powerOff() { log("hal POWER OFF"); }
void beep(Tone t) {
  static const char* names[] = {"tick", "tap", "select", "success", "error", "chime"};
  if (t != Tone::Tick) log("beep %s", names[(int)t]);
}
void setSoundEnabled(bool) {}
uint64_t chipId() { return 0xC0FFEE; }
}  // namespace hal

namespace hostlink {
void begin() {}
void poll() {}
bool linked() { return g_model.linked; }
void sendVolume(int v) { log("-> vol %d", v); }
void sendMute(bool m) { log("-> mute %d", m); }
void sendMedia(const char* a) { log("-> media %s", a); }
void sendFace(const char* f) { log("-> face %s", f); }
void sendMove(char s, float x, float y) { log("-> mv %c %.1f %.1f", s, x, y); }
void sendScroll(float y, float) { log("-> scroll %.1f", y); }
void sendClick(char b, int n) { log("-> click %c %d", b, n); }
void sendButton(char b, bool d) { log("-> btn %c %d", b, d); }
void sendClaude(const char* a) { log("-> claude %s", a); }
void sendAnswer(const char* id, bool ok) { log("-> ans %s %s", id, ok ? "allow" : "deny"); }
void sendDial(const char* a, int d) { log("-> dial %s %d", a, d); }
void sendGameTurn(float deg) { log("-> game turn %.1f", deg); }
void sendGame(const char* in, const char* v) { log("-> game %s %s", in, v ? v : ""); }
void sendCall(const char* a) { log("-> call %s", a); }
void sendFocus(const char* s, int m) { log("-> focus %s %d", s, m); }
void sendLog(const char*, ...) {}
void pairProgress(int n) { log("-> pair progress %d", n); }
void pairResult(bool ok) {
  log("-> pair %s", ok ? "ok" : "fail");
  g_model.pairing = false;
  if (ok) g_model.linked = true;
  model_mark(D_PAIR | D_LINK);
}
}  // namespace hostlink
