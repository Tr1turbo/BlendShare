
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;


namespace Triturbo.BlendShapeShare.Util
{
    public static class MeshUtil
    {
        public static Mesh FindMeshAsset(Object root, string meshName)
        {
            string path = AssetDatabase.GetAssetPath(root);
            if(string.IsNullOrEmpty(path)) return null;
            
            Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            foreach (var asset in subAssets)
            {
                if (asset is Mesh mesh && asset.name == meshName)
                {
                    return mesh;
                }
            }
            return null;
        }
        
        
        public static Dictionary<string, Mesh> GetMeshes(Object root)
        {
            string path = AssetDatabase.GetAssetPath(root);
            if(string.IsNullOrEmpty(path)) return null;
            Dictionary<string, Mesh> meshes = new ();
            
            Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(path);
            foreach (var asset in subAssets)
            {
                if (asset is Mesh mesh)
                {
                    meshes.Add(mesh.name, mesh);
                }
            }
            
            return meshes;
        }
    } 
}

