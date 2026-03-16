import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RoverApiService, SystemInfo } from '../../services/rover-api.service';
import { RoverSignalRService, TelemetryData } from '../../services/rover-signalr.service';
import { SoundService } from '../../services/sound.service';
import { JoystickComponent } from '../../components/joystick/joystick.component';
import { batteryVoltageToPercent } from '../../utils/battery';

@Component({
  selector: 'app-operator-page',
  standalone: true,
  imports: [CommonModule, JoystickComponent],
  templateUrl: './operator-page.component.html',
  styleUrl: './operator-page.component.scss'
})
export class OperatorPageComponent implements OnInit, OnDestroy {
  readonly batteryBarCount = [1, 2, 3, 4, 5] as const;
  readonly wifiBarCount = [1, 2, 3, 4] as const;

  private api = inject(RoverApiService);
  private signalr = inject(RoverSignalRService);
  sound = inject(SoundService);

  telemetry = this.signalr.telemetry;
  signalrConnected = this.signalr.connected;
  camSrc = signal<string | null>(null);
  irOn = signal<boolean | null>(null);
  soundAvailable = signal(false);
  systemInfo = signal<SystemInfo | null>(null);
  private systemInfoInterval?: ReturnType<typeof setInterval>;

  ngOnInit() {
    this.signalr.connect().catch(() => {});
    this.api.getStatus().subscribe({
      next: s => {
        this.irOn.set(s.irOn);
        this.soundAvailable.set(s.soundAvailable ?? false);
      }
    });
    // Single stream connection - URL is stable (no cache-busting)
    this.camSrc.set(this.api.getCameraStreamUrl());
    this.refreshSystemInfo();
    this.systemInfoInterval = setInterval(() => this.refreshSystemInfo(), 30_000);
  }

  ngOnDestroy() {
    if (this.systemInfoInterval) clearInterval(this.systemInfoInterval);
    this.camSrc.set(null); // Abort stream connection so browser closes the request
    this.signalr.stopDrive();
    this.sound.stopMicStream();
    this.sound.stopVoiceToRover();
  }

  refreshSystemInfo() {
    this.api.getSystemInfo().subscribe({
      next: s => this.systemInfo.set(s),
      error: () => this.systemInfo.set(null)
    });
  }

  onJoystickMove(e: { bearing: number; velocity: number }) {
    this.signalr.drive(e.bearing, e.velocity);
  }

  onJoystickStop() {
    this.signalr.stopDrive();
  }

  onStreamError() {
    this.camSrc.set(null);
  }

  retryStream() {
    this.camSrc.set(this.api.getCameraStreamUrl());
  }

  toggleIr() {
    this.api.toggleIr().subscribe({ next: r => this.irOn.set(r.on) });
  }

  speedKmh(t: TelemetryData | null): number {
    if (!t) return 0;
    const avg = (Math.abs(t.velocityLeftMmps) + Math.abs(t.velocityRightMmps)) / 2;
    return Math.round(avg * 0.0036 * 10) / 10;
  }

  batteryPercent(t: TelemetryData | null): number | null {
    if (t?.batteryVoltage == null) return null;
    return batteryVoltageToPercent(t.batteryVoltage);
  }

  /** 0–5 bars from battery percent. */
  batteryBars(t: TelemetryData | null): number {
    const pct = this.batteryPercent(t);
    if (pct == null) return -1;
    if (pct <= 0) return 0;
    return Math.min(5, Math.ceil(pct / 20));
  }

  /** 0–4 bars from WiFi RSSI dBm. */
  wifiBars(t: TelemetryData | null): number {
    const rssi = t?.wifiRssiDb;
    if (rssi == null) return -1;
    if (rssi >= -50) return 4;
    if (rssi >= -60) return 3;
    if (rssi >= -70) return 2;
    if (rssi >= -80) return 1;
    return 0;
  }

  memoryPercent(): number | null {
    const info = this.systemInfo();
    return info?.memoryUsedPercent ?? null;
  }

  toggleMicStream() {
    if (this.sound.micStreamActive()) {
      this.sound.stopMicStream();
    } else {
      this.sound.startMicStream();
    }
  }

  toggleVoiceToRover() {
    if (this.sound.voiceToRoverActive()) {
      this.sound.stopVoiceToRover();
    } else {
      this.sound.startVoiceToRover().catch((err) => {
        console.error('[Operator] Voice start failed:', err);
      });
    }
  }
}
