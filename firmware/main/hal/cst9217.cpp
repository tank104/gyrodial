// CST9217 touch controller. Register sequence ported from lewisxhe/SensorLib (MIT),
// TouchDrvCST92xx 0.3.3, as it ran under Arduino-ESP32: every read is a write, a STOP, then a
// separate read (SensorLib's Wire backend sends a STOP before requestFrom).
#include "cst9217.h"
#include <string.h>
#include <driver/gpio.h>
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

static const char* TAG = "hal";

constexpr uint16_t kChipCst9217 = 0x9217;
constexpr uint16_t kChipCst9220 = 0x9220;
constexpr uint8_t kAck = 0xAB;
constexpr uint8_t kMaxFingers = 2;

inline uint32_t le32(const uint8_t* b) {
  return (uint32_t)b[0] | ((uint32_t)b[1] << 8) | ((uint32_t)b[2] << 16) | ((uint32_t)b[3] << 24);
}

}  // namespace

bool Cst9217::command(const uint8_t* cmd, size_t cmdLen, uint8_t* in, size_t inLen) {
  if (!i2cWrite(addr_, cmd, cmdLen)) return false;
  return i2cRead(addr_, in, inLen);
}

void Cst9217::reset() {
  if (rst_ < 0) return;
  gpio_set_level((gpio_num_t)rst_, 0);
  sleepMs(10);
  gpio_set_level((gpio_num_t)rst_, 1);
}

bool Cst9217::begin(uint8_t addr) {
  addr_ = addr;
  // initImpl(): reset pin an output, interrupt pin an input (it is never read: the CST9217
  // pulses it rather than holding it, so touch.cpp polls)
  if (rst_ >= 0) {
    gpio_config_t io = {};
    io.pin_bit_mask = 1ULL << rst_;
    io.mode = GPIO_MODE_OUTPUT;
    gpio_config(&io);
  }
  if (irq_ >= 0) {
    gpio_config_t io = {};
    io.pin_bit_mask = 1ULL << irq_;
    io.mode = GPIO_MODE_INPUT;
    gpio_config(&io);
  }

  // getAttribute()
  reset();
  sleepMs(30);  // wait for it to leave boot mode

  // Enter command mode (0xD101). SensorLib ignores the result, and never sends 0xD109 (normal
  // mode) afterwards: touch reports were read in this state on the real board.
  const uint8_t enterCmd[2] = {0xD1, 0x01};
  i2cWrite(addr_, enterCmd, sizeof(enterCmd));
  sleepMs(10);

  uint8_t buf[8];
  memset(buf, 0, sizeof(buf));
  const uint8_t cmdCheck[2] = {0xD1, 0xFC};
  command(cmdCheck, 2, buf, 4);
  const uint32_t checkcode = le32(buf);

  memset(buf, 0, sizeof(buf));
  const uint8_t cmdRes[2] = {0xD1, 0xF8};
  command(cmdRes, 2, buf, 4);
  const uint16_t resX = (uint16_t)(buf[0] | (buf[1] << 8));
  const uint16_t resY = (uint16_t)(buf[2] | (buf[3] << 8));

  memset(buf, 0, sizeof(buf));
  const uint8_t cmdType[2] = {0xD2, 0x04};
  command(cmdType, 2, buf, 4);
  chipType_ = (uint16_t)(buf[2] | (buf[3] << 8));
  const uint16_t projectId = (uint16_t)(buf[0] | (buf[1] << 8));

  memset(buf, 0, sizeof(buf));
  const uint8_t cmdFw[2] = {0xD2, 0x08};
  command(cmdFw, 2, buf, 8);
  const uint32_t fwVersion = le32(buf);
  const uint32_t checksum = le32(buf + 4);

  if (fwVersion == 0xA5A5A5A5) {
    ESP_LOGW(TAG, "touch: CST92xx has no firmware");
    return false;
  }
  if ((checkcode & 0xFFFF0000) != 0xCACA0000) {
    ESP_LOGW(TAG, "touch: CST92xx info read failed (checkcode %08lx)", (unsigned long)checkcode);
    return false;
  }
  if (chipType_ != kChipCst9217 && chipType_ != kChipCst9220) {
    ESP_LOGW(TAG, "touch: unexpected chip type %04x", chipType_);
    return false;
  }
  ESP_LOGI(TAG, "touch: CST%04x project %04x fw %08lx checksum %08lx res %ux%u", chipType_, projectId,
           (unsigned long)fwVersion, (unsigned long)checksum, resX, resY);
  return true;
}

uint8_t Cst9217::getPoint(int16_t* xs, int16_t* ys, uint8_t maxPoints) {
  if (!xs || !ys || !maxPoints) return 0;

  // Read report 0xD000: finger 0 in bytes 0..4, palm/gesture in 4's high nibble, finger count in
  // 5 (low 7 bits), ack byte 0xAB in 6, finger 1 in 7..11
  uint8_t buf[kMaxFingers * 5 + 5];
  memset(buf, 0, sizeof(buf));
  const uint8_t readCmd[2] = {0xD0, 0x00};
  if (!command(readCmd, 2, buf, sizeof(buf))) return 0;
  // Acknowledge the report (always, before looking at it)
  const uint8_t ackCmd[3] = {0xD0, 0x00, kAck};
  if (!i2cWrite(addr_, ackCmd, sizeof(ackCmd))) return 0;

  if (buf[6] != kAck) return 0;
  const uint8_t n = buf[5] & 0x7F;
  if (n == 0 || n > kMaxFingers) return 0;

  // A finger counts only with event 0x06 (pressed) and a valid id; otherwise it reads as (0,0)
  // with event 0, as SensorLib's zeroed point does.
  uint8_t evt0 = 0;
  for (uint8_t i = 0; i < n; i++) {
    const uint8_t* d = buf + i * 5 + (i == 0 ? 0 : 2);
    const uint8_t id = d[0] >> 4, evt = d[0] & 0x0F;
    int16_t x = 0, y = 0;
    uint8_t e = 0;
    if (evt == 0x06 && id < kMaxFingers) {
      x = (int16_t)((d[1] << 4) | (d[3] >> 4));
      y = (int16_t)((d[2] << 4) | (d[3] & 0x0F));
      e = evt;
    }
    if (i == 0) evt0 = e;
    if (i < maxPoints) {
      xs[i] = x;
      ys[i] = y;
    }
  }
  if (evt0 == 0) return 0;  // first finger lifted: a release

  // updateXY(): no swap; mirror against the max coordinates
  for (uint8_t i = 0; i < n && i < maxPoints; i++) {
    if (mirrorX_ && xMax_) xs[i] = (int16_t)(xMax_ - xs[i]);
    if (mirrorY_ && yMax_) ys[i] = (int16_t)(yMax_ - ys[i]);
  }
  return n;
}

}  // namespace detail
}  // namespace hal
