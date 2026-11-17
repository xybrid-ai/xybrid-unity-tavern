#!/usr/bin/env python3
"""NVIDIA Audio2Face-3D v2.3 ("Mark", regression) -> ARKit / Sidekick blendshape weights, on CPU.

Reproduces, in numpy + onnxruntime, the geometry + blendshape-solve path of NVIDIA's open-source
Audio2Face-3D SDK (github.com/NVIDIA/Audio2Face-3D-SDK, MIT). Source references below are files in
that repo (audio2face-sdk/source/audio2face-core/... unless noted):

  1. Audio windowing      executor_regression_core.cpp GetProgressParameters, audio2x-common
                          audio_accumulator.cpp WindowProgress::GetStartSample / GetNbAvailableWindows /
                          AudioAccumulator::Read (zero padding, input_strength gain)
  2. Network inputs       executor_regression_core.cpp Init (implicit emotion = emotion DB row
                          (source_shot, source_frame)), model_regression.cpp (emotion = implicit|explicit)
  3. Output split         model_shared.cpp (skin PCA | tongue PCA | jaw 5x3 | eyes 4)
  4. PCA reconstruction   multitrack_animator.cpp MultiTrackAnimatorPcaReconstruction::Animate (no mean)
  5. Skin post-process    multitrack_animator_cuda.cu ComputeSkinPostProcessing + face mask kernel,
                          multitrack_animator.cpp SetParametersInternal (smoothing alpha)
  6. Tongue post-process  multitrack_animator_cuda.cu AnimateTongueKernel
  7. Jaw transform        multitrack_animator_cuda.cu ComputeJawTransformKernel (Kabsch on lower teeth)
  8. Eyes rotation        multitrack_animator_cuda.cu ComputeEyesRotation (strength, offsets, saccade)
  9. Blendshape solve     blendshape_solver_base.cpp Prepare (AMat, regularizer scales),
                          blendshape_solver.cpp SolveAsync/cpuSolve (b, temporal, cancel, mult/offset),
                          bvls.cpp solveSystem (bounded-variable least squares) -- ported 1:1 below.
 10. Config parsing       parse_helper.cpp ReadAnimatorParams_INTERNAL (model_config.json keys),
                          BlendshapeSolverConfigOwner::Init (bs_*_config.json keys)

The math is ported from that MIT-licensed SDK (see LICENSE-NVIDIA-Audio2Face-3D-SDK.txt); the model files
themselves are under the NVIDIA Open Model License and stay outside the repository (README.md).

A2F.process_sidekick() is the game path used by a2f_worker.py (one rest-normalized solve per frame, Sidekick
fixes applied). A2F.process() keeps the full SDK-faithful output for offline inspection:
  a2f_mark.py <wav> <out.json> [--emotion joy=0.5,anger=0.2] [--fps 30] [--model-dir DIR]
"""
import argparse
import json
import math
import os
import sys
import time
import wave
from fractions import Fraction

import numpy as np
import onnxruntime as ort

DEFAULT_MODEL_DIR = os.environ.get('A2F_MODEL_DIR') or os.path.expanduser(
    '~/Developer/xybrid-models/audio2face-3d-v2.3-mark')


# ----------------------------------------------------------------------------------------------
# bvls.cpp port (bounded-variable least squares: min ||A x - B||^2 s.t. l <= x <= u)
# ----------------------------------------------------------------------------------------------
def _lstsq(A, b):
    # Eigen JacobiSVD::solve == minimum-norm least squares
    return np.linalg.lstsq(A, b, rcond=None)[0]


def _constrained_min(x, A, B, on_bound):
    free = np.flatnonzero(on_bound == 0)
    if free.size == 0:
        return x
    if free.size == on_bound.size:
        return _lstsq(A, B)
    x0 = x.copy()
    x0[free] = 0.0
    xf = _lstsq(A[:, free], B - A @ x0)
    x = x.copy()
    x[free] = xf
    return x


def _update_on_bound(x, l, u):
    ob = np.zeros(x.size, np.int64)
    ob[x == u] = 1
    ob[x == l] = -1          # C++ checks x==l first
    return ob


def _kkt(g, on_bound):
    gk = g * on_bound
    free = on_bound == 0
    gk[free] = np.abs(g[free])
    return float(gk.max())


def _var_to_detach(x, A, B, l, u):
    g = A.T @ (A @ x - B)
    val = np.zeros(x.size)
    atl = x == l
    atu = (~atl) & (x == u)
    val[atl & (g < 0)] = -g[atl & (g < 0)]
    val[atu & (g > 0)] = g[atu & (g > 0)]
    i = int(np.argmax(val))
    return i if val[i] > 0.0 else -1


