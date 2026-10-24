import argparse
import asyncio
import json
import math
import struct
import wave

import websockets

SAMPLE_RATE = 16000
FRAME_SAMPLES = 320
FRAME_BYTES = FRAME_SAMPLES * 2


def tone_pcm(freq: float, seconds: float, amplitude: float = 0.3) -> bytes:
    n = int(SAMPLE_RATE * seconds)
    peak = int(32767 * amplitude)
    samples = (int(peak * math.sin(2 * math.pi * freq * i / SAMPLE_RATE)) for i in range(n))
    return struct.pack(f"<{n}h", *samples)


def wav_pcm(path: str) -> bytes:
    with wave.open(path, "rb") as wav:
        if (wav.getframerate(), wav.getsampwidth(), wav.getnchannels()) != (SAMPLE_RATE, 2, 1):
            raise SystemExit(f"{path} must be 16 kHz, 16-bit, mono")
        return wav.readframes(wav.getnframes())


async def stream(url: str, device: str, pcm: bytes, realtime: bool = True) -> None:
    async with websockets.connect(url) as ws:
        await ws.send(json.dumps({"type": "hello", "device": device}))
        await ws.send(json.dumps({"type": "speech_start"}))
        loop = asyncio.get_running_loop()
        start = loop.time()
        for i, offset in enumerate(range(0, len(pcm), FRAME_BYTES)):
            await ws.send(pcm[offset:offset + FRAME_BYTES])
            if realtime:
                delay = start + (i + 1) * 0.02 - loop.time()
                if delay > 0:
                    await asyncio.sleep(delay)
        await ws.send(json.dumps({"type": "speech_end"}))


def main() -> None:
    parser = argparse.ArgumentParser(description="Stream audio to the server like a duck would.")
    parser.add_argument("--url", default="ws://127.0.0.1:8000/ws")
    parser.add_argument("--device", default="fake-duck")
    parser.add_argument("--freq", type=float, default=440.0, help="Test tone frequency in Hz")
    parser.add_argument("--seconds", type=float, default=3.0, help="Test tone length")
    parser.add_argument("--wav", help="Stream this 16 kHz 16-bit mono WAV instead of a tone")
    parser.add_argument("--fast", action="store_true", help="Send as fast as possible instead of real time")
    args = parser.parse_args()

    pcm = wav_pcm(args.wav) if args.wav else tone_pcm(args.freq, args.seconds)
    asyncio.run(stream(args.url, args.device, pcm, realtime=not args.fast))


if __name__ == "__main__":
    main()
