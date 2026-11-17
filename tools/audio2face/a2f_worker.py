#!/usr/bin/env python3
"""Resident Audio2Face-3D worker for the Tavern's Unity bridge (Assets/Scripts/Audio2FaceBridge.cs).

Loads the model once, prints {"ready": true, "load_s": ...} on stdout, then answers one JSON request per stdin
line with one JSON reply line:

  request  {"id": 7, "pcm": "/tmp/.../line.pcm", "rate": 24000, "emotion": "joy=0.5", "fps": 30, "out": "/tmp/.../line.json"}
  reply    {"id": 7, "ok": true, "frames": 171, "ms": 1450.2}   or   {"id": 7, "ok": false, "error": "..."}

"pcm" is raw 16-bit little-endian mono PCM. The track goes to "out" as
{"fps": 30.0, "frames": N, "names": [...], "weights": [frame-major, N * len(names) values in 0..1]}.
Only JSON goes to stdout; diagnostics go to stderr. Model directory: A2F_MODEL_DIR (or the first argument),
ONNX Runtime threads: A2F_THREADS (0 = its default).
"""
import json
import os
import sys
import time

import numpy as np

from a2f_mark import A2F, DEFAULT_MODEL_DIR


def reply(message):
    sys.stdout.write(json.dumps(message, separators=(',', ':')) + '\n')
    sys.stdout.flush()


def handle(a2f, request):
    started = time.perf_counter()
    pcm = np.fromfile(request['pcm'], dtype='<i2').astype(np.float32) / 32768.0
    fps = float(request.get('fps', 30.0))
    names, weights = a2f.process_sidekick(pcm, int(request['rate']), request.get('emotion', ''), fps)
    track = {'fps': fps, 'frames': int(weights.shape[0]), 'names': names,
             'weights': np.round(weights, 4).ravel().tolist()}
    with open(request['out'], 'w') as f:
        json.dump(track, f, separators=(',', ':'))
    return {'id': request.get('id'), 'ok': True, 'frames': track['frames'],
            'ms': round((time.perf_counter() - started) * 1000.0, 1)}


def main():
    model_dir = os.environ.get('A2F_MODEL_DIR') or (sys.argv[1] if len(sys.argv) > 1 else DEFAULT_MODEL_DIR)
    threads = int(os.environ.get('A2F_THREADS', '0') or 0)
    started = time.perf_counter()
    try:
        a2f = A2F(model_dir, threads=threads)
    except Exception as e:  # Reported to Unity, which then leaves the faces on their own lip sync
        reply({'ready': False, 'error': f'{type(e).__name__}: {e}'})
        return 1
    reply({'ready': True, 'load_s': round(time.perf_counter() - started, 3)})

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request = None
        try:
            request = json.loads(line)
            reply(handle(a2f, request))
        except Exception as e:
            reply({'id': request.get('id') if isinstance(request, dict) else None, 'ok': False,
                   'error': f'{type(e).__name__}: {e}'})
    return 0


if __name__ == '__main__':
    sys.exit(main())