def _make_point_feasible(x, s, l, u):
    alpha, idx, bv = 1.0, -1, 0.0
    for i in range(x.size):           # first strictly-smaller alpha wins, as in C++
        if x[i] == s[i]:
            continue
        if s[i] < l[i]:
            at = (x[i] - l[i]) / (x[i] - s[i])
            if at < alpha:
                idx, bv, alpha = i, l[i], at
        elif s[i] > u[i]:
            at = (u[i] - x[i]) / (s[i] - x[i])
            if at < alpha:
                idx, bv, alpha = i, u[i], at
    x = x + alpha * (s - x)
    if idx != -1:
        x[idx] = bv
    return alpha, x


def bvls_solve(A, B, l, u, tol=1e-10):
    n = B.size
    # initialFeasiblePoint
    x = np.zeros(n)
    on = np.zeros(n, np.int64)
    while True:
        x = _constrained_min(x, A, B, on)
        lo = (on == 0) & (x < l)
        hi = (on == 0) & ~lo & (x > u)
        x[lo] = l[lo]; on[lo] = -1
        x[hi] = u[hi]; on[hi] = 1
        if not (lo.any() or hi.any()):
            break
    r = A @ x - B
    g = A.T @ r
    cost = 0.5 * float(r @ r)
    on = _update_on_bound(x, l, u)
    opt = _kkt(g, on)
    prev = opt
    for _ in range(n):
        if opt < tol:
            break
        j = _var_to_detach(x, A, B, l, u)
        if j == -1:
            break
        on[j] = 0
        while True:
            s = _constrained_min(x.copy(), A, B, on)
            alpha, x = _make_point_feasible(x, s, l, u)
            if alpha == 1.0:
                break
            on = _update_on_bound(x, l, u)
        r = A @ x - B
        g = A.T @ r
        cost_new = 0.5 * float(r @ r)
        if cost_new > cost:
            break
        cost = cost_new
        opt = _kkt(g, on)
        if abs(prev - opt) < 1e-6:
            break
        prev = opt
    return x


# ----------------------------------------------------------------------------------------------
# Blendshape solver (blendshape_solver_base.cpp Prepare + blendshape_solver.cpp cpuSolve)
# ----------------------------------------------------------------------------------------------
class BlendshapeSolver:
    def __init__(self, bs_npz, cfg, temporal_consistent=False):
        p = cfg['blendshape_params']
        names = [n.decode() for n in bs_npz['poseNames']]
        assert names[0] == 'neutral'
        self.pose_names = names[1:]
        npose = len(self.pose_names)
        neutral = bs_npz['neutral'].astype(np.float64)
        self.mask = bs_npz['frontalMask'].astype(np.int64) if 'frontalMask' in bs_npz.files else None
        sel = self.mask if self.mask is not None else slice(None)
        self.active = np.array(p['bsSolveActivePoses'], bool)
        assert self.active.size == npose
        self.act_idx = np.flatnonzero(self.active)
        # delta poses restricted to active poses + frontal mask (Prepare: blendshapeDeltas)
        self.A = np.stack([bs_npz[self.pose_names[i]][sel].reshape(-1).astype(np.float64)
                           for i in self.act_idx], 1)
        self.neutral_masked = neutral[sel].reshape(-1)
        nb = self.act_idx.size
        # scaleFactor = (|bbox(full neutral)| / templateBBSize)^2
        bb = float(np.linalg.norm(neutral.max(0) - neutral.min(0)))
        self.template_bb = float(p.get('templateBBSize', 54.7))
        self.scale = (bb / self.template_bb) ** 2
        self.L1, self.L2 = float(p['strengthL1regularization']), float(p['strengthL2regularization'])
        self.T, self.Sym = float(p['strengthTemporalSmoothing']), float(p['strengthSymmetry'])

        def pairs(arr):
            act = [arr[i] for i in self.act_idx]       # re-indexed to active poses
            d = {}
            for k, v in enumerate(act):
                if v != -1:
                    d.setdefault(v, []).append(k)
            return [tuple(v) for v in d.values() if len(v) == 2]
        sym = pairs(p['bsSolveSymmetryPoses'])
        self.cancel = pairs(p['bsSolveCancelPoses'])
        Sm = np.zeros((len(sym), nb))
        for r, (i, j) in enumerate(sym):
            Sm[r, i], Sm[r, j] = 1.0, -1.0
        s = self.scale
        self.AMat = (self.A.T @ self.A
                     + self.L1 * self.L1 * 0.25 * s * np.ones((nb, nb))
                     + self.L2 * 10.0 * s * np.eye(nb)
                     + self.T * 100.0 * s * np.eye(nb)
                     + self.Sym * 10.0 * s * Sm.T @ Sm)
        # SDK quirk (CPU + GPU): b uses TemporalReg*scale*prev while AMat uses TemporalReg*100*scale.
        self.b_temporal = self.T * s * (100.0 if temporal_consistent else 1.0)
        self.mult = np.array(p['bsWeightMultipliers'], np.float64)
        self.offs = np.array(p['bsWeightOffsets'], np.float64)
        self.l = np.zeros(nb)
        self.prev = np.zeros(nb)
        self.n_sym_pairs = len(sym)

    def reset(self):
        self.prev[:] = 0.0

    def masked_target(self, full_pose_flat):
        if self.mask is None:
            return full_pose_flat
        return full_pose_flat.reshape(-1, 3)[self.mask].reshape(-1)

    def solve(self, target_masked):
        """target_masked: flattened target positions on the solver's masked vertices."""
        atb = self.A.T @ (target_masked - self.neutral_masked)
        b = atb + self.b_temporal * self.prev
        u = np.ones_like(self.l)
        x = bvls_solve(self.AMat, b, self.l, u)
        if self.cancel:
            for i, j in self.cancel:
                u[j if x[i] >= x[j] else i] = 1e-10
            x = bvls_solve(self.AMat, b, self.l, u)
        self.prev = x.copy()
        w = np.zeros(len(self.pose_names))
        w[self.act_idx] = x
        return w * self.mult + self.offs


