// QMI8658 6-axis IMU, driven exactly as SensorLib's SensorQMI8658 drives it. Only what motion.cpp
// uses: reset + WHO_AM_I, accelerometer/gyroscope range, rate and low-pass setup, and reads.
#pragma once
#include <stdint.h>

namespace hal {
namespace detail {

class Qmi8658 {
 public:
  // Register encodings (the enum values are the bits written)
  enum AccelRange : uint8_t { ACC_RANGE_2G, ACC_RANGE_4G, ACC_RANGE_8G, ACC_RANGE_16G };
  // With both sensors on (6DOF), the accelerometer runs at the gyro's rate whatever is set here
  enum AccelOdr : uint8_t {
    ACC_ODR_1000Hz = 3, ACC_ODR_500Hz, ACC_ODR_250Hz, ACC_ODR_125Hz, ACC_ODR_62_5Hz, ACC_ODR_31_25Hz
  };
  enum GyroRange : uint8_t {
    GYR_RANGE_16DPS, GYR_RANGE_32DPS, GYR_RANGE_64DPS, GYR_RANGE_128DPS, GYR_RANGE_256DPS,
    GYR_RANGE_512DPS, GYR_RANGE_1024DPS
  };
  enum GyroOdr : uint8_t {
    GYR_ODR_7174_4Hz, GYR_ODR_3587_2Hz, GYR_ODR_1793_6Hz, GYR_ODR_896_8Hz, GYR_ODR_448_4Hz,
    GYR_ODR_224_2Hz, GYR_ODR_112_1Hz, GYR_ODR_56_05Hz, GYR_ODR_28_025Hz
  };
  enum LpfMode : uint8_t { LPF_MODE_0, LPF_MODE_1, LPF_MODE_2, LPF_MODE_3, LPF_OFF };

  // Soft reset (waits up to 500 ms for it), address auto-increment on, WHO_AM_I == 0x05, then
  // the CTRL9 handshake set up and the chip's firmware revision/USID read (logged).
  // Call under an I2cGuard.
  bool begin(uint8_t addr);
  bool configAccelerometer(AccelRange range, AccelOdr odr, LpfMode lpf);
  bool configGyroscope(GyroRange range, GyroOdr odr, LpfMode lpf);
  bool enableAccelerometer();
  bool enableGyroscope();
  // The latest output registers, scaled to g / deg/s. There is no data-ready check (SensorLib has
  // none either): false only if that sensor isn't enabled or the read failed.
  bool getAccelerometer(float& x, float& y, float& z);
  bool getGyroscope(float& x, float& y, float& z);

 private:
  int readReg(uint8_t reg);                              // -1 on a bus error
  bool writeReg(uint8_t reg, uint8_t value);
  bool updateReg(uint8_t reg, uint8_t keepMask, uint8_t orBits);  // read, & keepMask, | orBits, write
  bool setBit(uint8_t reg, uint8_t bit);
  bool clrBit(uint8_t reg, uint8_t bit);
  bool writeCommand(uint8_t cmd, uint32_t waitMs = 1000);
  bool readXyz(uint8_t reg, float scale, float& x, float& y, float& z);

  uint8_t addr_ = 0;
  float accelScale_ = 0, gyroScale_ = 0;
  bool accelOn_ = false, gyroOn_ = false;
};

}  // namespace detail
}  // namespace hal
