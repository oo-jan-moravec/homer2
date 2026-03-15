import { Component, output, signal, ElementRef, ViewChild, AfterViewInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-joystick',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './joystick.component.html',
  styleUrl: './joystick.component.scss'
})
export class JoystickComponent implements AfterViewInit, OnDestroy {
  @ViewChild('pad') padRef!: ElementRef<HTMLDivElement>;

  move = output<{ bearing: number; velocity: number }>();
  stop = output<void>();

  stick = signal<{ x: number; y: number }>({ x: 0, y: 0 });
  active = signal(false);

  private center = { x: 0, y: 0 };
  private radius = 0;
  private pointerId: number | null = null;

  ngAfterViewInit() {
    const el = this.padRef?.nativeElement;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    this.radius = Math.min(rect.width, rect.height) / 2 - 35;
    this.center = { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };

    el.addEventListener('pointerdown', this.onDown);
    window.addEventListener('pointermove', this.onMove);
    window.addEventListener('pointerup', this.onUp);
    window.addEventListener('pointercancel', this.onUp);
  }

  ngOnDestroy() {
    const el = this.padRef?.nativeElement;
    if (el) el.removeEventListener('pointerdown', this.onDown);
    window.removeEventListener('pointermove', this.onMove);
    window.removeEventListener('pointerup', this.onUp);
    window.removeEventListener('pointercancel', this.onUp);
  }

  private onDown = (e: PointerEvent) => {
    this.pointerId = e.pointerId;
    this.active.set(true);
    (e.target as HTMLElement).setPointerCapture(e.pointerId);
    this.update(e);
  };

  private onMove = (e: PointerEvent) => {
    if (this.pointerId !== e.pointerId) return;
    this.update(e);
  };

  private onUp = () => {
    this.pointerId = null;
    this.active.set(false);
    this.stick.set({ x: 0, y: 0 });
    this.stop.emit();
  };

  private update(e: PointerEvent) {
    const dx = e.clientX - this.center.x;
    const dy = e.clientY - this.center.y;
    const dist = Math.sqrt(dx * dx + dy * dy);
    const clamped = Math.min(1, dist / this.radius);
    const x = dist > 0 ? (dx / dist) * clamped : 0;
    const y = dist > 0 ? (dy / dist) * clamped : 0;
    this.stick.set({ x, y });

    // Math coords: 0°=right, 90°=up. Rover expects: 0°=forward(up), 90°=right, 180°=back, 270°=left
    const raw = (Math.atan2(-y, x) * 180 / Math.PI + 360) % 360;
    const bearing = Math.round((450 - raw) % 360);
    const velocity = Math.round(clamped * 9);
    this.move.emit({ bearing, velocity });
  }
}
