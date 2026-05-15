#!/usr/bin/env bash
# One-time host setup for audio-passthrough Windows VM testing.
#
# What this does:
#   1. Installs PulseAudio + sox + python venv tooling + piper-tts
#   2. Starts PulseAudio as a user service
#   3. Creates the winpepper_mic null-sink (its .monitor source is what QEMU reads)
#   4. Downloads a piper voice model for TTS synthesis
#   5. Grants /dev/kvm access to the current user
#
# After running this, launch the VM with scripts/launch-qemu.sh and use
# scripts/say.sh "text" to speak into the VM's virtual microphone.

set -euo pipefail

PIPER_HOME="$HOME/.local/share/piper"
PIPER_VENV="$HOME/.venvs/piper"
PIPER_VOICE_BASE="https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium"

# ----------------------------------------------------------------------------
# 1) Packages
# ----------------------------------------------------------------------------
sudo apt-get update -qq
sudo apt-get install -y pulseaudio pulseaudio-utils sox python3-pip python3-venv qemu-system-x86

# ----------------------------------------------------------------------------
# 2) PulseAudio user service
# ----------------------------------------------------------------------------
mkdir -p "$HOME/.config/pulse"
cat > "$HOME/.config/pulse/default.pa" <<'PA'
.include /etc/pulse/default.pa
load-module module-null-sink sink_name=winpepper_mic sink_properties=device.description=Winpepper_Virtual_Mic
PA

systemctl --user enable --now pulseaudio.socket pulseaudio.service >/dev/null
sleep 1
pactl info >/dev/null
echo "PulseAudio ready: $(pactl info | grep 'Server String')"
pactl list short sources | grep winpepper

# ----------------------------------------------------------------------------
# 3) piper-tts in a venv (avoids system Python pollution)
# ----------------------------------------------------------------------------
if [ ! -x "$PIPER_VENV/bin/piper" ]; then
    python3 -m venv "$PIPER_VENV"
    "$PIPER_VENV/bin/pip" install --quiet piper-tts
fi

# ----------------------------------------------------------------------------
# 4) piper voice model (en_US lessac, medium quality)
# ----------------------------------------------------------------------------
mkdir -p "$PIPER_HOME"
for f in en_US-lessac-medium.onnx en_US-lessac-medium.onnx.json; do
    if [ ! -s "$PIPER_HOME/$f" ]; then
        echo "Downloading $f..."
        curl -fsSL -o "$PIPER_HOME/$f" "$PIPER_VOICE_BASE/$f"
    fi
done

# ----------------------------------------------------------------------------
# 5) /dev/kvm access
# ----------------------------------------------------------------------------
if [ ! -w /dev/kvm ]; then
    sudo usermod -aG kvm "$USER"
    sudo chmod 0666 /dev/kvm
    echo "Granted /dev/kvm access (group membership applies on next login)"
fi

echo ""
echo "Host audio setup done."
echo "  Piper:           $PIPER_VENV/bin/piper -m $PIPER_HOME/en_US-lessac-medium.onnx"
echo "  Null sink:       winpepper_mic"
echo "  Monitor source:  winpepper_mic.monitor  (QEMU reads from here)"
echo ""
echo "Next: ./scripts/launch-qemu.sh  (boots Windows VM)"
echo "Then: ./scripts/say.sh \"hello world\"  (speaks into the VM's virtual mic)"
