// ES8311 speaker codec: just enough to play 16-bit audio from the ESP32's I2S as slave.
//
// The register sequence is ported from Espressif's es8311 driver (Apache-2.0,
// SPDX-FileCopyrightText: 2015-2022 Espressif Systems (Shanghai) CO LTD), as shipped in Waveshare's
// ESP32-S3-Touch-AMOLED-1.75C examples, reduced to one clock setup: MCLK from the MCLK pin at 256 fs.
#include "../config.h"
#include "hal_internal.h"

static const char* TAG = "hal";

namespace hal {
namespace detail {

namespace {

bool writeReg(uint8_t reg, uint8_t val) { return i2cWriteReg(I2C_ADDR_ES8311, reg, val); }
bool readReg(uint8_t reg, uint8_t* val) { return i2cReadRegs(I2C_ADDR_ES8311, reg, val, 1); }

// One row of Espressif's coefficient table: {mclk, rate, pre_div, pre_multi, adc_div, dac_div,
// fs_mode, lrck_h, lrck_l, bclk_div, adc_osr, dac_osr}
struct Coeff {
  uint32_t mclk, rate;
  uint8_t preDiv, preMulti, adcDiv, dacDiv, fsMode, lrckH, lrckL, bclkDiv, adcOsr, dacOsr;
};
constexpr Coeff kCoeffs[] = {
    {4096000, 16000, 0x01, 0x00, 0x01, 0x01, 0x00, 0x00, 0xff, 0x04, 0x10, 0x10},
};

}  // namespace

bool es8311Begin(uint32_t sampleRate, uint32_t mclkHz) {
  const Coeff* c = nullptr;
  for (const Coeff& k : kCoeffs)
    if (k.mclk == mclkHz && k.rate == sampleRate) c = &k;
  if (!c) return false;

  I2cGuard lock;
  uint8_t id1 = 0, id2 = 0;
  if (!readReg(0xFD, &id1) || !readReg(0xFE, &id2) || id1 != 0x83 || id2 != 0x11) {
    ESP_LOGW(TAG, "audio: ES8311 not found (id %02x %02x)", id1, id2);
    return false;
  }

  bool ok = true;
  // reset, then power on as slave
  ok &= writeReg(0x00, 0x1F);
  sleepMs(5);
  ok &= writeReg(0x00, 0x00);
  ok &= writeReg(0x00, 0x80);

  // clocks: all on, MCLK from the MCLK pin, not inverted
  ok &= writeReg(0x01, 0x3F);
  ok &= writeReg(0x02, (uint8_t)(((c->preDiv - 1) << 5) | (c->preMulti << 3)));
  ok &= writeReg(0x03, (uint8_t)((c->fsMode << 6) | c->adcOsr));
  ok &= writeReg(0x04, c->dacOsr);
  ok &= writeReg(0x05, (uint8_t)(((c->adcDiv - 1) << 4) | (c->dacDiv - 1)));
  ok &= writeReg(0x06, (uint8_t)(c->bclkDiv < 19 ? c->bclkDiv - 1 : c->bclkDiv));  // SCLK not inverted
  ok &= writeReg(0x07, c->lrckH);
  ok &= writeReg(0x08, c->lrckL);

  // serial port: slave, I2S, 16-bit in and out
  ok &= writeReg(0x09, 0x0C);
  ok &= writeReg(0x0A, 0x0C);

  // analog power-up, DAC on, output to the headphone/line driver (feeds the speaker amp)
  ok &= writeReg(0x0D, 0x01);
  ok &= writeReg(0x0E, 0x02);
  ok &= writeReg(0x12, 0x00);
  ok &= writeReg(0x13, 0x10);
  ok &= writeReg(0x1C, 0x6A);
  ok &= writeReg(0x37, 0x08);  // bypass the DAC equalizer
  ok &= writeReg(0x31, 0x00);  // DAC unmuted
  ok &= writeReg(0x32, 0xBF);  // DAC volume 0 dB; loudness is set by the samples
  if (!ok) ESP_LOGE(TAG, "audio: ES8311 register writes failed");
  return ok;
}

void es8311SetVolume(uint8_t reg32) {
  I2cGuard lock;
  writeReg(0x32, reg32);
}

}  // namespace detail
}  // namespace hal
