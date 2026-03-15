// TB6612FNG rover - serial motor protocol (UNO) + encoders on D9/D6 (PCINT) + speed (km/h) + battery voltage
//
// ========== PROTOCOL (115200 8N1, newline-terminated) ==========
//
// COMMANDS (host -> rover):
// +----------+------------------+------------------------------------------------+
// | Command  | Format           | Description                                    |
// +----------+------------------+------------------------------------------------+
// | DRIVE    | <bearing> <vel>   | bearing 0-359, velocity 0-9. 0=fwd,90=rt,180=bk |
// | RESET    | R                 | Zero encoder counts                            |
// | TELEM    | T                 | Request one telemetry line (reply = T,...)      |
// | ENC      | ENC 0|1 [kp [max]]| Straight-line correction (see ENC section)      |
// +----------+------------------+------------------------------------------------+
//
// ENC (encoder correction for bearing 0 or 180 only):
// +----------+------------------+------------------------------------------------+
// | Form     | Example         | Description                                    |
// +----------+------------------+------------------------------------------------+
// | ENC 0    | ENC 0           | Disable correction                              |
// | ENC 1    | ENC 1           | Enable with defaults (kp=50, max=35)             |
// | ENC 1 kp [max] | ENC 1 20  | Set kp; max unchanged. ENC 1 20 50 sets both.  |
// +----------+------------------+------------------------------------------------+
// kp: 1-100, P-gain (higher=stronger correction). max: 1-255, cap on correction.  |
// Every 50ms: correction = kp*(dL-dR), clamped by max. Slows ahead wheel, speeds   |
// lagging wheel to keep rover straight.                                           |
// +----------+------------------+------------------------------------------------+
//
// REPLIES (rover -> host):
// +----------+------------------+------------------------------------------------+
// | Trigger  | Reply            | Description                                    |
// +----------+------------------+------------------------------------------------+
// | Startup  | #HOMER <ver> READY | Ready, <ver>=firmware version                  |
// | Reset    | #R               | Encoders zeroed                                 |
// | T cmd    | le,re,dist_mm,vL,vR,vBat | Telemetry line (see table below)        |
// | ENC cmd  | #ENC en kp max   | Encoder config echo                             |
// | Parse err| ERR parse        | Invalid command                                 |
// +----------+------------------+------------------------------------------------+
//
// TELEMETRY (rover -> host, on T command):
// +------+------+--------+---------+---------+--------+
// |  le  |  re  | dist_mm| vLmmps  | vRmmps  | vBat   |
// +------+------+--------+---------+---------+--------+
// | le,re=cumul edges; dist_mm=avg distance (mm), reset on R; v=mm/s; vBat=Vin V |
// +------+------+--------+---------+---------+--------+
//
// WATCHDOG: 500ms without command -> motors stop
//
// ========================================================================

#include <Arduino.h>
#include <avr/interrupt.h>
#include <math.h>

//================ VERSION 8 =================
const char VERSION[] = "8";

// ===== Battery voltage (A0) =====
const int batteryPin = A0;
const float R1 = 47000.0;
const float R2 = 22000.0;
const float ADC_REF = 5.0;
const int ADC_MAX = 1023;

// ===== Motor A (LEFT) =====
const uint8_t PWMA = 5;
const uint8_t AIN1 = 8;
const uint8_t AIN2 = 12;

// ===== Motor B (RIGHT) =====
const uint8_t PWMB = 11;
const uint8_t BIN1 = 7;
const uint8_t BIN2 = 13;

// ===== Encoders (PCINT pins) =====
const uint8_t ENC_LEFT  = 9;  // D9
const uint8_t ENC_RIGHT = 6;  // D6

const unsigned long GLITCH_US = 0;

const float WHEEL_DIAMETER_CM = 6.5f;
const int   EDGES_PER_REV     = 10;   // RISING edges per rev; VERIFY
const float CM_PER_EDGE       = (PI * WHEEL_DIAMETER_CM) / EDGES_PER_REV;

// ----- Command / watchdog -----
unsigned long lastCmdMs = 0;
const unsigned long CMD_TIMEOUT_MS = 500;
int16_t targetL = 0;
int16_t targetR = 0;
int lastBearing = -1;  // from last command; -1 = not straight mode

