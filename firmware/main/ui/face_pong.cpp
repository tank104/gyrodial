// Pong, round: the ball bounces around inside the glass and two paddles on the rim, opposite each
// other, turn together with the puck. Left alone it plays itself, which is also what the puck does
// while it's waiting for the PC.
#include "ui_common.h"

namespace ui {
namespace {

constexpr float RIM_R = 214;           // paddles ride on this circle
constexpr float PADDLE_W = 14;
constexpr float PADDLE_SPAN = 40;      // degrees
constexpr float BALL_D = 22;
constexpr float CONTACT_R = RIM_R - PADDLE_W / 2 - BALL_D / 2;  // where the ball meets a paddle
constexpr float START_SPEED = 170;     // px/s
constexpr float MAX_SPEED = 430;
constexpr uint32_t AUTO_AFTER_MS = 5000;

lv_obj_t* root;
lv_obj_t* track;
lv_obj_t* paddleA;
lv_obj_t* paddleB;
lv_obj_t* ball;
lv_obj_t* scoreLbl;
lv_obj_t* status;

float paddle = 0;                 // clockwise from 12
float bx = 0, by = 0;             // ball, from the center
float vx = 0, vy = 0;
int score = 0;
int shownScore = -1;
uint32_t lastTick = 0;
uint32_t lastHuman = 0;
uint32_t serveAt = 0;             // non-zero: the ball waits in the middle until then
float aiError = 0;
bool shownLinked = true;

float angleOf(float x, float y) {  // clockwise from 12
  float a = atan2f(x, -y) * 180.0f / PI_F;
  return a < 0 ? a + 360.0f : a;
}

void drawPaddles() {
  arcSpan(paddleA, paddle - PADDLE_SPAN / 2, paddle + PADDLE_SPAN / 2);
  arcSpan(paddleB, paddle + 180 - PADDLE_SPAN / 2, paddle + 180 + PADDLE_SPAN / 2);
}

void drawBall() { at(ball, CX + (int)lroundf(bx), CY + (int)lroundf(by)); }

void drawScore() {
  if (score == shownScore) return;
  shownScore = score;
  lv_label_set_text_fmt(scoreLbl, "%d", score);
}

void serve(uint32_t now, uint32_t delay) {
  bx = by = 0;
  vx = vy = 0;
  serveAt = now + delay;
  if (serveAt == 0) serveAt = 1;
  drawBall();
}

void launch() {
  float a = (float)rndRange(0, 359);
  vx = START_SPEED * sinf(a * PI_F / 180.0f);
  vy = -START_SPEED * cosf(a * PI_F / 180.0f);
  aiError = (float)rndRange(-14, 14);
}

void refreshStatus() {
  bool linked = g_model.linked;
  // On USB power with nobody talking, the cable is in: it's the app that isn't there
  setText(status, hal::usbPowered() ? "Open Puck on your PC" : "Looking for your PC\xE2\x80\xA6");
  if (linked == shownLinked) return;
  shownLinked = linked;
  hide(status, linked);
}

void create(lv_obj_t* r) {
  root = r;
  track = arc(root, (int)RIM_R, 2, COL_GREY3);
  arcSpan(track, 0, 360);
  scoreLbl = label(root, &nunito_digits_150, COL_GREY3, "0");
  at(scoreLbl, CX, CY - 4);
  status = hintLabel(root, "Looking for your PC\xE2\x80\xA6", CY + 104, 320);
  paddleA = arc(root, (int)RIM_R, (int)PADDLE_W, COL_YELLOW);
  paddleB = arc(root, (int)RIM_R, (int)PADDLE_W, COL_YELLOW);
  ball = circle(root, (int)BALL_D, COL_WHITE);
  drawPaddles();
  shownLinked = !g_model.linked;
  refreshStatus();
}

void enter() {
  uint32_t now = lv_tick_get();
  lastTick = now;
  lastHuman = 0;
  score = 0;
  shownScore = -1;
  drawScore();
  serve(now, 500);
  drawPaddles();
  refreshStatus();
}

void input(const InputEvent& e) {
  if (e.type == Ev::Turn) {
    paddle = fmodf(paddle + e.value + 360.0f, 360.0f);
    lastHuman = lv_tick_get();
    if (!lastHuman) lastHuman = 1;
    drawPaddles();
  } else if (e.type == Ev::Tap && serveAt) {
    serveAt = lv_tick_get();  // serve now
  }
}

void update(uint32_t dirty) {
  if (dirty & (D_LINK | D_BAT)) refreshStatus();
}

void autoplay(float dt) {
  // Aim the nearer paddle at where the ball will reach the rim, a little off on purpose
  float pv = bx * vx + by * vy, vv = vx * vx + vy * vy, pp = bx * bx + by * by;
  if (vv < 1) return;
  float disc = pv * pv - vv * (pp - CONTACT_R * CONTACT_R);
  if (disc < 0) return;
  float t = (-pv + sqrtf(disc)) / vv;
  float target = angleOf(bx + vx * t, by + vy * t) + aiError;
  float dA = angleDiff(target, paddle), dB = angleDiff(target, paddle + 180);
  float d = fabsf(dA) < fabsf(dB) ? dA : dB;
  float step = 260.0f * dt;
  paddle = fmodf(paddle + clampf(d, -step, step) + 360.0f, 360.0f);
  drawPaddles();
}

void tick(uint32_t now) {
  float dt = (int32_t)(now - lastTick) / 1000.0f;
  lastTick = now;
  if (dt > 0.04f) dt = 0.04f;
  if (dt <= 0) return;

  bool human = lastHuman && (int32_t)(now - lastHuman) < (int32_t)AUTO_AFTER_MS;
  if (serveAt) {
    if ((int32_t)(now - serveAt) >= 0) {
      serveAt = 0;
      launch();
    }
    if (!human) autoplay(dt);
    return;
  }
  if (!human) autoplay(dt);

  bx += vx * dt;
  by += vy * dt;
  float r = sqrtf(bx * bx + by * by);
  if (r >= CONTACT_R) {
    float a = angleOf(bx, by);
    float slack = PADDLE_SPAN / 2 + 3.0f;
    float offA = angleDiff(a, paddle), offB = angleDiff(a, paddle + 180);
    float off = fabsf(offA) < fabsf(offB) ? offA : offB;
    float nx = bx / r, ny = by / r;
    bool outward = vx * nx + vy * ny > 0;
    if (fabsf(off) <= slack && outward) {
      // reflect about the rim's normal, then bend it a little by where it hit the paddle
      float dot = vx * nx + vy * ny;
      vx -= 2 * dot * nx;
      vy -= 2 * dot * ny;
      float bend = -off / slack * 0.35f;
      float c = cosf(bend), s = sinf(bend);
      float nvx = vx * c - vy * s, nvy = vx * s + vy * c;
      if (nvx * nx + nvy * ny < 0) {  // never bend it back out
        vx = nvx;
        vy = nvy;
      }
      float sp = sqrtf(vx * vx + vy * vy);
      float want = clampf(sp * 1.05f, START_SPEED, MAX_SPEED);
      vx *= want / sp;
      vy *= want / sp;
      bx = nx * (CONTACT_R - 1);
      by = ny * (CONTACT_R - 1);
      score++;
      aiError = (float)rndRange(-16, 16) * (score > 12 ? 1.6f : 1.0f);
      if (human) hal::beep(Tone::Tick);
    } else if (r > CONTACT_R + BALL_D) {
      if (human && score > 0) hal::beep(Tone::Error);
      score = 0;
      serve(now, 700);
    }
  }
  drawBall();
  drawScore();
}

}  // namespace

const Face FACE_PONG = {"pong", "Pong", create, enter, nullptr, input, update, tick};

}  // namespace ui
