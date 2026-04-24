#include <Wire.h>
#include <Adafruit_Sensor.h>
#include <Adafruit_BNO055.h>
#include <utility/imumaths.h> 
#include <Preferences.h> 

#define LED_PIN 2 
#define TCAADDR 0x70 

// Create TWO sensor objects. (IDs 0 and 1)
Adafruit_BNO055 bnoBicep = Adafruit_BNO055(0, 0x28);   
Adafruit_BNO055 bnoForearm = Adafruit_BNO055(1, 0x28); 

Preferences preferences; 

int accelStep = 0;
int calibPhase = 0; // 0=Mag, 1=Accel, 2=Gyro, 3=Done
unsigned long stepTimer = 0;
const int HOLD_TIME = 2000; 

// Separate calibration states
bool bicepCalibrated = false;
bool forearmCalibrated = false;

// Failsafe counter
int crashCounter = 0; 

// =====================================================================
// HELPER FUNCTIONS 
// =====================================================================

void tcaSelect(uint8_t i) {
  if (i > 7) return;
  Wire.beginTransmission(TCAADDR);
  Wire.write(1 << i);
  Wire.endTransmission();
}

void flashMessage(String msg) {
  static String lastMsg = "";
  if (msg != lastMsg) {
    Serial.println(msg);
    lastMsg = msg;
  }
}

void blinkLED(int rate) {
  static unsigned long last = 0;
  if (millis() - last >= rate) {
    digitalWrite(LED_PIN, !digitalRead(LED_PIN));
    last = millis();
  }
}

// Updated to accept a pointer so it knows WHICH sensor to read during calibration
void checkPhysicalOrientation(Adafruit_BNO055* sensor) {
  sensors_event_t accelData;
  sensor->getEvent(&accelData, Adafruit_BNO055::VECTOR_ACCELEROMETER);
  
  float x = accelData.acceleration.x;
  float y = accelData.acceleration.y;
  float z = accelData.acceleration.z;

  if (x == 0.00 && y == 0.00 && z == 0.00) {
     Serial.println("\n[CRITICAL ERROR] Sensor crashed! Rebooting...");
     delay(1000); ESP.restart();
  }

  static unsigned long lastDebug = 0;
  if (millis() - lastDebug > 1000) {
     Serial.println("   [Live Radar] X: " + String(x, 1) + " | Y: " + String(y, 1) + " | Z: " + String(z, 1));
     lastDebug = millis();
  }

  bool inPosition = false;

  switch (accelStep) {
    case 0: flashMessage("STEP 1: Point Y-Axis UP (+Y). Target: Y > 8.0"); if (y > 8.0) inPosition = true; break;
    case 1: flashMessage("STEP 2: Point Y-Axis DOWN (-Y). Target: Y < -8.0"); if (y < -8.0) inPosition = true; break;
    case 2: flashMessage("STEP 3: Point Z-Axis UP (+Z). Target: Z > 8.0"); if (z > 8.0) inPosition = true; break;
    case 3: flashMessage("STEP 4: Point Z-Axis DOWN (-Z). Target: Z < -8.0"); if (z < -8.0) inPosition = true; break;
    case 4: flashMessage("STEP 5: Point X-Axis DOWN (-X). Target: X < -8.0"); if (x < -8.0) inPosition = true; break;
    case 5: flashMessage("STEP 6: Point X-Axis UP (+X). Target: X > 8.0"); if (x > 8.0) inPosition = true; break;
  }

  if (inPosition) {
    blinkLED(50); 
    if (stepTimer == 0) stepTimer = millis();
    if (millis() - stepTimer > HOLD_TIME) {
      accelStep++; stepTimer = 0;
      Serial.println("\n>>> POSITION CAPTURED! <<<\n");
    }
  } else {
    blinkLED(400); stepTimer = 0; 
  }
}

// =====================================================================
// MAIN SETUP
// =====================================================================

void setup(void) {
  pinMode(LED_PIN, OUTPUT);
  Serial.begin(115200);
  Wire.begin(); 
  Wire.setClock(400000); // Fast I2C to handle two sensors smoothly

  Serial.println("\n--- INITIALIZING HARDWARE ---");

  // Init Bicep (Channel 0)
  tcaSelect(0);
  delay(10);
  if(!bnoBicep.begin()) {
    Serial.println("ERROR: Bicep BNO055 (Ch 0) not detected!");
    while(1);
  }

  // Init Forearm (Channel 1)
  tcaSelect(1);
  delay(10);
  if(!bnoForearm.begin()) {
    Serial.println("ERROR: Forearm BNO055 (Ch 1) not detected!");
    while(1);
  }
  
  preferences.begin("bno_calib", false);

  // 1. CHECK BICEP MEMORY
  if (preferences.getBytesLength("off_bicep") == sizeof(adafruit_bno055_offsets_t)) {
    adafruit_bno055_offsets_t savedBicep;
    preferences.getBytes("off_bicep", &savedBicep, sizeof(savedBicep));
    
    tcaSelect(0);
    bnoBicep.setSensorOffsets(savedBicep);
    bnoBicep.setExtCrystalUse(true);
    bicepCalibrated = true; 
    Serial.println("[OK] BICEP Memory securely loaded. (Untouched)");
  }

  // 2. CHECK FOREARM MEMORY
  if (preferences.getBytesLength("off_forearm") == sizeof(adafruit_bno055_offsets_t)) {
    adafruit_bno055_offsets_t savedForearm;
    preferences.getBytes("off_forearm", &savedForearm, sizeof(savedForearm));
    
    tcaSelect(1);
    bnoForearm.setSensorOffsets(savedForearm);
    bnoForearm.setExtCrystalUse(true);
    forearmCalibrated = true; 
    Serial.println("[OK] FOREARM Memory securely loaded.");
  }

  if (bicepCalibrated && forearmCalibrated) {
      digitalWrite(LED_PIN, HIGH);
      Serial.println("\n--- BOTH SENSORS READY: STREAMING TELEMETRY ---");
      delay(1000); 
  }
}