# ----------------------------------------------------------------------------------------------
# Sidekick mapping
# ----------------------------------------------------------------------------------------------
SIDEKICK_NAMES = [
    'cheekPuffLeft', 'eyeWideUpperLeft', 'cheekPuffRight', 'cheekSquintLeft', 'eyeLookOutLeft', 'cheekSquintRight',
    'eyeBlinkLowerLeft', 'mouthClose', 'mouthDimpleLeft', 'mouthDimpleRight', 'mouthFrownLeft', 'eyeLookOutRight',
    'mouthFrownRight', 'mouthFunnel', 'mouthLeft', 'eyeLookInRight', 'eyeBlinkLowerRight', 'mouthLowerDownLeft',
    'mouthLowerDownRight', 'eyeWideUpperRight', 'mouthPressLeft', 'mouthPressRight', 'mouthPucker', 'mouthRight',
    'eyeLookUpLeft', 'mouthRollLower', 'mouthRollOutLower', 'mouthRollOutUpper', 'mouthRollUpper', 'eyeBlinkUpperLeft',
    'mouthShrugLower', 'mouthShrugUpper', 'mouthSmileLeft', 'mouthSmileRight', 'mouthStretchLeft', 'eyeLookUpRight',
    'mouthStretchRight', 'mouthUpperUpLeft', 'mouthUpperUpRight', 'eyeBlinkUpperRight', 'eyeLookDownLeft',
    'eyeSquintLeft', 'eyeLookDownRight', 'eyeLookInLeft', 'eyeSquintRight', 'eyeWideLowerLeft', 'eyeWideLowerRight',
    'cheekHollowLeft', 'cheekHollowRight', 'irisSmallLeft', 'irisSmallRight', 'jawBackward', 'jawForward',
    'tongueCurlUp', 'tongueDown', 'tongueIn', 'tongueLower', 'tongueOut', 'tongueRaise', 'tongueSideCurlDown',
    'tongueSideCurlUp', 'tongueTwistLeft', 'tongueTwistRight', 'tongueUp', 'jawLeft', 'jawOpen', 'jawRight',
    'tongueCurlDown', 'tongueCurlLeft', 'tongueCurlRight', 'noseSneerLeft', 'noseSneerRight', 'browFrownLeft',
    'browInnerDownLeft', 'browInnerUpLeft', 'browRaiseLeft', 'browOuterDownLeft', 'browOuterUpLeft', 'browFrownRight',
    'browInnerDownRight', 'browInnerUpRight', 'browRaiseRight', 'browOuterDownRight', 'browOuterUpRight']
assert len(SIDEKICK_NAMES) == 84

# Measured on Mark's bs_skin.npz (see report): mouthClose alone drops the lower lip/chin ~0.18-0.20 of
# jawOpen with the lips kept together (absolute pose, not a jawOpen corrective). Sidekick mouthClose is a
# corrective that seals the lips over jawOpen 0.37 (project memory "sidekick-face-rig").
MARK_MOUTHCLOSE_JAW = 0.20
SIDEKICK_MOUTHCLOSE_SEAL_JAW = 0.37
# Mark eyeBlink: lower lid rises ~0.10 vs upper lid ~0.49-0.54 cm -> ~0.2.
BLINK_LOWER = 0.2
# Mark browDown: inner/middle brow down + strong medial pull (knit); outer third ~60% of middle.
BROWDOWN_INNER, BROWDOWN_OUTER, BROWDOWN_FROWN = 1.0, 0.5, 0.5