// ----- Straight-line encoder correction (bearing 0 or 180 only) -----
// Protocol: ENC 0 = off, ENC 1 = on (defaults), ENC 1 <kp> <max> = on with params
const unsigned long STRAIGHT_SAMPLE_MS = 50;
static bool encCorrectionEnabled = true;
static int encKp = 50;   // P gain: lower = gentler
static int encMax = 35;  // cap to avoid overshoot

// ----- Encoder counters -----
volatile long leftEdges  = 0;
volatile long rightEdges = 0;

volatile unsigned long lastLeftUs  = 0;
volatile unsigned long lastRightUs = 0;

volatile uint8_t lastLeftState  = 0;
volatile uint8_t lastRightState = 0;

// ----- Telemetry -----
static unsigned long lastTelemMs = 0;
static long lastLEdges = 0;
static long lastREdges = 0;

static inline int16_t clamp_i16(long v) {
  if (v > 32767) return 32767;
  if (v < -32768) return -32768;
  return (int16_t)v;
}

// ---------- battery voltage ----------
float readBatteryVoltage() {
  const int samples = 20;
  long sum = 0;

  for (int i = 0; i < samples; i++) {
    sum += analogRead(batteryPin);
    delay(5);
  }

  float adc = sum / (float)samples;
  float vOut = adc * ADC_REF / ADC_MAX;
  float vIn = vOut * (R1 + R2) / R2;

  return vIn;
}

// ---------- robust line parser (no String) ----------
static char lineBuf[64];
static uint8_t lineLen = 0;
inline void resetLine() { lineLen = 0; lineBuf[0] = '\0'; }

bool readLine(char *out, size_t outSize) {
  while (Serial.available()) {
    char c = (char)Serial.read();
    if (c == '\r') continue;

    if (c == '\n') {
      if (lineLen >= outSize) lineLen = outSize - 1;
      out[lineLen] = '\0';
      resetLine();
      return true;
    }
    if (lineLen < outSize - 1) out[lineLen++] = c;
  }
  return false;
}

// ---------- motor helpers ----------
inline void setLeft(int16_t sp) {
  sp = constrain(sp, -255, 255);
  if (sp == 0) {
    digitalWrite(AIN1, LOW); digitalWrite(AIN2, LOW);
    analogWrite(PWMA, 0);
    return;
  }
  if (sp > 0) { digitalWrite(AIN1, HIGH); digitalWrite(AIN2, LOW);  analogWrite(PWMA, (uint8_t)sp); }
  else        { digitalWrite(AIN1, LOW);  digitalWrite(AIN2, HIGH); analogWrite(PWMA, (uint8_t)(-sp)); }
}

inline void setRight(int16_t sp) {
  sp = constrain(sp, -255, 255);
  if (sp == 0) {
    digitalWrite(BIN1, LOW); digitalWrite(BIN2, LOW);
    analogWrite(PWMB, 0);
    return;
  }
  if (sp > 0) { digitalWrite(BIN1, HIGH); digitalWrite(BIN2, LOW);  analogWrite(PWMB, (uint8_t)sp); }
  else        { digitalWrite(BIN1, LOW);  digitalWrite(BIN2, HIGH); analogWrite(PWMB, (uint8_t)(-sp)); }
}

inline void stopNow() {
  targetL = 0;
  targetR = 0;
  setLeft(0);
  setRight(0);
}

// ---- PCINT enable helper (uses Arduino core macros; avoids PBx/PDx assumptions) ----
static void enablePcint(uint8_t pin) {
  volatile uint8_t *pcicr = digitalPinToPCICR(pin);
  if (!pcicr) return; // pin does not support PCINT on this core/board
  *pcicr |= _BV(digitalPinToPCICRbit(pin));
  *digitalPinToPCMSK(pin) |= _BV(digitalPinToPCMSKbit(pin));
}

// fast pin read (still portable on AVR core)
static inline uint8_t readPinFast(uint8_t pin) {
  uint8_t bit = digitalPinToBitMask(pin);
  uint8_t port = digitalPinToPort(pin);
  volatile uint8_t *in = portInputRegister(port);
  return (*in & bit) ? 1 : 0;
}

