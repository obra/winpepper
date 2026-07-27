#!/usr/bin/env bash
# Stage per-model eval directories under C:\Users\dan\winpepper-evals\models\<model-name>\
# mirroring the production layout (one gguf at the root + runtime/<tarball-dir>/ DLLs) so
# the bench's --model-dir can consume them.
#
# Host safety: READS the production runtime under %LOCALAPPDATA%\winpepper\models
# (never writes there); writes only under C:\Users\dan\winpepper-evals\models\.
# Idempotent: skips work that is already done.
#
# Usage: ./scripts/setup-asr-eval-models.sh
set -euo pipefail

EVAL_MODELS=/mnt/c/Users/dan/winpepper-evals/models
PROD_RUNTIME="/mnt/c/Users/dan/AppData/Local/winpepper/models/nemotron-streaming-en/runtime/transcribe-native-windows-x86_64-cpu-vulkan"
RUNTIME_SUBDIR="runtime/transcribe-native-windows-x86_64-cpu-vulkan"
QWEN_EXPECTED_BYTES=2185030624

[[ -f "$PROD_RUNTIME/transcribe.dll" ]] || {
  echo "setup-asr-eval-models: production runtime not found at $PROD_RUNTIME" >&2; exit 2; }

stage_runtime() { # stage_runtime <model-dir>
  local dst="$1/$RUNTIME_SUBDIR"
  if [[ -f "$dst/transcribe.dll" ]]; then echo "  runtime already staged: $dst"; return 0; fi
  mkdir -p "$dst"
  cp -r "$PROD_RUNTIME/." "$dst/"
  echo "  runtime copied FROM production (read-only source) -> $dst"
}

stage_model() { # stage_model <model-name> <source-gguf-filename>
  local name="$1" gguf="$2"
  local src="$EVAL_MODELS/$gguf" dir="$EVAL_MODELS/$name"
  echo "== $name =="
  [[ -f "$src" || -f "$dir/$gguf" ]] || { echo "  SKIPPED (gguf not found: $src)"; return 0; }
  mkdir -p "$dir"
  if [[ ! -f "$dir/$gguf" ]]; then
    cp "$src" "$dir/$gguf"
    echo "  gguf copied -> $dir/$gguf"
  else
    echo "  gguf already staged: $dir/$gguf"
  fi
  stage_runtime "$dir"
}

# 1) nemotron-3.5 (streaming candidate) -- download known complete
stage_model "nemotron-3.5-asr-streaming-0.6b" "nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf"

# 2) Qwen3-ASR (batch-only candidate) -- verify the download is complete first
QWEN_SRC="$EVAL_MODELS/Qwen3-ASR-1.7B-Q8_0.gguf"
QWEN_DIR="$EVAL_MODELS/qwen3-asr-1.7b"
echo "== qwen3-asr-1.7b =="
qwen_size=0
[[ -f "$QWEN_DIR/Qwen3-ASR-1.7B-Q8_0.gguf" ]] && qwen_size=$(stat -c%s "$QWEN_DIR/Qwen3-ASR-1.7B-Q8_0.gguf")
[[ "$qwen_size" -ne "$QWEN_EXPECTED_BYTES" && -f "$QWEN_SRC" ]] && qwen_size=$(stat -c%s "$QWEN_SRC")
if [[ "$qwen_size" -eq "$QWEN_EXPECTED_BYTES" ]]; then
  stage_model "qwen3-asr-1.7b" "Qwen3-ASR-1.7B-Q8_0.gguf"
else
  echo "  SKIPPED (incomplete download: ${qwen_size} of ${QWEN_EXPECTED_BYTES} bytes) -- rerun when complete"
fi

echo "setup-asr-eval-models: done"