DIRECT = ['cheekSquintLeft', 'cheekSquintRight', 'eyeSquintLeft', 'eyeSquintRight', 'jawForward', 'jawLeft',
          'jawRight', 'mouthDimpleLeft', 'mouthDimpleRight', 'mouthFrownLeft', 'mouthFrownRight', 'mouthFunnel',
          'mouthLeft', 'mouthRight', 'mouthLowerDownLeft', 'mouthLowerDownRight', 'mouthPressLeft',
          'mouthPressRight', 'mouthPucker', 'mouthRollLower', 'mouthRollUpper', 'mouthShrugLower',
          'mouthShrugUpper', 'mouthSmileLeft', 'mouthSmileRight', 'mouthStretchLeft', 'mouthStretchRight',
          'mouthUpperUpLeft', 'mouthUpperUpRight', 'noseSneerLeft', 'noseSneerRight', 'browOuterUpLeft',
          'browOuterUpRight']
TONGUE_MAP = {  # Sidekick <- A2F tongue pose (bs_tongue.npz names)
    'tongueCurlUp': 'tongueTipUp', 'tongueCurlDown': 'tongueTipDown', 'tongueCurlLeft': 'tongueTipLeft',
    'tongueCurlRight': 'tongueTipRight', 'tongueSideCurlUp': 'tongueRollUp', 'tongueSideCurlDown': 'tongueRollDown',
    'tongueTwistLeft': 'tongueRollLeft', 'tongueTwistRight': 'tongueRollRight', 'tongueUp': 'tongueUp',
    'tongueDown': 'tongueDown', 'tongueIn': 'tongueIn'}


# Game fixes on top of the mapping, found by rendering the output on Sidekick faces: its tongue shapes push the tongue
# through the chin at A2F's resting tongueDown (so the tongue is left out), and mouthRollLower folds a second crease
# under the lower lip above ~0.3 (project memory "sidekick-face-rig").
SIDEKICK_CAPS = {'mouthRollLower': 0.3}


def map_to_sidekick(W, arkit, Tw, tongue_names, tongue_offsets):
    a = {n: W[:, i] for i, n in enumerate(arkit)}
    t = {n: Tw[:, i] for i, n in enumerate(tongue_names)}
    out = {}
    for n in DIRECT:
        out[n] = a[n]
    for s in ('Left', 'Right'):
        out['eyeBlinkUpper' + s] = a['eyeBlink' + s]
        out['eyeBlinkLower' + s] = BLINK_LOWER * a['eyeBlink' + s]
        out['eyeWideUpper' + s] = a['eyeWide' + s]
        out['cheekPuff' + s] = a['cheekPuff']
        out['browInnerUp' + s] = a['browInnerUp']
        out['browInnerDown' + s] = BROWDOWN_INNER * a['browDown' + s]
        out['browOuterDown' + s] = BROWDOWN_OUTER * a['browDown' + s]
        out['browFrown' + s] = BROWDOWN_FROWN * a['browDown' + s]
    jaw = np.clip(a['jawOpen'] + MARK_MOUTHCLOSE_JAW * a['mouthClose'], 0, 1)
    out['jawOpen'] = jaw
    mc = MARK_MOUTHCLOSE_JAW * a['mouthClose'] / SIDEKICK_MOUTHCLOSE_SEAL_JAW
    out['mouthClose'] = np.minimum(np.minimum(mc, 1.0), jaw / SIDEKICK_MOUTHCLOSE_SEAL_JAW)
    for sk, tn in TONGUE_MAP.items():
        k = tongue_names.index(tn)
        out[sk] = np.clip(t[tn] - tongue_offsets[k], 0, 1)   # drop Mark-specific rest offset (Tw must be unclipped)
    names = [n for n in SIDEKICK_NAMES if not n.startswith('eyeLook')]
    zero = np.zeros(W.shape[0])
    M = np.stack([np.clip(out.get(n, zero), 0, 1) for n in names], 1)
    return names, M


# ----------------------------------------------------------------------------------------------
# Pipeline
# ----------------------------------------------------------------------------------------------
def resample(x, sr_in, sr_out):
    """Band-limited resampling of a whole clip (FFT), so no ffmpeg/scipy is needed."""
    x = np.asarray(x, np.float64)
    if sr_in == sr_out or x.size == 0:
        return x
    n_out = max(1, int(round(x.size * sr_out / sr_in)))
    X = np.fft.rfft(x)
    Y = np.zeros(n_out // 2 + 1, complex)
    k = min(X.size, Y.size)
    Y[:k] = X[:k]
    return np.fft.irfft(Y, n_out) * (n_out / x.size)


def load_audio_16k(path, sr):
    """16-bit PCM WAV (mono or channel-averaged) resampled to sr."""
    with wave.open(path, 'rb') as w:
        if w.getsampwidth() != 2:
            raise ValueError(f'{path}: only 16-bit PCM WAV is supported')
        ch, rate = w.getnchannels(), w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), '<i2').astype(np.float64) / 32768.0
    if ch > 1:
        x = x.reshape(-1, ch).mean(1)
    return resample(x, rate, sr).astype(np.float32)


