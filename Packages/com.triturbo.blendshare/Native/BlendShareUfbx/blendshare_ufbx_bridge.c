#include "blendshare_ufbx_bridge.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "ufbx.h"

struct bs_ufbx_scene {
    ufbx_scene *scene;
    char **node_paths;
};

static int32_t bs_min_i32_size(size_t value)
{
    return value > (size_t)INT32_MAX ? INT32_MAX : (int32_t)value;
}

static int32_t bs_write_error(char *error, int32_t error_size, const char *message)
{
    if (error != NULL && error_size > 0) {
        const char *text = message != NULL ? message : "Unknown ufbx error";
        snprintf(error, (size_t)error_size, "%s", text);
    }
    return 0;
}

static int32_t bs_copy_string(const char *src, size_t length, char *dst, int32_t dst_size)
{
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
    return bs_min_i32_size(length);
}

static char *bs_dup_string(const char *src, size_t length)
{
    char *result = (char*)malloc(length + 1);
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

static ufbx_blend_shape *bs_get_blend_shape(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t frame_index, ufbx_real *weight)
{
    ufbx_blend_channel *channel = bs_get_blend_channel(scene, mesh_index, deformer_index, channel_index);
    if (channel == NULL || frame_index < 0 || (size_t)frame_index >= channel->keyframes.count) {
        return NULL;
    }
    ufbx_blend_keyframe keyframe = channel->keyframes.data[frame_index];
    if (weight != NULL) {
        *weight = keyframe.target_weight;
    }
    return keyframe.shape;
}

static int32_t bs_node_index(bs_ufbx_scene *scene, const ufbx_node *node)
{
    if (scene == NULL || scene->scene == NULL || node == NULL) {
        return -1;
    }
    for (size_t i = 0; i < scene->scene->nodes.count; i++) {
        if (scene->scene->nodes.data[i] == node) {
            return bs_min_i32_size(i);
        }
    }
    return -1;
}

static int32_t bs_node_type(const ufbx_node *node)
{
    if (node == NULL) return 0;
    if (node->is_root) return 4;
    if (node->mesh != NULL) return 1;
    if (node->bone != NULL) return 3;
    return 2;
}

static void bs_matrix_identity(bs_ufbx_matrix *dst)
{
    for (int i = 0; i < 16; i++) dst->m[i] = 0.0;
    dst->m[0] = 1.0;
    dst->m[5] = 1.0;
    dst->m[10] = 1.0;
    dst->m[15] = 1.0;
}

static void bs_matrix_from_ufbx(bs_ufbx_matrix *dst, const ufbx_matrix *src)
{
    if (dst == NULL) return;
    if (src == NULL) {
        bs_matrix_identity(dst);
        return;
    }
    dst->m[0] = src->m00; dst->m[1] = src->m10; dst->m[2] = src->m20; dst->m[3] = 0.0;
    dst->m[4] = src->m01; dst->m[5] = src->m11; dst->m[6] = src->m21; dst->m[7] = 0.0;
    dst->m[8] = src->m02; dst->m[9] = src->m12; dst->m[10] = src->m22; dst->m[11] = 0.0;
    dst->m[12] = src->m03; dst->m[13] = src->m13; dst->m[14] = src->m23; dst->m[15] = 1.0;
}

static void bs_transform_to_trs(const ufbx_transform *transform, double *translation, double *rotation, double *scale)
{
    if (translation != NULL) {
        translation[0] = transform != NULL ? transform->translation.x : 0.0;
        translation[1] = transform != NULL ? transform->translation.y : 0.0;
        translation[2] = transform != NULL ? transform->translation.z : 0.0;
    }
    if (rotation != NULL) {
        rotation[0] = 0.0;
        rotation[1] = 0.0;
        rotation[2] = 0.0;
    }
    if (scale != NULL) {
        scale[0] = transform != NULL ? transform->scale.x : 1.0;
        scale[1] = transform != NULL ? transform->scale.y : 1.0;
        scale[2] = transform != NULL ? transform->scale.z : 1.0;
    }
}

static ufbx_vec3 bs_vertex_attrib_value(const ufbx_vertex_vec3 *attrib, const ufbx_mesh *mesh, size_t vertex_index)
{
    if (attrib == NULL || mesh == NULL || !attrib->exists || vertex_index >= mesh->vertex_first_index.count) {
        ufbx_vec3 zero = { 0.0, 0.0, 0.0 };
        return zero;
    }
    uint32_t first_index = mesh->vertex_first_index.data[vertex_index];
    if (first_index == UFBX_NO_INDEX || (size_t)first_index >= attrib->indices.count) {
        ufbx_vec3 zero = { 0.0, 0.0, 0.0 };
        return zero;
    }
    uint32_t value_index = attrib->indices.data[first_index];
    if ((size_t)value_index >= attrib->values.count) {
        ufbx_vec3 zero = { 0.0, 0.0, 0.0 };
        return zero;
    }
    return attrib->values.data[value_index];
}

static int32_t bs_copy_vec3_list(const ufbx_vec3 *values, size_t count, double *dst_xyz, int32_t dst_count)
{
    if (dst_xyz == NULL || dst_count < 0 || (size_t)dst_count < count) {
        return 0;
    }
    for (size_t i = 0; i < count; i++) {
        dst_xyz[i * 3 + 0] = values[i].x;
        dst_xyz[i * 3 + 1] = values[i].y;
        dst_xyz[i * 3 + 2] = values[i].z;
    }
    return 1;
}

static int32_t bs_copy_vertex_attrib(const ufbx_vertex_vec3 *attrib, const ufbx_mesh *mesh, double *dst_xyz, int32_t dst_count)
{
    if (mesh == NULL || dst_xyz == NULL || dst_count < 0 || (size_t)dst_count < mesh->num_vertices) {
        return 0;
    }
    for (size_t i = 0; i < mesh->num_vertices; i++) {
        ufbx_vec3 value = bs_vertex_attrib_value(attrib, mesh, i);
        dst_xyz[i * 3 + 0] = value.x;
        dst_xyz[i * 3 + 1] = value.y;
        dst_xyz[i * 3 + 2] = value.z;
    }
    return 1;
}

static void bs_build_node_paths(bs_ufbx_scene *handle)
{
    if (handle == NULL || handle->scene == NULL) return;
    size_t count = handle->scene->nodes.count;
    handle->node_paths = (char**)calloc(count, sizeof(char*));
    if (handle->node_paths == NULL) return;

    for (size_t i = 0; i < count; i++) {
        ufbx_node *node = handle->scene->nodes.data[i];
        if (node == NULL || node->is_root) {
            handle->node_paths[i] = bs_dup_string("", 0);
            continue;
        }

        size_t length = 0;
        size_t depth = 0;
        for (ufbx_node *current = node; current != NULL && !current->is_root; current = current->parent) {
            length += current->name.length;
            depth++;
        }
        if (depth > 1) length += depth - 1;

        char *path = (char*)malloc(length + 1);
        if (path == NULL) continue;
        path[length] = '\0';
        size_t offset = length;
        for (ufbx_node *current = node; current != NULL && !current->is_root; current = current->parent) {
            if (offset < length) {
                path[--offset] = '/';
            }
            offset -= current->name.length;
            memcpy(path + offset, current->name.data, current->name.length);
        }
        handle->node_paths[i] = path;
    }
}

BS_UFBX_API int32_t bs_ufbx_load(const char *path, bs_ufbx_scene **out_scene, char *error, int32_t error_size)
{
    if (out_scene == NULL) {
        return bs_write_error(error, error_size, "Output scene pointer is null.");
    }
    *out_scene = NULL;
    if (path == NULL || path[0] == '\0') {
        return bs_write_error(error, error_size, "FBX path is empty.");
    }

    ufbx_load_opts opts;
    memset(&opts, 0, sizeof(opts));
    opts.evaluate_skinning = false;
    opts.skip_skin_vertices = false;

    ufbx_error uerr;
    memset(&uerr, 0, sizeof(uerr));
    ufbx_scene *loaded = ufbx_load_file(path, &opts, &uerr);
    if (loaded == NULL) {
        char formatted[1024];
        ufbx_format_error(formatted, sizeof(formatted), &uerr);
        return bs_write_error(error, error_size, formatted);
    }

    bs_ufbx_scene *handle = (bs_ufbx_scene*)calloc(1, sizeof(bs_ufbx_scene));
    if (handle == NULL) {
        ufbx_free_scene(loaded);
        return bs_write_error(error, error_size, "Failed to allocate ufbx scene handle.");
    }
    handle->scene = loaded;
    bs_build_node_paths(handle);
    *out_scene = handle;
    if (error != NULL && error_size > 0) error[0] = '\0';
    return 1;
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

BS_UFBX_API int32_t bs_ufbx_get_node_count(bs_ufbx_scene *scene)
{
    return scene != NULL && scene->scene != NULL ? bs_min_i32_size(scene->scene->nodes.count) : 0;
}

BS_UFBX_API int32_t bs_ufbx_get_node_info(bs_ufbx_scene *scene, int32_t node_index, bs_ufbx_node_info *out_info)
{
    ufbx_node *node = bs_get_node(scene, node_index);
    if (node == NULL || out_info == NULL) return 0;
    memset(out_info, 0, sizeof(*out_info));
    out_info->id = node->element_id;
    out_info->parent_index = bs_node_index(scene, node->parent);
    out_info->type = bs_node_type(node);
    bs_transform_to_trs(&node->local_transform, out_info->local_translation, out_info->local_rotation, out_info->local_scale);
    out_info->name_length = bs_min_i32_size(node->name.length);
    const char *path = scene->node_paths != NULL ? scene->node_paths[node_index] : "";
    out_info->path_length = bs_min_i32_size(path != NULL ? strlen(path) : 0);
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_copy_node_name(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size)
{
    ufbx_node *node = bs_get_node(scene, node_index);
    return node != NULL ? bs_copy_string(node->name.data, node->name.length, dst, dst_size) : 0;
}

BS_UFBX_API int32_t bs_ufbx_copy_node_path(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size)
{
    if (scene == NULL || scene->node_paths == NULL || node_index < 0 || (size_t)node_index >= scene->scene->nodes.count) return 0;
    const char *path = scene->node_paths[node_index];
    return bs_copy_string(path, path != NULL ? strlen(path) : 0, dst, dst_size);
}

BS_UFBX_API int32_t bs_ufbx_get_mesh_count(bs_ufbx_scene *scene)
{
    return scene != NULL && scene->scene != NULL ? bs_min_i32_size(scene->scene->meshes.count) : 0;
}

BS_UFBX_API int32_t bs_ufbx_get_mesh_info(bs_ufbx_scene *scene, int32_t mesh_index, bs_ufbx_mesh_info *out_info)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    if (mesh == NULL || out_info == NULL) return 0;
    memset(out_info, 0, sizeof(*out_info));
    out_info->id = mesh->element_id;
    out_info->node_index = mesh->instances.count > 0 ? bs_node_index(scene, mesh->instances.data[0]) : -1;
    out_info->control_point_count = bs_min_i32_size(mesh->num_vertices);
    out_info->skin_count = bs_min_i32_size(mesh->skin_deformers.count);
    out_info->blend_deformer_count = bs_min_i32_size(mesh->blend_deformers.count);
    out_info->name_length = bs_min_i32_size(mesh->name.length);
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_copy_mesh_name(bs_ufbx_scene *scene, int32_t mesh_index, char *dst, int32_t dst_size)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    return mesh != NULL ? bs_copy_string(mesh->name.data, mesh->name.length, dst, dst_size) : 0;
}

BS_UFBX_API int32_t bs_ufbx_copy_control_points(bs_ufbx_scene *scene, int32_t mesh_index, double *dst_xyz, int32_t dst_vertex_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    return mesh != NULL ? bs_copy_vec3_list(mesh->vertices.data, mesh->vertices.count, dst_xyz, dst_vertex_count) : 0;
}

BS_UFBX_API int32_t bs_ufbx_copy_control_point_normals(bs_ufbx_scene *scene, int32_t mesh_index, double *dst_xyz, int32_t dst_vertex_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    return bs_copy_vertex_attrib(mesh != NULL ? &mesh->vertex_normal : NULL, mesh, dst_xyz, dst_vertex_count);
}

BS_UFBX_API int32_t bs_ufbx_copy_control_point_tangents(bs_ufbx_scene *scene, int32_t mesh_index, double *dst_xyz, int32_t dst_vertex_count)
{
    ufbx_mesh *mesh = bs_get_mesh(scene, mesh_index);
    return bs_copy_vertex_attrib(mesh != NULL ? &mesh->vertex_tangent : NULL, mesh, dst_xyz, dst_vertex_count);
}

BS_UFBX_API int32_t bs_ufbx_get_skin_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, bs_ufbx_skin_info *out_info)
{
    ufbx_skin_deformer *skin = bs_get_skin(scene, mesh_index, skin_index);
    if (skin == NULL || out_info == NULL) return 0;
    memset(out_info, 0, sizeof(*out_info));
    out_info->id = skin->element_id;
    out_info->cluster_count = bs_min_i32_size(skin->clusters.count);
    out_info->name_length = bs_min_i32_size(skin->name.length);
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_copy_skin_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, char *dst, int32_t dst_size)
{
    ufbx_skin_deformer *skin = bs_get_skin(scene, mesh_index, skin_index);
    return skin != NULL ? bs_copy_string(skin->name.data, skin->name.length, dst, dst_size) : 0;
}

BS_UFBX_API int32_t bs_ufbx_get_skin_cluster_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, bs_ufbx_cluster_info *out_info)
{
    ufbx_skin_cluster *cluster = bs_get_cluster(scene, mesh_index, skin_index, cluster_index);
    if (cluster == NULL || out_info == NULL) return 0;
    memset(out_info, 0, sizeof(*out_info));
    out_info->id = cluster->element_id;
    out_info->bone_node_index = bs_node_index(scene, cluster->bone_node);
    out_info->weight_count = bs_min_i32_size(cluster->num_weights);
    out_info->name_length = bs_min_i32_size(cluster->name.length);
    ufbx_matrix bone_to_world = cluster->bind_to_world;
    ufbx_matrix mesh_to_world = ufbx_matrix_mul(&bone_to_world, &cluster->mesh_node_to_bone);
    bs_matrix_from_ufbx(&out_info->mesh_bind_world, &mesh_to_world);
    bs_matrix_from_ufbx(&out_info->bone_bind_world, &cluster->bind_to_world);
    bs_matrix_from_ufbx(&out_info->mesh_node_to_bone, &cluster->mesh_node_to_bone);
    bs_matrix_from_ufbx(&out_info->geometry_to_bone, &cluster->geometry_to_bone);
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_copy_cluster_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, char *dst, int32_t dst_size)
{
    ufbx_skin_cluster *cluster = bs_get_cluster(scene, mesh_index, skin_index, cluster_index);
    return cluster != NULL ? bs_copy_string(cluster->name.data, cluster->name.length, dst, dst_size) : 0;
}

