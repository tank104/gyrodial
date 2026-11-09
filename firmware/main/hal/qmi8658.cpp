// QMI8658 IMU. Register sequence ported from lewisxhe/SensorLib (MIT), SensorQMI8658 0.3.3.
#include "qmi8658.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

static const char* TAG = "hal";

constexpr uint8_t kRegWhoAmI = 0x00;
constexpr uint8_t kRegCtrl1 = 0x02;
constexpr uint8_t kRegCtrl2 = 0x03;  // accelerometer: self-test | range (6:4) | ODR (3:0)
constexpr uint8_t kRegCtrl3 = 0x04;  // gyroscope: self-test | range (6:4) | ODR (3:0)
constexpr uint8_t kRegCtrl5 = 0x06;  // low-pass filters: gyro mode (6:5) + enable (4), accel mode (2:1) + enable (0)
constexpr uint8_t kRegCtrl7 = 0x08;  // sensor enables: gyro (1), accel (0)
constexpr uint8_t kRegCtrl8 = 0x09;
constexpr uint8_t kRegCtrl9 = 0x0A;  // host command
constexpr uint8_t kRegStatusInt = 0x2D;
constexpr uint8_t kRegAxL = 0x35;
constexpr uint8_t kRegGxL = 0x3B;
constexpr uint8_t kRegDqwL = 0x49;
constexpr uint8_t kRegDvxL = 0x51;
constexpr uint8_t kRegRstResult = 0x4D;
constexpr uint8_t kRegReset = 0x60;

constexpr uint8_t kWhoAmI = 0x05;
constexpr uint8_t kResetCmd = 0xB0;
constexpr uint8_t kResetDone = 0x80;
constexpr uint8_t kCmdAck = 0x00;
constexpr uint8_t kCmdCopyUsid = 0x10;
constexpr uint8_t kAccelLpfKeep = 0xF9;
constexpr uint8_t kGyroLpfKeep = 0x9F;

}  // namespace

int Qmi8658::readReg(uint8_t reg) {
  uint8_t v = 0;
  return i2cReadRegs(addr_, reg, &v, 1) ? v : -1;
}

bool Qmi8658::writeReg(uint8_t reg, uint8_t value) { return i2cWriteReg(addr_, reg, value); }

bool Qmi8658::updateReg(uint8_t reg, uint8_t keepMask, uint8_t orBits) {
  const int v = readReg(reg);
  if (v < 0) return false;
  return writeReg(reg, (uint8_t)((v & keepMask) | orBits));
}

// SensorLib writes 0xFF | bit when the read fails; here a failed read writes nothing.
bool Qmi8658::setBit(uint8_t reg, uint8_t bit) {
  const int v = readReg(reg);
  if (v < 0) return false;
  return writeReg(reg, (uint8_t)(v | (1u << bit)));
}

bool Qmi8658::clrBit(uint8_t reg, uint8_t bit) {
  const int v = readReg(reg);
  if (v < 0) return false;
  return writeReg(reg, (uint8_t)(v & ~(1u << bit)));
}

// CTRL9 handshake (CTRL8 = 0x80 makes STATUS_INT bit 7 the "command done" flag): write the
// command, wait for done, ack it, wait for done to clear. A failed status read ends a wait early.
bool Qmi8658::writeCommand(uint8_t cmd, uint32_t waitMs) {
  if (!writeReg(kRegCtrl9, cmd)) return false;
  uint32_t start = nowMs();
  int v;
  do {
    v = readReg(kRegStatusInt);
    sleepMs(1);
    if (nowMs() - start > waitMs) {
      ESP_LOGW(TAG, "imu: CTRL9 command %02x timed out", cmd);
      return false;
    }
  } while (v != -1 && !(v & 0x80));

  if (!writeReg(kRegCtrl9, kCmdAck)) return false;
  start = nowMs();
  do {
    v = readReg(kRegStatusInt);
    sleepMs(1);
    if (nowMs() - start > waitMs) {
      ESP_LOGW(TAG, "imu: CTRL9 done flag didn't clear (command %02x)", cmd);
      return false;
    }
  } while (v != -1 && (v & 0x80));
  return true;
}

