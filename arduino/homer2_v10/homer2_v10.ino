// TB6612FNG rover – serial motor protocol (UNO) + encoders on D9/D6 (PCINT)
// + speed (mm/s) + battery voltage + HW watchdog + non-blocking ADC
//
// v10: robustness overhaul (fixes intermittent motor-stop bug in v9)
//   - Non-blocking battery ADC  (eliminates the 100 ms stall every 5 s)
//   - AVR hardware watchdog      (auto-resets MCU on any firmware hang)
//   - Timer-register guard       (detects + repairs PWM config corruption)
//   - Serial parser hardened     (empty lines, null bytes, overflow)
//   - Encoder-correction state   (properly reset on bearing change)
//   - Watchdog-fire diagnostic   (#WDSTOP printed once so host can see it)
// v10.1: HC-SR04 on D2=TRIG, D3=ECHO; non-blocking range; CSV field us_mm;
//   auto-telemetry interval 200 ms (was 5 s)
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
// kp: 1-100, P-gain (higher=stronger correction). max: 1-255, cap on correction.
// Every 50 ms: correction = kp*(dL-dR), clamped by max. Slows ahead wheel, speeds
// lagging wheel to keep rover straight.
//
// REPLIES (rover -> host):
// +----------+------------------+------------------------------------------------+
// | Trigger  | Reply            | Description                                    |
// +----------+------------------+------------------------------------------------+
// | Startup  | #HOMER <ver> READY | Ready, <ver>=firmware version                  |
// | Reset    | #R               | Encoders zeroed                                 |
// | T cmd    | le,re,dist_mm,vL,vR,vBat,us_mm | Telemetry (us_mm=ultrasonic mm, -1=invalid) |
// | Auto     | same CSV every 200 ms    | Unsolicited telemetry; no wdog refresh       |
// | ENC cmd  | #ENC en kp max   | Encoder config echo                             |
// | WD stop  | #WDSTOP          | Watchdog fired, motors stopped (one-shot)       |
// | Parse err| ERR parse        | Invalid command                                 |
// | Timer fix| #WARN timer-fix  | Timer register corruption detected + repaired   |
// +----------+------------------+------------------------------------------------+
//
// TELEMETRY (rover -> host, on T command or auto every 200 ms):
// le,re=cumul edges; dist_mm=encoder odometer (mm), reset on R; v=mm/s;
// vBat=pack V (11S NiMH via divider); us_mm=HC-SR04 range (mm), -1=no echo/out of range
//
// WATCHDOG: 500 ms without command -> motors stop
//
// ========================================================================

#include <Arduino.h>
#include <avr/interrupt.h>
#include <avr/wdt.h>
#include <math.h>
#include <string.h>

// ================ VERSION 10 =================
const char VERSION[] = "10.1";

// #define DEBUG_DRIVE 1

// ===== Timing constants =====
const unsigned long AUTO_TELEM_MS  = 200;
const unsigned long CMD_TIMEOUT_MS = 500;

// ===== Battery voltage (A0) — 11-cell NiMH pack =====
const uint8_t BATTERY_PIN = A0;
const float   R1          = 47000.0f;
const float   R2          = 22000.0f;
const float   ADC_REF     = 5.0f;
const int     ADC_MAX     = 1023;
const float   BATTERY_VOLTAGE_CALIB = 1.0f;

// Non-blocking ADC: one analogRead() per loop, averaged over ADC_WINDOW samples
const uint8_t       ADC_WINDOW      = 20;
const unsigned long ADC_SAMPLE_MS   = 5;   // min interval between samples
static float        cachedBatteryV  = 0.0f;
static long         adcSum          = 0;
static uint8_t      adcCount        = 0;
static unsigned long lastAdcSampleMs = 0;

// ===== Motor A (LEFT) =====
const uint8_t PWMA = 5;
const uint8_t AIN1 = 8;
const uint8_t AIN2 = 12;

// ===== Motor B (RIGHT) =====
const uint8_t PWMB = 11;
const uint8_t BIN1 = 7;
const uint8_t BIN2 = 13;

