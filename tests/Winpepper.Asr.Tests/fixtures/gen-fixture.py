# Run from repo root: python3 tests/Winpepper.Asr.Tests/fixtures/gen-fixture.py
import json, math, struct, wave
import numpy as np

SAMPLE_RATE = 16000
DURATION = 1.0
FREQ = 440.0

samples = np.sin(2 * np.pi * FREQ * np.arange(int(SAMPLE_RATE * DURATION)) / SAMPLE_RATE).astype(np.float32)
pcm16 = (samples * 32767).astype(np.int16)
with wave.open('tests/Winpepper.Asr.Tests/fixtures/tone-440hz-1s.wav', 'wb') as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(SAMPLE_RATE)
    w.writeframes(pcm16.tobytes())

def slaney_mel_filters(n_fft, n_mels, sr, fmin=0.0, fmax=None):
    if fmax is None: fmax = sr / 2
    def hz_to_mel(f):
        f = np.asarray(f, dtype=np.float64)
        below = f < 1000.0
        mel = np.where(below, f * 3.0 / 200.0,
                       15.0 + np.log(f / 1000.0) / (np.log(6.4) / 27.0))
        return mel
    def mel_to_hz(m):
        m = np.asarray(m, dtype=np.float64)
        below = m < 15.0
        f = np.where(below, m * 200.0 / 3.0,
                     1000.0 * np.exp((m - 15.0) * (np.log(6.4) / 27.0)))
        return f
    mel_min, mel_max = hz_to_mel(fmin), hz_to_mel(fmax)
    mel_points = np.linspace(mel_min, mel_max, n_mels + 2)
    hz_points = mel_to_hz(mel_points)
    bins = hz_points * (n_fft / sr)

    filters = np.zeros((n_mels, n_fft // 2 + 1), dtype=np.float64)
    for i in range(n_mels):
        left, center, right = bins[i], bins[i+1], bins[i+2]
        for k in range(n_fft // 2 + 1):
            if k < left or k > right: continue
            if k <= center:
                filters[i, k] = (k - left) / (center - left + 1e-12)
            else:
                filters[i, k] = (right - k) / (right - center + 1e-12)
        enorm = 2.0 / (hz_points[i+2] - hz_points[i])
        filters[i] *= enorm
    return filters

def stft_power(x, n_fft=512, hop=160, win_len=400):
    w = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(win_len) / (win_len - 1))
    offset = (n_fft - win_len) // 2
    window = np.zeros(n_fft, dtype=np.float64)
    window[offset:offset + win_len] = w

    pad = n_fft // 2
    x_padded = np.pad(x.astype(np.float64), pad_width=pad, mode='constant')
    n_frames = (len(x_padded) - n_fft) // hop + 1
    out = np.zeros((n_frames, n_fft // 2 + 1), dtype=np.float64)
    for t in range(n_frames):
        frame = x_padded[t * hop : t * hop + n_fft] * window
        spec = np.fft.rfft(frame, n=n_fft)
        out[t] = (spec.real * spec.real + spec.imag * spec.imag)
    return out

def compute_parakeet_features(samples_f32, sr=16000, n_mels=128, n_fft=512, hop=160,
                              win_len=400, preemphasis=0.97):
    x = samples_f32.astype(np.float64).copy()
    for j in range(len(x) - 1, 0, -1):
        x[j] = x[j] - preemphasis * x[j - 1]

    power = stft_power(x, n_fft=n_fft, hop=hop, win_len=win_len)
    mel_filters = slaney_mel_filters(n_fft, n_mels, sr)
    mel = power @ mel_filters.T

    mel_offset = 2 ** -24
    log_mel = np.log(np.maximum(mel + mel_offset, 1e-30))

    n_frames = log_mel.shape[0]
    mean = log_mel.mean(axis=0)
    var = log_mel.var(axis=0, ddof=1 if n_frames > 1 else 0)
    std = np.sqrt(var) + 1e-5
    norm = (log_mel - mean) / std
    return norm.astype(np.float32)

# Compute features from the WAV-roundtripped samples so the C# reader (which
# reads int16 PCM from the .wav) sees the same input as this reference.
samples_from_wav = (pcm16.astype(np.float32) / 32768.0)
features = compute_parakeet_features(samples_from_wav)
with open('tests/Winpepper.Asr.Tests/fixtures/tone-440hz-1s.mel.json', 'w') as f:
    json.dump({
        'shape': list(features.shape),
        'first_six_frames': features[:6].tolist(),
        'last_frame': features[-1].tolist(),
    }, f)
print(f"Wrote tone-440hz-1s.wav and tone-440hz-1s.mel.json shape={features.shape}")
