# Audio2Face dev bridge

Drives NPC mouths with NVIDIA Audio2Face-3D v2.3 ("Mark") in the Unity Editor on macOS, as a stand-in until
the model runs through the Xybrid SDK. Each spoken TTS line goes to a resident Python worker
(`a2f_worker.py`), which returns Sidekick mouth and jaw weights at 30 fps; NPCFace plays them under its own
brows, blinks, gaze and head gestures (the "hybrid" face).

## Setup (once per machine)

1. Model files (NVIDIA Open Model License, about 300 MB; not in the repository): download
   [nvidia/Audio2Face-3D-v2.3-Mark](https://huggingface.co/nvidia/Audio2Face-3D-v2.3-Mark) into
   `~/Developer/xybrid-models/audio2face-3d-v2.3-mark/` (it needs `network.onnx`, `network_info.json`,
   `model_config.json`, `model_data.npz`, `bs_skin.npz`, `bs_skin_config.json`, `bs_tongue.npz`,
   `bs_tongue_config.json` and `implicit_emo_db.npz`).
2. Python environment (ignored by Git):

   ```sh
   cd tools/audio2face
   python3 -m venv .venv
   .venv/bin/pip install -r requirements.txt
   ```

Other locations can be set in the project `.env`: `A2F_PYTHON`, `A2F_MODEL_DIR` (`~/` is expanded) and
`A2F_THREADS` (ONNX Runtime threads, default 6).

## Use

On the `DialogueManagerV2` component, set **Audio2Face (dev bridge) > Audio2Face**:

- **Off**: Rhubarb lip sync only.
- **Swap When Ready**: the voice starts at once on the Rhubarb mouth, which hands over to Audio2Face once
  its track is ready, about a quarter of the line's length later.
- **Hold Voice Until Ready**: each line waits for its track and uses it from the first word.

The Console logs `Audio2Face held ...` or `Audio2Face mouth for ...` for each line. If the setup is
missing, the bridge warns once and the faces keep Rhubarb.

## Files

- `a2f_mark.py`: the Audio2Face pipeline (windowing, network, skin post-processing, bounded least-squares
  blend shape solve), ported from NVIDIA's MIT-licensed
  [Audio2Face-3D SDK](https://github.com/NVIDIA/Audio2Face-3D-SDK) (`LICENSE-NVIDIA-Audio2Face-3D-SDK.txt`),
  plus the ARKit to Sidekick mapping. `process_sidekick()` is the game path. Run as a script, it writes the
  full SDK-faithful output for one WAV.
- `a2f_worker.py`: the resident worker that Unity's `Audio2FaceBridge` talks to (JSON lines over stdin and
  stdout).