// Set to an actual pin number if STBY is wired to the Arduino; -1 = not used
const int8_t STBY_PIN = -1;

// ===== HC-SR04 ultrasonic (front obstacle range) =====
const uint8_t US_TRIG_PIN = 2;   // D2
const uint8_t US_ECHO_PIN = 3;   // D3
const unsigned long US_SAMPLE_INTERVAL_MS = 200;
// Echo timeout ~4 m round-trip; mm ≈ pulse_us * 10 / 58
const unsigned long US_ECHO_TIMEOUT_US = 24000;
const unsigned long US_PULSE_MIN_US    = 120;   // below ~2 cm, unreliable

enum UsPhase { US_IDLE, US_WAIT_HIGH, US_WAIT_LOW };
static UsPhase       usState          = US_IDLE;
static unsigned long usPhaseStartUs   = 0;
static unsigned long usPulseRiseUs    = 0;
static unsigned long usLastCompleteMs = 0;
static int           usCachedMm       = -1;

// ===== Encoders (PCINT pins) =====
const uint8_t ENC_LEFT  = 9;   // D9  (PB1, PCINT0 group)
const uint8_t ENC_RIGHT = 6;   // D6  (PD6, PCINT2 group)

const unsigned long GLITCH_US = 0;

const float WHEEL_DIAMETER_CM = 6.5f;
const int   EDGES_PER_REV     = 10;
const float CM_PER_EDGE       = (PI * WHEEL_DIAMETER_CM) / EDGES_PER_REV;

// ----- Drive state -----
static unsigned long lastCmdMs = 0;
static int16_t targetL = 0;
static int16_t targetR = 0;
static int     lastBearing = -1;
static bool    watchdogFiredFlag = false;   // one-shot diagnostic

// ----- Straight-line encoder correction -----
const unsigned long STRAIGHT_SAMPLE_MS = 50;
static bool    encCorrectionEnabled = false;
static int     encKp  = 50;
static int     encMax = 35;
static long    encLastL = 0;
static long    encLastR = 0;
static unsigned long encLastT = 0;
static int16_t straightCorrection = 0;

// ----- Encoder counters (ISR-shared) -----
volatile long leftEdges  = 0;
volatile long rightEdges = 0;
volatile unsigned long lastLeftUs  = 0;
volatile unsigned long lastRightUs = 0;
volatile uint8_t lastLeftState  = 0;
volatile uint8_t lastRightState = 0;

// ----- Telemetry state -----
static unsigned long lastTelemMs     = 0;
static unsigned long lastAutoTelemMs = 0;
static long lastLEdges = 0;
static long lastREdges = 0;

// ----- Timer-register guard (captured in setup after Arduino init) -----
static uint8_t expected_TCCR0A_wgm;   // lower 2 bits (WGM01:WGM00)
static uint8_t expected_TCCR0B_lo;     // lower 4 bits (WGM02 + CS0x)
static uint8_t expected_TCCR2A_wgm;
static uint8_t expected_TCCR2B_lo;
static unsigned long lastTimerCheckMs = 0;

// ----- Serial line buffer -----
static char    lineBuf[64];
static uint8_t lineLen = 0;

// =====================================================================
//  Early WDT disable — runs before main(), prevents bootloader hang
//  if a previous WDT reset occurred.
// =====================================================================
void wdt_init(void) __attribute__((naked)) __attribute__((section(".init3")));
void wdt_init(void) {
    MCUSR = 0;
    wdt_disable();
}

// =====================================================================
//  Helpers
// =====================================================================
static inline int16_t clamp_i16(long v) {
    if (v >  32767) return  32767;
    if (v < -32768) return -32768;
    return (int16_t)v;
}

// ----- Non-blocking battery ADC -----
static void updateBatteryAdc() {
    unsigned long now = millis();
    if (now - lastAdcSampleMs < ADC_SAMPLE_MS) return;
    lastAdcSampleMs = now;

    adcSum += analogRead(BATTERY_PIN);
    adcCount++;

    if (adcCount >= ADC_WINDOW) {
        float adc  = adcSum / (float)ADC_WINDOW;
        float vOut = adc * ADC_REF / ADC_MAX;
        cachedBatteryV = vOut * (R1 + R2) / R2 * BATTERY_VOLTAGE_CALIB;
        adcSum   = 0;
        adcCount = 0;
    }
}

