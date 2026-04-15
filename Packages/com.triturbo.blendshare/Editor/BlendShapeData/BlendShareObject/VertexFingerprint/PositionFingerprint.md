# PositionFingerprint Algorithm

## The Problem

Unity splits FBX control points into multiple vertices at UV seams and hard edges. A single FBX control point may map to dozens of Unity vertices that all share the same position — so base position alone cannot uniquely identify the correspondence. The fingerprint algorithm resolves this by combining base position with blendshape delta vectors.

## Data Model

Each `PositionFingerprint` holds:

| Field | Source (FBX) | Source (Unity) |
|-------|-------------|----------------|
| `BasePosition` | Control point position × `importScale` | `mesh.vertices[i]` |
| `_relativeSamples[k]` | Blendshape k last-frame delta × `importScale` | Blendshape k last-frame `deltaVertices[i]` |

`k` is the index within the **shared blendshape sequence** — a list of blendshapes that exist with the same name in both the FBX and the Unity mesh. Only blendshapes that appear in both sides and have a non-zero delta anywhere are included.

## Matching Algorithm

For each Unity vertex, `TryFindMatchingCandidateIndex` searches the FBX fingerprint set in two stages:

### Stage 1 — Base Position Lookup

A `CandidateIndex` provides an O(1) spatial hash over FBX fingerprints, keyed by `BasePositionKey = (floor(x/ε), floor(y/ε), floor(z/ε))` with `ε = 1e-5`.

To avoid missing matches that fall across grid boundaries, the query probes all **27 neighboring cells** (a 3×3×3 block centred on the query key), then confirms each hit with a component-wise `|Δ| ≤ ε` check.

| Base position match count | Next step |
|--------------------------|-----------|
| 0 | Fall through to Stage 2 (global scan) |
| 1 | Return directly — unique match |
| > 1 | Disambiguate with Stage 2 (restricted to these candidates) |

### Stage 2 — Relative Distance Disambiguation

`RelativeDistanceTo` computes the **sum of per-blendshape Euclidean distances** between delta vectors:

```
D(a, b) = Σ_k ‖a._relativeSamples[k] − b._relativeSamples[k]‖
```

The candidate with the minimum D is chosen. An early-exit cuts the inner loop as soon as the running sum exceeds the current best, keeping the typical cost low.

When Stage 1 found 0 matches (no base position overlap), Stage 2 scans the entire FBX fingerprint set. This is the worst case and indicates a precision or scaling mismatch.

## Why Blendshape Deltas Work as a Discriminant

Split vertices that come from the same control point have *identical* blendshape deltas — Unity copies the delta from the control point to all its split vertices. Two distinct control points nearly always have different delta profiles across multiple blendshapes, making collisions extremely unlikely. More blendshapes in the shared sequence = stronger fingerprint.

## Construction (`PositionFingerprintFactory`)

- **FBX side:** reads control point positions and blendshape frames from `FbxMeshSnapshot`.
- **Unity side:** reads `mesh.vertices` and `mesh.GetBlendShapeFrameVertices` data.
- Only the **last frame** of each blendshape is used (`TryGetLastFrame`).
- Only blendshapes with at least one non-zero delta are included in the sequence.

## Known Limitations and Advice

### 1. Last frame only
`TryGetLastFrame` always picks `frames[frames.Length - 1]`. For most FBX exports this is the 100% weight frame, which is fine. If a character rig has an intermediate blend frame with a larger spatial spread than the last frame, the last frame's deltas could be a weaker discriminant. Consider exposing a frame selection strategy if degenerate cases arise.

### 2. Silent fallback on base-position miss
When Stage 1 returns 0 matches, the algorithm silently falls back to a global relative-distance scan. This almost certainly means the scales are mismatched or the mesh has been significantly altered. There is no warning emitted. Adding a `Debug.LogWarning` here would make mapping failures much easier to diagnose.

### 3. Sample length mismatch is silent
`RelativeDistanceTo` returns `double.PositiveInfinity` when `_relativeSamples.Length` differs between the two fingerprints. This causes the vertex to be unmatched with no diagnostic. It should not happen given the current construction path, but if it ever does it is invisible. A contract check or assertion in the factory would catch this early.

### 4. FBX-mode blendshape pairing is by index order
`PairBlendShapeDataSequences` pairs FBX and Unity blendshapes by their position in the respective sequences (truncating to `min(fbx count, unity count)`). This assumes Unity preserves FBX blendshape order, which it normally does, but any reordering (e.g., from a DCC tool) would silently produce wrong pairings. The legacy path (paired by name) is more robust; consider applying name-based pairing to the FBX-asset path as well.

### 5. `CandidateIndex._basePositionMatches` is a shared mutable field
The reused `List<int>` is cleared at the start of each `TryFindMatchingCandidateIndex` call. This is safe for single-threaded Unity editor use, but it means `CandidateIndex` is not safe to share across threads. A comment noting this would prevent confusion if parallel processing is added later.

### 6. Epsilon hardcoded to `1e-5f`
`FingerprintEpsilon = 1e-5f` is appropriate for meshes in Unity's metre-based coordinate system, but could be too tight for very small meshes or too loose for extremely dense meshes. Consider making it configurable or auto-scaling it relative to the mesh's bounding box diagonal.