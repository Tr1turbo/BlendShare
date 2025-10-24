# BlendShare Changelog

## [1.0.1] - 2025/10/24
### Fix
- Corrected the order of blendshape data application when rebuilding an FBX if the target is a **GeneratedMeshAssetSO**.
- Previously, the blendshapes could be applied in the wrong order, causing unexpected results.

## [1.0.0] - 2025/10/22

### New Features

**Advanced Mesh Generator**

* Apply multiple `BlendShapeDataSO` assets and merge all blendshapes into a single model.

**API Updates**

* `GeneratedMeshAssetSO CreateMeshAsset(Object targetMeshContainer, IEnumerable<BlendShapeDataSO> blendShapes, string path)`
* `bool CreateFbx(GameObject source, IEnumerable<BlendShapeDataSO> blendShapes, string outputPath = null, bool onlyNecessary = false)`

**Deprecated Methods**

* `CreateMeshAsset(List<Mesh>, string)`
* `CreateMeshAsset(this BlendShapeDataSO, string, GameObject)`

  > These methods are deprecated because the naming was confusing. They only save the mesh into an asset and do **not** create meshes with applied blendshapes.

**New Mesh Saving Workflow**

* Use `GeneratedMeshAssetSO.SaveMeshesToAsset()` to save generated meshes to asset files.

**Metadata Improvements in `GeneratedMeshAsset`**

* Stores the original FBX that provided the mesh.
* Includes a hash to detect if the original FBX was modified.
* Tracks all applied `BlendShare BlendShapes Data Assets`.

**Assign Mesh Feature**

* Automatically assigns all meshes in the asset to the target GameObject based on mesh name.

**Localization Support**

* English, Japanese, Traditional Chinese, and Simplified Chinese supported.



## [0.0.11] 

### New Features

* **Apply Transform Option**

  * `Apply Rotation` added to handle orientation differences from models exported with different rotation settings.
  * The object’s rotation is applied before calculating blendshape differences to ensure proper alignment.

### Fixes

* **Nested FBX Node Issue**

  * Fixed a bug where nested FBX nodes could not be found, preventing blendshape extraction.
  * Nested FBX nodes are now correctly detected and processed.
