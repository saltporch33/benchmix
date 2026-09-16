# Spike 03 — how well can Benchmix detect tempo and key?

Run 2026-09-16. Code in `spikes/03-analysis/`.

Daniel chose *"Detect both, suggest the match"*, so this had to be measured rather than
assumed. The project's own standing lesson applies: **calibrate any detector against known
ground truth before believing it.**

## The test set was free

His Sports DJ library turned out to be Serato-tagged: of 35 MP3s, **34 carry `TBPM` and 32
carry `TKEY`**. That is a labelled ground-truth set of real funk/hip-hop/rock full mixes —
exactly the material Benchmix will see. Every number below is scored against those tags.

## Tempo: works, ship it

Spectral-flux onset envelope → autocorrelation reinforced by a comb over integer multiples of
each candidate period → log-Gaussian perceptual prior centred at 120 BPM → parabolic peak
refinement.

| | |
| --- | --- |
| Exact (within 2%) | **32 / 34 — 94%** |
| Half or double time | 2 / 34 — 6% |
| **Genuinely wrong** | **0 / 34 — 0%** |
| Cost | ~0.9 s per track over a 3-minute window |

The two misses are *Walking On The Moon* (120 → 60.1) and one *Higher Ground* (139 → 69.7) —
both exact half-time, which is a defensible reading of those tracks and is a one-click ×2
toggle in the UI, not an error. **Nothing was ever simply wrong**, which is the property that
matters: a confidently wrong BPM would silently ruin a mashup.

Tuning was measured too: mean offset **16 cents**, max **49 cents**. Records are not all at
A=440, so tuning estimation earns its place.

## Key: honestly mediocre — so the design works around it

Five published key profiles × three chroma front ends × six bass-weighting levels were scored,
30 combinations in all:

| chroma front end | best profile | exact |
| --- | --- | --- |
| Naive full-spectrum | Triadic | **47%** |
| Spectral peaks + tuning + harmonic de-weighting | Albrecht-Shanahan | 38% |
| Bass-weighted blends (6 levels) | Krumhansl @ 0.55 | 44% |

The ceiling is **47% exact, 59% within a harmonic neighbour**. The whole 30-cell matrix spans
28–47%; with n=32 those gaps are one or two tracks, i.e. noise. Tuning further on this set
would be overfitting, so it stopped here.

That is a fair result for a from-scratch implementation — commercial detectors have far more
machinery behind them — but it is **not good enough to quietly drive a pitch-shift decision.**

### A refuted idea, recorded so it is not retried

The research round recommended skipping key names entirely and instead **cross-correlating the
two tracks' chroma over the 12 rotations** to rank candidate semitone shifts. It is an elegant
idea. It was measured over all 888 ordered pairs of labelled tracks and it **does not work**:

| | top-1 | in top 3 |
| --- | --- | --- |
| Naive chroma | 3.4% | 19.1% |
| Peaks + tuning | 7.4% | 24.1% |
| **Random guessing** | **8.3%** | **25.0%** |

At or below chance. In hindsight the theory is weak: two different songs in the same key have
different chord progressions and different instrumentation, so their raw chroma vectors have no
reason to align. Worse, the identity rotation is systematically favoured by shared spectral
bias, which is visible in the worked example. **Do not build this.**

## What Benchmix does instead

1. **Read the tags first.** `TBPM` / `TKEY` cover 97% and 91% of his own library, written by
   Serato, which is better than anything built here. Free accuracy, instantly.
2. **Analyse only when tags are missing.** Tempo detection is trustworthy on its own (94%/0%
   wrong). Key is shown as an **estimate, labelled as one**, with its confidence.
3. **Derive the suggested shift from the two keys via the Camelot wheel** — the standard
   harmonic-mixing rule — rather than from chroma correlation.
4. **Let his ears settle it.** The suggestion comes with one-click audition of the neighbouring
   options (−2, −1, suggested, +1, +2). For a judgement this uncertain, three seconds of
   listening beats any detector, and the UI should be built to make that trivial rather than to
   pretend the number is authoritative.
