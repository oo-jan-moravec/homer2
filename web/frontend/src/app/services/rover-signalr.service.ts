import { Injectable, signal, computed, OnDestroy } from '@angular/core';
import * as signalR from '@microsoft/signalr';

export interface TelemetryData {
  leftEdges: number;
  rightEdges: number;
  distanceMm: number;
  velocityLeftMmps: number;
  velocityRightMmps: number;
  batteryVoltage: number;
  wifiRssiDb?: number | null;
  pingMs?: number | null;
}

@Injectable({ providedIn: 'root' })
export class RoverSignalRService implements OnDestroy {
  private driveConnection: signalR.HubConnection | null = null;
  private telemetryConnection: signalR.HubConnection | null = null;

  telemetry = signal<TelemetryData | null>(null);
  connected = signal(false);

  private driveInterval: ReturnType<typeof setInterval> | null = null;
  private currentBearing = 0;
  private currentVelocity = 0;

  ngOnDestroy() {
    this.disconnect();
  }

  async connect(): Promise<void> {
    if (this.connected()) return;

    const baseUrl = window.location.origin;
    this.driveConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/drive`)
      .withAutomaticReconnect()
      .build();

    this.telemetryConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/telemetry`)
      .withAutomaticReconnect()
      .build();

    this.telemetryConnection.on('ReceiveTelemetry', (data: TelemetryData) => {
      this.telemetry.set(data);
    });

    await this.driveConnection.start();
    await this.telemetryConnection.start();
    this.connected.set(true);
  }

  disconnect(): void {
    this.stopDrive();
    this.driveConnection?.stop();
    this.telemetryConnection?.stop();
    this.driveConnection = null;
    this.telemetryConnection = null;
    this.connected.set(false);
    this.telemetry.set(null);
  }

  drive(bearing: number, velocity: number): void {
    this.currentBearing = Math.round(Math.max(0, Math.min(359, bearing)));
    this.currentVelocity = Math.round(Math.max(0, Math.min(9, velocity)));
    this.sendDrive();
    if (this.currentVelocity > 0) this.ensureDriveLoop();
  }

  stopDrive(): void {
    this.currentVelocity = 0;
    this.sendDrive();
    if (this.driveInterval) {
      clearInterval(this.driveInterval);
      this.driveInterval = null;
    }
  }

  private ensureDriveLoop(): void {
    if (this.driveInterval) return;
    this.driveInterval = setInterval(() => this.sendDrive(), 250);
  }

  private sendDrive(): void {
    this.driveConnection?.invoke('Drive', {
      bearing: this.currentBearing,
      velocity: this.currentVelocity
    }).catch(() => {});
  }
}