def kabsch_open_deg(src, tgt):
    sm, tm = src.mean(0), tgt.mean(0)
    H = (src - sm).T @ (tgt - tm)
    U, _, Vt = np.linalg.svd(H)
    d = np.sign(np.linalg.det(Vt.T @ U.T))
    R = Vt.T @ np.diag([1.0, 1.0, d]) @ U.T       # tgt ~= R @ src + t
    return math.degrees(math.atan2(R[2, 1], R[1, 1])), R, tm - R @ sm


def parse_emotion(s, names):
    e = np.zeros(len(names), np.float32)
    for kv in filter(None, (s or '').split(',')):
        k, v = kv.split('=')
        k = k.strip().lower()
        if k not in names:
            raise SystemExit(f'unknown emotion {k!r}; valid: {names}')
        e[names.index(k)] = float(v)
    return e




class A2F:
    """Loads Mark v2.3 once; process() runs one clip (SDK geometry executor + blendshape solve executor)."""

    def __init__(self, model_dir=DEFAULT_MODEL_DIR, temporal_consistent=False, threads=0):
        t0 = time.perf_counter()
        self.model_dir = model_dir
        self.temporal_consistent = temporal_consistent
        J = lambda f: json.load(open(os.path.join(model_dir, f)))
        self.info = J('network_info.json')
        self.cfg = J('model_config.json')['config']
        self.skin_cfg, self.tongue_cfg = J('bs_skin_config.json'), J('bs_tongue_config.json')
        md = np.load(os.path.join(model_dir, 'model_data.npz'), allow_pickle=False)
        bs = np.load(os.path.join(model_dir, 'bs_skin.npz'), allow_pickle=False)
        bt = np.load(os.path.join(model_dir, 'bs_tongue.npz'), allow_pickle=False)
        edb = np.load(os.path.join(model_dir, 'implicit_emo_db.npz'), allow_pickle=False)
        so = ort.SessionOptions(); so.log_severity_level = 3
        if threads > 0:
            so.intra_op_num_threads = threads
        self.sess = ort.InferenceSession(os.path.join(model_dir, 'network.onnx'), so,
                                         providers=['CPUExecutionProvider'])
        self.skin = BlendshapeSolver(bs, self.skin_cfg, temporal_consistent)
        self.skin_n = BlendshapeSolver(bs, self.skin_cfg, temporal_consistent)   # rest-normalized solve
        self.tongue = BlendshapeSolver(bt, self.tongue_cfg, temporal_consistent)
        c, p = self.cfg, self.info['params']
        mask = self.skin.mask
        # Skin PCA basis / animator data on the solver's frontal mask only: the skin post-process is
        # per-vertex (face mask uses global min/max Y), so this is exact for everything the solve sees.
        self.P_skin = md['shapes_matrix_skin'][:, mask, :].reshape(p['num_shapes_skin'], -1).astype(np.float64)
        mean_full = md['shapes_mean_skin']
        self.mean_skin = mean_full[mask].reshape(-1).astype(np.float64)
        eye_close = md['eye_close_pose_delta'][mask].reshape(-1).astype(np.float64)
        lip_open = md['lip_open_pose_delta'][mask].reshape(-1).astype(np.float64)
        y = mean_full[:, 1].astype(np.float64)
        ymin, ymax = y.min(), y.max()
        fm = 1.0 / (1.0 + np.exp(-(c['face_mask_level'] - (y[mask] - ymin) / (ymax - ymin)) / c['face_mask_softness']))
        self.fmask3 = np.repeat(fm, 3)
        blink_offset = 0.0   # parse_helper.cpp: "blink offset is not present in the config file"
        self.const_delta = eye_close * (-c['eyelid_open_offset'] + blink_offset * c['blink_strength']) \
            + lip_open * c['lip_open_offset']
        self.P_tongue = md['shapes_matrix_tongue'].reshape(p['num_shapes_tongue'], -1).astype(np.float64)
        self.mean_tongue = md['shapes_mean_tongue'].reshape(-1).astype(np.float64)
        self.tongue_off = np.tile([0.0, c['tongue_height_offset'], c['tongue_depth_offset']],
                                  self.mean_tongue.size // 3)
        self.neutral_jaw = md['neutral_jaw'].astype(np.float64)
        self.jaw_off = np.array([0.0, c['lower_teeth_height_offset'], c['lower_teeth_depth_offset']])
        self.sacc = md['saccade_rot_matrix'].astype(np.float64)
        self.eye_offs = np.array([c['right_eye_rot_x_offset'], c['right_eye_rot_y_offset'],
                                  c['left_eye_rot_x_offset'], c['left_eye_rot_y_offset']])
        shots = [n.decode() for n in edb['emo_spec_names']]
        si = shots.index(c['source_shot'])
        assert c['source_frame'] < edb['emo_spec_size'][si]
        self.implicit = edb['emo_db'][edb['emo_spec_start'][si] + c['source_frame']].astype(np.float32)
        self.emo_names = p['explicit_emotions']
        # lip seam diagnostics (Mark mesh centre-line vertices: upper-lip lower edge / lower-lip upper edge)
        mpos = {v: k for k, v in enumerate(mask.tolist())}
        self.ui = np.array([3 * mpos[v] + 1 for v in (552, 70, 15552, 35236)])
        self.li = np.array([3 * mpos[v] + 1 for v in (15415, 80, 15417, 35104)])
        nm = self.skin.neutral_masked
        self.gap_neutral = nm[self.ui].mean() - nm[self.li].mean()
        # Rest reference for the Sidekick mapping: network on an all-zero window, neutral explicit emotion.
        r0 = self._network(np.zeros((1, self.info['audio_params']['buffer_len']), np.float32),
                           np.zeros(len(self.emo_names), np.float32))[0]
        ns = p['num_shapes_skin']
        self.rest_geo = self._skin_geo_unsmoothed(r0[:ns])
        self.rest_w = self.skin.solve(self.rest_geo); self.skin.reset()
        nt, nj = p['num_shapes_tongue'], p['result_jaw_size']
        self.rest_jaw_deg = self._jaw(r0[ns + nt:ns + nt + nj])
        self.rest_eyes_raw = r0[ns + nt + nj:]
        self.load_s = time.perf_counter() - t0

    def _network(self, wins, explicit, batch=64):
        emotion = np.concatenate([self.implicit, explicit])[None, None, :].astype(np.float32)
        outs = []
        for b0 in range(0, len(wins), batch):
            w = wins[b0:b0 + batch, None, :]
            outs.append(self.sess.run(None, {'input': w, 'emotion': np.repeat(emotion, len(w), 0)})[0][:, 0])
        return np.concatenate(outs).astype(np.float64)

    def _skin_geo_unsmoothed(self, coefs):
        c = self.cfg
        delta = c['skin_strength'] * (coefs @ self.P_skin) + self.const_delta
        return self.mean_skin + delta * c['upper_face_strength'] * (1 - self.fmask3) \
            + delta * c['lower_face_strength'] * self.fmask3

    def _jaw(self, jaw_delta):
        c = self.cfg
        tj = self.neutral_jaw + jaw_delta.reshape(-1, 3) * c['lower_teeth_strength'] + self.jaw_off
        return kabsch_open_deg(self.neutral_jaw, tj)[0]

    def process_sidekick(self, x, sr_in, emotion_str='', fps=30.0):
        """Game path: mono samples at any rate -> (Sidekick shape names, frames x names weights in 0..1).

        Same windowing, network and skin post-processing as process(), but only the rest-normalized skin solve
        runs (the face is neutral in silence), the tongue and eyes are skipped, and SIDEKICK_CAPS apply.
        """
        c, p, ap = self.cfg, self.info['params'], self.info['audio_params']
        explicit = parse_emotion(emotion_str, self.emo_names)
        sr, L, ofs = ap['samplerate'], ap['buffer_len'], ap['buffer_ofs']
        x = resample(x, sr_in, sr).astype(np.float32)
        fr = Fraction(fps).limit_denominator(1001)
        num, den = fr.numerator, fr.denominator
        target = lambda i: (i * sr * den) // num
        nf = 0
        while target(nf) < x.size:
            nf += 1
        arkit, tongue_names = self.skin_n.pose_names, self.tongue.pose_names
        if nf == 0:
            names, M = map_to_sidekick(np.zeros((0, len(arkit))), arkit, np.zeros((0, len(tongue_names))),
                                       tongue_names, self.tongue.offs)
            return names, M
        pad = np.concatenate([np.zeros(L, np.float32), x, np.zeros(L, np.float32)])
        wins = np.stack([pad[L + target(i) - ofs: L + target(i) - ofs + L] for i in range(nf)]) \
            * np.float32(c['input_strength'])
        res = self._network(wins, explicit)
        skin_pca = res[:, :p['num_shapes_skin']] @ self.P_skin

        dt = den / num
        a_lo = 1.0 - 0.5 ** (dt / c['lower_face_smoothing']) if c['lower_face_smoothing'] > 0 else -1.0
        a_up = 1.0 - 0.5 ** (dt / c['upper_face_smoothing']) if c['upper_face_smoothing'] > 0 else -1.0
        Wn = np.zeros((nf, len(arkit)))
        self.skin_n.reset()
        nm = self.skin_n.neutral_masked
        lo1 = lo2 = up1 = up2 = None
        for i in range(nf):
            delta = c['skin_strength'] * skin_pca[i] + self.const_delta
            if i == 0 or a_lo <= 0:
                lo1, lo2 = delta.copy(), delta.copy()
            else:
                lo1 += (delta - lo1) * a_lo; lo2 += (lo1 - lo2) * a_lo
            if i == 0 or a_up <= 0:
                up1, up2 = delta.copy(), delta.copy()
            else:
                up1 += (delta - up1) * a_up; up2 += (up1 - up2) * a_up
            geo = self.mean_skin + up2 * c['upper_face_strength'] * (1.0 - self.fmask3) \
                + lo2 * c['lower_face_strength'] * self.fmask3
            Wn[i] = self.skin_n.solve(geo - self.rest_geo + nm)

        # Tongue left out: zero weights map below Sidekick's zero after the rest offset is removed
        names, M = map_to_sidekick(np.clip(Wn, 0, 1), arkit, np.zeros((nf, len(tongue_names))),
                                   tongue_names, self.tongue.offs)
        for n, cap in SIDEKICK_CAPS.items():
            if n in names:
                k = names.index(n)
                M[:, k] = np.minimum(M[:, k], cap)
        return names, M

    def process(self, wav, emotion_str='', fps=30.0, keep_geo=False):
        c, p, ap = self.cfg, self.info['params'], self.info['audio_params']
        explicit = parse_emotion(emotion_str, self.emo_names)
        # --- audio + windowing (WindowProgress: start = floor(i*sr/fps) - buffer_ofs) -------------
        t1 = time.perf_counter()
        sr, L, ofs = ap['samplerate'], ap['buffer_len'], ap['buffer_ofs']
        x = load_audio_16k(wav, sr)
        N = x.size
        fr = Fraction(fps).limit_denominator(1001)
        num, den = fr.numerator, fr.denominator
        target = lambda i: (i * sr * den) // num
        nf = 0
        while target(nf) < N:          # closed track: every frame whose target sample is inside the audio
            nf += 1
        pad = np.concatenate([np.zeros(L, np.float32), x, np.zeros(L, np.float32)])
        wins = np.stack([pad[L + target(i) - ofs: L + target(i) - ofs + L] for i in range(nf)]) \
            * np.float32(c['input_strength'])
        t_audio = time.perf_counter() - t1
        # --- network ---------------------------------------------------------------------------
        t2 = time.perf_counter()
        res = self._network(wins, explicit)
        t_net = time.perf_counter() - t2
        ns, nt, nj = p['num_shapes_skin'], p['num_shapes_tongue'], p['result_jaw_size']
        c_skin, c_tongue = res[:, :ns], res[:, ns:ns + nt]
        c_jaw, c_eyes = res[:, ns + nt:ns + nt + nj], res[:, ns + nt + nj:ns + nt + nj + p['result_eyes_size']]
        # --- post-processing + solve ------------------------------------------------------------
        t3 = time.perf_counter()
        dt = den / num
        a_lo = 1.0 - 0.5 ** (dt / c['lower_face_smoothing']) if c['lower_face_smoothing'] > 0 else -1.0
        a_up = 1.0 - 0.5 ** (dt / c['upper_face_smoothing']) if c['upper_face_smoothing'] > 0 else -1.0
        skin_pca = c_skin @ self.P_skin
        npose, ntp = len(self.skin.pose_names), len(self.tongue.pose_names)
        W, Wn, Tw = np.zeros((nf, npose)), np.zeros((nf, npose)), np.zeros((nf, ntp))
        jaw_deg, eyes = np.zeros(nf), np.zeros((nf, 4))
        gap_geo, gap_bs, gap_n = np.zeros(nf), np.zeros(nf), np.zeros(nf)
        geos = [] if keep_geo else None
        self.skin.reset(); self.skin_n.reset(); self.tongue.reset()
        nm = self.skin.neutral_masked
        live, nsac = 0.0, self.sacc.shape[0]
        lo1 = lo2 = up1 = up2 = None
        for i in range(nf):
            delta = c['skin_strength'] * skin_pca[i] + self.const_delta
            if i == 0 or a_lo <= 0:
                lo1, lo2 = delta.copy(), delta.copy()
            else:
                lo1 += (delta - lo1) * a_lo; lo2 += (lo1 - lo2) * a_lo
            if i == 0 or a_up <= 0:
                up1, up2 = delta.copy(), delta.copy()
            else:
                up1 += (delta - up1) * a_up; up2 += (up1 - up2) * a_up
            geo = self.mean_skin + up2 * c['upper_face_strength'] * (1.0 - self.fmask3) \
                + lo2 * c['lower_face_strength'] * self.fmask3
            if keep_geo:
                geos.append(geo.copy())
            W[i] = self.skin.solve(geo)
            Wn[i] = self.skin_n.solve(geo - self.rest_geo + nm)
            gap_geo[i] = geo[self.ui].mean() - geo[self.li].mean() - self.gap_neutral
            rec = nm + self.skin.A @ W[i][self.skin.act_idx]
            gap_bs[i] = rec[self.ui].mean() - rec[self.li].mean() - self.gap_neutral
            recn = nm + self.skin_n.A @ Wn[i][self.skin_n.act_idx]
            gap_n[i] = recn[self.ui].mean() - recn[self.li].mean() - self.gap_neutral
            tgeo = self.mean_tongue + (c_tongue[i] @ self.P_tongue) * c['tongue_strength'] + self.tongue_off
            Tw[i] = self.tongue.solve(tgeo)
            jaw_deg[i] = self._jaw(c_jaw[i])
            fidx = int(math.fmod(c['eye_saccade_seed'] + live, nsac))
            eyes[i] = self.eye_offs + c_eyes[i] * c['eyeballs_strength'] \
                + c['saccade_strength'] * np.tile(self.sacc[fidx], 2)
            live = math.fmod(live + dt * 30.0, nsac)
        t_post = time.perf_counter() - t3

        arkit, tongue_names = self.skin.pose_names, self.tongue.pose_names
        W, Wn, Twc = np.clip(W, 0, 1), np.clip(Wn, 0, 1), np.clip(Tw, 0, 1)
        # Review fix: pass the UNclipped tongue weights so NVIDIA's +0.2 tongueDown rest offset is removed before
        # clipping (clipping first capped Sidekick tongueDown at 0.8; latent on these clips, max was 0.51).
        sk_names, SK = map_to_sidekick(Wn, arkit, Tw, tongue_names, self.tongue.offs)
        dur = N / sr
        proc = t_audio + t_net + t_post
        r4 = lambda a: np.round(a, 4).tolist()
        out = {
            'fps': float(fps), 'duration': round(dur, 4), 'source': os.path.abspath(wav),
            'emotion': {n: float(v) for n, v in zip(self.emo_names, explicit)},
            'arkit_names': arkit, 'weights': r4(W),
            'jaw_open_deg': r4(jaw_deg),
            'eye_rot_deg': r4(eyes),
            'eye_rot_layout': ['right_x', 'right_y', 'left_x', 'left_y'],
            'tongue_names': tongue_names, 'tongue_weights': r4(Twc),
            'weights_rest_normalized': r4(Wn),
            'sidekick': {'names': sk_names, 'weights': r4(SK), 'source': 'weights_rest_normalized + tongue_weights',
                         'omitted': [n for n in SIDEKICK_NAMES if n.startswith('eyeLook')]},
            'rest': {'weights': r4(self.rest_w), 'jaw_open_deg': round(self.rest_jaw_deg, 4),
                     'eye_rot_deg': r4(self.eye_offs + self.rest_eyes_raw * c['eyeballs_strength'])},
            'diagnostics': {'lip_gap_geometry_cm': r4(gap_geo), 'lip_gap_solved_cm': r4(gap_bs),
                            'lip_gap_restnorm_cm': r4(gap_n),
                            'frame_time_s': r4(np.array([target(i) / sr for i in range(nf)]))},
            'timing': {'load_s': round(self.load_s, 3), 'audio_s': round(t_audio, 3),
                       'network_s': round(t_net, 3), 'postprocess_solve_s': round(t_post, 3),
                       'rtf_excl_load': round(proc / dur, 4), 'rtf_incl_load': round((proc + self.load_s) / dur, 4)},
            'model': {'name': 'mark_v2.3', 'dir': self.model_dir, 'temporal_consistent': self.temporal_consistent,
                      'skin_scale_factor': self.skin.scale, 'tongue_scale_factor': self.tongue.scale},
        }
        if keep_geo:
            out['_geo'] = np.array(geos)
            out['_res'] = res
        return out


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    ap.add_argument('wav'); ap.add_argument('out')
    ap.add_argument('--emotion', default='', help='explicit emotions, e.g. joy=0.5,anger=0.2')
    ap.add_argument('--fps', type=float, default=30.0)
    ap.add_argument('--model-dir', default=DEFAULT_MODEL_DIR)
    ap.add_argument('--temporal-consistent', action='store_true',
                    help='use TemporalReg*100*scale in b as well (SDK uses *1; default = SDK-faithful)')
    a = ap.parse_args()
    out = A2F(a.model_dir, a.temporal_consistent).process(a.wav, a.emotion, a.fps)
    with open(a.out, 'w') as f:
        json.dump(out, f, separators=(',', ':'))
    t = out['timing']
    print(f"{os.path.basename(a.wav)}: {len(out['weights'])} frames @{a.fps:g} fps, {out['duration']:.2f}s audio, "
          f"load {t['load_s']}s, net {t['network_s']}s, post+solve {t['postprocess_solve_s']}s, "
          f"RTF {t['rtf_excl_load']} (incl. load {t['rtf_incl_load']}) -> {a.out}", file=sys.stderr)


if __name__ == '__main__':
    main()
