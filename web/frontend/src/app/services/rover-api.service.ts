import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface RoverStatus {
  serialConnected: boolean;
  lcdAvailable: boolean;
  irAvailable: boolean;
  irOn: boolean;
  cameraAvailable: boolean;
  soundAvailable?: boolean;
  timestamp: string;
}

export interface SystemInfo {
  hostname: string;
  os: string;
  ipAddresses: string[];
  uptime: string;
  cpuTempC: string | null;
  cpuCores: number;
  loadAverage: string;
  memoryUsedMb: string;
  memoryTotalMb: string;
  diskFreeGb: string | null;
  diskTotalGb: string | null;
  memoryUsedPercent?: number;
}

export interface TelemetryData {
  leftEdges: number;
  rightEdges: number;
  distanceMm: number;
  velocityLeftMmps: number;
  velocityRightMmps: number;
  batteryVoltage: number;
  /** HC-SR04 obstacle range (mm); null if no reading. */
  ultrasonicMm?: number | null;
  wifiRssiDb?: number | null;
  pingMs?: number | null;
}

export interface SerialTraceLine {
  at: string;
  dir: string;
  line: string;
}

export interface WifiApEntry {
  bssid: string;
  ssid?: string | null;
  freqMHz?: number | null;
  signalDbm?: number | null;
}

export interface WifiSurvey {
  iface?: string | null;
  currentBssid?: string | null;
  currentSsid?: string | null;
  accessPoints: WifiApEntry[];
  error?: string | null;
}

export interface SerialDebugSnapshot {
  driveSends: number;
  driveLockTimeouts: number;
  telemetryReadTimeoutMs: number;
  driveLockWaitMs: number;
  recent: SerialTraceLine[];
  /** Last "bearing vel" written to serial after a successful Drive send. */
  lastDriveLine?: string | null;
}

@Injectable({ providedIn: 'root' })
export class RoverApiService {
  private http = inject(HttpClient);
  private base = '/api/rover';

  getOperatorGate(): Observable<{ passwordRequired: boolean }> {
    return this.http.get<{ passwordRequired: boolean }>(`${this.base}/operator/gate`);
  }

  unlockOperator(password: string): Observable<{ ok: boolean }> {
    return this.http.post<{ ok: boolean }>(`${this.base}/operator/unlock`, { password });
  }

  getStatus(): Observable<RoverStatus> {
    return this.http.get<RoverStatus>(`${this.base}/status`);
  }

  getSystemInfo(): Observable<SystemInfo> {
    return this.http.get<SystemInfo>(`${this.base}/sysinfo`);
  }

  /** Linux rover only: runs `iw dev … scan`; may take a few seconds. */
  getWifiSurvey(): Observable<WifiSurvey> {
    return this.http.get<WifiSurvey>(`${this.base}/wifi-survey`);
  }

  getTelemetry(): Observable<TelemetryData> {
    return this.http.get<TelemetryData>(`${this.base}/telemetry`);
  }

  setLcd(line1: string, line2: string): Observable<void> {
    return this.http.post<void>(`${this.base}/lcd`, { line1, line2 });
  }

  clearLcd(): Observable<void> {
    return this.http.post<void>(`${this.base}/lcd/clear`, {});
  }

  setIr(on: boolean): Observable<{ on: boolean }> {
    return this.http.post<{ on: boolean }>(`${this.base}/ir`, { on });
  }

  toggleIr(): Observable<{ on: boolean }> {
    return this.http.post<{ on: boolean }>(`${this.base}/ir/toggle`, {});
  }

  captureCamera(): Observable<Blob> {
    return this.http.get(`${this.base}/camera`, { responseType: 'blob' });
  }

  getCameraUrl(): string {
    return `${this.base}/camera?t=${Date.now()}`;
  }

  /** Live MJPEG stream URL for real-time video feed. Use as img src. */
  getCameraStreamUrl(): string {
    return `${this.base}/camera/stream`;
  }

  getCameraQuality(): Observable<{ preset: string }> {
    return this.http.get<{ preset: string }>(`${this.base}/camera/quality`);
  }

  setCameraQuality(preset: string): Observable<{ preset: string }> {
    return this.http.post<{ preset: string }>(`${this.base}/camera/quality`, { preset });
  }

  resetEncoders(): Observable<void> {
    return this.http.post<void>(`${this.base}/encoders/reset`, {});
  }

  setEncoderConfig(enabled: boolean, kp?: number, max?: number): Observable<void> {
    return this.http.post<void>(`${this.base}/enc`, { enabled, kp, max });
  }

  getLcdAutoEnabled(): Observable<{ enabled: boolean }> {
    return this.http.get<{ enabled: boolean }>(`${this.base}/lcd/auto`);
  }

  setLcdAutoEnabled(enabled: boolean): Observable<{ enabled: boolean }> {
    return this.http.post<{ enabled: boolean }>(`${this.base}/lcd/auto`, { enabled });
  }

  getSerialDebug(): Observable<SerialDebugSnapshot> {
    return this.http.get<SerialDebugSnapshot>(`${this.base}/serial-debug`);
  }
}