// =====================================================================
// MAIN LOOP
// =====================================================================

void loop(void) {
  
  // --- PHASE 1: SEQUENTIAL CALIBRATION ---
  if (!bicepCalibrated || !forearmCalibrated) {
    
    // Determine which sensor we are currently calibrating
    bool calibratingBicep = !bicepCalibrated;
    uint8_t currentChannel = calibratingBicep ? 0 : 1;
    String currentName = calibratingBicep ? "BICEP (Ch 0)" : "FOREARM (Ch 1)";
    Adafruit_BNO055* activeBNO = calibratingBicep ? &bnoBicep : &bnoForearm;

    tcaSelect(currentChannel);
    delay(5);

    uint8_t system, gyro, accel, mag = 0;
    activeBNO->getCalibration(&system, &gyro, &accel, &mag);

    if (mag == 3 && calibPhase == 0) calibPhase = 1; 
    if (accelStep >= 6 && calibPhase == 1) calibPhase = 2; 
    if (gyro == 3 && calibPhase == 2) calibPhase = 3; 

    if (calibPhase == 0) {
      flashMessage("CALIBRATING " + currentName + " | Move in a slow Figure-8. (Mag Level: " + String(mag) + "/3)");
      blinkLED(100);
    } else if (calibPhase == 1) {
      checkPhysicalOrientation(activeBNO); 
    } else if (calibPhase == 2) {
      flashMessage("CALIBRATING " + currentName + " | Place flat and leave perfectly STILL. (Gyro Level: " + String(gyro) + "/3)");
      blinkLED(1000);
    } else if (calibPhase == 3) {
      Serial.println("\n--- " + currentName + " CALIBRATION SUCCESSFUL ---");

      adafruit_bno055_offsets_t newOffsets;
      activeBNO->getSensorOffsets(newOffsets);
      
      // Save permanently to the correct memory slot
      if (calibratingBicep) {
          preferences.putBytes("off_bicep", &newOffsets, sizeof(newOffsets));
          bicepCalibrated = true;
          Serial.println("--- BICEP LOCKED TO ESP32 MEMORY ---");
      } else {
          preferences.putBytes("off_forearm", &newOffsets, sizeof(newOffsets));
          forearmCalibrated = true;
          Serial.println("--- FOREARM LOCKED TO ESP32 MEMORY ---");
      }
      
      // Reset variables so the next sensor starts from Phase 0
      calibPhase = 0;
      accelStep = 0;
      
      if (bicepCalibrated && forearmCalibrated) {
          digitalWrite(LED_PIN, HIGH);
          Serial.println("\n--- BOTH SENSORS READY: STREAMING TELEMETRY ---");
          delay(1000); 
      }
    }
    return; // Don't stream until both are calibrated
  }

  // --- PHASE 2: DUAL STREAMING QUATERNIONS ---
  
  // Read Bicep
  tcaSelect(0);
  delay(2); // Tiny delay to let Multiplexer switch cleanly
  imu::Quaternion qB = bnoBicep.getQuat();

  // Read Forearm
  tcaSelect(1);
  delay(2);
  imu::Quaternion qF = bnoForearm.getQuat();

  // Unified Failsafe: If EITHER sensor crashes (pure zeroes), trigger a restart.
  if ((qB.w() == 0.00 && qB.x() == 0.00 && qB.y() == 0.00 && qB.z() == 0.00) ||
      (qF.w() == 0.00 && qF.x() == 0.00 && qF.y() == 0.00 && qF.z() == 0.00)) {
      
      crashCounter++;
      if (crashCounter > 10) {
          Serial.println("[SYSTEM ALERT] A Sensor Died. Rebooting...");
          delay(1000); 
          ESP.restart(); 
      }
  } else {
      crashCounter = 0; 
      
      // Print ALL data in one clean string for Unity
      Serial.print("B_W:"); Serial.print(qB.w(), 4);
      Serial.print(" B_X:"); Serial.print(qB.x(), 4);
      Serial.print(" B_Y:"); Serial.print(qB.y(), 4);
      Serial.print(" B_Z:"); Serial.print(qB.z(), 4);
      
      Serial.print(" F_W:"); Serial.print(qF.w(), 4);
      Serial.print(" F_X:"); Serial.print(qF.x(), 4);
      Serial.print(" F_Y:"); Serial.print(qF.y(), 4);
      Serial.print(" F_Z:"); Serial.print(qF.z(), 4);
      Serial.print("\n");
  }
  
  delay(15);
}