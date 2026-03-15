import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RoverApiService, RoverStatus, TelemetryData } from '../../services/rover-api.service';
import { RoverSignalRService } from '../../services/rover-signalr.service';

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
  telemetry = this.signalr.telemetry;
  signalrConnected = this.signalr.connected;

  lcdLine1 = '';
  lcdLine2 = '';
  encEnabled = true;
  encKp = 50;
  encMax = 35;
  driveBearing = 0;
  driveVel = 0;
  message = signal<string>('');
  camSrc = signal<string | null>(null);

  ngOnInit() {
    this.api.getStatus().subscribe({
      next: s => this.status.set(s),
      error: () => this.message.set('API offline')
    });
    this.signalr.connect().catch(() => this.message.set('SignalR failed'));
  }

  ngOnDestroy() {
    this.signalr.stopDrive();
  }

  refreshStatus() {
    this.api.getStatus().subscribe({ next: s => this.status.set(s) });
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

  toggleIr() {
    this.api.toggleIr().subscribe({ next: () => this.refreshStatus() });
  }

  setIr(on: boolean) {
    this.api.setIr(on).subscribe({ next: () => this.refreshStatus() });
  }

  refreshCamera() {
    this.camSrc.set(this.api.getCameraUrl() + '&t=' + Date.now());
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
}
