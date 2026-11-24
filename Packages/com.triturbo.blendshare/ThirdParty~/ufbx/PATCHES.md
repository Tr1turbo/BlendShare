# Vendored `ufbx` Patches

Upstream source: `https://github.com/ufbx/ufbx`  
Vendored tag: **`v0.23.1`**

All local modifications are delimited by `/* BLENDSHARE_PATCH_BEGIN ... */` and `/* BLENDSHARE_PATCH_END */`.

## Patch 1: Preserve raw 32-bit `LayerElementSmoothing` bitmasks (`face_smoothing_group`)

### Motivation
Upstream `ufbx` decodes `LayerElementSmoothing/Smoothing` with array target type `'b'` (boolean), which collapses FBX 32-bit `ByPolygon` smoothing group bitmasks (e.g., `0, 1, 2, 3`, `0x100`, `0x80000000`) into 1-byte booleans during parsing—even before DOM retention. Unity's FBX normal calculation and BlendShare's forward reconstruction need the original 32-bit bitmask.

### Changes
1. **`ufbx.h`** (`struct ufbx_mesh`): Added `ufbx_uint32_list face_smoothing_group`.
2. **`ufbx.c`** (`UFBXI_PARSE_LAYER_ELEMENT_SMOOTHING`): Changed `info->type` from `'b'` to `'i'` so `Smoothing` arrays decode as 32-bit integers.
3. **`ufbx.c`** (`ufbxi_binary_convert_array` packed `case 'i': case 'c'` AND legacy multivalue scalar `case 'C' / 'B'`): Zero-extend `(uint8_t)*val` instead of sign-extending `char` so byte masks with bit 7 set (`0x80`) are preserved as `0x00000080` rather than `0xFFFFFF80` across both binary array encodings.
4. **`ufbx.c`** (`ufbxi_LayerElementSmoothing`):
   - `ByPolygon`: Read `mesh->face_smoothing_group` with `ufbxi_read_truncated_array(..., 'i', mesh->num_faces)` and derive `mesh->face_smoothing[i] = (face_smoothing_group[i] != 0)`. Note: deriving `mask != 0` fixes an upstream edge case where `0x100` truncated to `(uint8_t)0` (`false`) under `'b'`.
   - `ByEdge`: Read raw `'i'` array with `ufbxi_read_truncated_array(..., 'i', mesh->num_edges)` and derive `mesh->edge_smoothing[i] = (raw[i] != 0)`.
5. **`ufbx.c`** (`ufbx_subdivide_mesh`): Propagate `mesh->face_smoothing_group` to subdivided child faces alongside `mesh->face_smoothing`.

### Re-applying on Upstream Upgrade
Search for `BLENDSHARE_PATCH_BEGIN` in `ufbx.h` and `ufbx.c` to inspect the 5 hunks, then re-apply on the new upstream files and run the native smoothing verification harness (Salt oracle + `0x100` / `0x80000000` / `0x80` byte fixtures).
