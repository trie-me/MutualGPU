# MutualGPU FLUX.2 worker

This is a standalone provider process. It uses the MutualGPU Node SDK for enrollment, assignment acknowledgement, progress, reconnect/rebinding, result upload, and completion. A persistent Python subprocess owns the FLUX.2 pipeline and GPU memory, so one model load is reused across the SDK client's sequential tasks.

The default capability name is `flux2-klein-4b`. Enrollment is a complete replacement, so use a provider key dedicated to this worker. The provider key binds the SDK session to the execution unit; the worker never passes that key to Python.

## Prerequisites

- Node.js 20 or newer.
- Python 3.10 or newer is recommended.
- An NVIDIA CUDA GPU or Apple Silicon with MPS.
- A MutualGPU provider key for one execution unit.
- Enough local storage to cache the selected model.

CPU inference is disabled by default because this worker advertises GPU-backed work. Set `MUTUALGPU_FLUX2_ALLOW_CPU=true` only for local functional testing.

## Install

From this directory, let mise provide the declared Python runtime:

```sh
mise install
```

Mise pins Python 3.12 and uv 0.11. Python dependencies are declared in `pyproject.toml`; `uv run` synchronizes that project environment when the worker starts. Install the Node packages through your normal dependency workflow.

The npm dependencies point at the sibling SDK packages in this source checkout. The worker remains a separate process and package; it does not link into the API host.

Model weights, framework binaries, caches, virtual environments, and generated outputs are runtime artifacts and must not be committed. Common model and checkpoint formats are ignored within this worker. Operators remain responsible for verifying the license and permitted use of any model selected through `MUTUALGPU_FLUX2_MODEL`.

The default model is `black-forest-labs/FLUX.2-klein-4B`, the Apache-2.0 consumer-GPU variant. It and the output safety checker are downloaded from Hugging Face on the first task and then cached. To prepare them before accepting work:

```sh
mise exec -- uv run python src/inference.py --warmup
```

Use `MUTUALGPU_FLUX2_LOCAL_FILES_ONLY=true` after prewarming to prevent model downloads while serving tasks.

## Run

```sh
export MUTUALGPU_API_URL=https://mutualgpu.example
export MUTUALGPU_PROVIDER_KEY=replace-with-the-dedicated-provider-key
mise exec -- uv run npm start
```

From the repository root, after exporting the provider key, use:

```sh
just flux2-worker
```

When no credential environment variable is present, the launcher prompts for either a protected static-binding file or hidden passcode input. Before connecting, it displays the non-secret API, capability, model, device, credential source, and artifact policy; synchronizes the declared environment; and probes the accelerator. It then starts directly and reports runtime, enrollment, and connection stages separately with actionable safe diagnostics.

For a protected operator file containing one `<execution-unit-id> <provider-key>` record per line, avoid copying the static credential into shell history:

```sh
just flux2-worker-file /private/tmp/mutualgpu-provider-keys.example 4
```

The process probes CUDA/MPS before enrolling, advertises the `flux2-klein-4b` capability through `ProviderClient.enroll`, then opens the provider session through `ProviderClient.connect`. Requestor prompts are sent to Python over stdin rather than process arguments and are not written to worker logs.

Supported scalar inputs are `prompt`, `num_inference_steps`, `guidance_scale`, `width`, `height`, and `seed`. The distilled FLUX.2 Klein model defaults to four inference steps; the request contract defaults guidance scale to `7`. A successful task uploads:

- `result.zip` containing `image.png`, `metadata.json`, and `logs.txt`;
- a PNG preview and 256-pixel thumbnail;
- bounded metadata and logs.

Every output passes through the configured safety checker before publication. A flagged image becomes a terminal `content_safety` failure labelled as inappropriate content; it is never retried or published.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `MUTUALGPU_FLUX2_CAPABILITY` | `flux2-klein-4b` | Enrolled capability name |
| `MUTUALGPU_FLUX2_MODEL` | `black-forest-labs/FLUX.2-klein-4B` | Compatible FLUX.2 Klein Diffusers model ID or local path |
| `MUTUALGPU_FLUX2_SAFETY_MODEL` | `CompVis/stable-diffusion-safety-checker` | Output safety-checker model ID or local path |
| `MUTUALGPU_FLUX2_DEVICE` | `auto` | `auto`, `cuda`, `mps`, or (when allowed) `cpu` |
| `MUTUALGPU_FLUX2_MACHINE_TIER` | `Large` | MutualGPU provider classification |
| `MUTUALGPU_FLUX2_COMPUTE_TIER` | machine tier | Scheduler compute envelope |
| `MUTUALGPU_FLUX2_MEMORY_GIB` | `16` | Scheduler memory envelope |
| `MUTUALGPU_FLUX2_TIMEOUT_SECONDS` | `900` | Per-image timeout, 30–3600 seconds |
| `MUTUALGPU_FLUX2_LOCAL_FILES_ONLY` | `false` | Require an already cached/local model |
| `MUTUALGPU_FLUX2_ALLOW_CPU` | `false` | Permit CPU-only development runs |
| `MUTUALGPU_FLUX2_HEARTBEAT_SECONDS` | `15` | Connected/idle status log interval |

The tier and memory values are scheduling claims, not automatic hardware detection. Configure them to match the execution unit actually bound to the provider key.

The foreground stream logs SDK startup stages, periodic connected/idle heartbeats, task acceptance, every inference phase update, Python model loading, packaging, upload, and terminal outcomes. Step updates are coalesced to the SDK's supported rate and the newest update is flushed before upload, so the requestor does not remain at 1% during inference. It never logs provider passcodes, task handles, prompts, or scalar request data.

## Verify

The lightweight tests do not download a model or require a GPU:

```sh
npm run check
```

To probe the installed Python stack and accelerator without loading model weights:

```sh
mise exec -- uv run python src/inference.py --probe
```
