#!/usr/bin/env bash
# Builds ONNX (int4, WebGPU = Mac GPU) versions of all 7 Microsoft MediPhi models
# into the folders referenced by appsettings.json "Profiles".
# Usage:  bash build-mediphi-onnx.sh                 (all 7)
#         bash build-mediphi-onnx.sh MediPhi-Instruct  (just one)
set -euo pipefail

ROOT="/Volumes/Crucial X9/BHRFMoldels"
OUT="$ROOT/onnx"
CACHE="$ROOT/hf-cache"
MODELS=("${@:-MediPhi-Instruct MediPhi MediPhi-Clinical MediPhi-MedCode MediPhi-Guidelines MediPhi-PubMed MediPhi-MedWiki}")
read -r -a MODELS <<< "${MODELS[*]}"

if [[ ! -d "$HOME/ortgenai" ]]; then
  python3 -m venv "$HOME/ortgenai"
fi
source "$HOME/ortgenai/bin/activate"
pip install -q -U onnxruntime-genai onnx transformers torch huggingface_hub

mkdir -p "$OUT" "$CACHE"

for m in "${MODELS[@]}"; do
  dest="$OUT/${m}-webgpu-int4"
  if [[ -f "$dest/genai_config.json" ]]; then
    echo "==> $m already built, skipping"; continue
  fi
  echo "==> Building $m -> $dest"
  python -m onnxruntime_genai.models.builder \
    -m "microsoft/$m" \
    -o "$dest" \
    -p int4 \
    -e webgpu \
    -c "$CACHE"
  # free ~7.6 GB of source weights before the next model
  rm -rf "$CACHE"/*
done

echo; du -sh "$OUT"/*
