import math
import os
import socket
import struct
import subprocess
import sys
import time
import wave
from pathlib import Path

import pytest

SERVER_DIR = Path(__file__).parent


def _free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@pytest.fixture
def server(tmp_path):
    port = _free_port()
    env = {**os.environ, "QUACK_RECORDINGS_DIR": str(tmp_path)}
    proc = subprocess.Popen(
        [sys.executable, "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", str(port)],
        cwd=SERVER_DIR,
        env=env,
    )
    deadline = time.time() + 15
    while time.time() < deadline:
        try:
            socket.create_connection(("127.0.0.1", port), timeout=0.2).close()
            break
        except OSError:
            time.sleep(0.1)
    else:
        proc.kill()
        pytest.fail("server did not start")
    yield port, tmp_path
    proc.terminate()
    proc.wait(timeout=10)


def _wait_for_wav(directory: Path) -> Path:
    deadline = time.time() + 5
    while time.time() < deadline:
        files = list(directory.glob("*.wav"))
        if files:
            path = files[0]
            with wave.open(str(path), "rb") as wav:
                if wav.getnframes() > 0:
                    return path
        time.sleep(0.1)
    pytest.fail("no WAV saved")


def test_fake_duck_tone_is_saved_as_wav(server):
    port, recordings = server
    subprocess.run(
        [sys.executable, "fake_duck.py", "--url", f"ws://127.0.0.1:{port}/ws",
         "--device", "duck-test", "--freq", "1000", "--seconds", "2", "--fast"],
        cwd=SERVER_DIR,
        check=True,
    )
    path = _wait_for_wav(recordings)
    assert path.name.startswith("duck-test-")

    with wave.open(str(path), "rb") as wav:
        assert (wav.getframerate(), wav.getsampwidth(), wav.getnchannels()) == (16000, 2, 1)
        n = wav.getnframes()
        samples = struct.unpack(f"<{n}h", wav.readframes(n))
    assert n == 32000

    crossings = sum(1 for a, b in zip(samples, samples[1:]) if a < 0 <= b)
    assert abs(crossings / 2 - 1000) < 5

    rms = math.sqrt(sum(s * s for s in samples) / n)
    assert abs(rms - 32767 * 0.3 / math.sqrt(2)) < 100
