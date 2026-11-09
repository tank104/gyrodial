// CST9217 touch controller (Hynitron), driven exactly as SensorLib's TouchDrvCST92xx drives it.
// Only what touch.cpp uses: reset + info-block check, and reading up to two touch points.
#pragma once
#include <stddef.h>
#include <stdint.h>

namespace hal {
namespace detail {

class Cst9217 {
 public:
  void setPins(int rst, int irq) {
    rst_ = rst;
    irq_ = irq;
  }
  // Reset the chip, enter command mode (0xD101) and read its info block. False unless the
  // firmware checkcode, firmware presence and chip type (CST9217/CST9220) all check out.
  // Holds the bus for ~50 ms of waits; call it under an I2cGuard.
  bool begin(uint8_t addr);
  // Mirroring is done against these, as SensorLib does: x' = xMax - x (not xMax - 1 - x)
  void setMaxCoordinates(uint16_t x, uint16_t y) {
    xMax_ = x;
    yMax_ = y;
  }
  void setMirrorXY(bool mirrorX, bool mirrorY) {
    mirrorX_ = mirrorX;
    mirrorY_ = mirrorY;
  }
  // Touches now down (0..2), their coordinates into xs/ys (at most maxPoints of each written).
  // 0 on no touch, a missing ack byte, or a bus error. Call under an I2cGuard.
  uint8_t getPoint(int16_t* xs, int16_t* ys, uint8_t maxPoints);

  uint16_t chipType() const { return chipType_; }

 private:
  bool command(const uint8_t* cmd, size_t cmdLen, uint8_t* in, size_t inLen);
  void reset();

  uint8_t addr_ = 0;
  int rst_ = -1, irq_ = -1;
  uint16_t xMax_ = 0, yMax_ = 0;
  bool mirrorX_ = false, mirrorY_ = false;
  uint16_t chipType_ = 0;
};

}  // namespace detail
}  // namespace hal
