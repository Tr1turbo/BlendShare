#include "blendshare_ufbx_bridge.h"

#include <limits.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#include "ufbx.h"

#define BS_STATIC_ASSERT(name, cond) typedef char bs_static_assert_##name[(cond) ? 1 : -1]

/* Frozen ABI v3 struct size and alignment invariants across Win/macOS/Linux x64 & arm64 */
BS_STATIC_ASSERT(vec2_size, sizeof(bs_ufbx_vec2) == 16);
BS_STATIC_ASSERT(vec3_size, sizeof(bs_ufbx_vec3) == 24);
BS_STATIC_ASSERT(vec4_size, sizeof(bs_ufbx_vec4) == 32);
BS_STATIC_ASSERT(quat_size, sizeof(bs_ufbx_quat) == 32);
BS_STATIC_ASSERT(matrix_size, sizeof(bs_ufbx_matrix) == 128);
BS_STATIC_ASSERT(edge_size, sizeof(bs_ufbx_edge) == 8);
BS_STATIC_ASSERT(axes_size, sizeof(bs_ufbx_axes) == 12);
BS_STATIC_ASSERT(load_options_size, sizeof(bs_ufbx_load_options) == 32);
BS_STATIC_ASSERT(load_options_unit_off, offsetof(bs_ufbx_load_options, target_unit_meters) == 24);
BS_STATIC_ASSERT(scene_info_size, sizeof(bs_ufbx_scene_info) == 56);
BS_STATIC_ASSERT(scene_info_unit_off, offsetof(bs_ufbx_scene_info, unit_meters) == 8);
BS_STATIC_ASSERT(node_info_size, sizeof(bs_ufbx_node_info) == 960);
BS_STATIC_ASSERT(node_info_lcl_off, offsetof(bs_ufbx_node_info, lcl_translation) == 72);
BS_STATIC_ASSERT(node_info_mat_off, offsetof(bs_ufbx_node_info, node_to_parent) == 448);
BS_STATIC_ASSERT(material_info_size, sizeof(bs_ufbx_material_info) == 16);
BS_STATIC_ASSERT(mesh_info_size, sizeof(bs_ufbx_mesh_info) == 104);
BS_STATIC_ASSERT(mesh_part_info_size, sizeof(bs_ufbx_mesh_part_info) == 12);
BS_STATIC_ASSERT(layer_set_info_size, sizeof(bs_ufbx_layer_set_info) == 8);
BS_STATIC_ASSERT(attrib_info_size, sizeof(bs_ufbx_attrib_info) == 20);
BS_STATIC_ASSERT(skin_info_size, sizeof(bs_ufbx_skin_info) == 16);
BS_STATIC_ASSERT(cluster_info_size, sizeof(bs_ufbx_cluster_info) == 536);
BS_STATIC_ASSERT(cluster_info_mat_off, offsetof(bs_ufbx_cluster_info, mesh_bind_world) == 24);
BS_STATIC_ASSERT(blend_deformer_info_size, sizeof(bs_ufbx_blend_deformer_info) == 16);
BS_STATIC_ASSERT(blend_channel_info_size, sizeof(bs_ufbx_blend_channel_info) == 16);
BS_STATIC_ASSERT(blend_keyframe_info_size, sizeof(bs_ufbx_blend_keyframe_info) == 40);

struct bs_ufbx_scene {
    ufbx_scene *scene;
    char **node_paths;
};

static int32_t bs_checked_i32(size_t value, int32_t *out_value)
{
    if (value > (size_t)INT32_MAX) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }
    *out_value = (int32_t)value;
    return BS_UFBX_OK;
}

static int32_t bs_write_error(char *error, int32_t error_size, const char *message, int32_t status)
{
    if (error != NULL && error_size > 0) {
        const char *text = message != NULL ? message : "Unknown ufbx error";
        snprintf(error, (size_t)error_size, "%s", text);
    }
    return status;
}

static int32_t bs_copy_string(const char *src, size_t length, char *dst, int32_t dst_size)
{
    int32_t len_i32 = 0;
    if (bs_checked_i32(length, &len_i32) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }
    if (dst != NULL && dst_size > 0) {
        size_t copy_len = length;
        if (copy_len > (size_t)(dst_size - 1)) {
            copy_len = (size_t)(dst_size - 1);
        }
        if (src != NULL && copy_len > 0) {
            memcpy(dst, src, copy_len);
        }
        dst[copy_len] = '\0';
    }
    return len_i32;
}

static char *bs_dup_string(const char *src, size_t length)
{
    char *result = (char *)malloc(length + 1);
    if (result == NULL) {
        return NULL;
    }
    if (src != NULL && length > 0) {
        memcpy(result, src, length);
    }
    result[length] = '\0';
    return result;
}

static ufbx_node *bs_get_node(bs_ufbx_scene *scene, int32_t index)
{
    if (scene == NULL || scene->scene == NULL || index < 0 || (size_t)index >= scene->scene->nodes.count) {
        return NULL;
    }
    return scene->scene->nodes.data[index];
}

static ufbx_material *bs_get_material(bs_ufbx_scene *scene, int32_t index)
{
    if (scene == NULL || scene->scene == NULL || index < 0 || (size_t)index >= scene->scene->materials.count) {
        return NULL;
    }
    return scene->scene->materials.data[index];
}

static ufbx_mesh *bs_get_mesh(bs_ufbx_scene *scene, int32_t index)
{
    if (scene == NULL || scene->scene == NULL || index < 0 || (size_t)index >= scene->scene->meshes.count) {
        return NULL;
    }
    return scene->scene->meshes.data[index];
}

static ufbx_skin_deformer *bs_get_skin(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || skin_index < 0 || (size_t)skin_index >= mesh->skin_deformers.count) {
        return NULL;
    }
    return mesh->skin_deformers.data[skin_index];
}

static ufbx_skin_cluster *bs_get_cluster(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index)
{
    ufbx_skin_deformer *skin = bs_get_skin(scene, mesh_index, skin_index);
    if (skin == NULL || cluster_index < 0 || (size_t)cluster_index >= skin->clusters.count) {
        return NULL;
    }
    return skin->clusters.data[cluster_index];
}

static ufbx_blend_deformer *bs_get_blend_deformer(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || deformer_index < 0 || (size_t)deformer_index >= mesh->blend_deformers.count) {
        return NULL;
    }
    return mesh->blend_deformers.data[deformer_index];
}

static ufbx_blend_channel *bs_get_blend_channel(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index)
{
    ufbx_blend_deformer *deformer = bs_get_blend_deformer(scene, mesh_index, deformer_index);
    if (deformer == NULL || channel_index < 0 || (size_t)channel_index >= deformer->channels.count) {
        return NULL;
    }
    return deformer->channels.data[channel_index];
}

static const ufbx_blend_keyframe *bs_get_blend_keyframe(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t deformer_index,
    int32_t channel_index,
    int32_t keyframe_index)
{
    ufbx_blend_channel *channel = bs_get_blend_channel(scene, mesh_index, deformer_index, channel_index);
    if (channel == NULL || keyframe_index < 0 || (size_t)keyframe_index >= channel->keyframes.count) {
        return NULL;
    }
    return &channel->keyframes.data[keyframe_index];
}

/* O(1) typed_id lookups verified against the scene's typed list */
static int32_t bs_node_index_o1(const ufbx_scene *scene, const ufbx_node *node)
{
    if (scene == NULL || node == NULL) {
        return -1;
    }
    uint32_t tid = node->typed_id;
    if ((size_t)tid < scene->nodes.count && scene->nodes.data[tid] == node && tid <= (uint32_t)INT32_MAX) {
        return (int32_t)tid;
    }
    return -1;
}

