import { Injectable, signal, computed, OnDestroy } from '@angular/core';
import * as signalR from '@microsoft/signalr';

export interface TelemetryData {
  leftEdges: number;
  rightEdges: number;
  distanceMm: number;
  velocityLeftMmps: number;
  velocityRightMmps: number;
  batteryVoltage: number;
  ultrasonicMm?: number | null;
  wifiRssiDb?: number | null;
  pingMs?: number | null;
}

@Injectable({ providedIn: 'root' })
export class RoverSignalRService implements OnDestroy {
  private driveConnection: signalR.HubConnection | null = null;
  private telemetryConnection: signalR.HubConnection | null = null;

  telemetry = signal<TelemetryData | null>(null);
  connected = signal(false);

  /** Initial negotiate/start failed, or hubs closed after connection (retries exhausted). */
  linkFault = signal(false);
  /** Automatic reconnect in progress (connection dropped). */
  reconnecting = signal(false);
  /** Flash red on HUD when fault or reconnecting. */
  readonly linkAlarm = computed(() => this.linkFault() || this.reconnecting());

  /** Activity LED: flashes on each Drive hub invoke (outbound). */
  driveTxLed = signal(false);
  /** Activity LED: flashes on each ReceiveTelemetry message (inbound). */
  telemetryRxLed = signal(false);

  private driveLedClearTimer: ReturnType<typeof setTimeout> | null = null;
  private telemetryLedClearTimer: ReturnType<typeof setTimeout> | null = null;
  private static readonly ledHoldMs = 130;

  private driveInterval: ReturnType<typeof setInterval> | null = null;
  private currentBearing = 0;
  private currentVelocity = 0;
  /** True while `disconnect()` is tearing down hubs (ignore onclose/onreconnecting). */
  private intentionalStop = false;

  ngOnDestroy() {
    this.disconnect();
  }

  async connect(): Promise<void> {
    if (this.connected()) return;

    this.intentionalStop = false;
    this.linkFault.set(false);
    this.reconnecting.set(false);

    const baseUrl = window.location.origin;
    this.driveConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/drive`)
      .withAutomaticReconnect()
      .build();

    this.telemetryConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/telemetry`)
      .withAutomaticReconnect()
      .build();

    this.wireHub(this.driveConnection);
    this.wireHub(this.telemetryConnection);

    this.telemetryConnection.on('ReceiveTelemetry', (data: TelemetryData) => {
      this.telemetry.set(data);
      this.pulseTelemetryRxLed();
    });

    try {
      await this.driveConnection.start();
      await this.telemetryConnection.start();
      this.syncConnectedState();
    } catch (e) {
      console.error('[SignalR] connect failed (check dev proxy: /hubs → backend, ws: true)', e);
      this.linkFault.set(true);
      this.connected.set(false);
      this.reconnecting.set(false);
      await this.silentStopConnections();
      throw e;
    }
  }

  disconnect(): void {
    this.intentionalStop = true;
    this.clearActivityLeds();
    this.stopDrive();
    this.linkFault.set(false);
    this.reconnecting.set(false);
    const d = this.driveConnection;
    const t = this.telemetryConnection;
    this.driveConnection = null;
    this.telemetryConnection = null;
    this.connected.set(false);
    this.telemetry.set(null);
    void Promise.all([d?.stop() ?? Promise.resolve(), t?.stop() ?? Promise.resolve()]).finally(() => {
      this.intentionalStop = false;
    });
  }

  private wireHub(conn: signalR.HubConnection): void {
    conn.onreconnecting(() => {
      if (this.intentionalStop) return;
      this.reconnecting.set(true);
      this.connected.set(false);
    });

    conn.onreconnected(() => {
      if (this.intentionalStop) return;
      this.syncConnectedState();
    });

    conn.onclose(() => {
      if (this.intentionalStop) return;
      this.reconnecting.set(false);
      this.syncConnectedState();
      if (!this.connected()) {
        this.linkFault.set(true);
      }
    });
  }

  private syncConnectedState(): void {
    const d = this.driveConnection?.state === signalR.HubConnectionState.Connected;
    const t = this.telemetryConnection?.state === signalR.HubConnectionState.Connected;
    const ok = !!(d && t);
    this.connected.set(ok);
    if (ok) {
      this.linkFault.set(false);
      this.reconnecting.set(false);
    }
  }

  /** Stop hubs after failed start without treating closure as a user-visible fault. */
  private async silentStopConnections(): Promise<void> {
    this.intentionalStop = true;
    try {
      await this.driveConnection?.stop();
      await this.telemetryConnection?.stop();
    } catch {
      /* ignore */
    } finally {
      this.driveConnection = null;
      this.telemetryConnection = null;
      this.intentionalStop = false;
    }
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
    if (!this.driveConnection || this.driveConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }
    this.pulseDriveTxLed();
    this.driveConnection
      .invoke('Drive', this.currentBearing, this.currentVelocity)
      .catch((err) => console.error('[SignalR] Drive invoke failed', err));
  }

  private pulseDriveTxLed(): void {
    this.driveTxLed.set(true);
    if (this.driveLedClearTimer) clearTimeout(this.driveLedClearTimer);
    this.driveLedClearTimer = setTimeout(() => {
      this.driveTxLed.set(false);
      this.driveLedClearTimer = null;
    }, RoverSignalRService.ledHoldMs);
  }

  private pulseTelemetryRxLed(): void {
    this.telemetryRxLed.set(true);
    if (this.telemetryLedClearTimer) clearTimeout(this.telemetryLedClearTimer);
    this.telemetryLedClearTimer = setTimeout(() => {
      this.telemetryRxLed.set(false);
      this.telemetryLedClearTimer = null;
    }, RoverSignalRService.ledHoldMs);
  }

  private clearActivityLeds(): void {
    if (this.driveLedClearTimer) {
      clearTimeout(this.driveLedClearTimer);
      this.driveLedClearTimer = null;
    }
    if (this.telemetryLedClearTimer) {
      clearTimeout(this.telemetryLedClearTimer);
      this.telemetryLedClearTimer = null;
    }
    this.driveTxLed.set(false);
    this.telemetryRxLed.set(false);
  }
}
