import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface RoverStatus {
  serialConnected: boolean;
  lcdAvailable: boolean;
  irAvailable: boolean;
  irOn: boolean;
  cameraAvailable: boolean;
  timestamp: string;
}

export interface TelemetryData {
  leftEdges: number;
  rightEdges: number;
  distanceMm: number;
  velocityLeftMmps: number;
  velocityRightMmps: number;
  batteryVoltage: number;
}

@Injectable({ providedIn: 'root' })
export class RoverApiService {
  private http = inject(HttpClient);
  private base = '/api/rover';

  getStatus(): Observable<RoverStatus> {
    return this.http.get<RoverStatus>(`${this.base}/status`);
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

  resetEncoders(): Observable<void> {
    return this.http.post<void>(`${this.base}/encoders/reset`, {});
  }

  setEncoderConfig(enabled: boolean, kp?: number, max?: number): Observable<void> {
    return this.http.post<void>(`${this.base}/enc`, { enabled, kp, max });
  }
}
