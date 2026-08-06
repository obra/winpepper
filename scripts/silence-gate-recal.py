#!/usr/bin/env python3
"""Silence-gate recalibration: replicate SilenceTrimmer gate over the archived
100-WAV corpus, mark known false-rejects, evaluate candidate constants."""
import glob, json, os, re, wave
from datetime import datetime
import numpy as np

BASE = '/mnt/c/Users/dan/AppData/Local/winpepper'
FRAME = 320  # 20 ms @ 16 kHz
FRAME_MS = 20
MASK_MS, BUDGET_MS = 1500, 100  # current deployed worst-case cue mask/budget
MASK_F = (MASK_MS + FRAME_MS - 1) // FRAME_MS
BUDGET_F = (BUDGET_MS + FRAME_MS - 1) // FRAME_MS

# ---- 1. drop lines from logs (local time) ----
drop_re = re.compile(r'^(\S+) \[INF\] dropped silent recording, (\d+) ms'
                     r'(?: \(voiced (\d+) ms, clear (\d+) ms, max frame rms ([\d.]+))?')
drops = []
for lf in sorted(glob.glob(f'{BASE}/logs/*.log')):
    for line in open(lf, errors='replace'):
        m = drop_re.match(line)
        if m:
            t = datetime.fromisoformat(m.group(1))
            drops.append(dict(t=t, ms=int(m.group(2)),
                              voiced=None if m.group(3) is None else int(m.group(3)),
                              clear=None if m.group(4) is None else int(m.group(4)),
                              maxrms=None if m.group(5) is None else float(m.group(5))))
print(f'drop lines: {len(drops)}')

# ---- 2. wavs + labels ----
idx = json.load(open(f'{BASE}/history/index.json'))
entries = idx if isinstance(idx, list) else idx.get('items') or idx.get('entries')
by_wav = {}
for e in entries:
    p = e.get('wavRelativePath')
    if p: by_wav[os.path.basename(p)] = e

FALSE_REJECT_LOCAL = [  # the three known-speech drops ("you have")
    datetime(2026, 8, 5, 0, 4, 22), datetime(2026, 8, 5, 0, 7, 36), datetime(2026, 8, 5, 0, 7, 38)]
CUE_BUG_VICTIMS = {'173b20b3', '525f0643', '003777a1', '4bf32da1'}  # real speech, fixed bug

wavs = []
for p in sorted(glob.glob(f'{BASE}/history/*/*.wav')):
    mt = datetime.fromtimestamp(os.path.getmtime(p))
    w = wave.open(p); n = w.getnframes()
    x = np.frombuffer(w.readframes(n), dtype=np.int16).astype(np.float64) / 32768.0
    w.close()
    dur = int(len(x) / 16000 * 1000)
    match = None
    for d in drops:
        if abs((mt - d['t']).total_seconds()) < 5 and abs(d['ms'] - dur) <= 200:
            match = d; break
    name = os.path.basename(p)
    e = by_wav.get(name)
    fr = bool(match) and any(abs((match['t'] - t).total_seconds()) < 3 for t in FALSE_REJECT_LOCAL)
    wavs.append(dict(p=p, name=name, mt=mt, x=x, dur=dur, drop=match,
                     transcript=(e or {}).get('rawTranscript'), false_reject=fr,
                     cue_bug=name[:8] in CUE_BUG_VICTIMS))
print(f'wavs: {len(wavs)}, matched-to-drop: {sum(1 for w in wavs if w["drop"])}, '
      f'false-rejects found: {sum(1 for w in wavs if w["false_reject"])}')

# ---- 3. gate replication ----
def gate(x):
    fc = len(x) // FRAME
    if fc == 0: return None
    fr = x[:fc * FRAME].reshape(fc, FRAME)
    rms = np.sqrt((fr * fr).mean(axis=1))
    srt = np.sort(rms)
    def pct(p):
        i = int(np.floor(p * (len(srt) - 1)))
        return srt[max(0, min(i, len(srt) - 1))]
    p90 = pct(0.90)
    mask_f = min(MASK_F, fc)
    post_max = rms[mask_f:].max() if mask_f < fc else 0.0
    out = dict(fc=fc, p90=p90, maxrms=post_max, rms=rms, mask_f=mask_f)
    if p90 < 0.004:  # P90-silent path: voiced undefined (0)
        out['voiced'] = 0
        thr = None
    else:
        noise = pct(0.10)
        thr = max(3.0 * noise, 0.002)
        thr = min(thr, 0.15 * p90)
        v = rms >= thr
        vin = v[:mask_f].sum()
        out['voiced'] = (int(v.sum()) - min(BUDGET_F, int(vin))) * FRAME_MS
    def clear_ms(floor):
        c = rms >= floor
        cin = c[:mask_f].sum()
        return (int(c.sum()) - min(BUDGET_F, int(cin))) * FRAME_MS
    out['clear_ms'] = clear_ms
    out['clear02'] = clear_ms(0.02)
    out['thr'] = thr
    return out