// ----- HC-SR04 (non-blocking: one state transition per loop) -----
static void updateUltrasonic() {
    unsigned long nowMs = millis();

    switch (usState) {
        case US_IDLE:
            if (usLastCompleteMs != 0 &&
                (nowMs - usLastCompleteMs) < US_SAMPLE_INTERVAL_MS)
                return;
            digitalWrite(US_TRIG_PIN, LOW);
            delayMicroseconds(2);
            digitalWrite(US_TRIG_PIN, HIGH);
            delayMicroseconds(10);
            digitalWrite(US_TRIG_PIN, LOW);
            usPhaseStartUs = micros();
            usState         = US_WAIT_HIGH;
            break;

        case US_WAIT_HIGH:
            if (digitalRead(US_ECHO_PIN) == HIGH) {
                usPulseRiseUs = micros();
                usState       = US_WAIT_LOW;
            } else if (micros() - usPhaseStartUs > US_ECHO_TIMEOUT_US) {
                usCachedMm       = -1;
                usLastCompleteMs = nowMs;
                usState          = US_IDLE;
            }
            break;

        case US_WAIT_LOW:
            if (digitalRead(US_ECHO_PIN) == LOW) {
                unsigned long dur = micros() - usPulseRiseUs;
                if (dur < US_PULSE_MIN_US || dur > US_ECHO_TIMEOUT_US)
                    usCachedMm = -1;
                else {
                    long mm = (long)(dur * 10UL) / 58UL;
                    if (mm > 5000L)
                        usCachedMm = -1;
                    else
                        usCachedMm = (int)mm;
                }
                usLastCompleteMs = nowMs;
                usState          = US_IDLE;
            } else if (micros() - usPulseRiseUs > US_ECHO_TIMEOUT_US) {
                usCachedMm       = -1;
                usLastCompleteMs = nowMs;
                usState          = US_IDLE;
            }
            break;
    }
}

// ----- Timer-register guard -----
static void captureTimerDefaults() {
    expected_TCCR0A_wgm = TCCR0A & 0x03;
    expected_TCCR0B_lo  = TCCR0B & 0x0F;
    expected_TCCR2A_wgm = TCCR2A & 0x03;
    expected_TCCR2B_lo  = TCCR2B & 0x0F;
}

static void verifyTimerRegisters() {
    bool ok = true;

    if ((TCCR0A & 0x03) != expected_TCCR0A_wgm) {
        TCCR0A = (TCCR0A & 0xFC) | expected_TCCR0A_wgm;
        ok = false;
    }
    if ((TCCR0B & 0x0F) != expected_TCCR0B_lo) {
        TCCR0B = (TCCR0B & 0xF0) | expected_TCCR0B_lo;
        ok = false;
    }
    if ((TCCR2A & 0x03) != expected_TCCR2A_wgm) {
        TCCR2A = (TCCR2A & 0xFC) | expected_TCCR2A_wgm;
        ok = false;
    }
    if ((TCCR2B & 0x0F) != expected_TCCR2B_lo) {
        TCCR2B = (TCCR2B & 0xF0) | expected_TCCR2B_lo;
        ok = false;
    }

    if (!ok) Serial.println(F("#WARN timer-fix"));
}

// ----- Motor helpers -----
static void setLeft(int16_t sp) {
    sp = constrain(sp, -255, 255);
    if (sp == 0) {
        digitalWrite(AIN1, LOW);
        digitalWrite(AIN2, LOW);
        analogWrite(PWMA, 0);
    } else if (sp > 0) {
        digitalWrite(AIN1, HIGH);
        digitalWrite(AIN2, LOW);
        analogWrite(PWMA, (uint8_t)sp);
    } else {
        digitalWrite(AIN1, LOW);
        digitalWrite(AIN2, HIGH);
        analogWrite(PWMA, (uint8_t)(-sp));
    }
}

