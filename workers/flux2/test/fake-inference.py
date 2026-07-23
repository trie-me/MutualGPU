#!/usr/bin/env python3
import json
import os
import sys
from pathlib import Path

model = "secret-leaked" if os.environ.get("MUTUALGPU_PROVIDER_KEY") else "fake-model"
print(json.dumps({"type": "ready", "device": "fake-gpu", "model": model}), flush=True)
for line in sys.stdin:
    request = json.loads(line)
    if request.get("prompt") == "blocked":
        print(json.dumps({"type": "error", "id": request["id"], "category": "SafetyCheckRejected"}), flush=True)
        continue

    output = Path(request["outputDirectory"])
    (output / "result.zip").write_bytes(b"PKfake")
    png = bytes((0x89, 0x50, 0x4E, 0x47, 0x00))
    (output / "image.png").write_bytes(png)
    (output / "thumbnail.png").write_bytes(png)
    print(json.dumps({
        "type": "progress", "id": request["id"], "phase": "inference",
        "percent": 50, "message": "fake step"
    }), flush=True)
    print(json.dumps({
        "type": "result", "id": request["id"], "metadata": {"durationMs": 1}
    }), flush=True)
