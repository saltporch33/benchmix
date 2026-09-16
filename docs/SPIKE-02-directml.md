# Spike 02 — can stem separation run in-process, on the GPU, inside one .exe?

Run 2026-09-16. Code in `spikes/02-directml/`.

Daniel chose "One .exe, keep it clean", and asked for stem separation as an *option*
alongside the EQ knobs, not as a replacement for them. That rules out a bundled Python +
PyTorch install, so separation has to run inside the .NET process.

## Result: viable

`Microsoft.ML.OnnxRuntime.DirectML` **1.24.4** (prebuilt native, no compiler needed):

```
ORT version    : 1.24.4.0
Available EPs  : DmlExecutionProvider, CPUExecutionProvider
DirectML device 0: session options created OK      <- the Arc 140V
DirectML device 1: "Specified display adapter handle is invalid"  <- expected, only one GPU
```

DirectML runs on any DX12 adapter, so the Intel Arc 140V is supported without CUDA. CPU is
available as a fallback on machines with no usable GPU.

## Model choice — decided, with a caveat to resolve at build time

**Use MDX-Net (`.onnx`) models, not Demucs.** The reason is supply-chain, not quality:

- MDX-Net models are **natively ONNX** — that is the format the authors ship. No conversion
  step, no conversion bugs.
- Every Demucs ONNX on HuggingFace is an **unofficial community export** with single- or
  double-digit download counts. Depending on one of those inside a tool meant to last is a bad
  trade for a marginal quality gain.

**Open item for the build:** MDX-Net weights are mirrored across scattered community repos
rather than one authoritative host. Before shipping the stems feature, pin one specific URL and
record its **SHA-256** in the repo, so a swapped or vanished mirror is detected rather than
silently trusted. The model downloads on first use (it is far too big to embed); the app works
fully without it, since stems are an option and the EQ path never depends on them.
