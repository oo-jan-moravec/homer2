import { Injectable, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';

/**
 * Live camera preview via SignalR streaming (WebSocket). Avoids HTTP MJPEG, which is often
 * buffered by Cloudflare Tunnel and never displays incrementally in an img element.
 */
@Injectable({ providedIn: 'root' })
export class RoverCameraStreamService {
  /** Blob object URL for use as img [src]; null when stopped or disconnected. */
  previewUrl = signal<string | null>(null);

  private hub: signalR.HubConnection | null = null;
  private streamSubscription: { dispose(): void } | null = null;
  private lastObjectUrl: string | null = null;

  async start(): Promise<void> {
    await this.stop();

    const baseUrl = window.location.origin;
    const conn = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/camera`)
      .withAutomaticReconnect()
      .build();
    this.hub = conn;

    conn.onclose(() => {
      if (this.hub !== conn) return;
      this.clearPreviewOnly();
    });

    try {
      await conn.start();
    } catch (e) {
      console.error('[CameraHub] connect failed', e);
      this.hub = null;
      return;
    }

    this.attachFrameStream(conn);

    conn.onreconnected(() => {
      if (this.hub !== conn) return;
      this.attachFrameStream(conn);
    });
  }

  async stop(): Promise<void> {
    this.streamSubscription?.dispose();
    this.streamSubscription = null;

    if (this.lastObjectUrl) {
      URL.revokeObjectURL(this.lastObjectUrl);
      this.lastObjectUrl = null;
    }
    this.previewUrl.set(null);

    const c = this.hub;
    this.hub = null;
    if (c) {
      try {
        await c.stop();
      } catch {
        /* ignore */
      }
    }
  }

  private attachFrameStream(conn: signalR.HubConnection): void {
    this.streamSubscription?.dispose();
    this.streamSubscription = null;

    const streamResult = conn.stream<string>('streamCamera');
    this.streamSubscription = streamResult.subscribe({
      next: (b64: string) => this.applyFrame(b64),
      error: (err) => {
        console.error('[CameraHub] stream error', err);
        this.clearPreviewOnly();
      },
      complete: () => this.clearPreviewOnly()
    });
  }

  private applyFrame(b64: string): void {
    if (typeof b64 !== 'string' || !b64.length) return;
    try {
      const bin = atob(b64);
      const len = bin.length;
      const bytes = new Uint8Array(len);
      for (let i = 0; i < len; i++) bytes[i] = bin.charCodeAt(i);
      const blob = new Blob([bytes], { type: 'image/jpeg' });
      const url = URL.createObjectURL(blob);
      const prev = this.lastObjectUrl;
      this.lastObjectUrl = url;
      this.previewUrl.set(url);
      if (prev) URL.revokeObjectURL(prev);
    } catch {
      /* invalid frame */
    }
  }

  private clearPreviewOnly(): void {
    if (this.lastObjectUrl) {
      URL.revokeObjectURL(this.lastObjectUrl);
      this.lastObjectUrl = null;
    }
    this.previewUrl.set(null);
  }
}