for w in wavs:
    w['g'] = gate(w['x'])

# ---- 4. validate replication against recent logged drops ----
print('\n== replication check (logged vs computed) ==')
for w in wavs:
    if w['drop'] and w['drop']['voiced'] is not None and w['drop']['t'] >= datetime(2026, 8, 4, 20):
        g = w['g']
        print(f"{w['name'][:8]} {w['drop']['t']:%m-%d %H:%M:%S} "
              f"logged v/c/m={w['drop']['voiced']}/{w['drop']['clear']}/{w['drop']['maxrms']:.4f}  "
              f"computed v/c/m={g['voiced']}/{g['clear02']}/{g['maxrms']:.4f}  fr={w['false_reject']}")

# ---- 5. corpus table ----
speech = [w for w in wavs if not w['drop'] and not w['cue_bug']]
drops_w = [w for w in wavs if w['drop']]
frs = [w for w in wavs if w['false_reject']]
nonspeech = [w for w in drops_w if not w['false_reject']]
print(f'\ncorpus: speech(kept)={len(speech)}  drop-archived={len(drops_w)} '
      f'(false-rejects={len(frs)}, presumed-non-speech={len(nonspeech)}, cue-bug-victims={sum(1 for w in wavs if w["cue_bug"])})')

print('\n== presumed NON-SPEECH drops (current-gate drops, exc. known speech) ==')
for w in sorted(nonspeech, key=lambda w: -w['g']['maxrms']):
    g = w['g']
    fl = {f: g['clear_ms'](f) for f in (0.008, 0.010, 0.012, 0.014, 0.016)}
    print(f"{w['name'][:8]} {w['mt']:%m-%d %H:%M} dur={w['dur']:5d} p90={g['p90']:.4f} "
          f"voiced={g['voiced']:4d} c02={g['clear02']:3d} max={g['maxrms']:.4f} "
          f"c@8/10/12/14/16m={fl[0.008]:4d}/{fl[0.010]:4d}/{fl[0.012]:4d}/{fl[0.014]:4d}/{fl[0.016]:4d}")

print('\n== FALSE-REJECTS (must pass) ==')
for w in frs:
    g = w['g']
    fl = {f: g['clear_ms'](f) for f in (0.008, 0.010, 0.012, 0.014, 0.016)}
    print(f"{w['name'][:8]} {w['mt']:%m-%d %H:%M} dur={w['dur']:5d} p90={g['p90']:.4f} "
          f"voiced={g['voiced']:4d} c02={g['clear02']:3d} max={g['maxrms']:.4f} "
          f"c@8/10/12/14/16m={fl[0.008]:4d}/{fl[0.010]:4d}/{fl[0.012]:4d}/{fl[0.014]:4d}/{fl[0.016]:4d}")

# ---- 6. tightest-margin kept speech ----
tight = sorted(speech, key=lambda w: (w['g']['voiced'], w['g']['clear02']))[:10]
print('\n== tightest-margin KEPT speech (voiced, clear02) ==')
for w in tight:
    g = w['g']
    print(f"{w['name'][:8]} {w['mt']:%m-%d %H:%M} dur={w['dur']:5d} voiced={g['voiced']:4d} "
          f"c02={g['clear02']:3d} max={g['maxrms']:.4f} p90={g['p90']:.4f} "
          f"txt={str(w['transcript'])[:40]!r}")

# ---- 7. candidate evaluation ----
def passes(w, rule):
    g = w['g']
    if g['p90'] < 0.004: return False
    ok = g['voiced'] >= rule.get('voiced_min', 600) or g['clear02'] >= rule.get('clear02_min', 100)
    mid = rule.get('mid')
    if mid:
        ok = ok or g['clear_ms'](mid[0]) >= mid[1]
    return ok

print('\n== candidate rules ==')
cands = [('CURRENT (600 | 100@0.02)', {}),
         ('voiced 600->350', dict(voiced_min=350)),
         ('voiced 600->300', dict(voiced_min=300)),
         ('clear floor 0.02->0.012 @100ms', dict(mid=(0.012, 100))),
         ('mid tier 200ms@0.010', dict(mid=(0.010, 200))),
         ('mid tier 240ms@0.008', dict(mid=(0.008, 240))),
         ('mid tier 300ms@0.008', dict(mid=(0.008, 300))),
         ('mid tier 200ms@0.012', dict(mid=(0.012, 200))),
         ('mid tier 160ms@0.014', dict(mid=(0.014, 160)))]
for name, rule in cands:
    fr_ok = sum(1 for w in frs if passes(w, rule))
    fa = [w for w in nonspeech if passes(w, rule)]
    sp_ok = sum(1 for w in speech if passes(w, rule))
    print(f"{name:34s} rescues {fr_ok}/{len(frs)} FRs | speech kept {sp_ok}/{len(speech)} | "
          f"non-speech flipped to ACCEPT: {len(fa)} {[w['name'][:8] for w in fa]}")
