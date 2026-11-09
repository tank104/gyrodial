// The picker: the side button brings it up over any face. Turn through the faces (one every 24°,
// stopping at the ends), tap or press the button to open one, hold to back out.
#include "ui_common.h"

namespace ui {
namespace picker {

static constexpr float STEP_DEG = 24.0f;

static lv_obj_t* s_box;
static lv_obj_t* s_dots;
static lv_obj_t* s_glyph;
static lv_obj_t* s_name;
static lv_obj_t* s_hint;
static FaceId s_items[F_COUNT];
static int s_n = 0;
static int s_sel = 0;
static bool s_open = false;
static uint32_t s_lastInput = 0;
static Detents s_det;

// glyph parts that need to outlive the call that drew them
static Spark s_spark;
static Mic s_mic;
static lv_point_t s_needlePts[2];

void create() {
  s_box = box(lv_layer_top(), LCD_W, LCD_H, COL_BG, 0);
  lv_obj_set_pos(s_box, 0, 0);
  s_dots = plain(s_box);
  lv_obj_set_size(s_dots, LCD_W, LCD_H);
  s_glyph = plain(s_box);
  lv_obj_set_size(s_glyph, 180, 180);
  at(s_glyph, CX, CY - 26);
  s_name = label(s_box, &nunito_40, COL_WHITE, "");
  at(s_name, CX, 326);
  s_hint = hintLabel(s_box, "Tap: open \xC2\xB7 Hold: back", 380);
  hide(s_box, true);
}

bool isOpen() { return s_open; }

static void buildList() {
  s_n = 0;
  s_items[s_n++] = F_HOME;
  if (g_model.callOn) s_items[s_n++] = F_CALL;
  if (g_model.dialOn) s_items[s_n++] = F_DIALKIT;
  if (g_model.gameOn) s_items[s_n++] = F_GAME;
  s_items[s_n++] = F_TRACKPAD;
  s_items[s_n++] = F_CLAUDE;
  s_items[s_n++] = F_FOCUS;
  s_items[s_n++] = F_SPIN;
  s_items[s_n++] = F_DICE;
  s_items[s_n++] = F_PONG;
}

static void render(int dir) {
  lv_obj_clean(s_dots);
  const float spacing = 10.0f;
  float start = -spacing * (s_n - 1) / 2.0f;
  for (int i = 0; i < s_n; i++) {
    bool on = i == s_sel;
    lv_obj_t* d = circle(s_dots, on ? 14 : 8, on ? COL_YELLOW : COL_GREY2);
    float x, y;
    polar(start + i * spacing, 204, &x, &y);
    at(d, (int)x, (int)y);
  }
  lv_obj_clean(s_glyph);
  drawGlyph(s_items[s_sel], s_glyph);
  setText(s_name, face(s_items[s_sel]).name);
  if (dir) {
    lv_obj_set_style_translate_x(s_glyph, 0, 0);
    rise(s_glyph, dir > 0 ? 10 : -10, 180);
  }
}

void open() {
  if (s_open) return;
  buildList();
  s_sel = 0;
  for (int i = 0; i < s_n; i++)
    if (s_items[i] == current()) s_sel = i;
  s_det.reset();
  s_open = true;
  s_lastInput = lv_tick_get();
  hal::setInputMode(InputMode::Ui);
  render(0);
  fadeIn(s_box, 150);
  hal::beep(Tone::Tap);
}

void close() {
  if (!s_open) return;
  s_open = false;
  fadeOut(s_box, 140);
  restoreInputMode();
}

void input(const InputEvent& e) {
  s_lastInput = lv_tick_get();
  switch (e.type) {
    case Ev::Turn: {
      int n = s_det.feed(e.value, STEP_DEG);
      if (!n) break;
      int sel = s_sel + n;
      if (sel < 0) sel = 0;
      if (sel > s_n - 1) sel = s_n - 1;
      if (sel == s_sel) {
        s_det.reset();  // at an end: it stops, it doesn't wind up
        break;
      }
      s_sel = sel;
      hal::beep(Tone::Tick);
      render(n);
      break;
    }
    case Ev::Tap:
    case Ev::Button: {
      FaceId f = s_items[s_sel];
      hal::beep(Tone::Select);
      s_open = false;
      fadeOut(s_box, 140);
      pickerChose(f);
      break;
    }
    case Ev::LongPress:
    case Ev::ButtonLong:
      close();
      break;
    default:
      break;
  }
}

void tick(uint32_t now) {
  if (s_open && (int32_t)(now - s_lastInput) > 8000) close();
}

// ---------------------------------------------------------------- glyphs

void drawGlyph(FaceId f, lv_obj_t* p, float k) {
  lv_obj_update_layout(p);
  float cx = lv_obj_get_width(p) / 2.0f, cy = lv_obj_get_height(p) / 2.0f;
  auto S = [k](float v) { return (int)lroundf(v * k); };
  switch (f) {
    case F_HOME: {
      lv_obj_t* l = box(p, S(34), S(84), COL_YELLOW, LV_RADIUS_CIRCLE);
      center(l, -S(36), 0);
      lv_obj_t* r = box(p, S(34), S(84), COL_YELLOW, LV_RADIUS_CIRCLE);
      center(r, S(36), 0);
      break;
    }
    case F_TRACKPAD: {
      lv_obj_t* sq = box(p, S(128), S(128), COL_BG, S(34));
      lv_obj_set_style_border_width(sq, S(6), 0);
      lv_obj_set_style_border_color(sq, rgb(COL_GREY1), 0);
      lv_obj_set_style_border_opa(sq, LV_OPA_COVER, 0);
      center(sq, 0, 0);
      lv_obj_t* glow = circle(p, S(52), COL_YELLOW, LV_OPA_20);
      center(glow, S(14), S(10));
      lv_obj_t* dot = circle(p, S(22), COL_YELLOW);
      center(dot, S(14), S(10));
      break;
    }
    case F_CLAUDE:
      s_spark.create(p, COL_CLAUDE);
      s_spark.draw(cx, cy, S(66), 0);
      break;
    case F_FOCUS: {
      lv_obj_t* a = arc(p, S(58), S(14), COL_YELLOW);
      arcTrack(a, COL_GREY3, 0, 360);
      arcSpan(a, 0, 250);
      lv_obj_t* n = label(p, &nunito_28, COL_WHITE, "25");
      center(n, 0, 0);
      break;
    }
    case F_SPIN: {
      for (int i = 0; i < 8; i++) {
        lv_obj_t* a = arc(p, S(56), S(30), i % 2 == 0 ? COL_YELLOW : COL_GREY3, false);
        arcSpan(a, i * 45 + 2, i * 45 + 43);
      }
      lv_obj_t* n = line(p, s_needlePts, S(9), COL_WHITE);
      float x, y;
      polar(55, S(36), &x, &y, (int)cx, (int)cy);
      setLine(n, s_needlePts, cx, cy, x, y);
      lv_obj_t* hub = circle(p, S(18), COL_WHITE);
      center(hub, 0, 0);
      break;
    }
    case F_DICE: {
      lv_obj_t* d = box(p, S(108), S(108), COL_WHITE, S(22));
      center(d, 0, 0);
      const int pos[5][2] = {{-1, -1}, {1, -1}, {0, 0}, {-1, 1}, {1, 1}};
      for (auto& q : pos) {
        lv_obj_t* pip = circle(d, S(18), COL_BG);
        center(pip, q[0] * S(29), q[1] * S(29));
      }
      break;
    }
    case F_PONG: {
      lv_obj_t* t = arc(p, S(68), S(2), COL_GREY3);
      arcSpan(t, 0, 360);
      lv_obj_t* a = arc(p, S(68), S(10), COL_YELLOW);
      arcSpan(a, 40, 80);
      lv_obj_t* b = arc(p, S(68), S(10), COL_YELLOW);
      arcSpan(b, 220, 260);
      lv_obj_t* ball = circle(p, S(16), COL_WHITE);
      center(ball, -S(12), -S(18));
      break;
    }
    case F_DIALKIT: {
      lv_obj_t* a = arc(p, S(60), S(12), COL_YELLOW);
      arcTrack(a, COL_GREY4, 225, 495);
      arcSpan(a, 225, 400);
      lv_obj_t* n = label(p, &nunito_28, COL_WHITE, "0.6");
      center(n, 0, 0);
      break;
    }
    case F_GAME: {
      lv_obj_t* t = arc(p, S(66), S(2), COL_GREY3);
      arcSpan(t, 0, 360);
      lv_obj_t* a = arc(p, S(66), S(14), COL_YELLOW);
      arcSpan(a, 300, 360);
      lv_obj_t* n = label(p, &nunito_28, COL_GREY1, LV_SYMBOL_PLAY);
      center(n, S(3), 0);
      break;
    }
    case F_CALL:
      s_mic.create(p, 0.62f * k);
      center(s_mic.root, 0, S(8));
      s_mic.set(g_model.callMuted);
      break;
    default:
      break;
  }
}

}  // namespace picker
}  // namespace ui
