import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RoverApiService } from '../../services/rover-api.service';
import { RoverSignalRService, TelemetryData } from '../../services/rover-signalr.service';
import { SoundService } from '../../services/sound.service';
import { JoystickComponent } from '../../components/joystick/joystick.component';
import { batteryVoltageToPercent } from '../../utils/battery';
import { wifiRssiToLabel } from '../../utils/wifi';

@Component({
  selector: 'app-operator-page',
  standalone: true,
  imports: [CommonModule, JoystickComponent],
  templateUrl: './operator-page.component.html',
  styleUrl: './operator-page.component.scss'
})
export class OperatorPageComponent implements OnInit, OnDestroy {
  private api = inject(RoverApiService);
  private signalr = inject(RoverSignalRService);
  sound = inject(SoundService);

  telemetry = this.signalr.telemetry;
  signalrConnected = this.signalr.connected;
  camSrc = signal<string | null>(null);
  irOn = signal<boolean | null>(null);
  soundAvailable = signal(false);

  ngOnInit() {
    this.signalr.connect().catch(() => {});
    this.api.getStatus().subscribe({
      next: s => {
        this.irOn.set(s.irOn);
        this.soundAvailable.set(s.soundAvailable ?? false);
      }
    });
    // Auto-start video stream on load
    this.camSrc.set(this.api.getCameraStreamUrl());
  }

  ngOnDestroy() {
    this.signalr.stopDrive();
    this.sound.stopMicStream();
    this.sound.stopVoiceToRover();
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

  wifiLabel(t: TelemetryData | null): string | null {
    return wifiRssiToLabel(t?.wifiRssiDb);
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
