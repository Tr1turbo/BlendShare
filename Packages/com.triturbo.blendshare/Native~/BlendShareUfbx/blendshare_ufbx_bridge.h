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

#define BS_UFBX_ABI_VERSION 3

typedef enum bs_ufbx_status {
    BS_UFBX_OK                   = 0,
    BS_UFBX_ERR_INVALID_ARG      = -1,
    BS_UFBX_ERR_OUT_OF_RANGE     = -2,
    BS_UFBX_ERR_BUFFER_TOO_SMALL = -3,
    BS_UFBX_ERR_UNAVAILABLE      = -4,
    BS_UFBX_ERR_PARSE_FAILED     = -5,
    BS_UFBX_ERR_OUT_OF_MEMORY    = -6
} bs_ufbx_status;

typedef enum bs_ufbx_load_flags {
    BS_UFBX_LOAD_NONE                     = 0,
    BS_UFBX_LOAD_IGNORE_ANIMATION         = 1 << 0,
    BS_UFBX_LOAD_IGNORE_EMBEDDED          = 1 << 1,
    BS_UFBX_LOAD_GENERATE_MISSING_NORMALS = 1 << 2,
    BS_UFBX_LOAD_LOAD_EXTERNAL_FILES      = 1 << 3,
    BS_UFBX_LOAD_RETAIN_VERTEX_ATTRIB_W   = 1 << 4,
    BS_UFBX_LOAD_USE_TARGET_AXES          = 1 << 5,
    BS_UFBX_LOAD_USE_TARGET_UNIT_METERS   = 1 << 6
} bs_ufbx_load_flags;

typedef enum bs_ufbx_node_attrib_kind {
    BS_UFBX_ATTRIB_NONE   = 0,
    BS_UFBX_ATTRIB_MESH   = 1,
    BS_UFBX_ATTRIB_BONE   = 2,
    BS_UFBX_ATTRIB_EMPTY  = 3,
    BS_UFBX_ATTRIB_CAMERA = 4,
    BS_UFBX_ATTRIB_LIGHT  = 5,
    BS_UFBX_ATTRIB_OTHER  = 6
} bs_ufbx_node_attrib_kind;

typedef enum bs_ufbx_attrib_kind {
    BS_UFBX_MESH_ATTRIB_NORMAL        = 0,
    BS_UFBX_MESH_ATTRIB_TANGENT       = 1,
    BS_UFBX_MESH_ATTRIB_BITANGENT     = 2,
    BS_UFBX_MESH_ATTRIB_UV            = 3,
    BS_UFBX_MESH_ATTRIB_COLOR         = 4,
    BS_UFBX_MESH_ATTRIB_VERTEX_CREASE = 5
} bs_ufbx_attrib_kind;

typedef enum bs_ufbx_blend_offset_outputs {
    BS_UFBX_BLEND_OUT_INDICES   = 1 << 0,
    BS_UFBX_BLEND_OUT_POSITIONS = 1 << 1,
    BS_UFBX_BLEND_OUT_NORMALS   = 1 << 2,
    BS_UFBX_BLEND_OUT_WEIGHTS   = 1 << 3
} bs_ufbx_blend_offset_outputs;

typedef struct bs_ufbx_vec2 {
    double x;
    double y;
} bs_ufbx_vec2;

typedef struct bs_ufbx_vec3 {
    double x;
    double y;
    double z;
} bs_ufbx_vec3;

typedef struct bs_ufbx_vec4 {
    double x;
    double y;
    double z;
    double w;
} bs_ufbx_vec4;

typedef struct bs_ufbx_quat {
    double x;
    double y;
    double z;
    double w;
} bs_ufbx_quat;

typedef struct bs_ufbx_matrix {
    double m[16];
} bs_ufbx_matrix;

typedef struct bs_ufbx_edge {
    int32_t corner_a;
    int32_t corner_b;
} bs_ufbx_edge;

/* Matches ufbx_coordinate_axis: 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z, 6=Unknown.
 * Note: front points opposite forward, matching ufbx convention. */
typedef struct bs_ufbx_axes {
    int32_t right;
    int32_t up;
    int32_t front;
} bs_ufbx_axes;

typedef struct bs_ufbx_load_options {
    uint32_t struct_size;
    uint32_t flags;
    bs_ufbx_axes target_axes;
    int32_t _pad0;
    double target_unit_meters;
} bs_ufbx_load_options;

typedef struct bs_ufbx_scene_info {
    uint32_t file_version;
    int32_t original_up_axis;
    double unit_meters;
    double original_unit_meters;
    double frames_per_second;
    bs_ufbx_axes axes;
    int32_t node_count;
    int32_t mesh_count;
    int32_t material_count;
} bs_ufbx_scene_info;

