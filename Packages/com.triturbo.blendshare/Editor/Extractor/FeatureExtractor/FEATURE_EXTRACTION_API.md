# Mesh Feature Extraction API

**Namespace:** `Triturbo.BlendShapeShare.Extractor`
**Assembly:** Editor-only. Depends on `UnityEditor.TypeCache`.

---

## Overview

The feature extraction API is a plugin-style pipeline that extracts mesh features (blendshapes, UV data, skin weights, etc.) from a pair of FBX assets and assembles a `BlendShareObject`. Each feature is handled by an independent extractor. New features register automatically — no edits to central orchestration code required.

The three roles in the system:

| Role | Responsibility |
|---|---|
| **Options** (`MeshFeatureExtractionOptions`) | Carries per-feature user settings and selection state |
| **Extractor** (`IMeshFeatureExtractor`) | Reads mesh data and produces a `MeshFeatureObject` |
| **Options Provider** (`IMeshFeatureExtractionOptionsProvider`) | Draws the feature's settings UI in the editor window |

---

## Core Types

### `MeshFeatureExtractionOptions`

Abstract base for per-feature extraction settings.

```csharp
public abstract class MeshFeatureExtractionOptions
{
    public abstract string FeatureId { get; }
    public bool Enabled = true;
}
```

- One concrete subclass per feature (e.g., `BlendShapeExtractionOptions`).
- `Enabled = false` causes the extractor to be skipped entirely.
- Instances are stored in `MeshFeatureExtractionOptionsSet`, keyed by concrete `Type`.

---

### `MeshFeatureExtractionOptionsSet`

A typed dictionary that holds one options instance per feature.

```csharp
public sealed class MeshFeatureExtractionOptionsSet
{
    public IEnumerable<MeshFeatureExtractionOptions> All { get; }

    public void Set<TOptions>(TOptions options)
        where TOptions : MeshFeatureExtractionOptions;

    public void Set(Type optionsType, MeshFeatureExtractionOptions options);

    public bool TryGet<TOptions>(out TOptions options)
        where TOptions : MeshFeatureExtractionOptions;

    public bool TryGet(Type optionsType, out MeshFeatureExtractionOptions options);
}
```

- Passing `null` to `Set` removes the entry for that type.
- The pipeline reads from this set to decide which extractors to run.

---

### `IMeshFeatureExtractor`

The contract every extractor implements.

```csharp
public interface IMeshFeatureExtractor
{
    string FeatureId { get; }
    Type FeatureType { get; }
    Type OptionsType { get; }

    MeshFeatureExtractionResult TryExtract(
        MeshFeatureExtractionContext context,
        out MeshFeatureObject feature);
}
```

- `FeatureId` must match `MeshFeatureObject.FeatureId` for the produced feature.
- `OptionsType` is used by the pipeline and registry for lookup.
- `TryExtract` is called once per mesh request.

---

### `MeshFeatureExtractor<TFeature, TOptions>`

Typed abstract base that handles the options check, leaving only extraction logic to subclasses.

```csharp
public abstract class MeshFeatureExtractor<TFeature, TOptions> : IMeshFeatureExtractor
    where TFeature : MeshFeatureObject
    where TOptions : MeshFeatureExtractionOptions
{
    public abstract string FeatureId { get; }
    public Type FeatureType => typeof(TFeature);
    public Type OptionsType => typeof(TOptions);

    // Called by the pipeline via IMeshFeatureExtractor.TryExtract.
    // Returns Skipped if options are absent or disabled.
    public MeshFeatureExtractionResult TryExtract(
        MeshFeatureExtractionContext context,
        out MeshFeatureObject feature);

    // Implement this. Only called when options are present and enabled.
    protected abstract MeshFeatureExtractionResult TryExtract(
        MeshFeatureExtractionContext context,
        TOptions options,
        out TFeature feature);
}
```

**Registration requirements:**
- Must be a concrete, non-generic class.
- Must have a public parameterless constructor.
- `FeatureId` must be non-empty, unique across all registered extractors.
- `OptionsType` must be unique across all registered extractors.