BS_UFBX_API int32_t bs_ufbx_copy_cluster_indices(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, int32_t *dst, int32_t dst_count)
{
    ufbx_skin_cluster *cluster = bs_get_cluster(scene, mesh_index, skin_index, cluster_index);
    if (cluster == NULL || dst == NULL || dst_count < 0 || (size_t)dst_count < cluster->vertices.count) return 0;
    for (size_t i = 0; i < cluster->vertices.count; i++) {
        dst[i] = (int32_t)cluster->vertices.data[i];
    }
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_copy_cluster_weights(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, double *dst, int32_t dst_count)
{
    ufbx_skin_cluster *cluster = bs_get_cluster(scene, mesh_index, skin_index, cluster_index);
    if (cluster == NULL || dst == NULL || dst_count < 0 || (size_t)dst_count < cluster->weights.count) return 0;
    for (size_t i = 0; i < cluster->weights.count; i++) {
        dst[i] = cluster->weights.data[i];
    }
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_get_blend_deformer_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, bs_ufbx_blend_deformer_info *out_info)
{
    ufbx_blend_deformer *deformer = bs_get_blend_deformer(scene, mesh_index, deformer_index);
    if (deformer == NULL || out_info == NULL) return 0;
    memset(out_info, 0, sizeof(*out_info));
    out_info->id = deformer->element_id;
    out_info->channel_count = bs_min_i32_size(deformer->channels.count);
    out_info->name_length = bs_min_i32_size(deformer->name.length);
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_copy_blend_deformer_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, char *dst, int32_t dst_size)
{
    ufbx_blend_deformer *deformer = bs_get_blend_deformer(scene, mesh_index, deformer_index);
    return deformer != NULL ? bs_copy_string(deformer->name.data, deformer->name.length, dst, dst_size) : 0;
}

BS_UFBX_API int32_t bs_ufbx_get_blend_channel_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, bs_ufbx_blend_channel_info *out_info)
{
    ufbx_blend_channel *channel = bs_get_blend_channel(scene, mesh_index, deformer_index, channel_index);
    if (channel == NULL || out_info == NULL) return 0;
    memset(out_info, 0, sizeof(*out_info));
    out_info->id = channel->element_id;
    out_info->frame_count = bs_min_i32_size(channel->keyframes.count);
    out_info->name_length = bs_min_i32_size(channel->name.length);
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_copy_blend_channel_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, char *dst, int32_t dst_size)
{
    ufbx_blend_channel *channel = bs_get_blend_channel(scene, mesh_index, deformer_index, channel_index);
    return channel != NULL ? bs_copy_string(channel->name.data, channel->name.length, dst, dst_size) : 0;
}

