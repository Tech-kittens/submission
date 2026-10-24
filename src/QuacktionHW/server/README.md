# Quacktion server

```powershell
py -3 -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python -m uvicorn main:app --host 0.0.0.0 --port 8000
```

The duck connects to `ws://<laptop-ip>:8000/ws`. Each connection's audio is saved to `recordings/<device>-<timestamp>.wav`.

Stream a test tone without hardware:

```powershell
.venv\Scripts\python fake_duck.py                 # 440 Hz for 3 s
.venv\Scripts\python fake_duck.py --wav clip.wav  # 16 kHz 16-bit mono WAV
```

Tests: `.venv\Scripts\python -m pytest`
