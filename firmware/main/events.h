// Input events: what the hardware layer (hal) hands the faces (ui).
#pragma once
#include <stdint.h>

enum class Ev : uint8_t {
  Turn,        // value = degrees turned since the last Turn event, clockwise positive (seen from the screen)
  Tap,         // short touch on the glass; x, y = where
  DoubleTap,   // second tap within 300 ms (the first Tap was already delivered)
  LongPress,   // finger held still for 600 ms; x, y
  Swipe,       // quick flick; dir = 'l' 'r' 'u' 'd'
  TouchDown,   // x, y
  TouchMove,   // x, y (at most ~60 Hz)
  TouchUp,     // x, y
  Button,      // side button (BOOT or PWR) short press
  ButtonLong,  // side button held 800 ms
  Lifted,      // picked up off the desk (screen no longer facing up)
  Laid,        // put back down flat
  Shake,       // shaken (dice)
  Wake,        // motion or touch while the screen was off/dim (the event that woke it is not delivered)
};

struct InputEvent {
  Ev type;
  float value;      // Turn: degrees
  int16_t x, y;     // touch position in screen pixels (0..465), 0,0 = top-left
  char dir;         // Swipe direction
  uint32_t ms;      // nowMs() when it happened
};

// What the input layer does with the glass and the motion on its own, without the faces.
enum class InputMode : uint8_t {
  Ui,        // every gesture goes to the faces as events
  Trackpad,  // flat: the glass moves the mouse, turning scrolls, tap clicks;
             // lifted: the hand's rotation moves the mouse (pointer), tap clicks.
             // Events are still posted to the faces for drawing, but the faces must not forward them.
};
