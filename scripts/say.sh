#!/usr/bin/env bash
# Synthesize speech and play it into the Windows VM's virtual microphone.
#
# Usage:
#   ./scripts/say.sh "hello world"
#   echo "long passage" | ./scripts/say.sh
#
# Prereqs (see scripts/setup-audio-host.sh):
#   - PulseAudio running with winpepper_mic null-sink loaded
#   - piper-tts installed in ~/.venvs/piper
#   - QEMU VM running (./scripts/launch-qemu.sh)
#
# This pipes TTS audio to the winpepper_mic null-sink; QEMU is reading from
# winpepper_mic.monitor, so Windows sees it as input on the Line In device.

set -euo pipefail
PIPER="$HOME/.venvs/piper/bin/piper"
MODEL="$HOME/.local/share/piper/en_US-lessac-medium.onnx"
SINK="winpepper_mic"

if [ ! -x "$PIPER" ]; then
    echo "piper not found. Run scripts/setup-audio-host.sh first." >&2
    exit 1
fi

TEXT="$*"
if [ -z "$TEXT" ]; then
    TEXT="$(cat)"
fi

TMP=$(mktemp --suffix=.wav)
trap "rm -f $TMP" EXIT

echo "$TEXT" | "$PIPER" -m "$MODEL" -f "$TMP" 2>/dev/null
paplay --device="$SINK" "$TMP"