typedef struct bs_ufbx_node_info {
    uint64_t id;
    uint64_t attrib_id;
    int32_t parent_index;
    int32_t is_root;
    int32_t is_bone;
    int32_t attrib_kind;
    int32_t mesh_index;
    int32_t material_count;
    int32_t rotation_order;
    int32_t original_inherit_mode;
    int32_t inherit_mode;
    int32_t rotation_active;
    int32_t name_length;
    int32_t path_length;
    int32_t attrib_name_length;
    int32_t _pad0;

    bs_ufbx_vec3 lcl_translation;
    bs_ufbx_vec3 lcl_rotation;
    bs_ufbx_vec3 lcl_scale;
    bs_ufbx_vec3 pre_rotation;
    bs_ufbx_vec3 post_rotation;
    bs_ufbx_vec3 rotation_pivot;
    bs_ufbx_vec3 scaling_pivot;
    bs_ufbx_vec3 rotation_offset;
    bs_ufbx_vec3 scaling_offset;

    bs_ufbx_vec3 local_translation;
    bs_ufbx_quat local_rotation;
    bs_ufbx_vec3 local_scale;
    bs_ufbx_vec3 geometry_translation;
    bs_ufbx_quat geometry_rotation;
    bs_ufbx_vec3 geometry_scale;
    bs_ufbx_matrix node_to_parent;
    bs_ufbx_matrix node_to_world;
    bs_ufbx_matrix geometry_to_node;
    bs_ufbx_matrix geometry_to_world;
} bs_ufbx_node_info;

typedef struct bs_ufbx_material_info {
    uint64_t id;
    int32_t name_length;
    int32_t _pad0;
} bs_ufbx_material_info;

typedef struct bs_ufbx_mesh_info {
    uint64_t id;
    int32_t name_length;
    int32_t instance_count;
    int32_t control_point_count;
    int32_t face_count;
    int32_t face_corner_count;
    int32_t triangle_count;
    int32_t max_face_triangles;
    int32_t edge_count;
    int32_t uv_set_count;
    int32_t color_set_count;
    int32_t material_count;
    int32_t material_part_count;
    int32_t material_part_usage_count;
    int32_t face_group_count;
    int32_t skin_count;
    int32_t blend_deformer_count;
    int32_t has_face_smoothing;
    int32_t has_face_smoothing_group;
    int32_t has_face_material;
    int32_t has_face_group;
    int32_t has_face_hole;
    int32_t has_edge_smoothing;
    int32_t has_edge_crease;
    int32_t has_edge_visibility;
} bs_ufbx_mesh_info;

typedef struct bs_ufbx_mesh_part_info {
    int32_t part_index;
    int32_t face_count;
    int32_t triangle_count;
} bs_ufbx_mesh_part_info;

typedef struct bs_ufbx_layer_set_info {
    int32_t set_index;
    int32_t name_length;
} bs_ufbx_layer_set_info;

typedef struct bs_ufbx_attrib_info {
    int32_t exists;
    int32_t unique_per_vertex;
    int32_t value_count;
    int32_t corner_count;
    int32_t values_w_count;
} bs_ufbx_attrib_info;

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
    int32_t _pad0;
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
    int32_t keyframe_count;
    int32_t name_length;
} bs_ufbx_blend_channel_info;

typedef struct bs_ufbx_blend_keyframe_info {
    uint64_t shape_id;
    double target_weight;
    double effective_weight;
    int32_t offset_count;
    int32_t has_normal_offsets;
    int32_t has_offset_weights;
    int32_t shape_name_length;
} bs_ufbx_blend_keyframe_info;

/* Scene lifecycle & metadata */
BS_UFBX_API int32_t bs_ufbx_get_abi_version(void);
BS_UFBX_API int32_t bs_ufbx_load_file(const char *path, const bs_ufbx_load_options *opts, bs_ufbx_scene **out_scene, char *error, int32_t error_size);
BS_UFBX_API int32_t bs_ufbx_load_memory(const void *data, int64_t data_size, const bs_ufbx_load_options *opts, bs_ufbx_scene **out_scene, char *error, int32_t error_size);
BS_UFBX_API void bs_ufbx_free(bs_ufbx_scene *scene);
BS_UFBX_API int32_t bs_ufbx_get_scene_info(bs_ufbx_scene *scene, bs_ufbx_scene_info *out_info);