static int32_t bs_mesh_index_o1(const ufbx_scene *scene, const ufbx_mesh *mesh)
{
    if (scene == NULL || mesh == NULL) {
        return -1;
    }
    uint32_t tid = mesh->typed_id;
    if ((size_t)tid < scene->meshes.count && scene->meshes.data[tid] == mesh && tid <= (uint32_t)INT32_MAX) {
        return (int32_t)tid;
    }
    return -1;
}

static int32_t bs_material_index_o1(const ufbx_scene *scene, const ufbx_material *material)
{
    if (scene == NULL || material == NULL) {
        return -1;
    }
    uint32_t tid = material->typed_id;
    if ((size_t)tid < scene->materials.count && scene->materials.data[tid] == material && tid <= (uint32_t)INT32_MAX) {
        return (int32_t)tid;
    }
    return -1;
}

static int32_t bs_node_attrib_kind(const ufbx_node *node)
{
    if (node == NULL) return BS_UFBX_ATTRIB_NONE;
    if (node->mesh != NULL) return BS_UFBX_ATTRIB_MESH;
    if (node->bone != NULL) return BS_UFBX_ATTRIB_BONE;
    if (node->camera != NULL) return BS_UFBX_ATTRIB_CAMERA;
    if (node->light != NULL) return BS_UFBX_ATTRIB_LIGHT;
    if (node->attrib != NULL) {
        if (node->attrib->type == UFBX_ELEMENT_EMPTY) return BS_UFBX_ATTRIB_EMPTY;
        return BS_UFBX_ATTRIB_OTHER;
    }
    return BS_UFBX_ATTRIB_NONE;
}

static bs_ufbx_vec2 bs_vec2(ufbx_vec2 v)
{
    bs_ufbx_vec2 r;
    r.x = (double)v.x;
    r.y = (double)v.y;
    return r;
}

static bs_ufbx_vec3 bs_vec3(ufbx_vec3 v)
{
    bs_ufbx_vec3 r;
    r.x = (double)v.x;
    r.y = (double)v.y;
    r.z = (double)v.z;
    return r;
}

static bs_ufbx_vec4 bs_vec4(ufbx_vec4 v)
{
    bs_ufbx_vec4 r;
    r.x = (double)v.x;
    r.y = (double)v.y;
    r.z = (double)v.z;
    r.w = (double)v.w;
    return r;
}

static bs_ufbx_quat bs_quat(ufbx_quat q)
{
    bs_ufbx_quat r;
    r.x = (double)q.x;
    r.y = (double)q.y;
    r.z = (double)q.z;
    r.w = (double)q.w;
    return r;
}

static bs_ufbx_axes bs_axes(ufbx_coordinate_axes a)
{
    bs_ufbx_axes r;
    r.right = (int32_t)a.right;
    r.up = (int32_t)a.up;
    r.front = (int32_t)a.front;
    return r;
}

static void bs_matrix_identity(bs_ufbx_matrix *dst)
{
    int i;
    for (i = 0; i < 16; i++) dst->m[i] = 0.0;
    dst->m[0] = 1.0;
    dst->m[5] = 1.0;
    dst->m[10] = 1.0;
    dst->m[15] = 1.0;
}

/* Convert column-vector affine ufbx_matrix to row-vector 4x4 layout matching FbxMatrix4x4 */
static void bs_matrix_from_ufbx(bs_ufbx_matrix *dst, const ufbx_matrix *src)
{
    if (dst == NULL) return;
    if (src == NULL) {
        bs_matrix_identity(dst);
        return;
    }
    dst->m[0]  = (double)src->m00; dst->m[1]  = (double)src->m10; dst->m[2]  = (double)src->m20; dst->m[3]  = 0.0;
    dst->m[4]  = (double)src->m01; dst->m[5]  = (double)src->m11; dst->m[6]  = (double)src->m21; dst->m[7]  = 0.0;
    dst->m[8]  = (double)src->m02; dst->m[9]  = (double)src->m12; dst->m[10] = (double)src->m22; dst->m[11] = 0.0;
    dst->m[12] = (double)src->m03; dst->m[13] = (double)src->m13; dst->m[14] = (double)src->m23; dst->m[15] = 1.0;
}

static int32_t bs_build_node_paths(bs_ufbx_scene *handle)
{
    size_t count = handle->scene->nodes.count;
    if (count == 0) {
        handle->node_paths = NULL;
        return BS_UFBX_OK;
    }

    handle->node_paths = (char **)calloc(count, sizeof(char *));
    if (handle->node_paths == NULL) {
        return BS_UFBX_ERR_OUT_OF_MEMORY;
    }

    for (size_t i = 0; i < count; i++) {
        ufbx_node *node = handle->scene->nodes.data[i];
        if (node == NULL || node->is_root) {
            handle->node_paths[i] = bs_dup_string("", 0);
            if (handle->node_paths[i] == NULL) {
                return BS_UFBX_ERR_OUT_OF_MEMORY;
            }
            continue;
        }

        size_t length = 0;
        size_t depth = 0;
        for (ufbx_node *cur = node; cur != NULL && !cur->is_root; cur = cur->parent) {
            length += cur->name.length;
            depth++;
        }
        if (depth > 1) length += depth - 1;

        char *path = (char *)malloc(length + 1);
        if (path == NULL) {
            return BS_UFBX_ERR_OUT_OF_MEMORY;
        }
        path[length] = '\0';
        size_t offset = length;
        int is_leaf = 1;
        for (ufbx_node *cur = node; cur != NULL && !cur->is_root; cur = cur->parent) {
            if (!is_leaf) {
                path[--offset] = '/';
            }
            is_leaf = 0;
            offset -= cur->name.length;
            if (cur->name.length > 0 && cur->name.data != NULL) {
                memcpy(path + offset, cur->name.data, cur->name.length);
            }
        }
        handle->node_paths[i] = path;
    }
    return BS_UFBX_OK;
}

static int32_t bs_configure_load_opts(const bs_ufbx_load_options *opts, ufbx_load_opts *out_uopts)
{
    memset(out_uopts, 0, sizeof(*out_uopts));
    out_uopts->evaluate_skinning = false;
    out_uopts->skip_skin_vertices = false;

    if (opts == NULL) {
        return BS_UFBX_OK;
    }
    if (opts->struct_size != (uint32_t)sizeof(bs_ufbx_load_options)) {
        return BS_UFBX_ERR_INVALID_ARG;
    }

    const uint32_t known_flags =
        BS_UFBX_LOAD_IGNORE_ANIMATION |
        BS_UFBX_LOAD_IGNORE_EMBEDDED |
        BS_UFBX_LOAD_GENERATE_MISSING_NORMALS |
        BS_UFBX_LOAD_LOAD_EXTERNAL_FILES |
        BS_UFBX_LOAD_RETAIN_VERTEX_ATTRIB_W |
        BS_UFBX_LOAD_USE_TARGET_AXES |
        BS_UFBX_LOAD_USE_TARGET_UNIT_METERS;
    if ((opts->flags & ~known_flags) != 0u) {
        return BS_UFBX_ERR_INVALID_ARG;
    }

    out_uopts->ignore_animation = (opts->flags & BS_UFBX_LOAD_IGNORE_ANIMATION) != 0u;
    out_uopts->ignore_embedded = (opts->flags & BS_UFBX_LOAD_IGNORE_EMBEDDED) != 0u;
    out_uopts->generate_missing_normals = (opts->flags & BS_UFBX_LOAD_GENERATE_MISSING_NORMALS) != 0u;
    out_uopts->load_external_files = (opts->flags & BS_UFBX_LOAD_LOAD_EXTERNAL_FILES) != 0u;
    out_uopts->retain_vertex_attrib_w = (opts->flags & BS_UFBX_LOAD_RETAIN_VERTEX_ATTRIB_W) != 0u;

    if ((opts->flags & BS_UFBX_LOAD_USE_TARGET_AXES) != 0u) {
        if (opts->target_axes.right < 0 || opts->target_axes.right >= (int32_t)UFBX_COORDINATE_AXIS_UNKNOWN ||
            opts->target_axes.up < 0 || opts->target_axes.up >= (int32_t)UFBX_COORDINATE_AXIS_UNKNOWN ||
            opts->target_axes.front < 0 || opts->target_axes.front >= (int32_t)UFBX_COORDINATE_AXIS_UNKNOWN) {
            return BS_UFBX_ERR_INVALID_ARG;
        }
        ufbx_coordinate_axes target_axes;
        target_axes.right = (ufbx_coordinate_axis)opts->target_axes.right;
        target_axes.up = (ufbx_coordinate_axis)opts->target_axes.up;
        target_axes.front = (ufbx_coordinate_axis)opts->target_axes.front;
        if (!ufbx_coordinate_axes_valid(target_axes)) {
            return BS_UFBX_ERR_INVALID_ARG;
        }
        out_uopts->target_axes = target_axes;
    }

    if ((opts->flags & BS_UFBX_LOAD_USE_TARGET_UNIT_METERS) != 0u) {
        if (!isfinite(opts->target_unit_meters) || !(opts->target_unit_meters > 0.0)) {
            return BS_UFBX_ERR_INVALID_ARG;
        }
        out_uopts->target_unit_meters = (ufbx_real)opts->target_unit_meters;
    }

    return BS_UFBX_OK;
}

