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
  /** Max stick translation (px); tied to pad size so CSS scaling stays aligned with math. */
  stickMaxPx = signal(70);
  active = signal(false);

  private center = { x: 0, y: 0 };
  private radius = 0;
  private pointerId: number | null = null;
  private resizeObserver: ResizeObserver | null = null;

  ngAfterViewInit() {
    const el = this.padRef?.nativeElement;
    if (!el) return;

    this.readGeometry();

    this.resizeObserver = new ResizeObserver(() => this.readGeometry());
    this.resizeObserver.observe(el);
    window.addEventListener('resize', this.onLayoutChange);
    window.addEventListener('scroll', this.onLayoutChange, true);
    const vv = window.visualViewport;
    vv?.addEventListener('resize', this.onLayoutChange);
    vv?.addEventListener('scroll', this.onLayoutChange);

    el.addEventListener('pointerdown', this.onDown);
    window.addEventListener('pointermove', this.onMove);
    window.addEventListener('pointerup', this.onUp);
    window.addEventListener('pointercancel', this.onUp);
  }

  ngOnDestroy() {
    const el = this.padRef?.nativeElement;
    if (el) {
      el.removeEventListener('pointerdown', this.onDown);
      this.resizeObserver?.unobserve(el);
    }
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    window.removeEventListener('resize', this.onLayoutChange);
    window.removeEventListener('scroll', this.onLayoutChange, true);
    const vv = window.visualViewport;
    vv?.removeEventListener('resize', this.onLayoutChange);
    vv?.removeEventListener('scroll', this.onLayoutChange);
    window.removeEventListener('pointermove', this.onMove);
    window.removeEventListener('pointerup', this.onUp);
    window.removeEventListener('pointercancel', this.onUp);
  }

  private onLayoutChange = () => this.readGeometry();

  private onDown = (e: PointerEvent) => {
    const pad = this.padRef?.nativeElement;
    if (!pad) return;

    // setPointerCapture only works on the hit target; currentTarget is the pad when the hit is .stick.
    const hit = this.pointerHitElement(pad, e.target);
    this.pointerId = e.pointerId;
    this.active.set(true);
    this.readGeometry();
    try {
      hit.setPointerCapture(e.pointerId);
    } catch {
      /* ignore */
    }
    this.update(e);
  };

  private onMove = (e: PointerEvent) => {
    if (this.pointerId !== e.pointerId) return;
    this.update(e);
  };

  private onUp = (e: PointerEvent) => {
    if (this.pointerId !== null && e.pointerId !== this.pointerId) return;
    this.pointerId = null;
    this.active.set(false);
    this.stick.set({ x: 0, y: 0 });
    this.stop.emit();
  };

  private pointerHitElement(pad: HTMLElement, target: EventTarget | null): HTMLElement {
    if (target instanceof HTMLElement && pad.contains(target)) return target;
    if (target instanceof Text && target.parentElement && pad.contains(target.parentElement)) {
      return target.parentElement;
    }
    return pad;
  }

  /** Sync center/radius with the pad’s current screen box (fixes stale math after resize, scroll, mobile UI). */
  private readGeometry() {
    const el = this.padRef?.nativeElement;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const r = Math.min(rect.width, rect.height);
    this.center = { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
    this.radius = Math.max(24, r / 2 - 35);
    this.stickMaxPx.set(Math.max(24, r / 2 - 30));
  }

  private update(e: PointerEvent) {
    this.readGeometry();
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
