// AXP2101 PMU, driven exactly as XPowersLib's XPowersAXP2101 drives it. Only what power.cpp uses:
// chip check, ADC/charger setup, the PWR key interrupts, battery/USB status, and power-off.
#pragma once
#include <stdint.h>

namespace hal {
namespace detail {

class Axp2101 {
 public:
  // Interrupt masks: byte 0 = INTEN1/INTSTS1, byte 1 = INTEN2/INTSTS2, byte 2 = INTEN3/INTSTS3
  static constexpr uint32_t ALL_IRQ = 0xFFFFFFFFu;
  static constexpr uint32_t PKEY_LONG_IRQ = 1u << 10;
  static constexpr uint32_t PKEY_SHORT_IRQ = 1u << 11;
  enum ChargeVoltage : uint8_t { CHG_VOL_4V = 1, CHG_VOL_4V1, CHG_VOL_4V2, CHG_VOL_4V35, CHG_VOL_4V4 };
  enum IrqTime : uint8_t { IRQ_TIME_1S, IRQ_TIME_1S5, IRQ_TIME_2S, IRQ_TIME_2S5 };

  // IC type register reads 0x4A; then TS-pin (NTC) measuring off, as XPowersLib's begin does
  bool begin(uint8_t addr);

  bool disableTSPinMeasure();
  bool enableBattDetection();
  bool enableVbusVoltageMeasure();
  bool enableBattVoltageMeasure();
  bool enableSystemVoltageMeasure();
  bool setChargeTargetVoltage(ChargeVoltage v);

  bool enableIRQ(uint32_t mask);
  bool disableIRQ(uint32_t mask);
  void setIrqLevelTime(IrqTime t);           // the PWR key's long-press time
  void clearIrqStatus();                     // write 1s to all three status registers
  uint32_t getIrqStatus();                   // read and remember the three status registers
  // From the last getIrqStatus(), and only if that interrupt is enabled
  bool isPekeyShortPressIrq() const;
  bool isPekeyLongPressIrq() const;

  bool isBatteryConnect();
  uint16_t getBattVoltage();                 // mV; 0 with no battery
  bool isCharging();
  bool isVbusIn();
  void shutdown();                           // everything off but the RTC supply

 private:
  int readReg(uint8_t reg);                  // -1 on a bus error
  bool writeReg(uint8_t reg, uint8_t value);
  bool setBit(uint8_t reg, uint8_t bit);
  bool clrBit(uint8_t reg, uint8_t bit);
  bool getBit(uint8_t reg, uint8_t bit);     // false on a bus error
  bool setIrqEnables(uint32_t mask, bool enable);

  uint8_t addr_ = 0;
  uint8_t intEnable_[3] = {0, 0, 0};         // what we last wrote to INTEN1..3
  uint8_t intStatus_[3] = {0, 0, 0};         // INTSTS1..3 as last read
};

}  // namespace detail
}  // namespace hal