static int32_t bs_wrap_loaded_scene(ufbx_scene *loaded, bs_ufbx_scene **out_scene, char *error, int32_t error_size)
{
    bs_ufbx_scene *handle = (bs_ufbx_scene *)calloc(1, sizeof(bs_ufbx_scene));
    if (handle == NULL) {
        ufbx_free_scene(loaded);
        return bs_write_error(error, error_size, "Failed to allocate ufbx scene handle.", BS_UFBX_ERR_OUT_OF_MEMORY);
    }
    handle->scene = loaded;
    int32_t path_status = bs_build_node_paths(handle);
    if (path_status != BS_UFBX_OK) {
        bs_ufbx_free(handle);
        return bs_write_error(error, error_size, "Failed to build node path table.", path_status);
    }

    *out_scene = handle;
    if (error != NULL && error_size > 0) {
        error[0] = '\0';
    }
    return BS_UFBX_OK;
}

static const ufbx_vertex_attrib *bs_resolve_attrib(
    const ufbx_mesh *mesh,
    int32_t attrib_kind,
    int32_t set_index,
    int32_t *out_status)
{
    *out_status = BS_UFBX_OK;
    if (mesh == NULL) {
        *out_status = BS_UFBX_ERR_OUT_OF_RANGE;
        return NULL;
    }

    switch (attrib_kind) {
    case BS_UFBX_MESH_ATTRIB_NORMAL:
        return (const ufbx_vertex_attrib *)&mesh->vertex_normal;

    case BS_UFBX_MESH_ATTRIB_TANGENT:
        if (set_index < 0) {
            return (const ufbx_vertex_attrib *)&mesh->vertex_tangent;
        }
        if ((size_t)set_index >= mesh->uv_sets.count) {
            *out_status = BS_UFBX_ERR_OUT_OF_RANGE;
            return NULL;
        }
        return (const ufbx_vertex_attrib *)&mesh->uv_sets.data[set_index].vertex_tangent;

    case BS_UFBX_MESH_ATTRIB_BITANGENT:
        if (set_index < 0) {
            return (const ufbx_vertex_attrib *)&mesh->vertex_bitangent;
        }
        if ((size_t)set_index >= mesh->uv_sets.count) {
            *out_status = BS_UFBX_ERR_OUT_OF_RANGE;
            return NULL;
        }
        return (const ufbx_vertex_attrib *)&mesh->uv_sets.data[set_index].vertex_bitangent;

    case BS_UFBX_MESH_ATTRIB_UV:
        if (set_index < 0) {
            return (const ufbx_vertex_attrib *)&mesh->vertex_uv;
        }
        if ((size_t)set_index >= mesh->uv_sets.count) {
            *out_status = BS_UFBX_ERR_OUT_OF_RANGE;
            return NULL;
        }
        return (const ufbx_vertex_attrib *)&mesh->uv_sets.data[set_index].vertex_uv;

    case BS_UFBX_MESH_ATTRIB_COLOR:
        if (set_index < 0) {
            return (const ufbx_vertex_attrib *)&mesh->vertex_color;
        }
        if ((size_t)set_index >= mesh->color_sets.count) {
            *out_status = BS_UFBX_ERR_OUT_OF_RANGE;
            return NULL;
        }
        return (const ufbx_vertex_attrib *)&mesh->color_sets.data[set_index].vertex_color;

    case BS_UFBX_MESH_ATTRIB_VERTEX_CREASE:
        return (const ufbx_vertex_attrib *)&mesh->vertex_crease;

    default:
        *out_status = BS_UFBX_ERR_INVALID_ARG;
        return NULL;
    }
}

/* =========================================================================
 * Scene lifecycle & metadata
 * ========================================================================= */

BS_UFBX_API int32_t bs_ufbx_get_abi_version(void)
{
    return BS_UFBX_ABI_VERSION;
}

BS_UFBX_API int32_t bs_ufbx_load_file(
    const char *path,
    const bs_ufbx_load_options *opts,
    bs_ufbx_scene **out_scene,
    char *error,
    int32_t error_size)
{
    if (out_scene == NULL) {
        return bs_write_error(error, error_size, "Output scene pointer is null.", BS_UFBX_ERR_INVALID_ARG);
    }
    *out_scene = NULL;
    if (path == NULL || path[0] == '\0') {
        return bs_write_error(error, error_size, "FBX path is empty.", BS_UFBX_ERR_INVALID_ARG);
    }

    ufbx_load_opts uopts;
    int32_t opt_status = bs_configure_load_opts(opts, &uopts);
    if (opt_status != BS_UFBX_OK) {
        return bs_write_error(error, error_size, "Invalid ufbx load options.", opt_status);
    }

    ufbx_error uerr;
    memset(&uerr, 0, sizeof(uerr));
    ufbx_scene *loaded = ufbx_load_file(path, &uopts, &uerr);
    if (loaded == NULL) {
        char formatted[1024];
        ufbx_format_error(formatted, sizeof(formatted), &uerr);
        return bs_write_error(error, error_size, formatted, BS_UFBX_ERR_PARSE_FAILED);
    }

    return bs_wrap_loaded_scene(loaded, out_scene, error, error_size);
}

BS_UFBX_API int32_t bs_ufbx_load_memory(
    const void *data,
    int64_t data_size,
    const bs_ufbx_load_options *opts,
    bs_ufbx_scene **out_scene,
    char *error,
    int32_t error_size)
{
    if (out_scene == NULL) {
        return bs_write_error(error, error_size, "Output scene pointer is null.", BS_UFBX_ERR_INVALID_ARG);
    }
    *out_scene = NULL;
    if (data == NULL || data_size <= 0) {
        return bs_write_error(error, error_size, "FBX memory buffer is empty.", BS_UFBX_ERR_INVALID_ARG);
    }

    ufbx_load_opts uopts;
    int32_t opt_status = bs_configure_load_opts(opts, &uopts);
    if (opt_status != BS_UFBX_OK) {
        return bs_write_error(error, error_size, "Invalid ufbx load options.", opt_status);
    }

    ufbx_error uerr;
    memset(&uerr, 0, sizeof(uerr));
    ufbx_scene *loaded = ufbx_load_memory(data, (size_t)data_size, &uopts, &uerr);
    if (loaded == NULL) {
        char formatted[1024];
        ufbx_format_error(formatted, sizeof(formatted), &uerr);
        return bs_write_error(error, error_size, formatted, BS_UFBX_ERR_PARSE_FAILED);
    }

    return bs_wrap_loaded_scene(loaded, out_scene, error, error_size);
}