bool Qmi8658::begin(uint8_t addr) {
  addr_ = addr;
  accelOn_ = gyroOn_ = false;

  // Soft reset; the result register reads 0x80 once it's done (up to ~15 ms)
  writeReg(kRegReset, kResetCmd);
  bool reset = false;
  int v = -1;
  const uint32_t start = nowMs();
  while (nowMs() - start < 500) {
    v = readReg(kRegRstResult);
    if (v == kResetDone) {
      setBit(kRegCtrl1, 6);  // ADDR_AI: register address auto-increment for the burst reads
      reset = true;
      break;
    }
    sleepMs(10);
  }
  if (!reset) {
    ESP_LOGW(TAG, "imu: QMI8658 reset failed (%d)", v);
    return false;
  }

  const uint8_t id = (uint8_t)readReg(kRegWhoAmI);
  if (id != kWhoAmI) {
    ESP_LOGW(TAG, "imu: WHO_AM_I %02x, not a QMI8658", id);
    return false;
  }

  writeReg(kRegCtrl8, 0x80);  // STATUS_INT bit 7 as the CTRL9 handshake
  writeCommand(kCmdCopyUsid);  // firmware revision and USID into the dQW / dVX registers
  uint8_t rev[3] = {0, 0, 0}, usid[6] = {0, 0, 0, 0, 0, 0};
  i2cReadRegs(addr_, kRegDqwL, rev, sizeof(rev));
  i2cReadRegs(addr_, kRegDvxL, usid, sizeof(usid));
  ESP_LOGI(TAG, "imu: QMI8658 fw %02X%02X%02X usid %02X%02X%02X%02X%02X%02X", rev[0], rev[1], rev[2], usid[0],
           usid[1], usid[2], usid[3], usid[4], usid[5]);
  return true;
}

bool Qmi8658::configAccelerometer(AccelRange range, AccelOdr odr, LpfMode lpf) {
  const bool wasOn = accelOn_;
  if (wasOn && clrBit(kRegCtrl7, 0)) accelOn_ = false;

  if (!updateReg(kRegCtrl2, 0x8F, (uint8_t)(range << 4))) return false;
  accelScale_ = (float)(2 << range) / 32768.0f;  // ±2, 4, 8, 16 g full scale
  if (!updateReg(kRegCtrl2, 0xF0, odr)) return false;

  if (lpf != LPF_OFF) {
    if (!updateReg(kRegCtrl5, kAccelLpfKeep, (uint8_t)(lpf << 1))) return false;
    setBit(kRegCtrl5, 0);
  } else {
    clrBit(kRegCtrl5, 0);
  }

  if (wasOn) enableAccelerometer();
  return true;
}

bool Qmi8658::configGyroscope(GyroRange range, GyroOdr odr, LpfMode lpf) {
  const bool wasOn = gyroOn_;
  if (wasOn && clrBit(kRegCtrl7, 1)) gyroOn_ = false;

  if (!updateReg(kRegCtrl3, 0x8F, (uint8_t)(range << 4))) return false;
  gyroScale_ = (float)(16 << range) / 32768.0f;  // ±16 ... 1024 °/s full scale
  if (!updateReg(kRegCtrl3, 0xF0, odr)) return false;

  if (lpf != LPF_OFF) {
    if (!updateReg(kRegCtrl5, kGyroLpfKeep, (uint8_t)(lpf << 5))) return false;
    setBit(kRegCtrl5, 4);
  } else {
    clrBit(kRegCtrl5, 4);
  }

  if (wasOn) enableGyroscope();
  return true;
}

bool Qmi8658::enableAccelerometer() {
  if (setBit(kRegCtrl7, 0)) accelOn_ = true;
  return accelOn_;
}

bool Qmi8658::enableGyroscope() {
  if (setBit(kRegCtrl7, 1)) gyroOn_ = true;
  return gyroOn_;
}

// Little-endian X, Y, Z (CTRL1's BE bit is left at its reset value, as SensorLib leaves it)
bool Qmi8658::readXyz(uint8_t reg, float scale, float& x, float& y, float& z) {
  uint8_t b[6];
  if (!i2cReadRegs(addr_, reg, b, sizeof(b))) return false;
  x = (int16_t)((b[1] << 8) | b[0]) * scale;
  y = (int16_t)((b[3] << 8) | b[2]) * scale;
  z = (int16_t)((b[5] << 8) | b[4]) * scale;
  return true;
}

bool Qmi8658::getAccelerometer(float& x, float& y, float& z) {
  return accelOn_ && readXyz(kRegAxL, accelScale_, x, y, z);
}

bool Qmi8658::getGyroscope(float& x, float& y, float& z) {
  return gyroOn_ && readXyz(kRegGxL, gyroScale_, x, y, z);
}

}  // namespace detail
}  // namespace hal
