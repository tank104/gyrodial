// Little sounds: ticks for detents, taps, and a chime when the focus timer ends. A low-priority
// task synthesizes each tone (sine partials with an attack/decay envelope) into the I2S stream.
// The speaker amplifier is only enabled while something is playing, so the speaker doesn't hiss.
#include <string.h>
#include <algorithm>
#include <atomic>
#include <math.h>
#include <driver/gpio.h>
#include <driver/i2s_std.h>
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/task.h>
#include "../config.h"
#include "hal_internal.h"

static const char* TAG = "hal";

namespace hal {
namespace detail {

namespace {

constexpr uint32_t kRate = 16000;
constexpr uint32_t kMclk = kRate * 256;
constexpr int kFrames = 128;              // per write (8 ms)
constexpr uint32_t kAmpOffAfterMs = 300;  // keep the amp on briefly between close tones
constexpr uint32_t kAmpSettleMs = 8;
constexpr float kMasterGain = 0.35f;      // of full scale: a desk toy, not an alarm

struct Note {
  float hz;
  uint16_t startMs, lenMs;
  float amp;
  float decay;     // envelope time constant in ms (exponential); 0 = flat with a short release
  bool buzzy;      // add odd harmonics (for the error buzz)
};

struct Sound {
  const Note* notes;
  uint8_t count;
};

constexpr Note kTick[] = {{2400, 0, 10, 0.35f, 2.5f, false}};
constexpr Note kTap[] = {{1250, 0, 35, 0.55f, 9, false}};
constexpr Note kSelect[] = {{1046, 0, 70, 0.6f, 25, false}, {1568, 20, 60, 0.25f, 20, false}};
constexpr Note kSuccess[] = {{660, 0, 110, 0.6f, 45, false}, {990, 95, 180, 0.6f, 70, false}};
constexpr Note kError[] = {{165, 0, 200, 0.55f, 0, true}};
constexpr Note kChime[] = {
    {1047, 0, 600, 0.55f, 180, false},
    {1319, 160, 600, 0.5f, 180, false},
    {1568, 320, 650, 0.5f, 200, false},
    {2093, 480, 900, 0.45f, 260, false},
};

Sound soundFor(Tone t) {
  switch (t) {
    case Tone::Tick: return {kTick, 1};
    case Tone::Tap: return {kTap, 1};
    case Tone::Select: return {kSelect, 2};
    case Tone::Success: return {kSuccess, 2};
    case Tone::Error: return {kError, 1};
    case Tone::Chime: return {kChime, 4};
  }
  return {kTap, 1};
}

i2s_chan_handle_t s_tx = nullptr;
QueueHandle_t s_queue = nullptr;
std::atomic<bool> s_enabled{true};
bool s_ok = false;
int16_t s_buf[kFrames * 2];  // stereo frames

float envelope(const Note& n, float tMs) {
  if (tMs < 0 || tMs >= n.lenMs) return 0;
  const float attack = tMs < 2.0f ? tMs / 2.0f : 1.0f;
  float body;
  if (n.decay > 0) body = expf(-tMs / n.decay);
  else body = 1.0f;
  const float release = (n.lenMs - tMs) < 6.0f ? (n.lenMs - tMs) / 6.0f : 1.0f;
  return attack * body * release;
}

void writeBuf() {
  size_t written;
  i2s_channel_write(s_tx, s_buf, sizeof(s_buf), &written, portMAX_DELAY);
}

void writeSilence(int blocks) {
  memset(s_buf, 0, sizeof(s_buf));
  for (int i = 0; i < blocks; i++) writeBuf();
}

void setAmp(bool on) { gpio_set_level((gpio_num_t)PIN_PA_EN, on ? 1 : 0); }

void play(const Sound& s) {
  uint32_t total = 0;
  for (int i = 0; i < s.count; i++) total = std::max<uint32_t>(total, s.notes[i].startMs + s.notes[i].lenMs);
  const uint32_t frames = total * kRate / 1000;
  float phase[4] = {0, 0, 0, 0};
  for (uint32_t f0 = 0; f0 < frames; f0 += kFrames) {
    for (int i = 0; i < kFrames; i++) {
      const float tMs = (float)(f0 + i) * 1000.0f / kRate;
      float v = 0;
      for (int k = 0; k < s.count && k < 4; k++) {
        const Note& n = s.notes[k];
        const float e = envelope(n, tMs - n.startMs);
        if (e <= 0) continue;
        phase[k] += 2.0f * (float)M_PI * n.hz / kRate;
        if (phase[k] > 2.0f * (float)M_PI) phase[k] -= 2.0f * (float)M_PI;
        float w = sinf(phase[k]);
        if (n.buzzy) w = 0.7f * w + 0.2f * sinf(3 * phase[k]) + 0.1f * sinf(5 * phase[k]);
        v += n.amp * e * w;
      }
      v *= kMasterGain;
      if (v > 1) v = 1;
      if (v < -1) v = -1;
      const int16_t sample = (int16_t)(v * 32767);
      s_buf[2 * i] = sample;
      s_buf[2 * i + 1] = sample;
    }
    writeBuf();
  }
}

void audioTask(void*) {
  bool ampOn = false;
  Tone t;
  for (;;) {
    const TickType_t wait = ampOn ? pdMS_TO_TICKS(kAmpOffAfterMs) : portMAX_DELAY;
    if (xQueueReceive(s_queue, &t, wait) != pdTRUE) {
      // quiet for a while: amp off. The channel keeps clocking (the codec wants MCLK) and plays
      // zeros once its buffers run dry (auto_clear).
      setAmp(false);
      ampOn = false;
      continue;
    }
    if (!s_enabled.load()) continue;
    if (!ampOn) {
      writeSilence(1);
      setAmp(true);
      vTaskDelay(pdMS_TO_TICKS(kAmpSettleMs));
      ampOn = true;
    }
    play(soundFor(t));
    writeSilence(2);  // let the tail out of the DMA buffers before the amp may switch off
  }
}

}  // namespace

bool audioOk() { return s_ok; }

bool audioBegin() {
  gpio_reset_pin((gpio_num_t)PIN_PA_EN);
  gpio_set_direction((gpio_num_t)PIN_PA_EN, GPIO_MODE_OUTPUT);
  setAmp(false);

  // I2S first: the codec wants MCLK running while it's configured.
  i2s_chan_config_t chan = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_0, I2S_ROLE_MASTER);
  chan.dma_desc_num = 4;
  chan.dma_frame_num = kFrames;
  chan.auto_clear = true;  // an underrun plays silence, not the last buffer on repeat
  if (i2s_new_channel(&chan, &s_tx, nullptr) != ESP_OK) {
    ESP_LOGE(TAG, "audio: I2S channel failed; sound is off");
    return false;
  }
  i2s_std_config_t std = {};
  std.clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(kRate);
  std.clk_cfg.mclk_multiple = I2S_MCLK_MULTIPLE_256;
  std.slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_16BIT, I2S_SLOT_MODE_STEREO);
  std.gpio_cfg.mclk = (gpio_num_t)PIN_I2S_MCLK;
  std.gpio_cfg.bclk = (gpio_num_t)PIN_I2S_BCLK;
  std.gpio_cfg.ws = (gpio_num_t)PIN_I2S_LRCK;
  std.gpio_cfg.dout = (gpio_num_t)PIN_I2S_DOUT;
  std.gpio_cfg.din = I2S_GPIO_UNUSED;  // the mics (ES7210) aren't used
  if (i2s_channel_init_std_mode(s_tx, &std) != ESP_OK || i2s_channel_enable(s_tx) != ESP_OK) {
    ESP_LOGE(TAG, "audio: I2S setup failed; sound is off");
    i2s_del_channel(s_tx);
    s_tx = nullptr;
    return false;
  }
  sleepMs(5);

  if (!es8311Begin(kRate, kMclk)) {
    i2s_channel_disable(s_tx);
    i2s_del_channel(s_tx);
    s_tx = nullptr;
    return false;
  }
  s_queue = xQueueCreate(8, sizeof(Tone));
  xTaskCreatePinnedToCore(audioTask, "audio", 3072, nullptr, 2, nullptr, 0);
  s_ok = true;
  return true;
}

}  // namespace detail

void beep(Tone t) {
  if (!detail::s_ok || !detail::s_enabled.load()) return;
  // Never block the caller; a full queue just drops this one (ticks come in bursts).
  xQueueSend(detail::s_queue, &t, 0);
}

void setSoundEnabled(bool on) { detail::s_enabled = on; }

}  // namespace hal
