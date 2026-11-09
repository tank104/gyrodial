// Motion: the QMI8658 gyro + accelerometer. Turning the puck (the dial), lifting it, shaking it,
// and, in trackpad mode while it's held up, aiming it like a pointer.
#include <math.h>
#include <stdio.h>
#include <atomic>
#include "../config.h"
#include "../hostlink.h"
#include "hal_internal.h"
#include "qmi8658.h"

namespace hal {
namespace detail {

namespace {

static const char* TAG = "hal";

// ---- orientation assumptions (check on the real board; flip a sign here if one is backwards)
// The accelerometer reads +1 g on z when the puck lies screen-up (IMU z axis out of the screen).
constexpr float kScreenUpZ = +1.0f;
// Pointer: hand turned right moves the mouse right, nose up moves it up. These follow from the
// math below when the axes are as assumed; they're here so a mirrored board needs one edit.
constexpr float kPointerXSign = -1.0f;  // checked on a real 1.75C: +1 moved the pointer the wrong way
constexpr float kPointerYSign = +1.0f;

// ---- turning (the dial)
constexpr float kTurnStartDps = 4.0f;    // a turn starts above this...
constexpr float kTurnStopDps = 1.5f;     // ...and ends after kTurnStopUs below this
constexpr uint32_t kTurnStopUs = 150000;
constexpr float kTurnEmitDeg = 0.25f;
constexpr uint32_t kTurnEmitUs = 10000;  // at most 100 Turn events a second
constexpr float kScrollDegPerNotch = 15.0f;

// ---- gyro bias
constexpr float kStillDps = 2.0f;
constexpr float kStillAccelG = 0.04f;
constexpr uint32_t kStillUs = 500000;
constexpr float kBiasAlpha = 0.002f;     // ~1.5 s time constant at ~333 Hz

// ---- lifted / laid
// Lifted/laid with hysteresis, so a grip near one angle doesn't flick between trackpad and pointer
constexpr float kLiftCos = 0.866f;       // lifted once tilted past 30°...
constexpr float kLayCos = 0.940f;        // ...and laid only back within 20°
constexpr uint32_t kLiftDebounceUs = 150000;  // picking it up switches to the pointer quickly...
constexpr uint32_t kLayDebounceUs = 500000;   // ...but a grip that dips toward flat must stay there
                                              // a while before it counts as put down

// ---- shake
constexpr float kShakePeakG = 1.2f;      // deviation from 1 g
constexpr uint32_t kShakePeakGapUs = 80000;
constexpr uint32_t kShakeWindowUs = 700000;
constexpr uint32_t kShakeRepeatUs = 1000000;

// ---- "someone is handling it"
constexpr float kActiveDps = 20.0f;
constexpr float kActiveAccelG = 0.3f;
constexpr uint32_t kActiveQuietUs = 300000;

// ---- pointer
constexpr float kPointerDeadDps = 0.6f;  // the bias is learned, so only hand tremor is left to hide;
                                         // 1.5 swallowed slow, careful aiming
constexpr float kPointerSmooth = 0.35f;  // EMA weight of the new sample
constexpr uint32_t kPointerSendUs = 10000;
constexpr uint32_t kTapFreezeMs = 150;

Qmi8658 s_imu;
bool s_ok = false;
InputMode s_mode = InputMode::Ui;

std::atomic<float> s_heading{0};
std::atomic<bool> s_lifted{false};

float s_bias[3] = {0, 0, 0};
uint32_t s_lastUs = 0;
uint32_t s_stillUs = 0;

// turning
bool s_turning = false;
bool s_turnSwallowed = false;
float s_turnAccum = 0;
uint32_t s_turnQuietSince = 0;
uint32_t s_lastEmitUs = 0;

// lifted
bool s_flatKnown = false;
uint32_t s_flatCandidateSince = 0;

// shake
uint32_t s_peaks[4] = {0, 0, 0, 0};
int s_peakIdx = 0;
uint32_t s_lastPeakUs = 0;
uint32_t s_lastShakeUs = 0;

// activity
bool s_active = false;
uint32_t s_activeQuietSince = 0;

// pointer
float s_yawF = 0, s_pitchF = 0;
float s_ptrDx = 0, s_ptrDy = 0;
uint32_t s_ptrLastSend = 0;

inline float sqr(float v) { return v * v; }
inline float soft(float v, float dead) { return v > dead ? v - dead : (v < -dead ? v + dead : 0); }

bool readImu(float a[3], float w[3]) {
  I2cGuard lock;
  if (!s_imu.getAccelerometer(a[0], a[1], a[2])) return false;
  if (!s_imu.getGyroscope(w[0], w[1], w[2])) return false;
  return true;
}

void emitTurn(float physicalDeg) {
  if (s_turnSwallowed) return;
  const bool pad = s_mode == InputMode::Trackpad;
  if (pad && s_lifted.load()) return;  // aiming the pointer twists the wrist; that's not a turn
  const float v = physicalDeg * turnScale();
  post(Ev::Turn, v);
  if (pad) hostlink::sendScroll(-v / kScrollDegPerNotch);  // clockwise scrolls down
}

void updateTurn(float rate, float dt, uint32_t now) {
  const float mag = fabsf(rate);
  if (!s_turning) {
    if (mag <= kTurnStartDps) return;
    s_turning = true;
    s_turnAccum = 0;
    s_turnQuietSince = 0;
    s_turnSwallowed = !activityBegin(Src::Turn);
  }
  const float d = rate * dt;
  s_turnAccum += d;
  s_heading.store(s_heading.load() + d);
  activityContinue();

  if (mag < kTurnStopDps) {
    if (!s_turnQuietSince) s_turnQuietSince = now ? now : 1;
    else if (now - s_turnQuietSince >= kTurnStopUs) {
      if (fabsf(s_turnAccum) >= kTurnEmitDeg) emitTurn(s_turnAccum);
      s_turnAccum = 0;
      s_turning = false;
      return;
    }
  } else {
    s_turnQuietSince = 0;
  }
  if (fabsf(s_turnAccum) >= kTurnEmitDeg && now - s_lastEmitUs >= kTurnEmitUs) {
    emitTurn(s_turnAccum);
    s_turnAccum = 0;
    s_lastEmitUs = now;
  }
}

// Still = the gyro reading isn't changing and neither is the accelerometer. (The accelerometer's
// own scale error means |a| at rest isn't exactly 1 g, so only change is judged.)
//
// Until the bias has been learned once, "not changing" is all that's asked: a QMI8658 can sit
// 15 °/s or more off zero (measured on a 1.75C), so "close to the bias" can't be the test while
// the bias is still 0. Once it's learned, the reading must also be near it, so a slow steady
// turn isn't mistaken for drift and absorbed.
float s_aLp[3] = {0, 0, kScreenUpZ};
float s_wLp[3] = {0, 0, 0};
bool s_wLpSeeded = false;
bool s_biasLearned = false;
constexpr float kBiasAlphaFirst = 0.02f;   // ~50 ms time constant: the first fix is quick
constexpr float kBiasLearnedDps = 0.3f;    // first fix done when the estimate is this close
void updateBias(const float w[3], const float a[3], float dt) {
  if (!s_wLpSeeded) {
    for (int i = 0; i < 3; i++) s_wLp[i] = w[i];
    s_wLpSeeded = true;
  }
  const float dw = sqrtf(sqr(w[0] - s_wLp[0]) + sqr(w[1] - s_wLp[1]) + sqr(w[2] - s_wLp[2]));
  const float da = sqrtf(sqr(a[0] - s_aLp[0]) + sqr(a[1] - s_aLp[1]) + sqr(a[2] - s_aLp[2]));
  for (int i = 0; i < 3; i++) {
    s_wLp[i] += 0.05f * (w[i] - s_wLp[i]);
    s_aLp[i] += 0.05f * (a[i] - s_aLp[i]);
  }
  const float off = sqrtf(sqr(s_wLp[0] - s_bias[0]) + sqr(s_wLp[1] - s_bias[1]) + sqr(s_wLp[2] - s_bias[2]));
  const bool steady = dw < kStillDps && da < kStillAccelG;
  if (steady && (!s_biasLearned || off < kStillDps)) {
    s_stillUs += (uint32_t)(dt * 1e6f);
    if (s_stillUs >= kStillUs) {
      const float alpha = s_biasLearned ? kBiasAlpha : kBiasAlphaFirst;
      for (int i = 0; i < 3; i++) s_bias[i] += alpha * (s_wLp[i] - s_bias[i]);
      if (!s_biasLearned && off < kBiasLearnedDps) s_biasLearned = true;
    }
  } else {
    s_stillUs = 0;
  }
}

// "Up" in device axes, low-passed (~0.3 s). The raw accelerometer also carries the hand's own
// acceleration, which swung the pointer's axes about mid-aim (left/right bleeding into up/down)
// and could flick lifted/laid; orientation changes slowly by comparison.
float s_up[3] = {0, 0, kScreenUpZ};
bool s_upSeeded = false;
void updateUp(const float a[3], float amag) {
  if (amag < 0.5f) return;
  if (!s_upSeeded) {
    for (int i = 0; i < 3; i++) s_up[i] = a[i] / amag;
    s_upSeeded = true;
    return;
  }
  const float k = fabsf(amag - 1.0f) < 0.15f ? 0.01f : 0.003f;  // trust a busy accelerometer less
  float n = 0;
  for (int i = 0; i < 3; i++) {
    s_up[i] += k * (a[i] / amag - s_up[i]);
    n += sqr(s_up[i]);
  }
  n = sqrtf(n);
  if (n > 0.1f)
    for (float& c : s_up) c /= n;
}

void updateFlat(const float a[3], float amag, uint32_t now) {
  if (fabsf(amag - 1.0f) > 0.25f || amag < 0.1f) return;  // accelerating: gravity isn't readable
  (void)a;
  const float up = kScreenUpZ * s_up[2];  // cos of the tilt from screen-up
  const bool flat = up > (s_lifted.load() ? kLayCos : kLiftCos);
  if (!s_flatKnown) {
    s_flatKnown = true;
    s_lifted = !flat;
    return;
  }
  if (flat == !s_lifted.load()) {
    s_flatCandidateSince = 0;
    return;
  }
  if (!s_flatCandidateSince) s_flatCandidateSince = now ? now : 1;
  else if (now - s_flatCandidateSince >= (flat ? kLayDebounceUs : kLiftDebounceUs)) {
    s_flatCandidateSince = 0;
    s_lifted = !flat;
    activityBegin(Src::Motion);
    post(flat ? Ev::Laid : Ev::Lifted);
    s_yawF = s_pitchF = s_ptrDx = s_ptrDy = 0;
  }
}

void updateShake(float amag, uint32_t now) {
  if (fabsf(amag - 1.0f) <= kShakePeakG || now - s_lastPeakUs < kShakePeakGapUs) return;
  s_lastPeakUs = now;
  s_peaks[s_peakIdx] = now;
  s_peakIdx = (s_peakIdx + 1) % 4;
  int recent = 0;
  for (uint32_t t : s_peaks)
    if (t && now - t <= kShakeWindowUs) recent++;
  if (recent >= 3 && now - s_lastShakeUs >= kShakeRepeatUs) {
    s_lastShakeUs = now;
    for (uint32_t& t : s_peaks) t = 0;
    if (activityBegin(Src::Motion)) post(Ev::Shake);
  }
}

void updateActivity(const float w[3], float amag, uint32_t now) {
  const float wmag = sqrtf(sqr(w[0]) + sqr(w[1]) + sqr(w[2]));
  if (wmag > kActiveDps || fabsf(amag - 1.0f) > kActiveAccelG) {
    if (!s_active) {
      s_active = true;
      activityBegin(Src::Motion);  // wakes the display if it's off
    } else {
      activityContinue();
    }
    s_activeQuietSince = 0;
  } else if (s_active) {
    if (!s_activeQuietSince) s_activeQuietSince = now ? now : 1;
    else if (now - s_activeQuietSince >= kActiveQuietUs) s_active = false;
  }
}

// Held up and aimed at the screen: the hand's yaw moves the pointer sideways, its pitch up/down.
// u: up (gravity reaction) in device axes. f: where the puck points, out of its back (-z when the
// screen faces the user). r = f x u points to the user's right. Yaw is rotation about u, pitch
// about r (right-hand rule: positive yaw turns left, positive pitch raises the nose).
void updatePointer(const float a[3], const float w[3], float amag, float dt, uint32_t now) {
  if (s_mode != InputMode::Trackpad || !s_lifted.load() || amag < 0.5f) return;
  // Tapping the glass to click jolts the puck: hold the pointer still during a tap and just after,
  // so the click lands where you aimed. A thumb resting on the glass doesn't freeze it.
  if (tapInProgress(now / 1000, kTapFreezeMs)) {
    s_yawF = s_pitchF = 0;
    return;
  }
  (void)a;
  const float u[3] = {s_up[0], s_up[1], s_up[2]};
  const float f[3] = {0, 0, -kScreenUpZ};
  float r[3] = {f[1] * u[2] - f[2] * u[1], f[2] * u[0] - f[0] * u[2], f[0] * u[1] - f[1] * u[0]};
  const float rn = sqrtf(sqr(r[0]) + sqr(r[1]) + sqr(r[2]));
  if (rn < 0.3f) return;  // pointing at the ceiling or the floor: no stable horizon
  for (float& c : r) c /= rn;

  const float yaw = soft(w[0] * u[0] + w[1] * u[1] + w[2] * u[2], kPointerDeadDps);
  const float pitch = soft(w[0] * r[0] + w[1] * r[1] + w[2] * r[2], kPointerDeadDps);
  s_yawF += kPointerSmooth * (yaw - s_yawF);
  s_pitchF += kPointerSmooth * (pitch - s_pitchF);
  s_ptrDx += kPointerXSign * -s_yawF * dt;   // turning right (negative yaw) moves right
  s_ptrDy += kPointerYSign * -s_pitchF * dt; // nose up (positive pitch) moves up (screen y is down)
  if (fabsf(yaw) > 0 || fabsf(pitch) > 0) activityContinue();

  if (now - s_ptrLastSend >= kPointerSendUs) {
    s_ptrLastSend = now;
    if (fabsf(s_ptrDx) > 0.001f || fabsf(s_ptrDy) > 0.001f) {
      hostlink::sendMove('p', s_ptrDx, s_ptrDy);
      s_ptrDx = s_ptrDy = 0;
    }
  }
}

}  // namespace

bool motionBegin() {
  {
    I2cGuard lock;
    if (!s_imu.begin(I2C_ADDR_IMU)) {
      ESP_LOGW(TAG, "motion: QMI8658 not found; turning is off");
      s_ok = false;
      return false;
    }
    s_imu.configAccelerometer(Qmi8658::ACC_RANGE_4G, Qmi8658::ACC_ODR_500Hz, Qmi8658::LPF_MODE_0);
    s_imu.configGyroscope(Qmi8658::GYR_RANGE_1024DPS, Qmi8658::GYR_ODR_448_4Hz, Qmi8658::LPF_MODE_2);
    s_imu.enableAccelerometer();
    s_imu.enableGyroscope();
  }
  s_ok = true;
  sleepMs(60);  // first samples after enabling are settling

  // Seed the gyro bias from a short still average (the puck usually boots lying on a desk). If
  // it's being handled, start from zero and let the running estimate catch up.
  float sum[3] = {0, 0, 0}, sumSq = 0, a[3], w[3];
  int n = 0;
  for (int i = 0; i < 120; i++) {
    if (readImu(a, w)) {
      for (int k = 0; k < 3; k++) sum[k] += w[k];
      sumSq += sqr(w[0]) + sqr(w[1]) + sqr(w[2]);
      n++;
      for (int k = 0; k < 3; k++) s_aLp[k] = a[k];
    }
    sleepMs(3);
  }
  if (n > 20) {
    const float mean[3] = {sum[0] / n, sum[1] / n, sum[2] / n};
    const float var = sumSq / n - (sqr(mean[0]) + sqr(mean[1]) + sqr(mean[2]));
    // Total variance over the three axes: up to ~1.4 °/s of noise per axis still counts as lying still
    if (var < 6.0f) {
      for (int k = 0; k < 3; k++) s_bias[k] = mean[k];
      s_biasLearned = true;
    }
  }
  s_lastUs = nowUs();
  return true;
}

void motionModeChanged(InputMode mode) {
  s_mode = mode;
  s_yawF = s_pitchF = s_ptrDx = s_ptrDy = 0;
}

void motionPoll(uint32_t now) {
  if (!s_ok) return;
  float a[3], w[3];
  if (!readImu(a, w)) return;
  float dt = (now - s_lastUs) * 1e-6f;
  s_lastUs = now;
  if (dt <= 0 || dt > 0.02f) dt = 0.003f;  // first sample, or a stall: assume the nominal period

  const float amag = sqrtf(sqr(a[0]) + sqr(a[1]) + sqr(a[2]));
  updateBias(w, a, dt);
  const float wc[3] = {w[0] - s_bias[0], w[1] - s_bias[1], w[2] - s_bias[2]};

  updateTurn(TURN_SIGN * wc[2], dt, now);
  updateUp(a, amag);
  updateFlat(a, amag, now);
  updateShake(amag, now);
  updateActivity(wc, amag, now);
  updatePointer(a, wc, amag, dt, now);
}

float motionHeading() { return s_heading.load(); }
bool motionLifted() { return s_lifted.load(); }

int motionDiag(char* buf, size_t len) {
  if (!s_ok) return snprintf(buf, len, "imu=0");
  // A fresh read rather than the input task's last sample: diagnostics shouldn't need its state
  float a[3], w[3];
  if (!readImu(a, w)) return snprintf(buf, len, "imu=1 read=fail");
  return snprintf(buf, len,
                  "imu=1 a=%.2f,%.2f,%.2f g=%.1f,%.1f,%.1f bias=%.1f,%.1f,%.1f%s lifted=%d active=%d turning=%d heading=%.0f",
                  a[0], a[1], a[2], w[0] - s_bias[0], w[1] - s_bias[1], w[2] - s_bias[2], s_bias[0], s_bias[1],
                  s_bias[2], s_biasLearned ? "" : "(learning)", (int)s_lifted.load(), (int)s_active, (int)s_turning,
                  s_heading.load());
}

}  // namespace detail
}  // namespace hal
