#!/usr/bin/env python3
"""Persistent FLUX.2 inference subprocess for the Node provider binding."""

from __future__ import annotations

import json
from io import BytesIO
import os
import sys
import time
import zipfile
from pathlib import Path
from typing import Any

PIPELINE: Any = None
SAFETY_CHECKER: Any = None
SAFETY_PROCESSOR: Any = None
TORCH: Any = None
DEVICE = ""
MODEL = os.environ.get("MUTUALGPU_FLUX2_MODEL", "black-forest-labs/FLUX.2-klein-4B")
SAFETY_MODEL = os.environ.get("MUTUALGPU_FLUX2_SAFETY_MODEL", "CompVis/stable-diffusion-safety-checker")
PREVIEW_MAX_DIMENSION = 576
PREVIEW_TARGET_BYTES = 180 * 1024
PREVIEW_DIMENSIONS = (PREVIEW_MAX_DIMENSION, 448, 384, 320)
PREVIEW_COLORS = (256, 192, 128, 64)


def emit(message: dict[str, Any]) -> None:
    print(json.dumps(message, separators=(",", ":")), flush=True)


def log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def enabled(name: str) -> bool:
    return os.environ.get(name, "false").lower() == "true"


def initialize() -> str:
    global TORCH, DEVICE
    try:
        import torch
        import diffusers  # noqa: F401 - validates the runtime before enrollment
        from PIL import Image  # noqa: F401
    except ImportError as error:
        raise RuntimeError("FLUX.2 Python dependencies are not installed") from error

    TORCH = torch
    requested = os.environ.get("MUTUALGPU_FLUX2_DEVICE", "auto").lower()
    available = {
        "cuda": torch.cuda.is_available(),
        "mps": bool(getattr(torch.backends, "mps", None) and torch.backends.mps.is_available()),
        "cpu": enabled("MUTUALGPU_FLUX2_ALLOW_CPU"),
    }
    if requested == "auto":
        DEVICE = next((name for name in ("cuda", "mps", "cpu") if available[name]), "")
    elif requested in available and available[requested]:
        DEVICE = requested
    else:
        raise RuntimeError(f"Requested FLUX.2 device is unavailable: {requested}")
    if not DEVICE:
        raise RuntimeError("No CUDA or MPS GPU is available")
    log(f"runtime ready: python={sys.version_info.major}.{sys.version_info.minor} torch={torch.__version__} device={DEVICE}")
    return DEVICE


def load_pipeline() -> Any:
    global PIPELINE
    if PIPELINE is not None:
        return PIPELINE

    try:
        from diffusers import Flux2KleinPipeline
        log(f"loading model: {MODEL}")
        dtype = TORCH.bfloat16 if DEVICE == "cuda" else TORCH.float16 if DEVICE == "mps" else TORCH.float32
        PIPELINE = Flux2KleinPipeline.from_pretrained(
            MODEL,
            torch_dtype=dtype,
            local_files_only=enabled("MUTUALGPU_FLUX2_LOCAL_FILES_ONLY"),
        )
        if DEVICE == "cuda":
            PIPELINE.enable_model_cpu_offload()
        else:
            PIPELINE.to(DEVICE)
    except Exception as error:
        PIPELINE = None
        raise RuntimeError("FLUX.2 model could not be loaded") from error
    log(f"model loaded on {DEVICE}")
    return PIPELINE


def load_safety_checker() -> tuple[Any, Any]:
    global SAFETY_CHECKER, SAFETY_PROCESSOR
    if SAFETY_CHECKER is not None and SAFETY_PROCESSOR is not None:
        return SAFETY_CHECKER, SAFETY_PROCESSOR
    try:
        import numpy as np  # noqa: F401 - validates the safety checker dependency
        from diffusers.pipelines.stable_diffusion.safety_checker import StableDiffusionSafetyChecker
        from transformers import CLIPImageProcessor

        log(f"loading safety checker: {SAFETY_MODEL}")
        local_only = enabled("MUTUALGPU_FLUX2_LOCAL_FILES_ONLY")
        SAFETY_CHECKER = StableDiffusionSafetyChecker.from_pretrained(SAFETY_MODEL, local_files_only=local_only).to("cpu")
        SAFETY_PROCESSOR = CLIPImageProcessor.from_pretrained(SAFETY_MODEL, local_files_only=local_only)
    except Exception as error:
        SAFETY_CHECKER = None
        SAFETY_PROCESSOR = None
        raise RuntimeError("FLUX.2 safety checker could not be loaded") from error
    log("safety checker loaded on cpu")
    return SAFETY_CHECKER, SAFETY_PROCESSOR


def image_is_inappropriate(image: Any) -> bool:
    import numpy as np

    checker, processor = load_safety_checker()
    clip_input = processor(images=[image], return_tensors="pt").pixel_values
    _, flags = checker(images=np.asarray([np.asarray(image)]), clip_input=clip_input)
    return any(bool(flag) for flag in flags)