static inline void handleEncodersPcint() {
  // sample both pins, count rising edges
  uint8_t sL = readPinFast(ENC_LEFT);
  uint8_t sR = readPinFast(ENC_RIGHT);

  if (sL && !lastLeftState) {
    unsigned long now = micros();
    if (!GLITCH_US || (now - lastLeftUs >= GLITCH_US)) {
      lastLeftUs = now;
      leftEdges++;
    }
  }
  if (sR && !lastRightState) {
    unsigned long now = micros();
    if (!GLITCH_US || (now - lastRightUs >= GLITCH_US)) {
      lastRightUs = now;
      rightEdges++;
    }
  }

  lastLeftState  = sL;
  lastRightState = sR;
}

// Define all PCINT vectors and route to the same handler.
// (On ATmega328P, PCINT1_vect may not exist; if your compiler complains, delete it.)
ISR(PCINT0_vect) { handleEncodersPcint(); }
#ifdef PCINT1_vect
ISR(PCINT1_vect) { handleEncodersPcint(); }
#endif
ISR(PCINT2_vect) { handleEncodersPcint(); }

void setup() {
  Serial.begin(115200);

  // Battery voltage (A0 - analog input, no extra setup needed)
  // A0 is default input on Arduino

  // Motor pins
  pinMode(PWMA, OUTPUT);
  pinMode(AIN1, OUTPUT);
  pinMode(AIN2, OUTPUT);

  pinMode(PWMB, OUTPUT);
  pinMode(BIN1, OUTPUT);
  pinMode(BIN2, OUTPUT);

  // Encoder pins
  pinMode(ENC_LEFT, INPUT_PULLUP);
  pinMode(ENC_RIGHT, INPUT_PULLUP);

  noInterrupts();
  lastLeftState  = readPinFast(ENC_LEFT);
  lastRightState = readPinFast(ENC_RIGHT);

  enablePcint(ENC_LEFT);
  enablePcint(ENC_RIGHT);
  interrupts();

  stopNow();
  lastCmdMs = millis();

  Serial.print("#HOMER 2.0 REV ");
  Serial.print(VERSION);
  Serial.println(" READY");
}

// bearing 0-359 -> differential: 0=forward, 90=right, 180=back, 270=left
// velocity 0-9 -> PWM scale
static void bearingVelocityToLR(int bearing, int velocity, int16_t *outL, int16_t *outR) {
  if (velocity <= 0) {
    *outL = 0;
    *outR = 0;
    return;
  }
  // scale 0-9 -> 0-150 (cap to avoid overheating)
  int v = map(velocity, 0, 9, 0, 150);
  float rad = (float)bearing * (PI / 180.0f);
  // differential: steer with sin, forward/back with cos
  float turn = sin(rad);
  float fwd  = cos(rad);
  float left  = fwd - turn;
  float right = fwd + turn;
  float mag = max((float)fabs(left), (float)fabs(right));
  if (mag > 1e-6f) {
    left  /= mag;
    right /= mag;
  }
  *outL = clamp_i16((long)round(left * v));
  *outR = clamp_i16((long)round(right * v));
}

