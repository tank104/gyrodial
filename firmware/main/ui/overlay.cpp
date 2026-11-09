// Things that sit over every face: the notification banner and the power-off question.
#include "ui_common.h"

namespace ui {
namespace overlay {

static constexpr uint32_t BANNER_MS = 4000;
static constexpr uint32_t POWER_TIMEOUT_MS = 6000;
static constexpr uint32_t HOLD_MS = 600;  // as long as the hal's long press: the ring fills while it counts
static constexpr int BANNER_Y = 112;      // between the clock row and the eyes
static constexpr int BANNER_TEXT_W = 266;

static lv_obj_t* s_banner;
static lv_obj_t* s_bannerLbl;
static uint32_t s_bannerAt = 0;
static bool s_bannerUp = false;

static lv_obj_t* s_power;
static lv_obj_t* s_holdRing;
static bool s_powerOpen = false;
static uint32_t s_powerAt = 0;
static uint32_t s_holdFrom = 0;  // a finger is down on the glass since (0: not holding)

static void tyCb(void* o, int32_t v) { lv_obj_set_style_translate_y((lv_obj_t*)o, (lv_coord_t)v, 0); }

static void slide(int from, int to, uint32_t ms, lv_anim_path_cb_t path, bool hideAfter) {
  lv_anim_del(s_banner, tyCb);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, s_banner);
  lv_anim_set_exec_cb(&a, tyCb);
  lv_anim_set_values(&a, from, to);
  lv_anim_set_time(&a, ms);
  lv_anim_set_path_cb(&a, path);
  if (hideAfter) lv_anim_set_ready_cb(&a, [](lv_anim_t* an) { lv_obj_add_flag((lv_obj_t*)an->var, LV_OBJ_FLAG_HIDDEN); });
  lv_anim_start(&a);
}

void create() {
  picker::create();

  // Up to two lines: the pill grows with its text
  s_banner = box(lv_layer_top(), 300, LV_SIZE_CONTENT, COL_GREY4, 26);
  lv_obj_set_style_border_width(s_banner, 2, 0);
  lv_obj_set_style_border_opa(s_banner, LV_OPA_COVER, 0);
  lv_obj_set_style_pad_ver(s_banner, 9, 0);
  lv_obj_set_style_pad_hor(s_banner, 16, 0);
  s_bannerLbl = label(s_banner, &nunito_22, COL_WHITE, "");
  lv_obj_set_width(s_bannerLbl, BANNER_TEXT_W);
  lv_label_set_long_mode(s_bannerLbl, LV_LABEL_LONG_DOT);
  lv_obj_align(s_bannerLbl, LV_ALIGN_TOP_MID, 0, 0);
  hide(s_banner, true);

  s_power = box(lv_layer_top(), LCD_W, LCD_H, COL_BG, 0);
  lv_obj_set_pos(s_power, 0, 0);
  // fills while the finger is held: let go early and it drains away
  s_holdRing = arc(s_power, 74, 10, COL_RED);
  arcTrack(s_holdRing, COL_GREY3, 0, 360);
  center(s_holdRing, 0, -70);
  lv_obj_t* icon = label(s_power, &lv_font_montserrat_48, COL_RED, LV_SYMBOL_POWER);
  at(icon, CX, CY - 70);
  lv_obj_t* title = label(s_power, &nunito_40, COL_WHITE, "Power off?");
  at(title, CX, CY + 48);
  hintLabel(s_power, "Hold to power off", CY + 96);
  hintLabel(s_power, "Tap to cancel");
  hide(s_power, true);
}

void banner(const char* text, bool claude) {
  setText(s_bannerLbl, text);
  // One or two lines: a label only dots off at a height it's been given, so give it one
  const lv_font_t* f = &nunito_22;
  lv_point_t sz;
  lv_txt_get_size(&sz, text, f, 0, 0, BANNER_TEXT_W, LV_TEXT_FLAG_NONE);
  lv_coord_t line = lv_font_get_line_height(f);
  lv_obj_set_height(s_bannerLbl, sz.y > line ? 2 * line : line);
  lv_obj_set_style_border_color(s_banner, rgb(claude ? COL_CLAUDE : COL_GREY3), 0);
  lv_obj_set_style_text_color(s_bannerLbl, rgb(claude ? COL_CLAUDE : COL_WHITE), 0);
  lv_obj_update_layout(s_banner);
  at(s_banner, CX, BANNER_Y);
  lv_obj_move_foreground(s_banner);
  hide(s_banner, false);
  if (!s_bannerUp) slide(-180, 0, 300, lv_anim_path_ease_out, false);
  s_bannerUp = true;
  s_bannerAt = lv_tick_get();
}

void powerConfirm() {
  if (s_powerOpen) return;
  picker::close();
  // On USB the PMU stays on whatever we do: just say how
  if (hal::usbPowered()) {
    banner("Unplug USB, then hold the button to power off", false);
    hal::beep(Tone::Select);
    return;
  }
  s_powerOpen = true;
  s_powerAt = lv_tick_get();
  s_holdFrom = 0;
  arcSpan(s_holdRing, 0, 0);
  hal::setInputMode(InputMode::Ui);
  lv_obj_move_foreground(s_power);
  fadeIn(s_power, 150);
  hal::beep(Tone::Select);
}

static void powerClose() {
  s_powerOpen = false;
  s_holdFrom = 0;
  fadeOut(s_power, 150);
  restoreInputMode();
}

bool powerOpen() { return s_powerOpen; }

void powerInput(const InputEvent& e) {
  s_powerAt = lv_tick_get();
  switch (e.type) {
    case Ev::TouchDown:
      s_holdFrom = lv_tick_get() | 1;
      break;
    case Ev::TouchUp:
      s_holdFrom = 0;
      arcSpan(s_holdRing, 0, 0);
      break;
    case Ev::LongPress:
      arcSpan(s_holdRing, 0, 360);
      hal::beep(Tone::Select);
      hal::powerOff();
      powerClose();  // if we're still here, the PMU said no
      break;
    case Ev::Tap:
    case Ev::Button:
    case Ev::ButtonLong:
      powerClose();
      break;
    default:
      break;
  }
}

void tick(uint32_t now) {
  if (s_bannerUp && (int32_t)(now - s_bannerAt) > (int32_t)BANNER_MS) {
    s_bannerUp = false;
    slide(0, -180, 260, lv_anim_path_ease_in, true);
  }
  if (!s_powerOpen) return;
  if (s_holdFrom) {
    float f = clampf((float)(int32_t)(now - s_holdFrom) / HOLD_MS, 0, 1);
    arcSpan(s_holdRing, 0, 360.0f * f);
  } else if ((int32_t)(now - s_powerAt) > (int32_t)POWER_TIMEOUT_MS) {
    powerClose();
  }
}

}  // namespace overlay
}  // namespace ui