---

### `MeshFeatureExtractionResult`

Returned by `TryExtract` to communicate outcome.

```csharp
public readonly struct MeshFeatureExtractionResult
{
    public MeshFeatureExtractionStatus Status { get; }   // Succeeded | Skipped | Failed
    public string Message { get; }
    public bool Succeeded { get; }

    public static MeshFeatureExtractionResult Success();
    public static MeshFeatureExtractionResult Skipped(string message = null);
    public static MeshFeatureExtractionResult Failed(string message);
}
```

- `Succeeded` → feature is added to the mesh data object.
- `Skipped` → feature is omitted silently (no log entry).
- `Failed` → `Debug.LogError` is emitted by the pipeline; feature is omitted.

---

## Session and Context

### `MeshFeatureExtractionSession`

Scoped to one pipeline run. Owns the mesh data sources and the options set. Implements `IDisposable` — the pipeline wraps it in a `using` block so FBX SDK resources are released after all meshes are processed.

```csharp
public sealed class MeshFeatureExtractionSession : IDisposable
{
    public GameObject SourceFbxAsset { get; }
    public GameObject OriginFbxAsset { get; }
    public MeshFeatureExtractionOptionsSet Options { get; }
    public IReadOnlyList<MeshFeatureExtractionMeshRequest> Meshes { get; }

    public UnityMeshExtractionSource SourceUnityMeshes { get; }
    public UnityMeshExtractionSource OriginUnityMeshes { get; }
    public FbxMeshExtractionSource SourceFbxMeshes { get; }
    public FbxMeshExtractionSource OriginFbxMeshes { get; }

#if ENABLE_FBX_SDK
    public FbxSdkExtractionSource SourceFbxScene { get; }
    public FbxSdkExtractionSource OriginFbxScene { get; }
#endif

    public MeshFeatureExtractionSession(
        GameObject sourceFbxAsset,
        GameObject originFbxAsset,
        MeshFeatureExtractionOptionsSet options,
        IEnumerable<MeshFeatureExtractionMeshRequest> meshes = null);
}
```

