import json
import logging
import os
import re
import wave
from datetime import datetime
from pathlib import Path

from fastapi import FastAPI, WebSocket, WebSocketDisconnect

SAMPLE_RATE = 16000
SAMPLE_WIDTH = 2
CHANNELS = 1

RECORDINGS_DIR = Path(os.environ.get("QUACK_RECORDINGS_DIR", Path(__file__).parent / "recordings"))

log = logging.getLogger("quacktion")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

app = FastAPI()


def _open_recording(device: str) -> tuple[wave.Wave_write, Path]:
    RECORDINGS_DIR.mkdir(parents=True, exist_ok=True)
    safe_device = re.sub(r"[^A-Za-z0-9_-]", "_", device) or "unknown"
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    path = RECORDINGS_DIR / f"{safe_device}-{stamp}.wav"
    wav = wave.open(str(path), "wb")
    wav.setnchannels(CHANNELS)
    wav.setsampwidth(SAMPLE_WIDTH)
    wav.setframerate(SAMPLE_RATE)
    return wav, path


@app.websocket("/ws")
async def duck_socket(ws: WebSocket) -> None:
    await ws.accept()
    device = "unknown"
    wav: wave.Wave_write | None = None
    path: Path | None = None
    pending = b""
    try:
        while True:
            msg = await ws.receive()
            if msg["type"] == "websocket.disconnect":
                break
            if msg.get("text") is not None:
                try:
                    data = json.loads(msg["text"])
                except json.JSONDecodeError:
                    log.warning("Ignoring non-JSON text frame from %s", device)
                    continue
                kind = data.get("type")
                if kind == "hello":
                    device = str(data.get("device", "unknown"))
                    log.info("hello from %s", device)
                else:
                    log.info("%s: %s", device, data)
            elif msg.get("bytes") is not None:
                if wav is None:
                    wav, path = _open_recording(device)
                    log.info("Recording %s to %s", device, path)
                chunk = pending + msg["bytes"]
                usable = len(chunk) - len(chunk) % SAMPLE_WIDTH
                wav.writeframes(chunk[:usable])
                pending = chunk[usable:]
    except WebSocketDisconnect:
        pass
    finally:
        if wav is not None:
            frames = wav.getnframes()
            wav.close()
            log.info("Saved %s (%.2f s)", path, frames / SAMPLE_RATE)
        log.info("%s disconnected", device)
