#ifndef BLENDSHARE_UFBX_BRIDGE_H
#define BLENDSHARE_UFBX_BRIDGE_H

#include <stdint.h>

#ifdef _WIN32
#define BS_UFBX_API __declspec(dllexport)
#else
#define BS_UFBX_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct bs_ufbx_scene bs_ufbx_scene;

typedef struct bs_ufbx_matrix {
    double m[16];
} bs_ufbx_matrix;

typedef struct bs_ufbx_node_info {
    uint64_t id;
    int32_t parent_index;
    int32_t type;
    double lcl_translation[3];
    double lcl_rotation[3];
    double lcl_scale[3];
    int32_t name_length;
    int32_t path_length;
    double euler_rotation[3];
    double pre_rotation[3];
    double post_rotation[3];
    double ufbx_local_translation[3];
    double ufbx_local_rotation[4];
    double ufbx_local_scale[3];
} bs_ufbx_node_info;

typedef struct bs_ufbx_mesh_info {
    uint64_t id;
    int32_t node_index;
    int32_t control_point_count;
    int32_t skin_count;
    int32_t blend_deformer_count;
    int32_t name_length;
} bs_ufbx_mesh_info;

typedef struct bs_ufbx_skin_info {
    uint64_t id;
    int32_t cluster_count;
    int32_t name_length;
} bs_ufbx_skin_info;

typedef struct bs_ufbx_cluster_info {
    uint64_t id;
    int32_t bone_node_index;
    int32_t weight_count;
    int32_t name_length;
    bs_ufbx_matrix mesh_bind_world;
    bs_ufbx_matrix bone_bind_world;
    bs_ufbx_matrix mesh_node_to_bone;
    bs_ufbx_matrix geometry_to_bone;
} bs_ufbx_cluster_info;

typedef struct bs_ufbx_blend_deformer_info {
    uint64_t id;
    int32_t channel_count;
    int32_t name_length;
} bs_ufbx_blend_deformer_info;

typedef struct bs_ufbx_blend_channel_info {
    uint64_t id;
    int32_t frame_count;
    int32_t name_length;
} bs_ufbx_blend_channel_info;

typedef struct bs_ufbx_blend_frame_info {
    uint64_t id;
    double weight;
    int32_t offset_count;
    int32_t name_length;
} bs_ufbx_blend_frame_info;

BS_UFBX_API int32_t bs_ufbx_load(const char *path, bs_ufbx_scene **out_scene, char *error, int32_t error_size);
BS_UFBX_API void bs_ufbx_free(bs_ufbx_scene *scene);

BS_UFBX_API int32_t bs_ufbx_get_node_count(bs_ufbx_scene *scene);
BS_UFBX_API int32_t bs_ufbx_get_node_info(bs_ufbx_scene *scene, int32_t node_index, bs_ufbx_node_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_node_name(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_node_path(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size);

BS_UFBX_API int32_t bs_ufbx_get_mesh_count(bs_ufbx_scene *scene);
BS_UFBX_API int32_t bs_ufbx_get_mesh_info(bs_ufbx_scene *scene, int32_t mesh_index, bs_ufbx_mesh_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_mesh_name(bs_ufbx_scene *scene, int32_t mesh_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_control_points(bs_ufbx_scene *scene, int32_t mesh_index, double *dst_xyz, int32_t dst_vertex_count);
BS_UFBX_API int32_t bs_ufbx_copy_control_point_normals(bs_ufbx_scene *scene, int32_t mesh_index, double *dst_xyz, int32_t dst_vertex_count);
BS_UFBX_API int32_t bs_ufbx_copy_control_point_tangents(bs_ufbx_scene *scene, int32_t mesh_index, double *dst_xyz, int32_t dst_vertex_count);

BS_UFBX_API int32_t bs_ufbx_get_skin_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, bs_ufbx_skin_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_skin_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_get_skin_cluster_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, bs_ufbx_cluster_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_cluster_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_cluster_indices(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_cluster_weights(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, double *dst, int32_t dst_count);

BS_UFBX_API int32_t bs_ufbx_get_blend_deformer_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, bs_ufbx_blend_deformer_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_blend_deformer_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_get_blend_channel_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, bs_ufbx_blend_channel_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_blend_channel_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_get_blend_frame_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t frame_index, bs_ufbx_blend_frame_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_blend_frame_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t frame_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_blend_frame_offsets(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t frame_index, int32_t *dst_indices, double *dst_position_xyz, double *dst_normal_xyz, int32_t dst_count);

#ifdef __cplusplus
}
#endif

#endif
