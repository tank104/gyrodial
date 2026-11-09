// The face simulator: drives the real faces through a script of PC states and dial gestures, and
// writes what the glass shows to tools/sim/out/*.png (plus out/sheet.png with all of them).
#include <lvgl.h>
#include <stdio.h>
#include <string.h>
#include <string>
#include <vector>
#include "events.h"
#include "hal.h"
#include "model.h"
#include "sim.h"
#include "ui.h"

namespace ui {
int pairTargetForTest(int i);  // face_pair.cpp
}

#define STB_IMAGE_WRITE_IMPLEMENTATION
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wmissing-field-initializers"
#include "stb_image_write.h"
#pragma GCC diagnostic pop

namespace {

constexpr int CX = LCD_W / 2, CY = LCD_H / 2;
constexpr int THUMB = LCD_W / 2;
struct Shot {
  std::string name;
  std::vector<uint8_t> px;  // THUMB x THUMB RGB
};
std::vector<Shot> shots;

void run(uint32_t ms) {
  uint32_t end = g_now + ms;
  while (g_now < end) {
    g_now += 5;
    uint32_t d = model_take_dirty();
    if (d) ui::update(d);
    ui::tick(g_now);
    lv_timer_handler();
  }
}

void ev(Ev t, float v = 0, int x = CX, int y = CY, char dir = 0) {
  InputEvent e{t, v, (int16_t)x, (int16_t)y, dir, g_now};
  ui::handleInput(e);
  run(20);
}

void turn(float deg, int steps = 12) {
  for (int i = 0; i < steps; i++) ev(Ev::Turn, deg / steps);
}

void mark(uint32_t d) {
  model_mark(d);
  run(30);
}

void set(char* dst, size_t n, const char* s) {
  strncpy(dst, s, n - 1);
  dst[n - 1] = 0;
}
#define SET(field, s) set(field, sizeof field, s)

void request(const char* face) {
  SET(g_model.faceRequest, face);
  mark(D_FACE_REQ);
  run(400);
}

void snap(const char* name) {
  lv_refr_now(nullptr);
  static uint8_t out[LCD_W * LCD_H * 3];
  for (int y = 0; y < LCD_H; y++)
    for (int x = 0; x < LCD_W; x++) {
      int dx = x - CX, dy = y - CY;
      const uint8_t* s = &g_fb[(y * LCD_W + x) * 3];
      uint8_t* o = &out[(y * LCD_W + x) * 3];
      bool inside = dx * dx + dy * dy <= (CX - 0) * (CX - 0);
      // outside the glass: the aluminium case
      o[0] = inside ? s[0] : 38;
      o[1] = inside ? s[1] : 38;
      o[2] = inside ? s[2] : 41;
    }
  std::string path = std::string("out/") + name + ".png";
  stbi_write_png(path.c_str(), LCD_W, LCD_H, 3, out, LCD_W * 3);
  Shot sh{name, std::vector<uint8_t>(THUMB * THUMB * 3)};
  for (int y = 0; y < THUMB; y++)
    for (int x = 0; x < THUMB; x++)
      for (int c = 0; c < 3; c++) {
        int sum = out[((2 * y) * LCD_W + 2 * x) * 3 + c] + out[((2 * y) * LCD_W + 2 * x + 1) * 3 + c] +
                  out[((2 * y + 1) * LCD_W + 2 * x) * 3 + c] + out[((2 * y + 1) * LCD_W + 2 * x + 1) * 3 + c];
        sh.px[(y * THUMB + x) * 3 + c] = (uint8_t)(sum / 4);
      }
  shots.push_back(std::move(sh));
  printf("snap %s  (face: %s)\n", name, ui::currentFace());
}

void sheet() {
  const int cols = 6, pad = 8;
  int rows = ((int)shots.size() + cols - 1) / cols;
  int W = cols * (THUMB + pad) + pad, H = rows * (THUMB + pad) + pad;
  std::vector<uint8_t> img(W * H * 3, 20);
  for (size_t i = 0; i < shots.size(); i++) {
    int ox = pad + (int)(i % cols) * (THUMB + pad), oy = pad + (int)(i / cols) * (THUMB + pad);
    for (int y = 0; y < THUMB; y++) memcpy(&img[((oy + y) * W + ox) * 3], &shots[i].px[y * THUMB * 3], THUMB * 3);
  }
  stbi_write_png("out/sheet.png", W, H, 3, img.data(), W * 3);
}

}  // namespace