/* Nodes & materials */
BS_UFBX_API int32_t bs_ufbx_get_node_info(bs_ufbx_scene *scene, int32_t node_index, bs_ufbx_node_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_node_name(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_node_path(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_node_attrib_name(bs_ufbx_scene *scene, int32_t node_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_node_material_indices(bs_ufbx_scene *scene, int32_t node_index, int32_t *dst, int32_t dst_count);

BS_UFBX_API int32_t bs_ufbx_get_material_info(bs_ufbx_scene *scene, int32_t material_index, bs_ufbx_material_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_material_name(bs_ufbx_scene *scene, int32_t material_index, char *dst, int32_t dst_size);

/* Mesh metadata, topology, faces, edges & material parts */
BS_UFBX_API int32_t bs_ufbx_get_mesh_info(bs_ufbx_scene *scene, int32_t mesh_index, bs_ufbx_mesh_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_mesh_name(bs_ufbx_scene *scene, int32_t mesh_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_mesh_instance_node_indices(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_mesh_material_indices(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);

BS_UFBX_API int32_t bs_ufbx_copy_control_points(bs_ufbx_scene *scene, int32_t mesh_index, bs_ufbx_vec3 *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_face_index_begins(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_face_sizes(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_face_corner_control_points(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_vertex_first_corners(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);

BS_UFBX_API int32_t bs_ufbx_copy_face_smoothing(bs_ufbx_scene *scene, int32_t mesh_index, uint8_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_face_smoothing_groups(bs_ufbx_scene *scene, int32_t mesh_index, uint32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_face_materials(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_face_groups(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_face_holes(bs_ufbx_scene *scene, int32_t mesh_index, uint8_t *dst, int32_t dst_count);

BS_UFBX_API int32_t bs_ufbx_copy_edges(bs_ufbx_scene *scene, int32_t mesh_index, bs_ufbx_edge *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_edge_smoothing(bs_ufbx_scene *scene, int32_t mesh_index, uint8_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_edge_creases(bs_ufbx_scene *scene, int32_t mesh_index, double *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_edge_visibility(bs_ufbx_scene *scene, int32_t mesh_index, uint8_t *dst, int32_t dst_count);

BS_UFBX_API int32_t bs_ufbx_get_material_part_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t part_index, bs_ufbx_mesh_part_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_material_part_faces(bs_ufbx_scene *scene, int32_t mesh_index, int32_t part_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_material_part_usage_order(bs_ufbx_scene *scene, int32_t mesh_index, int32_t *dst, int32_t dst_count);

/* Named UV / Color sets & unified indexed vertex attributes */
BS_UFBX_API int32_t bs_ufbx_get_uv_set_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t set_index, bs_ufbx_layer_set_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_uv_set_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t set_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_get_color_set_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t set_index, bs_ufbx_layer_set_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_color_set_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t set_index, char *dst, int32_t dst_size);

BS_UFBX_API int32_t bs_ufbx_get_attrib_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t attrib_kind, int32_t set_index, bs_ufbx_attrib_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_attrib_indices(bs_ufbx_scene *scene, int32_t mesh_index, int32_t attrib_kind, int32_t set_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_w(bs_ufbx_scene *scene, int32_t mesh_index, int32_t attrib_kind, int32_t set_index, double *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_real(bs_ufbx_scene *scene, int32_t mesh_index, int32_t attrib_kind, int32_t set_index, double *dst, int32_t dst_count, int32_t expand_per_corner);
BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_vec2(bs_ufbx_scene *scene, int32_t mesh_index, int32_t attrib_kind, int32_t set_index, bs_ufbx_vec2 *dst, int32_t dst_count, int32_t expand_per_corner);
BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_vec3(bs_ufbx_scene *scene, int32_t mesh_index, int32_t attrib_kind, int32_t set_index, bs_ufbx_vec3 *dst, int32_t dst_count, int32_t expand_per_corner);
BS_UFBX_API int32_t bs_ufbx_copy_attrib_values_vec4(bs_ufbx_scene *scene, int32_t mesh_index, int32_t attrib_kind, int32_t set_index, bs_ufbx_vec4 *dst, int32_t dst_count, int32_t expand_per_corner);
BS_UFBX_API int32_t bs_ufbx_copy_control_point_first_corner_attrib_vec3(bs_ufbx_scene *scene, int32_t mesh_index, int32_t attrib_kind, int32_t set_index, bs_ufbx_vec3 *dst, int32_t dst_count);

/* Skin deformers & clusters */
BS_UFBX_API int32_t bs_ufbx_get_skin_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, bs_ufbx_skin_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_skin_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_get_skin_cluster_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, bs_ufbx_cluster_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_cluster_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_copy_cluster_indices(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, int32_t *dst, int32_t dst_count);
BS_UFBX_API int32_t bs_ufbx_copy_cluster_weights(bs_ufbx_scene *scene, int32_t mesh_index, int32_t skin_index, int32_t cluster_index, double *dst, int32_t dst_count);

/* Blend deformers, channels & keyframes */
BS_UFBX_API int32_t bs_ufbx_get_blend_deformer_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, bs_ufbx_blend_deformer_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_blend_deformer_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_get_blend_channel_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, bs_ufbx_blend_channel_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_blend_channel_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, char *dst, int32_t dst_size);
BS_UFBX_API int32_t bs_ufbx_get_blend_keyframe_info(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t keyframe_index, bs_ufbx_blend_keyframe_info *out_info);
BS_UFBX_API int32_t bs_ufbx_copy_blend_shape_name(bs_ufbx_scene *scene, int32_t mesh_index, int32_t deformer_index, int32_t channel_index, int32_t keyframe_index, char *dst, int32_t dst_size);
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
    int32_t dst_count);

#ifdef __cplusplus
}
#endif

#endif