static void setRight(int16_t sp) {
    sp = constrain(sp, -255, 255);
    if (sp == 0) {
        digitalWrite(BIN1, LOW);
        digitalWrite(BIN2, LOW);
        analogWrite(PWMB, 0);
    } else if (sp > 0) {
        digitalWrite(BIN1, HIGH);
        digitalWrite(BIN2, LOW);
        analogWrite(PWMB, (uint8_t)sp);
    } else {
        digitalWrite(BIN1, LOW);
        digitalWrite(BIN2, HIGH);
        analogWrite(PWMB, (uint8_t)(-sp));
    }
}

static void stopMotors() {
    targetL = 0;
    targetR = 0;
    setLeft(0);
    setRight(0);
}

static void resetEncoderCorrection() {
    encLastL = 0;
    encLastR = 0;
    encLastT = 0;
    straightCorrection = 0;
}

// ----- PCINT helpers -----
static void enablePcint(uint8_t pin) {
    volatile uint8_t *pcicr = digitalPinToPCICR(pin);
    if (!pcicr) return;
    *pcicr |= _BV(digitalPinToPCICRbit(pin));
    *digitalPinToPCMSK(pin) |= _BV(digitalPinToPCMSKbit(pin));
}

static inline uint8_t readPinFast(uint8_t pin) {
    uint8_t bit  = digitalPinToBitMask(pin);
    uint8_t port = digitalPinToPort(pin);
    return (*portInputRegister(port) & bit) ? 1 : 0;
}

