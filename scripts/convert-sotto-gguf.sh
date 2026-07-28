#!/usr/bin/env bash
# convert-sotto-gguf.sh -- reproducible GGUF conversion for the sotto cleanup model.
#
# No public GGUF exists for juanquivilla/sotto-cleanup-lfm25-350m (MIT), so the
# registry entry `sotto-cleanup-lfm25-350m-q8_0` is manual-install-only. This
# script reproduces the exact registry artifact from source:
#
#   1. llama.cpp checkout pinned to the commit the registry hash was produced with
#   2. venv with the convert_hf_to_gguf.py requirements
#   3. HF source download (via huggingface_hub) if not already present
#   4. two tokenizer_config.json fixes (transformers-v5 artifacts that break
#      convert_hf_to_gguf.py): tokenizer_class TokenizersBackend ->
#      PreTrainedTokenizerFast, and drop extra_special_tokens
#   5. convert_hf_to_gguf.py --outtype q8_0
#   6. sha256 + size verification against the ModelRegistry values
#
# Usage (from WSL/Linux):
#   ./scripts/convert-sotto-gguf.sh                     # work dir ~/models-work, out <work>/sotto-cleanup-lfm25-350m-q8_0.gguf
#   ./scripts/convert-sotto-gguf.sh --out <path.gguf>   # custom output path
#   ./scripts/convert-sotto-gguf.sh --work-dir <dir>
#
# Install after conversion (Windows host, manual step -- see registry entry):
#   %LOCALAPPDATA%\winpepper\models\cleanup\sotto-cleanup-lfm25-350m-q8_0\sotto-cleanup-lfm25-350m-q8_0.gguf
set -euo pipefail

# --- pinned inputs (keep in lockstep with src/Winpepper.Models/ModelRegistry.cs) ---
LLAMA_CPP_REPO="https://github.com/ggml-org/llama.cpp"
LLAMA_CPP_COMMIT="1cbfd1988311775425d36c0ce066590f7d3049cf"
HF_REPO="juanquivilla/sotto-cleanup-lfm25-350m"
EXPECTED_SHA256="67113c655d523ea682ff30488900fb62415835d391ce77cd1cb97dff2f5d962d"
EXPECTED_SIZE=379215808

WORK_DIR="$HOME/models-work"
OUT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --work-dir) WORK_DIR="$2"; shift 2 ;;
    --out)      OUT="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done
[[ -n "$OUT" ]] || OUT="$WORK_DIR/sotto-cleanup-lfm25-350m-q8_0.gguf"
LLAMA_DIR="$WORK_DIR/llama.cpp"
SRC_DIR="$WORK_DIR/sotto-cleanup-lfm25-350m"
VENV_DIR="$LLAMA_DIR/.venv-convert"
mkdir -p "$WORK_DIR"

# --- 1. llama.cpp at the pinned commit ---
if [[ ! -d "$LLAMA_DIR/.git" ]]; then
  echo "== cloning llama.cpp @ ${LLAMA_CPP_COMMIT:0:12}"
  git clone --no-checkout "$LLAMA_CPP_REPO" "$LLAMA_DIR"
  git -C "$LLAMA_DIR" checkout "$LLAMA_CPP_COMMIT"
fi
HAVE_COMMIT=$(git -C "$LLAMA_DIR" rev-parse HEAD)
if [[ "$HAVE_COMMIT" != "$LLAMA_CPP_COMMIT" ]]; then
  echo "WARNING: llama.cpp checkout is at ${HAVE_COMMIT:0:12}, registry hash was produced" >&2
  echo "         with ${LLAMA_CPP_COMMIT:0:12}; the output hash may not match." >&2
fi

# --- 2. conversion venv ---
if [[ ! -f "$VENV_DIR/bin/python" ]]; then
  echo "== creating conversion venv"
  python3 -m venv "$VENV_DIR"
  "$VENV_DIR/bin/pip" install --quiet -r "$LLAMA_DIR/requirements/requirements-convert_hf_to_gguf.txt" huggingface_hub
fi
PY="$VENV_DIR/bin/python"

# --- 3. HF source ---
if [[ ! -f "$SRC_DIR/model.safetensors" ]]; then
  echo "== downloading $HF_REPO"
  "$PY" - "$HF_REPO" "$SRC_DIR" <<'PYEOF'
import sys
from huggingface_hub import snapshot_download
snapshot_download(repo_id=sys.argv[1], local_dir=sys.argv[2])
PYEOF
fi

# --- 4. tokenizer_config.json fixes (idempotent; keeps a .orig backup) ---
"$PY" - "$SRC_DIR/tokenizer_config.json" <<'PYEOF'
import json, shutil, sys
path = sys.argv[1]
cfg = json.load(open(path))
fixed = dict(cfg)
fixed.pop("extra_special_tokens", None)
if fixed.get("tokenizer_class") == "TokenizersBackend":
    fixed["tokenizer_class"] = "PreTrainedTokenizerFast"
if fixed != cfg:
    shutil.copyfile(path, path + ".orig")
    json.dump(fixed, open(path, "w"), indent=2)
    print("== tokenizer_config.json fixed (backup at .orig)")
else:
    print("== tokenizer_config.json already fixed")
PYEOF

# --- 5. convert ---
echo "== converting to q8_0 GGUF"
"$PY" "$LLAMA_DIR/convert_hf_to_gguf.py" "$SRC_DIR" --outtype q8_0 --outfile "$OUT"

# --- 6. verify against registry ---
ACTUAL_SHA256=$(sha256sum "$OUT" | cut -d' ' -f1)
ACTUAL_SIZE=$(stat -c%s "$OUT")
echo "out:    $OUT"
echo "sha256: $ACTUAL_SHA256 (expected $EXPECTED_SHA256)"
echo "size:   $ACTUAL_SIZE (expected $EXPECTED_SIZE)"
if [[ "$ACTUAL_SHA256" == "$EXPECTED_SHA256" && "$ACTUAL_SIZE" == "$EXPECTED_SIZE" ]]; then
  echo "CONVERT: VERIFIED -- matches ModelRegistry sha256/size"
else
  echo "CONVERT: MISMATCH -- do NOT install; check llama.cpp commit and source revision" >&2
  exit 1
fi
