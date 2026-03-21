import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RoverApiService, RoverStatus, SystemInfo, TelemetryData, SerialDebugSnapshot } from '../../services/rover-api.service';
import { RoverSignalRService } from '../../services/rover-signalr.service';
import { SoundService } from '../../services/sound.service';
import { JoystickComponent } from '../../components/joystick/joystick.component';
import { batteryVoltageToPercent } from '../../utils/battery';
import { wifiRssiToLabelAndDb } from '../../utils/wifi';

@Component({
  selector: 'app-operator-page',
  standalone: true,
  imports: [CommonModule, FormsModule, JoystickComponent],
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

  // Console overlay
  infoOverlayVisible = signal(false);
  status = signal<RoverStatus | null>(null);
  consoleMessage = signal('');
  videoQualityPreset = '480p';
  lcdLine1 = '';
  lcdLine2 = '';
  lcdAutoEnabled = true;
  encEnabled = true;
  encKp = 50;
  encMax = 35;
  manualBearing = 0;
  manualVel = 0;

  lastBearing = signal(0);
  lastVelocity = signal(0);
  serialDebug = signal<SerialDebugSnapshot | null>(null);

  private systemInfoInterval?: ReturnType<typeof setInterval>;
  private debugInterval?: ReturnType<typeof setInterval>;

  ngOnInit() {
    this.signalr.connect().catch(() => {});
    this.api.getStatus().subscribe({
      next: s => {
        this.status.set(s);
        this.irOn.set(s.irOn);
        this.soundAvailable.set(s.soundAvailable ?? false);
      }
    });
    this.camSrc.set(this.api.getCameraStreamUrl());
    this.refreshSystemInfo();
    this.systemInfoInterval = setInterval(() => this.refreshSystemInfo(), 30_000);
    this.api.getLcdAutoEnabled().subscribe({ next: r => this.lcdAutoEnabled = r.enabled, error: () => {} });
    this.api.getCameraQuality().subscribe({ next: r => this.videoQualityPreset = r.preset ?? '480p', error: () => {} });
    this.debugInterval = setInterval(() => {
      if (this.infoOverlayVisible()) this.refreshDebug();
    }, 1500);
  }

  ngOnDestroy() {
    if (this.systemInfoInterval) clearInterval(this.systemInfoInterval);
    if (this.debugInterval) clearInterval(this.debugInterval);
    this.camSrc.set(null);
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

  refreshStatus() {
    this.api.getStatus().subscribe({ next: s => this.status.set(s) });
  }

  refreshDebug() {
    this.api.getSerialDebug().subscribe({
      next: d => this.serialDebug.set(d),
      error: () => {}
    });
  }

  // Joystick drive
  onJoystickMove(e: { bearing: number; velocity: number }) {
    this.lastBearing.set(e.bearing);
    this.lastVelocity.set(e.velocity);
    this.signalr.drive(e.bearing, e.velocity);
  }
  onJoystickStop() {
    this.lastVelocity.set(0);
    this.signalr.stopDrive();
  }

  onStreamError() { this.camSrc.set(null); }
  retryStream() { this.camSrc.set(this.api.getCameraStreamUrl()); }

  toggleIr() {
    this.api.toggleIr().subscribe({ next: r => this.applyIrState(r.on) });
  }

  toggleMicStream() {
    if (this.sound.micStreamActive()) this.sound.stopMicStream();
    else this.sound.startMicStream();
  }

  toggleVoiceToRover() {
    if (this.sound.voiceToRoverActive()) this.sound.stopVoiceToRover();
    else this.sound.startVoiceToRover().catch(err => console.error('[Operator] Voice start failed:', err));
  }

  // Console: LCD
  setLcd() {
    this.api.setLcd(this.lcdLine1, this.lcdLine2).subscribe({
      next: () => this.consoleMessage.set('LCD updated'),
      error: () => this.consoleMessage.set('LCD failed')
    });
  }
  clearLcd() { this.api.clearLcd().subscribe(); }
  onLcdAutoChange(enabled: boolean) {
    this.lcdAutoEnabled = enabled;
    this.api.setLcdAutoEnabled(enabled).subscribe({
      next: r => this.lcdAutoEnabled = r.enabled,
      error: () => this.lcdAutoEnabled = !enabled
    });
  }

  // Console: IR
  setIr(on: boolean) {
    this.api.setIr(on).subscribe({ next: r => this.applyIrState(r.on) });
  }

  private applyIrState(on: boolean) {
    this.irOn.set(on);
    const s = this.status();
    if (s) this.status.set({ ...s, irOn: on });
  }

  // Console: video quality
  onVideoQualityChange(preset: string) {
    this.api.setCameraQuality(preset).subscribe({
      next: r => this.videoQualityPreset = r.preset,
      error: () => this.consoleMessage.set('Failed to set video quality')
    });
  }

  // Console: encoder
  resetEncoders() { this.api.resetEncoders().subscribe(); }
  setEncConfig() { this.api.setEncoderConfig(this.encEnabled, this.encKp, this.encMax).subscribe(); }

  // Console: manual drive buttons
  sendManualDrive() { this.signalr.drive(this.manualBearing, this.manualVel); }
  stopManualDrive() { this.signalr.stopDrive(); this.manualVel = 0; this.manualBearing = 0; }

  // Telemetry helpers
  speedKmh(t: TelemetryData | null): number {
    if (!t) return 0;
    return Math.round((Math.abs(t.velocityLeftMmps) + Math.abs(t.velocityRightMmps)) / 2 * 0.0036 * 10) / 10;
  }
  batteryPercent(t: TelemetryData | null): number | null {
    if (t?.batteryVoltage == null) return null;
    return batteryVoltageToPercent(t.batteryVoltage);
  }
  batteryBars(t: TelemetryData | null): number {
    const pct = this.batteryPercent(t);
    if (pct == null) return -1;
    if (pct <= 0) return 0;
    return Math.min(5, Math.ceil(pct / 20));
  }
  wifiBars(t: TelemetryData | null): number {
    const rssi = t?.wifiRssiDb;
    if (rssi == null) return -1;
    if (rssi >= -50) return 4;
    if (rssi >= -60) return 3;
    if (rssi >= -70) return 2;
    if (rssi >= -80) return 1;
    return 0;
  }
  memoryPercent(): number | null { return this.systemInfo()?.memoryUsedPercent ?? null; }
  wifiDisplay(t: TelemetryData | null): string { return wifiRssiToLabelAndDb(t?.wifiRssiDb); }
}