static inline void handleEncodersPcint() {
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

ISR(PCINT0_vect) { handleEncodersPcint(); }
#ifdef PCINT1_vect
ISR(PCINT1_vect) { handleEncodersPcint(); }
#endif
ISR(PCINT2_vect) { handleEncodersPcint(); }

// ----- Serial line reader (no String, no blocking) -----
static inline void resetLine() {
    lineLen = 0;
    lineBuf[0] = '\0';
}

static bool readLine(char *out, size_t outSize) {
    while (Serial.available()) {
        char c = (char)Serial.read();
        if (c == '\r' || c == '\0') continue;

        if (c == '\n') {
            if (lineLen == 0) continue;  // skip empty lines
            lineBuf[lineLen] = '\0';
            strncpy(out, lineBuf, outSize);
            out[outSize - 1] = '\0';
            resetLine();
            return true;
        }
        if (lineLen < sizeof(lineBuf) - 1)
            lineBuf[lineLen++] = c;
    }
    return false;
}

// ----- Bearing + velocity -> differential motor speeds -----
static void bearingVelocityToLR(int bearing, int velocity,
                                int16_t *outL, int16_t *outR) {
    if (velocity <= 0) { *outL = 0; *outR = 0; return; }

    int   v    = map(velocity, 0, 9, 0, 150);
    float rad  = (float)bearing * (PI / 180.0f);
    float fwd  = cos(rad);
    float turn = sin(rad);
    if (bearing > 90 && bearing < 270) turn = -turn;

    float left  = fwd - turn;
    float right = fwd + turn;
    float mag   = max((float)fabs(left), (float)fabs(right));
    if (mag > 1e-6f) { left /= mag; right /= mag; }

    *outL = clamp_i16((long)round(left  * v));
    *outR = clamp_i16((long)round(right * v));
}

// ----- Telemetry (non-blocking: uses cached battery voltage) -----
static void emitTelemetry(bool touchWatchdog) {
    if (touchWatchdog) lastCmdMs = millis();

    unsigned long nowMs = millis();
    unsigned long dtMs  = (lastTelemMs != 0) ? (nowMs - lastTelemMs) : 1;
    if (dtMs == 0) dtMs = 1;

    noInterrupts();
    long l = leftEdges;
    long r = rightEdges;
    interrupts();

    long dL = l - lastLEdges;
    long dR = r - lastREdges;
    lastLEdges  = l;
    lastREdges  = r;
    lastTelemMs = nowMs;

    float dt_s     = dtMs / 1000.0f;
    long  vL_mmps  = lroundf((dL * CM_PER_EDGE * 10.0f) / dt_s);
    long  vR_mmps  = lroundf((dR * CM_PER_EDGE * 10.0f) / dt_s);
    long  dist_mm  = lroundf((l + r) / 2.0f * CM_PER_EDGE * 10.0f);

    Serial.print(l);             Serial.print(',');
    Serial.print(r);             Serial.print(',');
    Serial.print(dist_mm);       Serial.print(',');
    Serial.print(clamp_i16(vL_mmps)); Serial.print(',');
    Serial.print(clamp_i16(vR_mmps)); Serial.print(',');
    Serial.print(cachedBatteryV, 2);
    Serial.print(',');
    Serial.println(usCachedMm);
}

// =====================================================================
//  setup
// =====================================================================
void setup() {
    Serial.begin(115200);

    // Motor pins
    pinMode(PWMA, OUTPUT);
    pinMode(AIN1, OUTPUT);
    pinMode(AIN2, OUTPUT);
    pinMode(PWMB, OUTPUT);
    pinMode(BIN1, OUTPUT);
    pinMode(BIN2, OUTPUT);

    if (STBY_PIN >= 0) {
        pinMode(STBY_PIN, OUTPUT);
        digitalWrite(STBY_PIN, HIGH);
    }

    pinMode(US_TRIG_PIN, OUTPUT);
    digitalWrite(US_TRIG_PIN, LOW);
    pinMode(US_ECHO_PIN, INPUT);

    // Encoder pins
    pinMode(ENC_LEFT,  INPUT_PULLUP);
    pinMode(ENC_RIGHT, INPUT_PULLUP);

    noInterrupts();
    lastLeftState  = readPinFast(ENC_LEFT);
    lastRightState = readPinFast(ENC_RIGHT);
    enablePcint(ENC_LEFT);
    enablePcint(ENC_RIGHT);
    interrupts();

    // Snapshot timer register defaults AFTER Arduino init configured them
    captureTimerDefaults();

    stopMotors();
    lastCmdMs       = millis();
    lastAutoTelemMs = millis();

    // Seed battery voltage with a single read so first telemetry is non-zero
    cachedBatteryV = analogRead(BATTERY_PIN) * ADC_REF / ADC_MAX
                     * (R1 + R2) / R2 * BATTERY_VOLTAGE_CALIB;

    // Enable hardware watchdog (2-second timeout)
    wdt_enable(WDTO_2S);

    Serial.print(F("#HOMER 2.0 REV "));
    Serial.print(VERSION);
    Serial.println(F(" READY"));
}

// =====================================================================
//  loop — fully non-blocking (no delay() calls)
// =====================================================================
void loop() {
    wdt_reset();

    // ---- 1. Non-blocking battery ADC ----
    updateBatteryAdc();

    // ---- 1b. HC-SR04 range (state machine) ----
    updateUltrasonic();

    // ---- 2. Command parsing ----
    char line[64];
    if (readLine(line, sizeof(line))) {
        int bearing = 0, velocity = 0;

        if ((line[0] == 'R' || line[0] == 'r') &&
            (line[1] == '\0' || line[1] == ' ')) {
            // Reset encoders
            noInterrupts();
            leftEdges  = 0;
            rightEdges = 0;
            interrupts();
            lastLEdges = 0;
            lastREdges = 0;
            resetEncoderCorrection();
            lastCmdMs = millis();
            Serial.println(F("#R"));

        } else if ((line[0] == 'T' || line[0] == 't') &&
                   (line[1] == '\0' || line[1] == ' ')) {
            emitTelemetry(true);

        } else if (line[0] == 'E' && line[1] == 'N' && line[2] == 'C' &&
                   (line[3] == '\0' || line[3] == ' ')) {
            int en = 0, kp = 0, maxc = 0;
            int n = sscanf(line + 3, "%d %d %d", &en, &kp, &maxc);
            if (n >= 1) {
                encCorrectionEnabled = (en != 0);
                if (!encCorrectionEnabled) resetEncoderCorrection();
                if (n >= 2 && encCorrectionEnabled && kp > 0)
                    encKp = constrain(kp, 1, 100);
                if (n >= 3 && encCorrectionEnabled && maxc > 0)
                    encMax = constrain(maxc, 1, 255);
                lastCmdMs = millis();
                Serial.print(F("#ENC "));
                Serial.print(encCorrectionEnabled ? 1 : 0);
                Serial.print(' ');
                Serial.print(encKp);
                Serial.print(' ');
                Serial.println(encMax);
            } else {
                Serial.println(F("ERR ENC parse"));
            }

        } else if (line[0] == 'M' &&
                   (line[1] == 'L' || line[1] == 'l' ||
                    line[1] == 'R' || line[1] == 'r')) {
            int val = 0;
            sscanf(line + 2, "%d", &val);
            val = constrain(val, -150, 150);
            lastCmdMs    = millis();
            lastBearing  = -1;
            resetEncoderCorrection();
            if (line[1] == 'L' || line[1] == 'l') {
                setLeft(val);
                targetL = val; targetR = 0;
                Serial.print(F("#ML ")); Serial.println(val);
            } else {
                setRight(val);
                targetL = 0; targetR = val;
                Serial.print(F("#MR ")); Serial.println(val);
            }

        } else if (sscanf(line, "%d %d", &bearing, &velocity) == 2) {
            bearing  = constrain(bearing,  0, 359);
            velocity = constrain(velocity, 0, 9);

            if (lastBearing != bearing) resetEncoderCorrection();
            lastBearing = bearing;

            bearingVelocityToLR(bearing, velocity, &targetL, &targetR);
            lastCmdMs        = millis();
            watchdogFiredFlag = false;   // clear one-shot if host is alive

#ifdef DEBUG_DRIVE
            Serial.print(F("#D b=")); Serial.print(bearing);
            Serial.print(F(" v="));   Serial.print(velocity);
            Serial.print(F(" L="));   Serial.print(targetL);
            Serial.print(F(" R="));   Serial.println(targetR);
#endif
        } else {
            Serial.println(F("ERR parse"));
        }
    }

    // ---- 3. Motor watchdog + apply ----
    if (millis() - lastCmdMs > CMD_TIMEOUT_MS) {
        stopMotors();
        if (!watchdogFiredFlag) {
            watchdogFiredFlag = true;
            Serial.println(F("#WDSTOP"));
        }
    } else {
        watchdogFiredFlag = false;
        int16_t appliedL = targetL;
        int16_t appliedR = targetR;

        // Encoder feedback for straight driving (bearing 0 or 180 only)
        if (encCorrectionEnabled &&
            (lastBearing == 0 || lastBearing == 180) &&
            (targetL != 0 || targetR != 0)) {

            unsigned long now = millis();
            noInterrupts();
            long le = leftEdges;
            long re = rightEdges;
            interrupts();

            unsigned long dt = (encLastT != 0) ? (now - encLastT) : 0;
            if (dt >= STRAIGHT_SAMPLE_MS && dt < 500) {
                long dL    = le - encLastL;
                long dR    = re - encLastR;
                long error = dL - dR;
                straightCorrection = (int16_t)constrain(
                    (long)encKp * error, -(long)encMax, (long)encMax);
                encLastL = le;
                encLastR = re;
                encLastT = now;
            } else if (dt >= 500 || encLastT == 0) {
                straightCorrection = 0;
                encLastL = le;
                encLastR = re;
                encLastT = now;
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

    // ---- 4. Auto-telemetry (non-blocking, no watchdog refresh) ----
    if (millis() - lastAutoTelemMs >= AUTO_TELEM_MS) {
        emitTelemetry(false);
        lastAutoTelemMs = millis();
    }

    // ---- 5. Periodic timer-register sanity check (every 1 s) ----
    if (millis() - lastTimerCheckMs >= 1000) {
        verifyTimerRegisters();
        lastTimerCheckMs = millis();
    }
}
