// AXP2101 PMU. Register sequence ported from lewisxhe/XPowersLib (MIT), XPowersAXP2101 0.2.6.
#include "axp2101.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

constexpr uint8_t kRegStatus1 = 0x00;        // 5: VBUS good, 3: battery present
constexpr uint8_t kRegStatus2 = 0x01;        // 7:5 battery current direction (1 = charging); 3 (see isVbusIn)
constexpr uint8_t kRegIcType = 0x03;
constexpr uint8_t kRegCommonConfig = 0x10;   // 0: soft power-off
constexpr uint8_t kRegIrqLevelCtrl = 0x27;   // 5:4 IRQ (long press) time
constexpr uint8_t kRegAdcChannelCtrl = 0x30; // 3: VSYS, 2: VBUS, 1: TS pin, 0: battery voltage
constexpr uint8_t kRegAdcBattH = 0x34;       // battery voltage, 13 bits, 1 mV/LSB
constexpr uint8_t kRegAdcBattL = 0x35;
constexpr uint8_t kRegIntEn1 = 0x40;
constexpr uint8_t kRegIntSts1 = 0x48;
constexpr uint8_t kRegCvChgVol = 0x64;       // 2:0 charge target voltage
constexpr uint8_t kRegBatDetCtrl = 0x68;     // 0: battery detection

constexpr uint8_t kChipId = 0x4A;

}  // namespace

int Axp2101::readReg(uint8_t reg) {
  uint8_t v = 0;
  return i2cReadRegs(addr_, reg, &v, 1) ? v : -1;
}

bool Axp2101::writeReg(uint8_t reg, uint8_t value) { return i2cWriteReg(addr_, reg, value); }

bool Axp2101::setBit(uint8_t reg, uint8_t bit) {
  const int v = readReg(reg);
  if (v < 0) return false;
  return writeReg(reg, (uint8_t)(v | (1u << bit)));
}

bool Axp2101::clrBit(uint8_t reg, uint8_t bit) {
  const int v = readReg(reg);
  if (v < 0) return false;
  return writeReg(reg, (uint8_t)(v & ~(1u << bit)));
}

bool Axp2101::getBit(uint8_t reg, uint8_t bit) {
  const int v = readReg(reg);
  return v >= 0 && (v & (1 << bit));
}

bool Axp2101::begin(uint8_t addr) {
  addr_ = addr;
  if (readReg(kRegIcType) != kChipId) return false;
  disableTSPinMeasure();
  return true;
}

bool Axp2101::disableTSPinMeasure() { return clrBit(kRegAdcChannelCtrl, 1); }
bool Axp2101::enableBattDetection() { return setBit(kRegBatDetCtrl, 0); }
bool Axp2101::enableVbusVoltageMeasure() { return setBit(kRegAdcChannelCtrl, 2); }
bool Axp2101::enableBattVoltageMeasure() { return setBit(kRegAdcChannelCtrl, 0); }
bool Axp2101::enableSystemVoltageMeasure() { return setBit(kRegAdcChannelCtrl, 3); }

bool Axp2101::setChargeTargetVoltage(ChargeVoltage v) {
  if (v > CHG_VOL_4V4) return false;
  const int r = readReg(kRegCvChgVol);
  if (r < 0) return false;
  return writeReg(kRegCvChgVol, (uint8_t)((r & 0xF8) | v));
}

// Read-modify-write of each INTENx register the mask touches. (XPowersLib would carry on with
// 0xFF when the read fails, enabling every interrupt in that register; here that register is
// left alone.)
bool Axp2101::setIrqEnables(uint32_t mask, bool enable) {
  bool ok = true;
  for (int i = 0; i < 3; i++) {
    const uint8_t bits = (uint8_t)(mask >> (8 * i));
    if (!bits) continue;
    const int cur = readReg((uint8_t)(kRegIntEn1 + i));
    if (cur < 0) {
      ok = false;
      continue;
    }
    intEnable_[i] = enable ? (uint8_t)(cur | bits) : (uint8_t)(cur & ~bits);
    ok = writeReg((uint8_t)(kRegIntEn1 + i), intEnable_[i]) && ok;
  }
  return ok;
}

bool Axp2101::enableIRQ(uint32_t mask) { return setIrqEnables(mask, true); }
bool Axp2101::disableIRQ(uint32_t mask) { return setIrqEnables(mask, false); }

void Axp2101::setIrqLevelTime(IrqTime t) {
  const int v = readReg(kRegIrqLevelCtrl);
  if (v < 0) return;
  writeReg(kRegIrqLevelCtrl, (uint8_t)((v & 0xCF) | (t << 4)));
}

void Axp2101::clearIrqStatus() {
  for (int i = 0; i < 3; i++) {
    writeReg((uint8_t)(kRegIntSts1 + i), 0xFF);
    intStatus_[i] = 0;
  }
}

// XPowersLib stores 0xFF for a register it failed to read, which reads as every interrupt
// pending (a phantom PWR-key long press on one bus glitch); here a failed read counts as none.
uint32_t Axp2101::getIrqStatus() {
  for (int i = 0; i < 3; i++) {
    const int v = readReg((uint8_t)(kRegIntSts1 + i));
    intStatus_[i] = v < 0 ? 0 : (uint8_t)v;
  }
  return ((uint32_t)intStatus_[0] << 16) | ((uint32_t)intStatus_[1] << 8) | intStatus_[2];
}

bool Axp2101::isPekeyShortPressIrq() const {
  const uint8_t m = (uint8_t)(PKEY_SHORT_IRQ >> 8);
  return (intEnable_[1] & m) && (intStatus_[1] & m) == m;
}

bool Axp2101::isPekeyLongPressIrq() const {
  const uint8_t m = (uint8_t)(PKEY_LONG_IRQ >> 8);
  return (intEnable_[1] & m) && (intStatus_[1] & m) == m;
}

bool Axp2101::isBatteryConnect() { return getBit(kRegStatus1, 3); }

uint16_t Axp2101::getBattVoltage() {
  if (!isBatteryConnect()) return 0;
  const int h = readReg(kRegAdcBattH);
  const int l = readReg(kRegAdcBattL);
  if (h < 0 || l < 0) return 0;
  return (uint16_t)(((h & 0x1F) << 8) | l);
}

bool Axp2101::isCharging() {
  const int v = readReg(kRegStatus2);
  return v >= 0 && (v >> 5) == 0x01;
}

// STATUS2 bit 3 clear and STATUS1 "VBUS good", as XPowersLib tests it
bool Axp2101::isVbusIn() { return !getBit(kRegStatus2, 3) && getBit(kRegStatus1, 5); }

void Axp2101::shutdown() { setBit(kRegCommonConfig, 0); }

}  // namespace detail
}  // namespace hal