BS_UFBX_API void bs_ufbx_free(bs_ufbx_scene *scene)
{
    if (scene == NULL) return;
    if (scene->node_paths != NULL && scene->scene != NULL) {
        for (size_t i = 0; i < scene->scene->nodes.count; i++) {
            free(scene->node_paths[i]);
        }
        free(scene->node_paths);
    }
    if (scene->scene != NULL) {
        ufbx_free_scene(scene->scene);
    }
    free(scene);
}

BS_UFBX_API int32_t bs_ufbx_get_scene_info(bs_ufbx_scene *scene, bs_ufbx_scene_info *out_info)
{
    if (scene == NULL || scene->scene == NULL || out_info == NULL) {
        return BS_UFBX_ERR_INVALID_ARG;
    }

    int32_t node_count = 0, mesh_count = 0, material_count = 0;
    if (bs_checked_i32(scene->scene->nodes.count, &node_count) != BS_UFBX_OK ||
        bs_checked_i32(scene->scene->meshes.count, &mesh_count) != BS_UFBX_OK ||
        bs_checked_i32(scene->scene->materials.count, &material_count) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    memset(out_info, 0, sizeof(*out_info));
    out_info->file_version = scene->scene->metadata.version;
    out_info->original_up_axis = (int32_t)scene->scene->settings.original_axis_up;
    out_info->unit_meters = (double)scene->scene->settings.unit_meters;
    out_info->original_unit_meters = (double)scene->scene->settings.original_unit_meters;
    out_info->frames_per_second = scene->scene->settings.frames_per_second;
    out_info->axes = bs_axes(scene->scene->settings.axes);
    out_info->node_count = node_count;
    out_info->mesh_count = mesh_count;
    out_info->material_count = material_count;
    return BS_UFBX_OK;
}

/* =========================================================================
 * Nodes & materials
 * ========================================================================= */

BS_UFBX_API int32_t bs_ufbx_get_node_info(bs_ufbx_scene *scene, int32_t node_index, bs_ufbx_node_info *out_info)
{
    if (scene == NULL || scene->scene == NULL || out_info == NULL) {
        return BS_UFBX_ERR_INVALID_ARG;
    }
    ufbx_node *node = bs_get_node(scene, node_index);
    if (node == NULL) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    const char *path = (scene->node_paths != NULL && scene->node_paths[node_index] != NULL)
        ? scene->node_paths[node_index]
        : "";
    size_t attrib_name_len_sz = node->attrib != NULL ? node->attrib->name.length : 0;
    int32_t name_len = 0, path_len = 0, mat_count = 0, attrib_name_len = 0;
    if (bs_checked_i32(node->name.length, &name_len) != BS_UFBX_OK ||
        bs_checked_i32(strlen(path), &path_len) != BS_UFBX_OK ||
        bs_checked_i32(node->materials.count, &mat_count) != BS_UFBX_OK ||
        bs_checked_i32(attrib_name_len_sz, &attrib_name_len) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    ufbx_vec3 one;
    one.x = 1.0;
    one.y = 1.0;
    one.z = 1.0;
    bs_ufbx_node_info local;
    memset(&local, 0, sizeof(local));
    local.id = node->element_id;
    local.attrib_id = node->attrib != NULL ? node->attrib->element_id : 0u;
    local.parent_index = bs_node_index_o1(scene->scene, node->parent);
    local.is_root = node->is_root ? 1 : 0;
    local.is_bone = node->bone != NULL ? 1 : 0;
    local.attrib_kind = bs_node_attrib_kind(node);
    local.mesh_index = bs_mesh_index_o1(scene->scene, node->mesh);
    local.material_count = mat_count;
    local.rotation_order = (int32_t)node->rotation_order;
    local.original_inherit_mode = (int32_t)node->original_inherit_mode;
    local.inherit_mode = (int32_t)node->inherit_mode;
    local.rotation_active = ufbx_find_bool(&node->props, "RotationActive", true) ? 1 : 0;
    local.name_length = name_len;
    local.path_length = path_len;
    local.attrib_name_length = attrib_name_len;

    local.lcl_translation = bs_vec3(ufbx_find_vec3(&node->props, UFBX_Lcl_Translation, ufbx_zero_vec3));
    local.lcl_rotation = bs_vec3(ufbx_find_vec3(&node->props, UFBX_Lcl_Rotation, ufbx_zero_vec3));
    local.lcl_scale = bs_vec3(ufbx_find_vec3(&node->props, UFBX_Lcl_Scaling, one));
    local.pre_rotation = bs_vec3(ufbx_find_vec3(&node->props, UFBX_PreRotation, ufbx_zero_vec3));
    local.post_rotation = bs_vec3(ufbx_find_vec3(&node->props, UFBX_PostRotation, ufbx_zero_vec3));
    local.rotation_pivot = bs_vec3(ufbx_find_vec3(&node->props, UFBX_RotationPivot, ufbx_zero_vec3));
    local.scaling_pivot = bs_vec3(ufbx_find_vec3(&node->props, UFBX_ScalingPivot, ufbx_zero_vec3));
    local.rotation_offset = bs_vec3(ufbx_find_vec3(&node->props, UFBX_RotationOffset, ufbx_zero_vec3));
    local.scaling_offset = bs_vec3(ufbx_find_vec3(&node->props, UFBX_ScalingOffset, ufbx_zero_vec3));

    local.local_translation = bs_vec3(node->local_transform.translation);
    local.local_rotation = bs_quat(node->local_transform.rotation);
    local.local_scale = bs_vec3(node->local_transform.scale);
    local.geometry_translation = bs_vec3(node->geometry_transform.translation);
    local.geometry_rotation = bs_quat(node->geometry_transform.rotation);
    local.geometry_scale = bs_vec3(node->geometry_transform.scale);

    bs_matrix_from_ufbx(&local.node_to_parent, &node->node_to_parent);
    bs_matrix_from_ufbx(&local.node_to_world, &node->node_to_world);
    bs_matrix_from_ufbx(&local.geometry_to_node, &node->geometry_to_node);
    bs_matrix_from_ufbx(&local.geometry_to_world, &node->geometry_to_world);
    *out_info = local;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_node_name(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size)
{
    ufbx_node *node = bs_get_node(scene, node_index);
    if (node == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_string(node->name.data, node->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_copy_node_path(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size)
{
    if (scene == NULL || scene->scene == NULL || node_index < 0 || (size_t)node_index >= scene->scene->nodes.count) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }
    const char *path = (scene->node_paths != NULL && scene->node_paths[node_index] != NULL)
        ? scene->node_paths[node_index]
        : "";
    return bs_copy_string(path, strlen(path), dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_copy_node_attrib_name(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size)
{
    ufbx_node *node = bs_get_node(scene, node_index);
    if (node == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (node->attrib == NULL) return BS_UFBX_ERR_UNAVAILABLE;
    return bs_copy_string(node->attrib->name.data, node->attrib->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_copy_node_material_indices(bs_ufbx_scene *scene, int32_t node_index, int32_t *dst, int32_t dst_count)
{
    ufbx_node *node = bs_get_node(scene, node_index);
    if (node == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(node->materials.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        dst[i] = bs_material_index_o1(scene->scene, node->materials.data[i]);
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_get_material_info(bs_ufbx_scene *scene, int32_t material_index, bs_ufbx_material_info *out_info)
{
    if (scene == NULL || scene->scene == NULL || out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_material *material = bs_get_material(scene, material_index);
    if (material == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t name_len = 0;
    if (bs_checked_i32(material->name.length, &name_len) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;

    memset(out_info, 0, sizeof(*out_info));
    out_info->id = material->element_id;
    out_info->name_length = name_len;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_material_name(bs_ufbx_scene *scene, int32_t material_index, char *dst, int32_t dst_size)
{
    ufbx_material *material = bs_get_material(scene, material_index);
    if (material == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_string(material->name.data, material->name.length, dst, dst_size);
}

/* =========================================================================
 * Mesh metadata, topology, faces, edges & material parts
 * ========================================================================= */

BS_UFBX_API int32_t bs_ufbx_get_mesh_info(bs_ufbx_scene *scene, int32_t mesh_index, bs_ufbx_mesh_info *out_info)
{
    if (scene == NULL || scene->scene == NULL || out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    bs_ufbx_mesh_info local;
    memset(&local, 0, sizeof(local));
    local.id = mesh->element_id;
    if (bs_checked_i32(mesh->name.length, &local.name_length) != BS_UFBX_OK ||
        bs_checked_i32(mesh->instances.count, &local.instance_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->num_vertices, &local.control_point_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->faces.count, &local.face_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->vertex_indices.count, &local.face_corner_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->num_triangles, &local.triangle_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->max_face_triangles, &local.max_face_triangles) != BS_UFBX_OK ||
        bs_checked_i32(mesh->edges.count, &local.edge_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->uv_sets.count, &local.uv_set_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->color_sets.count, &local.color_set_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->materials.count, &local.material_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->material_parts.count, &local.material_part_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->material_part_usage_order.count, &local.material_part_usage_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->face_groups.count, &local.face_group_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->skin_deformers.count, &local.skin_count) != BS_UFBX_OK ||
        bs_checked_i32(mesh->blend_deformers.count, &local.blend_deformer_count) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    local.has_face_smoothing = mesh->face_smoothing.count > 0 ? 1 : 0;
    local.has_face_smoothing_group = mesh->face_smoothing_group.count > 0 ? 1 : 0;
    local.has_face_material = mesh->face_material.count > 0 ? 1 : 0;
    local.has_face_group = mesh->face_group.count > 0 ? 1 : 0;
    local.has_face_hole = mesh->face_hole.count > 0 ? 1 : 0;
    local.has_edge_smoothing = mesh->edge_smoothing.count > 0 ? 1 : 0;
    local.has_edge_crease = mesh->edge_crease.count > 0 ? 1 : 0;
    local.has_edge_visibility = mesh->edge_visibility.count > 0 ? 1 : 0;
    *out_info = local;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_mesh_name(bs_ufbx_scene *scene, int32_t mesh_index, char *dst, int32_t dst_size)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_string(mesh->name.data, mesh->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_copy_mesh_instance_node_indices(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->instances.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        dst[i] = bs_node_index_o1(scene->scene, mesh->instances.data[i]);
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_mesh_material_indices(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->materials.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        dst[i] = bs_material_index_o1(scene->scene, mesh->materials.data[i]);
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_control_points(bs_ufbx_scene *scene, int32_t mesh_index, bs_ufbx_vec3 *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->vertices.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        dst[i] = bs_vec3(mesh->vertices.data[i]);
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_face_index_begins(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->faces.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t begin = mesh->faces.data[i].index_begin;
        dst[i] = begin <= (uint32_t)INT32_MAX ? (int32_t)begin : -1;
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_face_sizes(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->faces.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t size = mesh->faces.data[i].num_indices;
        dst[i] = size <= (uint32_t)INT32_MAX ? (int32_t)size : INT32_MAX;
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_face_corner_control_points(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->vertex_indices.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t cp = mesh->vertex_indices.data[i];
        dst[i] = (cp == UFBX_NO_INDEX || cp > (uint32_t)INT32_MAX) ? -1 : (int32_t)cp;
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_vertex_first_corners(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->vertex_first_index.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t corner = mesh->vertex_first_index.data[i];
        dst[i] = (corner == UFBX_NO_INDEX || corner > (uint32_t)INT32_MAX) ? -1 : (int32_t)corner;
    }
    return count;
}

static int32_t bs_copy_bool_list(const ufbx_bool_list *list, uint8_t *dst, int32_t dst_count)
{
    int32_t count = 0;
    if (bs_checked_i32(list->count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (count == 0) return BS_UFBX_ERR_UNAVAILABLE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        dst[i] = list->data[i] ? 1u : 0u;
    }
    return count;
}

static int32_t bs_copy_u32_as_i32_list(const ufbx_uint32_list *list, int32_t *dst, int32_t dst_count)
{
    int32_t count = 0;
    if (bs_checked_i32(list->count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (count == 0) return BS_UFBX_ERR_UNAVAILABLE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t v = list->data[i];
        dst[i] = (v == UFBX_NO_INDEX || v > (uint32_t)INT32_MAX) ? -1 : (int32_t)v;
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_face_smoothing(bs_ufbx_scene *scene, int32_t mesh_index, uint8_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_bool_list(&mesh->face_smoothing, dst, dst_count);
}

BS_UFBX_API int32_t bs_ufbx_copy_face_smoothing_groups(bs_ufbx_scene *scene, int32_t mesh_index, uint32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->face_smoothing_group.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (count == 0) return BS_UFBX_ERR_UNAVAILABLE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    memcpy(dst, mesh->face_smoothing_group.data, (size_t)count * sizeof(uint32_t));
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_face_materials(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_u32_as_i32_list(&mesh->face_material, dst, dst_count);
}

BS_UFBX_API int32_t bs_ufbx_copy_face_groups(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_u32_as_i32_list(&mesh->face_group, dst, dst_count);
}

BS_UFBX_API int32_t bs_ufbx_copy_face_holes(bs_ufbx_scene *scene, int32_t mesh_index, uint8_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_bool_list(&mesh->face_hole, dst, dst_count);
}

BS_UFBX_API int32_t bs_ufbx_copy_edges(bs_ufbx_scene *scene, int32_t mesh_index, bs_ufbx_edge *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->edges.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t a = mesh->edges.data[i].a;
        uint32_t b = mesh->edges.data[i].b;
        dst[i].corner_a = (a == UFBX_NO_INDEX || a > (uint32_t)INT32_MAX) ? -1 : (int32_t)a;
        dst[i].corner_b = (b == UFBX_NO_INDEX || b > (uint32_t)INT32_MAX) ? -1 : (int32_t)b;
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_edge_smoothing(bs_ufbx_scene *scene, int32_t mesh_index, uint8_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_bool_list(&mesh->edge_smoothing, dst, dst_count);
}

BS_UFBX_API int32_t bs_ufbx_copy_edge_creases(bs_ufbx_scene *scene, int32_t mesh_index, double *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->edge_crease.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (count == 0) return BS_UFBX_ERR_UNAVAILABLE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        dst[i] = (double)mesh->edge_crease.data[i];
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_edge_visibility(bs_ufbx_scene *scene, int32_t mesh_index, uint8_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_bool_list(&mesh->edge_visibility, dst, dst_count);
}

BS_UFBX_API int32_t bs_ufbx_get_material_part_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t part_index, bs_ufbx_mesh_part_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || part_index < 0 || (size_t)part_index >= mesh->material_parts.count) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    const ufbx_mesh_part *part = &mesh->material_parts.data[part_index];
    int32_t p_idx = 0, face_count = 0, tri_count = 0;
    if (bs_checked_i32(part->index, &p_idx) != BS_UFBX_OK ||
        bs_checked_i32(part->face_indices.count, &face_count) != BS_UFBX_OK ||
        bs_checked_i32(part->num_triangles, &tri_count) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    out_info->part_index = p_idx;
    out_info->face_count = face_count;
    out_info->triangle_count = tri_count;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_material_part_faces(bs_ufbx_scene *scene, int32_t mesh_index, int32_t part_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || part_index < 0 || (size_t)part_index >= mesh->material_parts.count) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    const ufbx_mesh_part *part = &mesh->material_parts.data[part_index];
    int32_t count = 0;
    if (bs_checked_i32(part->face_indices.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t fi = part->face_indices.data[i];
        dst[i] = (fi == UFBX_NO_INDEX || fi > (uint32_t)INT32_MAX) ? -1 : (int32_t)fi;
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_material_part_usage_order(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(mesh->material_part_usage_order.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t idx = mesh->material_part_usage_order.data[i];
        dst[i] = (idx == UFBX_NO_INDEX || idx > (uint32_t)INT32_MAX) ? -1 : (int32_t)idx;
    }
    return count;
}

/* =========================================================================
 * Named UV / Color sets & unified indexed vertex attributes
 * ========================================================================= */

BS_UFBX_API int32_t bs_ufbx_get_uv_set_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t set_index, bs_ufbx_layer_set_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || set_index < 0 || (size_t)set_index >= mesh->uv_sets.count) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    const ufbx_uv_set *set = &mesh->uv_sets.data[set_index];
    int32_t idx = 0, name_len = 0;
    if (bs_checked_i32(set->index, &idx) != BS_UFBX_OK ||
        bs_checked_i32(set->name.length, &name_len) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }
    out_info->set_index = idx;
    out_info->name_length = name_len;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_uv_set_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t set_index, char *dst, int32_t dst_size)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || set_index < 0 || (size_t)set_index >= mesh->uv_sets.count) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }
    const ufbx_uv_set *set = &mesh->uv_sets.data[set_index];
    return bs_copy_string(set->name.data, set->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_get_color_set_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t set_index, bs_ufbx_layer_set_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || set_index < 0 || (size_t)set_index >= mesh->color_sets.count) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    const ufbx_color_set *set = &mesh->color_sets.data[set_index];
    int32_t idx = 0, name_len = 0;
    if (bs_checked_i32(set->index, &idx) != BS_UFBX_OK ||
        bs_checked_i32(set->name.length, &name_len) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }
    out_info->set_index = idx;
    out_info->name_length = name_len;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_color_set_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t set_index, char *dst, int32_t dst_size)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || set_index < 0 || (size_t)set_index >= mesh->color_sets.count) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }
    const ufbx_color_set *set = &mesh->color_sets.data[set_index];
    return bs_copy_string(set->name.data, set->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_get_attrib_info(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t attrib_kind,
    int32_t set_index,
    bs_ufbx_attrib_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    int32_t status = BS_UFBX_OK;
    const ufbx_vertex_attrib *attrib = bs_resolve_attrib(mesh, attrib_kind, set_index, &status);
    if (attrib == NULL) return status;

    bs_ufbx_attrib_info local;
    memset(&local, 0, sizeof(local));
    if (!attrib->exists) {
        *out_info = local;
        return BS_UFBX_OK;
    }

    int32_t val_count = 0, corner_count = 0, w_count = 0;
    if (bs_checked_i32(attrib->values.count, &val_count) != BS_UFBX_OK ||
        bs_checked_i32(attrib->indices.count, &corner_count) != BS_UFBX_OK ||
        bs_checked_i32(attrib->values_w.count, &w_count) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    local.exists = 1;
    local.unique_per_vertex = attrib->unique_per_vertex ? 1 : 0;
    local.value_count = val_count;
    local.corner_count = corner_count;
    local.values_w_count = w_count;
    *out_info = local;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_attrib_indices(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t attrib_kind,
    int32_t set_index,
    int32_t *dst,
    int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    int32_t status = BS_UFBX_OK;
    const ufbx_vertex_attrib *attrib = bs_resolve_attrib(mesh, attrib_kind, set_index, &status);
    if (attrib == NULL) return status;
    if (!attrib->exists) return BS_UFBX_ERR_UNAVAILABLE;

    int32_t count = 0;
    if (bs_checked_i32(attrib->indices.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t vi = attrib->indices.data[i];
        dst[i] = (vi == UFBX_NO_INDEX || (size_t)vi >= attrib->values.count || vi > (uint32_t)INT32_MAX)
            ? -1
            : (int32_t)vi;
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_w(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t attrib_kind,
    int32_t set_index,
    double *dst,
    int32_t dst_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    int32_t status = BS_UFBX_OK;
    const ufbx_vertex_attrib *attrib = bs_resolve_attrib(mesh, attrib_kind, set_index, &status);
    if (attrib == NULL) return status;
    if (!attrib->exists || attrib->values_w.count == 0) return BS_UFBX_ERR_UNAVAILABLE;

    int32_t count = 0;
    if (bs_checked_i32(attrib->values_w.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        dst[i] = (double)attrib->values_w.data[i];
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_real(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t attrib_kind,
    int32_t set_index,
    double *dst,
    int32_t dst_count,
    int32_t expand_per_corner)
{
    if (attrib_kind != BS_UFBX_MESH_ATTRIB_VERTEX_CREASE) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    int32_t status = BS_UFBX_OK;
    const ufbx_vertex_real *attrib = (const ufbx_vertex_real *)bs_resolve_attrib(mesh, attrib_kind, set_index, &status);
    if (attrib == NULL) return status;
    if (!attrib->exists) return BS_UFBX_ERR_UNAVAILABLE;

    size_t raw_count = expand_per_corner ? attrib->indices.count : attrib->values.count;
    int32_t count = 0;
    if (bs_checked_i32(raw_count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    if (expand_per_corner) {
        for (int32_t i = 0; i < count; i++) {
            uint32_t vi = attrib->indices.data[i];
            dst[i] = (vi != UFBX_NO_INDEX && (size_t)vi < attrib->values.count) ? (double)attrib->values.data[vi] : 0.0;
        }
    } else {
        for (int32_t i = 0; i < count; i++) {
            dst[i] = (double)attrib->values.data[i];
        }
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_vec2(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t attrib_kind,
    int32_t set_index,
    bs_ufbx_vec2 *dst,
    int32_t dst_count,
    int32_t expand_per_corner)
{
    if (attrib_kind != BS_UFBX_MESH_ATTRIB_UV) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    int32_t status = BS_UFBX_OK;
    const ufbx_vertex_vec2 *attrib = (const ufbx_vertex_vec2 *)bs_resolve_attrib(mesh, attrib_kind, set_index, &status);
    if (attrib == NULL) return status;
    if (!attrib->exists) return BS_UFBX_ERR_UNAVAILABLE;

    size_t raw_count = expand_per_corner ? attrib->indices.count : attrib->values.count;
    int32_t count = 0;
    if (bs_checked_i32(raw_count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    bs_ufbx_vec2 zero = { 0.0, 0.0 };
    if (expand_per_corner) {
        for (int32_t i = 0; i < count; i++) {
            uint32_t vi = attrib->indices.data[i];
            dst[i] = (vi != UFBX_NO_INDEX && (size_t)vi < attrib->values.count) ? bs_vec2(attrib->values.data[vi]) : zero;
        }
    } else {
        for (int32_t i = 0; i < count; i++) {
            dst[i] = bs_vec2(attrib->values.data[i]);
        }
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_vec3(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t attrib_kind,
    int32_t set_index,
    bs_ufbx_vec3 *dst,
    int32_t dst_count,
    int32_t expand_per_corner)
{
    if (attrib_kind != BS_UFBX_MESH_ATTRIB_NORMAL &&
        attrib_kind != BS_UFBX_MESH_ATTRIB_TANGENT &&
        attrib_kind != BS_UFBX_MESH_ATTRIB_BITANGENT) {
        return BS_UFBX_ERR_INVALID_ARG;
    }
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    int32_t status = BS_UFBX_OK;
    const ufbx_vertex_vec3 *attrib = (const ufbx_vertex_vec3 *)bs_resolve_attrib(mesh, attrib_kind, set_index, &status);
    if (attrib == NULL) return status;
    if (!attrib->exists) return BS_UFBX_ERR_UNAVAILABLE;

    size_t raw_count = expand_per_corner ? attrib->indices.count : attrib->values.count;
    int32_t count = 0;
    if (bs_checked_i32(raw_count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    bs_ufbx_vec3 zero = { 0.0, 0.0, 0.0 };
    if (expand_per_corner) {
        for (int32_t i = 0; i < count; i++) {
            uint32_t vi = attrib->indices.data[i];
            dst[i] = (vi != UFBX_NO_INDEX && (size_t)vi < attrib->values.count) ? bs_vec3(attrib->values.data[vi]) : zero;
        }
    } else {
        for (int32_t i = 0; i < count; i++) {
            dst[i] = bs_vec3(attrib->values.data[i]);
        }
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_vec4(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t attrib_kind,
    int32_t set_index,
    bs_ufbx_vec4 *dst,
    int32_t dst_count,
    int32_t expand_per_corner)
{
    if (attrib_kind != BS_UFBX_MESH_ATTRIB_COLOR) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    int32_t status = BS_UFBX_OK;
    const ufbx_vertex_vec4 *attrib = (const ufbx_vertex_vec4 *)bs_resolve_attrib(mesh, attrib_kind, set_index, &status);
    if (attrib == NULL) return status;
    if (!attrib->exists) return BS_UFBX_ERR_UNAVAILABLE;

    size_t raw_count = expand_per_corner ? attrib->indices.count : attrib->values.count;
    int32_t count = 0;
    if (bs_checked_i32(raw_count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    bs_ufbx_vec4 zero = { 0.0, 0.0, 0.0, 0.0 };
    if (expand_per_corner) {
        for (int32_t i = 0; i < count; i++) {
            uint32_t vi = attrib->indices.data[i];
            dst[i] = (vi != UFBX_NO_INDEX && (size_t)vi < attrib->values.count) ? bs_vec4(attrib->values.data[vi]) : zero;
        }
    } else {
        for (int32_t i = 0; i < count; i++) {
            dst[i] = bs_vec4(attrib->values.data[i]);
        }
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_control_point_first_corner_attrib_vec3(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t attrib_kind,
    int32_t set_index,
    bs_ufbx_vec3 *dst,
    int32_t dst_count)
{
    if (attrib_kind != BS_UFBX_MESH_ATTRIB_NORMAL &&
        attrib_kind != BS_UFBX_MESH_ATTRIB_TANGENT &&
        attrib_kind != BS_UFBX_MESH_ATTRIB_BITANGENT) {
        return BS_UFBX_ERR_INVALID_ARG;
    }
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    int32_t status = BS_UFBX_OK;
    const ufbx_vertex_vec3 *attrib = (const ufbx_vertex_vec3 *)bs_resolve_attrib(mesh, attrib_kind, set_index, &status);
    if (attrib == NULL) return status;

    if (!attrib->exists) {
        return BS_UFBX_ERR_UNAVAILABLE;
    }

    int32_t count = 0;
    if (bs_checked_i32(mesh->num_vertices, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    bs_ufbx_vec3 zero = { 0.0, 0.0, 0.0 };
    for (int32_t i = 0; i < count; i++) {
        if ((size_t)i >= mesh->vertex_first_index.count) {
            dst[i] = zero;
            continue;
        }
        uint32_t first_corner = mesh->vertex_first_index.data[i];
        if (first_corner == UFBX_NO_INDEX || (size_t)first_corner >= attrib->indices.count) {
            dst[i] = zero;
            continue;
        }
        uint32_t vi = attrib->indices.data[first_corner];
        dst[i] = (vi != UFBX_NO_INDEX && (size_t)vi < attrib->values.count)
            ? bs_vec3(attrib->values.data[vi])
            : zero;
    }
    return count;
}

/* =========================================================================
 * Skin deformers & clusters
 * ========================================================================= */

BS_UFBX_API int32_t bs_ufbx_get_skin_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, bs_ufbx_skin_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_skin_deformer *skin = bs_get_skin(scene, mesh_index, skin_index);
    if (skin == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t cluster_count = 0, name_len = 0;
    if (bs_checked_i32(skin->clusters.count, &cluster_count) != BS_UFBX_OK ||
        bs_checked_i32(skin->name.length, &name_len) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    memset(out_info, 0, sizeof(*out_info));
    out_info->id = skin->element_id;
    out_info->cluster_count = cluster_count;
    out_info->name_length = name_len;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_skin_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, char *dst, int32_t dst_size)
{
    ufbx_skin_deformer *skin = bs_get_skin(scene, mesh_index, skin_index);
    if (skin == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_string(skin->name.data, skin->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_get_skin_cluster_info(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t skin_index,
    int32_t cluster_index,
    bs_ufbx_cluster_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_skin_cluster *cluster = bs_get_cluster(scene, mesh_index, skin_index, cluster_index);
    if (cluster == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t weight_count = 0, name_len = 0;
    if (bs_checked_i32(cluster->num_weights, &weight_count) != BS_UFBX_OK ||
        bs_checked_i32(cluster->name.length, &name_len) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    memset(out_info, 0, sizeof(*out_info));
    out_info->id = cluster->element_id;
    out_info->bone_node_index = bs_node_index_o1(scene->scene, cluster->bone_node);
    out_info->weight_count = weight_count;
    out_info->name_length = name_len;
    ufbx_matrix bone_to_world = cluster->bind_to_world;
    ufbx_matrix mesh_to_world = ufbx_matrix_mul(&bone_to_world, &cluster->mesh_node_to_bone);
    bs_matrix_from_ufbx(&out_info->mesh_bind_world, &mesh_to_world);
    bs_matrix_from_ufbx(&out_info->bone_bind_world, &cluster->bind_to_world);
    bs_matrix_from_ufbx(&out_info->mesh_node_to_bone, &cluster->mesh_node_to_bone);
    bs_matrix_from_ufbx(&out_info->geometry_to_bone, &cluster->geometry_to_bone);
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_cluster_name(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t skin_index,
    int32_t cluster_index,
    char *dst,
    int32_t dst_size)
{
    ufbx_skin_cluster *cluster = bs_get_cluster(scene, mesh_index, skin_index, cluster_index);
    if (cluster == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_string(cluster->name.data, cluster->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_copy_cluster_indices(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t skin_index,
    int32_t cluster_index,
    int32_t *dst,
    int32_t dst_count)
{
    ufbx_skin_cluster *cluster = bs_get_cluster(scene, mesh_index, skin_index, cluster_index);
    if (cluster == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(cluster->vertices.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        uint32_t cp = cluster->vertices.data[i];
        dst[i] = (cp == UFBX_NO_INDEX || cp > (uint32_t)INT32_MAX) ? -1 : (int32_t)cp;
    }
    return count;
}

BS_UFBX_API int32_t bs_ufbx_copy_cluster_weights(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t skin_index,
    int32_t cluster_index,
    double *dst,
    int32_t dst_count)
{
    ufbx_skin_cluster *cluster = bs_get_cluster(scene, mesh_index, skin_index, cluster_index);
    if (cluster == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t count = 0;
    if (bs_checked_i32(cluster->weights.count, &count) != BS_UFBX_OK) return BS_UFBX_ERR_OUT_OF_RANGE;
    if (dst_count < 0 || dst_count < count) return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    if (count == 0) return 0;
    if (dst == NULL) return BS_UFBX_ERR_INVALID_ARG;

    for (int32_t i = 0; i < count; i++) {
        dst[i] = (double)cluster->weights.data[i];
    }
    return count;
}

/* =========================================================================
 * Blend deformers, channels & keyframes
 * ========================================================================= */

BS_UFBX_API int32_t bs_ufbx_get_blend_deformer_info(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t deformer_index,
    bs_ufbx_blend_deformer_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_blend_deformer *deformer = bs_get_blend_deformer(scene, mesh_index, deformer_index);
    if (deformer == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t channel_count = 0, name_len = 0;
    if (bs_checked_i32(deformer->channels.count, &channel_count) != BS_UFBX_OK ||
        bs_checked_i32(deformer->name.length, &name_len) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    memset(out_info, 0, sizeof(*out_info));
    out_info->id = deformer->element_id;
    out_info->channel_count = channel_count;
    out_info->name_length = name_len;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_blend_deformer_name(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t deformer_index,
    char *dst,
    int32_t dst_size)
{
    ufbx_blend_deformer *deformer = bs_get_blend_deformer(scene, mesh_index, deformer_index);
    if (deformer == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_string(deformer->name.data, deformer->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_get_blend_channel_info(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t deformer_index,
    int32_t channel_index,
    bs_ufbx_blend_channel_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    ufbx_blend_channel *channel = bs_get_blend_channel(scene, mesh_index, deformer_index, channel_index);
    if (channel == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    int32_t kf_count = 0, name_len = 0;
    if (bs_checked_i32(channel->keyframes.count, &kf_count) != BS_UFBX_OK ||
        bs_checked_i32(channel->name.length, &name_len) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    memset(out_info, 0, sizeof(*out_info));
    out_info->id = channel->element_id;
    out_info->keyframe_count = kf_count;
    out_info->name_length = name_len;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_blend_channel_name(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t deformer_index,
    int32_t channel_index,
    char *dst,
    int32_t dst_size)
{
    ufbx_blend_channel *channel = bs_get_blend_channel(scene, mesh_index, deformer_index, channel_index);
    if (channel == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_string(channel->name.data, channel->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_get_blend_keyframe_info(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t deformer_index,
    int32_t channel_index,
    int32_t keyframe_index,
    bs_ufbx_blend_keyframe_info *out_info)
{
    if (out_info == NULL) return BS_UFBX_ERR_INVALID_ARG;
    const ufbx_blend_keyframe *kf = bs_get_blend_keyframe(scene, mesh_index, deformer_index, channel_index, keyframe_index);
    if (kf == NULL || kf->shape == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    ufbx_blend_shape *shape = kf->shape;
    int32_t offset_count = 0, name_len = 0;
    if (bs_checked_i32(shape->num_offsets, &offset_count) != BS_UFBX_OK ||
        bs_checked_i32(shape->name.length, &name_len) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }

    memset(out_info, 0, sizeof(*out_info));
    out_info->shape_id = shape->element_id;
    out_info->target_weight = (double)kf->target_weight;
    out_info->effective_weight = (double)kf->effective_weight;
    out_info->offset_count = offset_count;
    out_info->has_normal_offsets = shape->normal_offsets.count > 0 ? 1 : 0;
    out_info->has_offset_weights = shape->offset_weights.count > 0 ? 1 : 0;
    out_info->shape_name_length = name_len;
    return BS_UFBX_OK;
}

BS_UFBX_API int32_t bs_ufbx_copy_blend_shape_name(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t deformer_index,
    int32_t channel_index,
    int32_t keyframe_index,
    char *dst,
    int32_t dst_size)
{
    const ufbx_blend_keyframe *kf = bs_get_blend_keyframe(scene, mesh_index, deformer_index, channel_index, keyframe_index);
    if (kf == NULL || kf->shape == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;
    return bs_copy_string(kf->shape->name.data, kf->shape->name.length, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_copy_blend_shape_offsets(
    bs_ufbx_scene *scene,
    int32_t mesh_index,
    int32_t deformer_index,
    int32_t channel_index,
    int32_t keyframe_index,
    uint32_t output_mask,
    int32_t *dst_control_point_indices,
    bs_ufbx_vec3 *dst_position_offsets,
    bs_ufbx_vec3 *dst_normal_offsets,
    double *dst_offset_weights,
    int32_t dst_count)
{
    const ufbx_blend_keyframe *kf = bs_get_blend_keyframe(scene, mesh_index, deformer_index, channel_index, keyframe_index);
    if (kf == NULL || kf->shape == NULL) return BS_UFBX_ERR_OUT_OF_RANGE;

    const uint32_t known_outputs =
        BS_UFBX_BLEND_OUT_INDICES |
        BS_UFBX_BLEND_OUT_POSITIONS |
        BS_UFBX_BLEND_OUT_NORMALS |
        BS_UFBX_BLEND_OUT_WEIGHTS;
    if (output_mask == 0u || (output_mask & ~known_outputs) != 0u) {
        return BS_UFBX_ERR_INVALID_ARG;
    }

    ufbx_blend_shape *shape = kf->shape;
    int32_t count = 0;
    if (bs_checked_i32(shape->num_offsets, &count) != BS_UFBX_OK) {
        return BS_UFBX_ERR_OUT_OF_RANGE;
    }
    if (dst_count < 0 || dst_count < count) {
        return BS_UFBX_ERR_BUFFER_TOO_SMALL;
    }
    if (count == 0) {
        return 0;
    }

    /* Validate every requested output buffer BEFORE writing a single element. */
    if ((output_mask & BS_UFBX_BLEND_OUT_INDICES) != 0u && dst_control_point_indices == NULL) {
        return BS_UFBX_ERR_INVALID_ARG;
    }
    if ((output_mask & BS_UFBX_BLEND_OUT_POSITIONS) != 0u && dst_position_offsets == NULL) {
        return BS_UFBX_ERR_INVALID_ARG;
    }
    if ((output_mask & BS_UFBX_BLEND_OUT_NORMALS) != 0u && dst_normal_offsets == NULL) {
        return BS_UFBX_ERR_INVALID_ARG;
    }
    if ((output_mask & BS_UFBX_BLEND_OUT_WEIGHTS) != 0u && dst_offset_weights == NULL) {
        return BS_UFBX_ERR_INVALID_ARG;
    }

    bs_ufbx_vec3 zero = { 0.0, 0.0, 0.0 };
    if ((output_mask & BS_UFBX_BLEND_OUT_INDICES) != 0u) {
        for (int32_t i = 0; i < count; i++) {
            uint32_t cp = (size_t)i < shape->offset_vertices.count ? shape->offset_vertices.data[i] : UFBX_NO_INDEX;
            dst_control_point_indices[i] = (cp == UFBX_NO_INDEX || cp > (uint32_t)INT32_MAX) ? -1 : (int32_t)cp;
        }
    }
    if ((output_mask & BS_UFBX_BLEND_OUT_POSITIONS) != 0u) {
        for (int32_t i = 0; i < count; i++) {
            dst_position_offsets[i] = (size_t)i < shape->position_offsets.count
                ? bs_vec3(shape->position_offsets.data[i])
                : zero;
        }
    }
    if ((output_mask & BS_UFBX_BLEND_OUT_NORMALS) != 0u) {
        for (int32_t i = 0; i < count; i++) {
            dst_normal_offsets[i] = (size_t)i < shape->normal_offsets.count
                ? bs_vec3(shape->normal_offsets.data[i])
                : zero;
        }
    }
    if ((output_mask & BS_UFBX_BLEND_OUT_WEIGHTS) != 0u) {
        for (int32_t i = 0; i < count; i++) {
            dst_offset_weights[i] = (size_t)i < shape->offset_weights.count
                ? (double)shape->offset_weights.data[i]
                : 1.0;
        }
    }

    return count;
}