BS_UFBX_API int32_t bs_ufbx_get_blend_frame_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t frame_index, bs_ufbx_blend_frame_info *out_info)
{
    ufbx_real weight = 0.0;
    ufbx_blend_shape *shape = bs_get_blend_shape(scene, mesh_index, deformer_index, channel_index, frame_index, &weight);
    if (shape == NULL || out_info == NULL) return 0;
    memset(out_info, 0, sizeof(*out_info));
    out_info->id = shape->element_id;
    out_info->weight = weight;
    out_info->offset_count = bs_min_i32_size(shape->num_offsets);
    out_info->name_length = bs_min_i32_size(shape->name.length);
    return 1;
}

BS_UFBX_API int32_t bs_ufbx_copy_blend_frame_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t frame_index, char *dst, int32_t dst_size)
{
    ufbx_blend_shape *shape = bs_get_blend_shape(scene, mesh_index, deformer_index, channel_index, frame_index, NULL);
    return shape != NULL ? bs_copy_string(shape->name.data, shape->name.length, dst, dst_size) : 0;
}

BS_UFBX_API int32_t bs_ufbx_copy_blend_frame_offsets(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t frame_index, int32_t *dst_indices, double *dst_position_xyz, double *dst_normal_xyz, int32_t dst_count)
{
    ufbx_blend_shape *shape = bs_get_blend_shape(scene, mesh_index, deformer_index, channel_index, frame_index, NULL);
    if (shape == NULL || dst_indices == NULL || dst_position_xyz == NULL || dst_count < 0 || (size_t)dst_count < shape->num_offsets) return 0;
    for (size_t i = 0; i < shape->num_offsets; i++) {
        dst_indices[i] = (int32_t)shape->offset_vertices.data[i];
        ufbx_vec3 pos = shape->position_offsets.data[i];
        dst_position_xyz[i * 3 + 0] = pos.x;
        dst_position_xyz[i * 3 + 1] = pos.y;
        dst_position_xyz[i * 3 + 2] = pos.z;
        if (dst_normal_xyz != NULL) {
            ufbx_vec3 normal = { 0.0, 0.0, 0.0 };
            if (i < shape->normal_offsets.count) {
                normal = shape->normal_offsets.data[i];
            }
            dst_normal_xyz[i * 3 + 0] = normal.x;
            dst_normal_xyz[i * 3 + 1] = normal.y;
            dst_normal_xyz[i * 3 + 2] = normal.z;
        }
    }
    return 1;
}