namespace {

// Dial the pairing numbers in, as a person would: turn to each, tap
int s_dialValue = 0;
void dialTo(int target) {
  int steps = ((target - s_dialValue) % 40 + 40) % 40;
  if (steps) turn(steps * 9.0f + 0.4f, steps < 12 ? 12 : steps);
  s_dialValue = target;
}

}  // namespace

int main(int argc, char** argv) {
  g_quiet = argc > 1 && strcmp(argv[1], "-q") == 0;
  hal::begin();
  ui::begin();
  run(1500);
  snap("01_waiting_pong");
  g_usb = true;
  mark(D_BAT);
  run(300);
  snap("01b_waiting_usb");
  g_usb = false;
  mark(D_BAT);

  // ---- a new PC wants to pair, before anything is linked
  g_model.pairing = true;
  SET(g_model.pairHost, "OFFICE-PC");
  mark(D_PAIR);
  run(600);
  snap("02_pair");
  s_dialValue = 0;
  dialTo(ui::pairTargetForTest(0));
  run(100);
  snap("03_pair_turned");
  ev(Ev::Tap);
  run(300);
  snap("04_pair_one_done");
  dialTo((ui::pairTargetForTest(1) + 3) % 40);  // the wrong number
  ev(Ev::Tap);
  run(300);
  snap("05_pair_wrong");
  run(60000);
  snap("06_pair_countdown");
  for (int i = 0; i < 3; i++) {
    dialTo(ui::pairTargetForTest(i));
    ev(Ev::Tap);
    run(200);
  }
  run(300);
  snap("07_pair_paired");
  run(2500);

  // ---- the PC shows up
  g_model.linked = true;
  g_model.transport = Transport::Ble;
  SET(g_model.host, "OFFICE-PC");
  g_model.timeValid = true;
  g_model.epochAtSync = 1760704260;  // 12:31 UTC
  g_model.msAtSync = g_now;
  g_model.tzMin = -420;
  g_model.vol = 42;
  g_model.playing = true;
  SET(g_model.title, "Teardrop");
  SET(g_model.artist, "Massive Attack");
  SET(g_model.src, "Spotify");
  g_model.bat = 72;
  mark(D_LINK | D_AUDIO | D_MEDIA | D_TIME | D_BAT);
  run(900);
  snap("08_home_playing");
  ev(Ev::TouchDown, 0, 380, 150);
  run(300);
  snap("09_home_glance");
  ev(Ev::TouchUp, 0, 380, 150);
  ev(Ev::Tap, 0, 380, 150);
  g_model.playing = false;
  mark(D_MEDIA);
  run(1800);
  snap("10_home_paused");
  turn(30);
  run(60);
  snap("11_home_volume");
  g_model.vol = 52;
  run(2200);
  g_model.mute = true;
  g_model.playing = true;
  mark(D_AUDIO | D_MEDIA);
  run(900);
  snap("12_home_muted");
  g_model.mute = false;
  mark(D_AUDIO);
  g_model.bat = 9;
  g_model.chg = false;
  mark(D_BAT);
  run(600);
  snap("13_home_low_battery");
  run(5000);
  g_model.bat = 72;
  g_model.chg = true;
  mark(D_BAT);

  // ---- the picker and the trackpad
  ev(Ev::Button);
  run(300);
  snap("14_picker");
  turn(24);
  run(300);
  snap("15_picker_trackpad");
  ev(Ev::Tap);
  run(400);
  snap("16_trackpad");
  ev(Ev::TouchDown, 0, 290, 200);
  ev(Ev::TouchMove, 0, 300, 210);
  run(200);
  snap("17_trackpad_touch");
  ev(Ev::TouchUp, 0, 300, 210);
  g_lifted = true;
  ev(Ev::Lifted);
  run(400);
  snap("18_pointer");
  g_lifted = false;
  ev(Ev::Laid);

  // ---- Claude
  request("claude");
  snap("19_claude_idle");
  ev(Ev::Tap);
  g_model.claude = ClaudeState::Listening;
  SET(g_model.claudeText, "What is a good name for a round desk gadget");
  g_model.claudeLvl = 0.5f;
  mark(D_CLAUDE);
  run(500);
  snap("20_claude_listening");
  ev(Ev::Tap);
  g_model.claude = ClaudeState::Thinking;
  mark(D_CLAUDE);
  run(700);
  snap("21_claude_thinking");
  g_model.claude = ClaudeState::Reply;
  SET(g_model.claudeText,
      "Try \"Orbit\": it is round, it sits by your keyboard, and everything you do circles back to it. "
      "If you want something warmer, \"Pebble\" or \"Dot\" both feel friendly. A name you can say out loud "
      "without explaining tends to stick, so say each one a few times and keep the one that feels easiest. "
      "And if none of those land, think about what it does for you: it turns, it listens, it nudges you to "
      "focus. \"Nudge\" might be the one.");
  mark(D_CLAUDE);
  run(500);
  snap("22_claude_reply");
  turn(60);
  run(400);
  snap("23_claude_reply_scrolled");
  g_model.claude = ClaudeState::Error;
  SET(g_model.claudeText, "Add your Claude API key in Gyrodial settings on your PC");
  mark(D_CLAUDE);
  run(500);
  snap("24_claude_error");
  g_model.claude = ClaudeState::Idle;
  g_model.claudeText[0] = 0;
  mark(D_CLAUDE);

  // ---- Claude Code asks
  g_model.askActive = true;
  SET(g_model.askId, "r7");
  SET(g_model.askTool, "Bash");
  SET(g_model.askText, "npm run build && npm test -- --coverage");
  SET(g_model.askProj, "gyrodial");
  mark(D_ASK);
  run(500);
  snap("25_allow");
  run(400);
  ev(Ev::Tap);  // nothing chosen yet: it only shakes
  run(200);
  turn(-20);
  run(100);
  snap("26_allow_deny");
  turn(20);
  run(100);
  snap("27_allow_allow");
  ev(Ev::Tap);
  run(80);
  snap("28_allow_answering");
  run(400);
  g_model.askActive = false;
  mark(D_ASK);
  run(1200);
  g_model.askActive = true;
  SET(g_model.askId, "r8");
  SET(g_model.askTool, "github: create_pull_request");
  SET(g_model.askText,
      "{\"owner\":\"tank104\",\"repo\":\"gyrodial\",\"title\":\"Pairing: show progress on both sides\","
      "\"head\":\"pairing-progress\",\"base\":\"main\",\"body\":\"The dial reports each number as it is dialled "
      "in, and the app mirrors it. Also adds a result card.\"}");
  SET(g_model.askProj, "gyrodial");
  mark(D_ASK);
  run(500);
  snap("29_allow_long");
  ev(Ev::Swipe, 0, CX, CY, 'u');
  run(500);
  snap("30_allow_long_scrolled");
  g_model.askActive = false;
  mark(D_ASK);
  run(800);

  // ---- DialKit and a game, from home
  request("home");
  g_model.dialOn = true;
  SET(g_model.dialPanel, "Card");
  SET(g_model.dialName, "Corner radius");
  g_model.dialKind = DialKind::Num;
  g_model.dialVal = 18;
  g_model.dialMin = 0;
  g_model.dialMax = 48;
  g_model.dialStep = 1;
  SET(g_model.dialTxt, "18");
  g_model.dialIdx = 2;
  g_model.dialCnt = 6;
  mark(D_DIAL);
  run(500);
  snap("31_dialkit_num");
  g_model.dialKind = DialKind::Bool;
  SET(g_model.dialName, "Shadow");
  g_model.dialBool = true;
  g_model.dialIdx = 3;
  mark(D_DIAL);
  run(300);
  snap("32_dialkit_bool");
  g_model.dialKind = DialKind::Select;
  SET(g_model.dialName, "Easing");
  SET(g_model.dialTxt, "spring");
  g_model.dialIdx = 4;
  mark(D_DIAL);
  run(300);
  snap("33_dialkit_select");
  ev(Ev::Button);  // back to home: it stays away while the page keeps sending
  run(400);
  mark(D_DIAL);
  run(400);
  snap("34_dialkit_dismissed_home");
  g_model.dialOn = false;
  mark(D_DIAL);
  run(400);

  g_model.gameOn = true;
  SET(g_model.gameLabel, "PLAYER 1");
  g_model.gameAngle = 60;
  mark(D_GAME);
  run(500);
  snap("35_game");
  g_model.gameOn = false;
  mark(D_GAME);
  run(400);

  // ---- a Teams call
  g_model.callOn = true;
  SET(g_model.callApp, "Teams");
  mark(D_CALL);
  run(500);
  snap("36_call");
  ev(Ev::Tap);
  g_model.callMuted = true;
  mark(D_CALL);
  run(500);
  snap("37_call_muted");
  g_model.callOn = false;
  mark(D_CALL);
  run(400);

  // ---- focus
  request("focus");
  snap("38_focus_set");
  turn(-200, 40);  // down to a minute so it finishes quickly
  ev(Ev::Tap);
  run(20000);
  snap("39_focus_running");
  ev(Ev::Tap);
  run(300);
  snap("40_focus_paused");
  ev(Ev::Tap);
  run(42000);
  snap("41_focus_done");
  ev(Ev::Tap);

  // ---- toys
  request("spin");
  snap("42_spin");
  ev(Ev::Tap);
  run(400);
  snap("43_spin_spinning");
  run(7000);
  snap("44_spin_landed");
  request("dice");
  snap("45_dice_fresh");
  ev(Ev::Tap);
  run(1200);
  snap("46_dice");
  turn(30);
  ev(Ev::Tap);
  run(1200);
  snap("47_dice_three");
  request("pong");
  run(2500);
  snap("48_pong");

  // ---- another PC asks to pair, and the person says no; then one that goes away
  g_model.pairing = true;
  SET(g_model.pairHost, "STUDIO-LAPTOP");
  mark(D_PAIR);
  run(500);
  snap("49_pair_second_pc");
  ev(Ev::Button);
  run(400);
  snap("50_pair_cancelled");
  run(2500);
  g_model.pairing = true;
  mark(D_PAIR);
  run(500);
  g_model.pairing = false;
  mark(D_PAIR);
  run(400);
  snap("51_pair_stopped");
  run(2500);

  // ---- overlays
  request("home");
  SET(g_model.notifyText, "Claude needs your input on gyrodial: it wants to run the tests");
  g_model.notifyKind = NotifyKind::Claude;
  g_model.notifyAt = g_now;
  mark(D_NOTIFY);
  run(500);
  snap("52_banner");
  run(5000);
  ev(Ev::ButtonLong);
  run(300);
  ev(Ev::TouchDown);
  run(300);
  snap("53_power_holding");
  ev(Ev::TouchUp);
  ev(Ev::Tap);
  run(300);
  g_usb = true;
  ev(Ev::ButtonLong);
  run(400);
  snap("54_power_on_usb");
  g_usb = false;
  run(4500);

  // ---- the PC goes away
  g_model.linked = false;
  mark(D_LINK);
  run(1000);
  snap("55_offline_pong");

  sheet();
  printf("wrote out/*.png and out/sheet.png\n");
  return 0;
}