def encode_preview(image: Any) -> bytes:
    """Return a display-sized PNG without changing the full PNG result."""
    from PIL import Image

    for maximum_dimension in PREVIEW_DIMENSIONS:
        preview = image.copy()
        preview.thumbnail((maximum_dimension, maximum_dimension), Image.Resampling.LANCZOS)
        if preview.mode not in ("RGB", "L"):
            preview = preview.convert("RGB")
        for colors in PREVIEW_COLORS:
            palette = preview.quantize(colors=colors, method=Image.Quantize.MEDIANCUT)
            encoded = BytesIO()
            palette.save(encoded, format="PNG", optimize=True, compress_level=9)
            if encoded.tell() <= PREVIEW_TARGET_BYTES:
                return encoded.getvalue()

    # The final encode is deliberately small enough to keep a noisy image within
    # the preview budget while still preserving a useful task-list rendition.
    preview = image.copy()
    preview.thumbnail((256, 256), Image.Resampling.LANCZOS)
    if preview.mode not in ("RGB", "L"):
        preview = preview.convert("RGB")
    encoded = BytesIO()
    preview.quantize(colors=64, method=Image.Quantize.MEDIANCUT).save(encoded, format="PNG", optimize=True, compress_level=9)
    return encoded.getvalue()


def generate(request: dict[str, Any]) -> None:
    identifier = str(request["id"])
    output_directory = Path(request["outputDirectory"])
    output_directory.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    log(f"generation {identifier} started on {DEVICE}")
    emit({
        "type": "progress",
        "id": identifier,
        "phase": "model_load",
        "percent": 3,
        "message": "Loading the FLUX.2 model.",
    })
    pipeline = load_pipeline()
    steps = int(request["steps"])

    def progress(_pipeline: Any, step_index: int, _timestep: Any, callback_kwargs: dict[str, Any]) -> dict[str, Any]:
        percent = 10 + ((step_index + 1) / steps) * 80
        emit({
            "type": "progress",
            "id": identifier,
            "phase": "inference",
            "percent": percent,
            "message": f"FLUX.2 step {step_index + 1} of {steps}.",
        })
        return callback_kwargs

    generator = TORCH.Generator(device="cpu").manual_seed(int(request["seed"]))
    result = pipeline(
        prompt=request["prompt"],
        num_inference_steps=steps,
        guidance_scale=float(request["guidanceScale"]),
        width=int(request["width"]),
        height=int(request["height"]),
        generator=generator,
        callback_on_step_end=progress,
    )
    image = result.images[0]
    if image_is_inappropriate(image):
        raise RuntimeError("FLUX.2 safety checker rejected the generated image")

    image_path = output_directory / "image.png"
    preview_path = output_directory / "preview.png"
    thumbnail_path = output_directory / "thumbnail.png"
    image.save(image_path, format="PNG", optimize=True)
    preview_path.write_bytes(encode_preview(image))
    thumbnail = image.copy()
    thumbnail.thumbnail((256, 256))
    thumbnail.save(thumbnail_path, format="PNG", optimize=True)
    duration_ms = round((time.monotonic() - started) * 1000)
    metadata = {
        "model": MODEL,
        "device": DEVICE,
        "seed": int(request["seed"]),
        "width": int(request["width"]),
        "height": int(request["height"]),
        "numInferenceSteps": steps,
        "guidanceScale": float(request["guidanceScale"]),
        "durationMs": duration_ms,
        "preview": {"contentType": "image/png", "maximumDimension": PREVIEW_MAX_DIMENSION},
        "safetyCheckerApplied": True,
    }
    metadata_path = output_directory / "metadata.json"
    metadata_path.write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    logs_path = output_directory / "logs.txt"
    logs_path.write_text(f"FLUX.2 completed in {duration_ms} ms.\n", encoding="utf-8")
    with zipfile.ZipFile(output_directory / "result.zip", "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.write(image_path, "image.png")
        archive.write(metadata_path, "metadata.json")
        archive.write(logs_path, "logs.txt")
    log(f"generation {identifier} packaged in {duration_ms} ms")
    emit({"type": "result", "id": identifier, "metadata": metadata})


def safe_category(error: BaseException) -> str:
    messages: list[str] = []
    current: BaseException | None = error
    while current is not None and len(messages) < 8:
        messages.append(str(current))
        current = current.__cause__ or current.__context__
    message = " ".join(messages).lower()
    if "dependencies are not installed" in message:
        return "MissingDependencies"
    if "device is unavailable" in message or "no cuda or mps gpu" in message:
        return "GpuUnavailable"
    if "out of memory" in message:
        return "GpuOutOfMemory"
    if "model could not be loaded" in message:
        return "ModelLoadFailed"
    if "safety checker could not be loaded" in message:
        return "SafetyCheckerLoadFailed"
    if "safety checker" in message:
        return "SafetyCheckRejected"
    return type(error).__name__


def serve() -> None:
    initialize()
    load_pipeline()
    load_safety_checker()
    emit({"type": "ready", "device": DEVICE, "model": MODEL})
    log("runtime warmup complete; accepting generation requests")
    for line in sys.stdin:
        request: Any = None
        try:
            request = json.loads(line)
            if request.get("type") != "generate":
                continue
            generate(request)
        except BaseException as error:  # task errors become bounded protocol diagnostics
            emit({
                "type": "error",
                "id": str(request.get("id", "")) if isinstance(request, dict) else "",
                "category": safe_category(error),
            })


def main() -> None:
    command = sys.argv[1] if len(sys.argv) > 1 else "--probe"
    if command == "--serve":
        serve()
        return
    initialize()
    if command == "--warmup":
        load_pipeline()
        load_safety_checker()
    emit({"type": "ready", "device": DEVICE, "model": MODEL})


if __name__ == "__main__":
    try:
        main()
    except BaseException as error:
        emit({"type": "startup_error", "category": safe_category(error)})
        raise SystemExit(1) from None