void loop() {
  // ---- command handling: <bearing 0-359> <velocity 0-9> -----
  char line[64];
  if (readLine(line, sizeof(line))) {
    int bearing = 0, velocity = 0;

    // Reset encoders
    if ((line[0] == 'R' || line[0] == 'r') && (line[1] == '\0' || line[1] == ' ' || line[1] == '\r')) {
      noInterrupts();
      leftEdges = 0;
      rightEdges = 0;
      interrupts();
      lastLEdges = 0;
      lastREdges = 0;
      lastCmdMs = millis();
      Serial.println("#R");
    } else if ((line[0] == 'T' || line[0] == 't') && (line[1] == '\0' || line[1] == ' ' || line[1] == '\r')) {
      // Telemetry on request
      lastCmdMs = millis();
      unsigned long nowMs = millis();
      unsigned long dtMs = (lastTelemMs != 0) ? (nowMs - lastTelemMs) : 1;
      if (dtMs == 0) dtMs = 1;

      noInterrupts();
      long l = leftEdges;
      long r = rightEdges;
      interrupts();

      long dL = l - lastLEdges;
      long dR = r - lastREdges;
      lastLEdges = l;
      lastREdges = r;
      lastTelemMs = nowMs;

      float dt_s = dtMs / 1000.0f;
      float vL_mmps_f = (dL * CM_PER_EDGE * 10.0f) / dt_s;
      float vR_mmps_f = (dR * CM_PER_EDGE * 10.0f) / dt_s;
      long vL_mmps = lroundf(vL_mmps_f);
      long vR_mmps = lroundf(vR_mmps_f);

      // avg distance (mm) from both wheels; resets with R
      long dist_mm = lroundf((l + r) / 2.0f * CM_PER_EDGE * 10.0f);

      float voltage = readBatteryVoltage();

      Serial.print(l);
      Serial.print(",");
      Serial.print(r);
      Serial.print(",");
      Serial.print(dist_mm);
      Serial.print(",");
      Serial.print(clamp_i16(vL_mmps));
      Serial.print(",");
      Serial.print(clamp_i16(vR_mmps));
      Serial.print(",");
      Serial.println(voltage, 2);
    } else if (line[0] == 'E' && line[1] == 'N' && line[2] == 'C' && (line[3] == '\0' || line[3] == ' ')) {
      int en = 0, kp = 0, maxc = 0;
      int n = sscanf(line + 3, "%d %d %d", &en, &kp, &maxc);
      if (n >= 1) {
        encCorrectionEnabled = (en != 0);
        if (n >= 2 && encCorrectionEnabled && kp > 0)
          encKp = constrain(kp, 1, 100);
        if (n >= 3 && encCorrectionEnabled && maxc > 0)
          encMax = constrain(maxc, 1, 255);
        lastCmdMs = millis();
        Serial.print("#ENC ");
        Serial.print(encCorrectionEnabled ? 1 : 0);
        Serial.print(" ");
        Serial.print(encKp);
        Serial.print(" ");
        Serial.println(encMax);
      } else {
        Serial.println("ERR ENC parse");
      }
    } else if (sscanf(line, "%d %d", &bearing, &velocity) == 2) {
      bearing = constrain(bearing, 0, 359);
      velocity = constrain(velocity, 0, 9);
      lastBearing = bearing;
      bearingVelocityToLR(bearing, velocity, &targetL, &targetR);
      lastCmdMs = millis();
    } else {
      Serial.println("ERR parse");
    }
  }

  // ---- watchdog + motor apply ----
  if (millis() - lastCmdMs > CMD_TIMEOUT_MS) {
    stopNow();
  } else {
    int16_t appliedL = targetL;
    int16_t appliedR = targetR;

    // Encoder feedback for straight driving (0 or 180 deg only)
    if (encCorrectionEnabled && (lastBearing == 0 || lastBearing == 180) && (targetL != 0 || targetR != 0)) {
      static long lastEncL = 0, lastEncR = 0;
      static unsigned long lastEncT = 0;
      static int16_t straightCorrection = 0;

      unsigned long now = millis();
      noInterrupts();
      long le = leftEdges;
      long re = rightEdges;
      interrupts();

      unsigned long dt = (lastEncT != 0) ? (now - lastEncT) : 0;
      if (dt >= STRAIGHT_SAMPLE_MS && dt < 500) {
        long dL = le - lastEncL;
        long dR = re - lastEncR;
        long error = dL - dR;
        straightCorrection = (int16_t)constrain((long)encKp * error, -(long)encMax, (long)encMax);
        lastEncL = le;
        lastEncR = re;
        lastEncT = now;
      } else if (dt >= 500 || lastEncT == 0) {
        straightCorrection = 0;
        lastEncL = le;
        lastEncR = re;
        lastEncT = now;
      }

      if (lastBearing == 0) {
        appliedL -= straightCorrection;
        appliedR += straightCorrection;
      } else {
        appliedL += straightCorrection;
        appliedR -= straightCorrection;
      }
    }

    setLeft(appliedL);
    setRight(appliedR);
  }
}
