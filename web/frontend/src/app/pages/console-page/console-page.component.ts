import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RoverApiService, RoverStatus, SystemInfo, TelemetryData } from '../../services/rover-api.service';
import { RoverSignalRService } from '../../services/rover-signalr.service';
import { batteryVoltageToPercent } from '../../utils/battery';
import { wifiRssiToLabelAndDb } from '../../utils/wifi';

@Component({
  selector: 'app-console-page',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './console-page.component.html',
  styleUrl: './console-page.component.scss'
})
export class ConsolePageComponent implements OnInit, OnDestroy {
  private api = inject(RoverApiService);
  private signalr = inject(RoverSignalRService);

  status = signal<RoverStatus | null>(null);
  systemInfo = signal<SystemInfo | null>(null);
  telemetry = this.signalr.telemetry;
  signalrConnected = this.signalr.connected;

  lcdLine1 = '';
  lcdLine2 = '';
  lcdAutoEnabled = true;
  encEnabled = true;
  encKp = 50;
  encMax = 35;
  driveBearing = 0;
  driveVel = 0;
  message = signal<string>('');
  videoQualityPreset = '480p';

  ngOnInit() {
    this.api.getStatus().subscribe({
      next: s => this.status.set(s),
      error: () => this.message.set('API offline')
    });
    this.refreshSystemInfo();
    this.api.getLcdAutoEnabled().subscribe({
      next: r => this.lcdAutoEnabled = r.enabled,
      error: () => {}
    });
    this.api.getCameraQuality().subscribe({
      next: r => this.videoQualityPreset = r.preset ?? '480p',
      error: () => {}
    });
    this.signalr.connect().catch(() => this.message.set('SignalR failed'));
  }

  ngOnDestroy() {
    this.signalr.stopDrive();
  }

  refreshStatus() {
    this.api.getStatus().subscribe({ next: s => this.status.set(s) });
  }

  refreshSystemInfo() {
    this.api.getSystemInfo().subscribe({
      next: s => this.systemInfo.set(s),
      error: () => this.systemInfo.set(null)
    });
  }

  setLcd() {
    this.api.setLcd(this.lcdLine1, this.lcdLine2).subscribe({
      next: () => this.message.set('LCD updated'),
      error: () => this.message.set('LCD failed')
    });
  }

  clearLcd() {
    this.api.clearLcd().subscribe();
  }

  onLcdAutoChange(enabled: boolean) {
    this.lcdAutoEnabled = enabled;
    this.api.setLcdAutoEnabled(enabled).subscribe({
      next: r => this.lcdAutoEnabled = r.enabled,
      error: () => this.lcdAutoEnabled = !enabled
    });
  }

  toggleIr() {
    this.api.toggleIr().subscribe({ next: () => this.refreshStatus() });
  }

  setIr(on: boolean) {
    this.api.setIr(on).subscribe({ next: () => this.refreshStatus() });
  }

  onVideoQualityChange(preset: string) {
    this.api.setCameraQuality(preset).subscribe({
      next: r => this.videoQualityPreset = r.preset,
      error: () => this.message.set('Failed to set video quality')
    });
  }

  resetEncoders() {
    this.api.resetEncoders().subscribe();
  }

  setEncConfig() {
    this.api.setEncoderConfig(this.encEnabled, this.encKp, this.encMax).subscribe();
  }

  sendDrive() {
    this.signalr.drive(this.driveBearing, this.driveVel);
  }

  stopDrive() {
    this.signalr.stopDrive();
    this.driveVel = 0;
    this.driveBearing = 0;
  }

  bearingLabel(b: number): string {
    if (b === 0) return 'FWD'; if (b === 90) return 'RT'; if (b === 180) return 'BWD'; if (b === 270) return 'LT';
    return `${b}°`;
  }

  batteryPercent(t: TelemetryData | null): number | null {
    if (t?.batteryVoltage == null) return null;
    return batteryVoltageToPercent(t.batteryVoltage);
  }

  wifiDisplay(t: TelemetryData | null): string {
    return wifiRssiToLabelAndDb(t?.wifiRssiDb);
  }
}
