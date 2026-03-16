import { Injectable, signal, OnDestroy } from '@angular/core';
import * as signalR from '@microsoft/signalr';

const SAMPLE_RATE = 16000;
const CHUNK_SAMPLES = 1024;
const CHUNK_BYTES = CHUNK_SAMPLES * 2;

@Injectable({ providedIn: 'root' })
export class SoundService implements OnDestroy {
  private connection: signalR.HubConnection | null = null;
  private audioContext: AudioContext | null = null;
  private mediaStream: MediaStream | null = null;
  private processor: ScriptProcessorNode | null = null;
  private source: MediaStreamAudioSourceNode | null = null;

  micStreamActive = signal(false);
  voiceToRoverActive = signal(false);

  ngOnDestroy() {
    this.stopMicStream();
    this.stopVoiceToRover();
    this.disconnect();
  }

  async connect(): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) return;

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${window.location.origin}/hubs/sound`)
      .withAutomaticReconnect()
      .build();

    await this.connection.start();
  }

  disconnect(): void {
    this.connection?.stop();
    this.connection = null;
  }

  async startMicStream(): Promise<void> {
    await this.connect();
    if (!this.connection) return;

    this.audioContext = new AudioContext({ sampleRate: SAMPLE_RATE });
    if (this.audioContext.state === 'suspended') {
      await this.audioContext.resume();
    }
    this.nextPlayTime = 0;
    this.playQueue.length = 0;

    this.connection.on('ReceiveMicChunk', (data: unknown) => {
      this.playMicChunk(data);
    });
    this.connection.invoke('SubscribeToMicStream').catch(() => {});

    this.micStreamActive.set(true);
  }

  stopMicStream(): void {
    this.connection?.invoke('UnsubscribeFromMicStream').catch(() => {});
    this.connection?.off('ReceiveMicChunk');
    this.audioContext?.close();
    this.audioContext = null;
    this.micStreamActive.set(false);
  }

  private nextPlayTime = 0;
  private readonly playQueue: Float32Array[] = [];

  private playMicChunk(data: unknown): void {
    if (!this.audioContext) return;

    let samples: Float32Array;
    if (typeof data === 'string') {
      const binary = atob(data);
      const bytes = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
      samples = new Float32Array(bytes.length / 2);
      for (let i = 0; i < samples.length; i++) {
        const s = bytes[i * 2]! | (bytes[i * 2 + 1]! << 8);
        samples[i] = (s < 32768 ? s : s - 65536) / 32768;
      }
    } else if (data instanceof Array) {
      const arr = data as number[];
      samples = new Float32Array(arr.length / 2);
      for (let i = 0; i < samples.length; i++) {
        const s = (arr[i * 2] ?? 0) | ((arr[i * 2 + 1] ?? 0) << 8);
        samples[i] = (s < 32768 ? s : s - 65536) / 32768;
      }
    } else {
      return;
    }

    this.playQueue.push(samples);
    const minBuffers = 2;
    if (this.playQueue.length < minBuffers) return;

    const toPlay = this.playQueue.shift()!;
    const buffer = this.audioContext.createBuffer(1, toPlay.length, SAMPLE_RATE);
    buffer.copyToChannel(toPlay, 0);

    const now = this.audioContext.currentTime;
    const startTime = Math.max(now, this.nextPlayTime);
    this.nextPlayTime = startTime + buffer.duration;

    const node = this.audioContext.createBufferSource();
    node.buffer = buffer;
    node.connect(this.audioContext.destination);
    node.start(startTime);
  }

  async startVoiceToRover(): Promise<void> {
    console.log('[Sound] startVoiceToRover called');

    if (!navigator.mediaDevices?.getUserMedia) {
      console.error('[Sound] getUserMedia not available (requires HTTPS or localhost)');
      return;
    }

    await this.connect();
    if (!this.connection) {
      console.error('[Sound] No SignalR connection');
      return;
    }

    try {
      this.mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (err) {
      console.error('[Sound] getUserMedia failed:', err);
      return;
    }

    try {
      this.audioContext = new AudioContext();
    if (this.audioContext.state === 'suspended') {
      await this.audioContext.resume();
    }

    // Gain to boost quiet mic (rover expects reasonable levels)
    const gainNode = this.audioContext.createGain();
    gainNode.gain.value = 2.5;
    this.source = this.audioContext.createMediaStreamSource(this.mediaStream);
    this.source.connect(gainNode);

    const inputRate = this.audioContext.sampleRate;
    this.processor = this.audioContext.createScriptProcessor(2048, 1, 1);
    let sendCount = 0;
    this.processor.onaudioprocess = (e) => {
      const input = e.inputBuffer.getChannelData(0);
      const resampled = this.downsample(input, inputRate, SAMPLE_RATE);
      const byteLength = resampled.length * 2;
      const buffer = new ArrayBuffer(byteLength);
      const view = new DataView(buffer);
      let rms = 0;
      for (let i = 0; i < resampled.length; i++) {
        const s = Math.max(-1, Math.min(1, resampled[i]));
        rms += s * s;
        const val = s < 0 ? Math.round(s * 32768) : Math.round(s * 32767);
        view.setInt16(i * 2, val, true);
      }
      rms = Math.sqrt(rms / resampled.length);
      const bytes = new Uint8Array(buffer);
      let binary = '';
      const CHUNK = 8192;
      for (let i = 0; i < bytes.length; i += CHUNK) {
        binary += String.fromCharCode.apply(null, Array.from(bytes.subarray(i, i + CHUNK)));
      }
      const base64 = btoa(binary);
      sendCount++;
      if (sendCount <= 3 || sendCount % 50 === 0) {
        console.log(
          `[Sound] voice->rover chunk #${sendCount}: ${resampled.length} samples (${byteLength}B), RMS=${rms.toFixed(4)}`
        );
      }
      this.connection?.invoke('SendSpeakerChunk', base64).catch((err) => {
        console.warn('[Sound] SendSpeakerChunk failed:', err);
      });
    };
    gainNode.connect(this.processor);

    console.log(
      `[Sound] voice->rover started: context ${inputRate}Hz, resampling to ${SAMPLE_RATE}Hz, buffer 2048`
    );

    const silence = this.audioContext.createGain();
    silence.gain.value = 0;
    this.processor.connect(silence);
    silence.connect(this.audioContext.destination);
    this.voiceToRoverActive.set(true);
    } catch (err) {
      console.error('[Sound] voice setup failed:', err);
      this.mediaStream?.getTracks().forEach((t) => t.stop());
    }
  }

  private downsample(input: Float32Array, fromRate: number, toRate: number): Float32Array {
    if (fromRate <= toRate) return input;
    const ratio = fromRate / toRate;
    const outLen = Math.floor(input.length / ratio);
    const out = new Float32Array(outLen);
    for (let i = 0; i < outLen; i++) {
      const srcIdx = i * ratio;
      const lo = Math.floor(srcIdx);
      const hi = Math.min(lo + 1, input.length - 1);
      const frac = srcIdx - lo;
      out[i] = input[lo]! * (1 - frac) + input[hi]! * frac;
    }
    return out;
  }

  stopVoiceToRover(): void {
    this.processor?.disconnect();
    this.source?.disconnect();
    this.mediaStream?.getTracks().forEach((t) => t.stop());
    this.audioContext?.close();
    this.processor = null;
    this.source = null;
    this.mediaStream = null;
    this.audioContext = null;
    this.voiceToRoverActive.set(false);
  }
}