Sources are initialized eagerly at construction. FBX binary snapshots are read lazily on first access (see [Mesh Sources](#mesh-sources)).

---

### `MeshFeatureExtractionContext`

Scoped to one mesh within a session. Passed to every extractor.

```csharp
public sealed class MeshFeatureExtractionContext
{
    public MeshFeatureExtractionSession Session { get; }
    public MeshFeatureExtractionOptionsSet Options => Session.Options;

    public string MeshPath { get; }   // Hierarchy path relative to FBX root (e.g. "Body/Head")
    public string MeshName { get; }   // Unity mesh asset name

    public Mesh GetSourceUnityMesh();
    public Mesh GetOriginUnityMesh();

    public FbxMeshSnapshot GetSourceFbxMesh(FbxMeshReadOptions options);
    public FbxMeshSnapshot GetOriginFbxMesh(FbxMeshReadOptions options);
}
```

Mesh lookup uses `MeshPath` first, falling back to `MeshName`. Results are cached in the source objects, so multiple extractors calling `GetSourceFbxMesh` with identical options pay only one parse cost.

---

### `MeshFeatureExtractionMeshRequest`

Identifies a single mesh to extract features for.

```csharp
public sealed class MeshFeatureExtractionMeshRequest
{
    public string MeshPath;   // Hierarchy path (preferred identifier)
    public string MeshName;   // Mesh asset name (fallback)
}
```

Either field may be empty, but at least one must be set. The pipeline filters out requests where both are empty.

---

## Mesh Sources

### `UnityMeshExtractionSource`

Walks the `SkinnedMeshRenderer` components of an FBX asset's `GameObject` hierarchy and builds path-and-name lookup tables on first access (lazy, then cached).

```csharp
public sealed class UnityMeshExtractionSource
{
    public Mesh GetMesh(string meshPath, string meshName);
    public string GetMeshPath(string meshName);
}
```

### `FbxMeshExtractionSource`

Parses binary FBX files via `BinaryFbxMeshReader`. Results are cached by `(meshPath, meshName, FbxMeshReadOptions)`.

```csharp
public sealed class FbxMeshExtractionSource
{
    public FbxMeshSnapshot GetMesh(string meshPath, string meshName, FbxMeshReadOptions options);
}
```

`FbxMeshReadOptions` is a flags enum. The source normalizes options before caching: if `BlendShapes` or `BoneWeights` is set, `ControlPointPositions` is added automatically because those reads depend on control point data.

### `FbxSdkExtractionSource` *(requires `ENABLE_FBX_SDK`)*

Loads the full FBX scene via the Autodesk FBX SDK. Imported once per session. Must be disposed (handled by `MeshFeatureExtractionSession.Dispose`).

```csharp
public sealed class FbxSdkExtractionSource : IDisposable
{
    public bool TryGetRootNode(out FbxNode rootNode, out string error);
    public void Dispose();
}
```

---

## Auto-Registration

### `MeshFeatureExtractorRegistry`

Discovers and holds all extractor instances. Initialized at domain reload via `[InitializeOnLoad]`.

```csharp
[InitializeOnLoad]
public static class MeshFeatureExtractorRegistry
{
    public static IReadOnlyList<IMeshFeatureExtractor> Extractors { get; }

    public static IMeshFeatureExtractor GetByFeatureId(string featureId);
    public static IMeshFeatureExtractor GetByOptionsType(Type optionsType);

    public static void Reload();   // Re-discovers all types; useful in tests.
}
```

**Discovery rules:**
1. `TypeCache.GetTypesDerivedFrom<IMeshFeatureExtractor>()` — searches all loaded assemblies.
2. Abstract classes, interfaces, and open generic types are skipped.
3. Types without a public parameterless constructor are skipped with a logged error.
4. Duplicate `FeatureId` or duplicate `OptionsType` across registered instances produces a `Debug.LogError`.
5. Registered instances are sorted by `Type.FullName` for deterministic ordering.

### `MeshFeatureExtractionOptionsProviderRegistry`

Same discovery mechanism for `IMeshFeatureExtractionOptionsProvider`. Providers are sorted by `DisplayOrder`, then `Type.FullName`.

```csharp
[InitializeOnLoad]
public static class MeshFeatureExtractionOptionsProviderRegistry
{
    public static IReadOnlyList<IMeshFeatureExtractionOptionsProvider> Providers { get; }
    public static void Reload();
}
```

---

## Editor UI Extension

### `IMeshFeatureExtractionOptionsProvider`

Allows a feature to contribute its settings UI to the extraction editor window.

```csharp
public interface IMeshFeatureExtractionOptionsProvider
{
    string FeatureId { get; }
    Type OptionsType { get; }
    int DisplayOrder { get; }

    MeshFeatureExtractionOptions CreateDefaultOptions();

    void DrawOptionsGUI(
        MeshFeatureExtractionOptions options,
        MeshFeatureOptionsEditorContext context);
}
```

### `MeshFeatureExtractionOptionsProvider<TOptions>`

Typed abstract base; eliminates the cast inside `DrawOptionsGUI`.

```csharp
public abstract class MeshFeatureExtractionOptionsProvider<TOptions>
    : IMeshFeatureExtractionOptionsProvider
    where TOptions : MeshFeatureExtractionOptions
{
    public abstract string FeatureId { get; }
    public Type OptionsType => typeof(TOptions);
    public virtual int DisplayOrder => 0;

    protected abstract TOptions CreateDefault();
    protected abstract void DrawOptionsGUI(TOptions options, MeshFeatureOptionsEditorContext context);
}
```

**Registration requirements:** same as extractors (concrete, parameterless constructor, unique `FeatureId` and `OptionsType`).

### `MeshFeatureOptionsEditorContext`

Read-only editor context passed to `DrawOptionsGUI`. Provides the two FBX asset references and the current mesh list so provider UI can show per-mesh controls.

```csharp
public sealed class MeshFeatureOptionsEditorContext
{
    public GameObject SourceFbxAsset { get; }
    public GameObject OriginFbxAsset { get; }
    public IReadOnlyList<MeshFeatureExtractionMeshRequest> Meshes { get; }
}
```

---

## Pipeline

### `MeshFeatureExtractionPipeline`

Orchestrates a full extraction run.

```csharp
public sealed class MeshFeatureExtractionPipeline
{
    public BlendShareObject Extract(
        GameObject source,
        GameObject origin,
        IEnumerable<MeshFeatureExtractionMeshRequest> meshes,
        MeshFeatureExtractionOptionsSet options);
}
```

**Returns** a new in-memory `BlendShareObject` (not yet saved to disk), or `null` if inputs are invalid or no features were extracted from any mesh.

**Execution steps:**

```
MeshFeatureExtractionPipeline.Extract(source, origin, meshes, options)
│
├─ Validate inputs (non-null source/origin, at least one valid mesh request)
│
└─ using (session = new MeshFeatureExtractionSession(...))
   │
   ├─ For each MeshFeatureExtractionMeshRequest:
   │  │
   │  ├─ context = new MeshFeatureExtractionContext(session, meshPath, meshName)
   │  │
   │  ├─ meshDataObject = CreateMeshData(context)
   │  │    └─ Reads origin FBX control point count for fingerprinting
   │  │
   │  ├─ For each extractor in MeshFeatureExtractorRegistry.Extractors:
   │  │  │
   │  │  ├─ ShouldRunExtractor? (options present and Enabled == true)
   │  │  │    No  → skip (no log)
   │  │  │    Yes → extractor.TryExtract(context, out feature)
   │  │  │
   │  │  ├─ Succeeded  → meshDataObject.AddFeature(feature)
   │  │  ├─ Skipped    → (silent)
   │  │  └─ Failed     → Debug.LogError(message)
   │  │
   │  └─ meshDataObject.Features.Count > 0?
   │       Yes → BuildMappings(context) → meshDataObject.m_Mappings
   │             add to extractedMeshes
   │       No  → discard
   │
   └─ extractedMeshes.Count > 0?
        Yes → create BlendShareObject, call blendShare.SetMeshes(extractedMeshes), return it
        No  → return null
```

**Mapping build:** `UnityFbxVertexMappingBuilder.BuildFromFbx` is called per mesh after all features are extracted. If the resulting mapping is not valid (e.g., vertex count mismatch), the Unity blendshape cache stored by `BlendShapeFeatureExtractor` is applied as a fallback.

The pipeline does not save to disk. Callers pass the returned `BlendShareObject` to `BlendShareAssetService.Save`.

---

## Implementing a New Feature

A new feature requires three types and optionally a fourth for editor UI.

### 1. Data object

```csharp
public sealed class MyFeatureObject : MeshFeatureObject
{
    public const string Id = "my-feature";
    public override string FeatureId => Id;

    // Serialized fields storing the extracted data
}
```

### 2. Options

```csharp
public sealed class MyFeatureExtractionOptions : MeshFeatureExtractionOptions
{
    public override string FeatureId => MyFeatureObject.Id;

    // Feature-specific extraction settings
}
```

### 3. Extractor

```csharp
public sealed class MyFeatureExtractor
    : MeshFeatureExtractor<MyFeatureObject, MyFeatureExtractionOptions>
{
    public override string FeatureId => MyFeatureObject.Id;

    protected override MeshFeatureExtractionResult TryExtract(
        MeshFeatureExtractionContext context,
        MyFeatureExtractionOptions options,
        out MyFeatureObject feature)
    {
        feature = null;

        var mesh = context.GetSourceFbxMesh(FbxMeshReadOptions.ControlPointPositions);
        if (mesh == null)
        {
            return MeshFeatureExtractionResult.Failed("Source FBX mesh not found.");
        }

        feature = ScriptableObject.CreateInstance<MyFeatureObject>();
        // populate feature from mesh data
        return MeshFeatureExtractionResult.Success();
    }
}
```

The extractor is discovered automatically at domain reload. No other files need to be modified.

### 4. Options provider (optional)

```csharp
public sealed class MyFeatureOptionsProvider
    : MeshFeatureExtractionOptionsProvider<MyFeatureExtractionOptions>
{
    public override string FeatureId => MyFeatureObject.Id;
    public override int DisplayOrder => 10;

    protected override MyFeatureExtractionOptions CreateDefault()
        => new MyFeatureExtractionOptions();

    protected override void DrawOptionsGUI(
        MyFeatureExtractionOptions options,
        MeshFeatureOptionsEditorContext context)
    {
        options.Enabled = EditorGUILayout.Toggle("Enable My Feature", options.Enabled);
        // additional controls
    }
}
```

---

## Built-in Feature: BlendShapes

### `BlendShapeExtractionOptions`

```csharp
public sealed class BlendShapeExtractionOptions : MeshFeatureExtractionOptions
{
    public override string FeatureId => BlendShapeFeatureObject.Id;

    // Global fallback list; used when no mesh-specific list is set.
    public List<string> SelectedBlendShapeNames = new();

    public BlendShapeBaseMesh BaseMesh = BlendShapeBaseMesh.Source;
    public bool WeldVertices = true;
    public bool ApplyRotation;
    public bool ApplyScale;
    public bool ApplyTranslate;
    public float BlendShapeScale = 1f;
    public bool ApplyTransform { get; }   // true if any transform flag is set

    // Per-mesh shape name selection (overrides SelectedBlendShapeNames for that mesh).
    public void SetSelectedBlendShapeNames(string meshPath, string meshName, IEnumerable<string> shapeNames);
    public List<string> GetSelectedBlendShapeNames(string meshPath, string meshName);
}
```

`BaseMesh` controls which mesh is used as the delta base:
- `Source` — deltas are computed against the source FBX mesh.
- `Original` — deltas are computed against the origin FBX mesh.

### `BlendShapeFeatureExtractor`

Requires `ENABLE_FBX_SDK`. Uses `session.SourceFbxScene` and `session.OriginFbxScene` to read blendshape deformers, then stores a `UnityBlendShapeData` cache in the session for the subsequent mapping build step. Returns `Skipped` when no blendshape names are selected for the mesh.

---

## File Layout

```
Editor/Extractor/FeatureExtractor/
├── MeshFeatureExtractor.cs               IMeshFeatureExtractor, MeshFeatureExtractor<T,O>
├── MeshFeatureExtractionOptions.cs       MeshFeatureExtractionOptions, MeshFeatureExtractionOptionsSet
├── MeshFeatureExtractionResult.cs        MeshFeatureExtractionResult, MeshFeatureExtractionStatus
├── MeshFeatureExtractionContext.cs       MeshFeatureExtractionSession, MeshFeatureExtractionContext,
│                                         MeshFeatureExtractionMeshRequest
├── MeshFeatureExtractionSources.cs       UnityMeshExtractionSource, FbxMeshExtractionSource,
│                                         FbxSdkExtractionSource
├── MeshFeatureExtractionRegistries.cs    MeshFeatureExtractorRegistry,
│                                         MeshFeatureExtractionOptionsProviderRegistry
├── MeshFeatureExtractionOptionsProvider.cs  IMeshFeatureExtractionOptionsProvider,
│                                            MeshFeatureExtractionOptionsProvider<T>,
│                                            MeshFeatureOptionsEditorContext
├── MeshFeatureExtractionPipeline.cs      MeshFeatureExtractionPipeline
└── BlendShape/
    ├── BlendShapeExtractionOptions.cs    BlendShapeExtractionOptions, BlendShapeBaseMesh
    └── BlendShapeFeatureExtractor.cs     BlendShapeFeatureExtractor
```
