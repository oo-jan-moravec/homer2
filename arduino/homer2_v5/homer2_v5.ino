// TB6612FNG rover - serial motor protocol (UNO) + encoders on D9/D6 (PCINT) + speed (km/h)

#include <Arduino.h>
#include <avr/interrupt.h>



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

// ----- Encoder counters -----
volatile long leftEdges  = 0;
volatile long rightEdges = 0;

volatile unsigned long lastLeftUs  = 0;
volatile unsigned long lastRightUs = 0;

volatile uint8_t lastLeftState  = 0;
volatile uint8_t lastRightState = 0;

// ----- Telemetry -----
const unsigned long TELEMETRY_EVERY_MS = 200;
static uint16_t telemSeq = 0;

static inline int16_t clamp_i16(long v) {
  if (v > 32767) return 32767;
  if (v < -32768) return -32768;
  return (int16_t)v;
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

  Serial.println("#HOMER 2.0 READY");
}

void loop() {
  // ---- command handling ----
  char line[64];
  if (readLine(line, sizeof(line))) {
    char *p = line;
    while (*p == ' ' || *p == '\t') p++;

    if (*p == 'S') {
      stopNow();
      lastCmdMs = millis();
    } else if (*p == 'M') {
      int l = 0, r = 0;
      if (sscanf(p, "M %d %d", &l, &r) == 2) {
        targetL = constrain(l, -255, 255);
        targetR = constrain(r, -255, 255);
        lastCmdMs = millis();
      } else {
        Serial.println("ERR parse");
      }
    }
  }

  // ---- watchdog + motor apply ----
  if (millis() - lastCmdMs > CMD_TIMEOUT_MS) {
    stopNow();
  } else {
    setLeft(targetL);
    setRight(targetR);
  }

  // ---- telemetry (CSV) ----
  static unsigned long lastTelemMs = 0;
  static long lastLEdges = 0;
  static long lastREdges = 0;

  unsigned long nowMs = millis();
  if (nowMs - lastTelemMs >= TELEMETRY_EVERY_MS) {
    unsigned long dtMs = nowMs - lastTelemMs;
    lastTelemMs = nowMs;

    noInterrupts();
    long l = leftEdges;
    long r = rightEdges;
    interrupts();

    long dL = l - lastLEdges;
    long dR = r - lastREdges;
    lastLEdges = l;
    lastREdges = r;

    float dt_s = dtMs / 1000.0f;
    if (dt_s <= 0.0f) dt_s = 0.001f;

    // mm/s (avoid floats on the wire)
    float vL_mmps_f = (dL * CM_PER_EDGE * 10.0f) / dt_s; // cm->mm (x10)
    float vR_mmps_f = (dR * CM_PER_EDGE * 10.0f) / dt_s;

    long vL_mmps = lroundf(vL_mmps_f);
    long vR_mmps = lroundf(vR_mmps_f);

    // rpm * 10
    float revL = (float)dL / (float)EDGES_PER_REV;
    float revR = (float)dR / (float)EDGES_PER_REV;
    long rpmL_x10 = lroundf((revL / dt_s) * 600.0f); // *60*10
    long rpmR_x10 = lroundf((revR / dt_s) * 600.0f);

    // Output: T,seq,ms,le,re,dL,dR,vLmmps,vRmmps,rpmLx10,rpmRx10
    Serial.print("T,");
    Serial.print(telemSeq++);
    Serial.print(",");
    Serial.print(nowMs);
    Serial.print(",");
    Serial.print(l);
    Serial.print(",");
    Serial.print(r);
    Serial.print(",");
    Serial.print(clamp_i16(dL));
    Serial.print(",");
    Serial.print(clamp_i16(dR));
    Serial.print(",");
    Serial.print(clamp_i16(vL_mmps));
    Serial.print(",");
    Serial.print(clamp_i16(vR_mmps));
    Serial.print(",");
    Serial.print(clamp_i16(rpmL_x10));
    Serial.print(",");
    Serial.println(clamp_i16(rpmR_x10));
  }

 
}