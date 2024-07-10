using System.Collections;
using System.Collections.Generic;
using UnityEngine;


using UnityEditor;
#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif



namespace Triturbo.BlendShapeShare.BlendShapeData
{
    public static class BlendShapeAppender
    {

        public static List<Mesh> CreateMeshes(this BlendShapeDataSO so)
        {

            List<Mesh> meshesList = new List<Mesh>(so.m_MeshDataList.Count);

            foreach (var meshData in so.m_MeshDataList)
            {
                var mesh = CreateBlendShapesMesh(meshData, meshData.m_OriginMesh);
                if (mesh == null)
                {
                    return null;
                }
                
                mesh.name = meshData.m_MeshName;
                meshesList.Add(mesh);
            }

            return meshesList;
        }
        public static GeneratedMeshAssetSO CreateMeshAsset(List<Mesh> meshesList, string path)
        {
            var asset = ScriptableObject.CreateInstance<GeneratedMeshAssetSO>();

            AssetDatabase.CreateAsset(asset, path);
            foreach (var mesh in meshesList)
            {
                AssetDatabase.AddObjectToAsset(mesh, path);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return asset;
        }

        public static GeneratedMeshAssetSO CreateMeshAsset(this BlendShapeDataSO so, string path)
        {

            List<Mesh> meshesList = new List<Mesh>(so.m_MeshDataList.Count);

            foreach (var meshData in so.m_MeshDataList)
            {
                var mesh = CreateBlendShapesMesh(meshData, meshData.m_OriginMesh);
                if (mesh == null)
                {
                    return null;
                }
                mesh.name = meshData.m_MeshName;
                meshesList.Add(mesh);
            }

            var asset = ScriptableObject.CreateInstance<GeneratedMeshAssetSO>();

            AssetDatabase.CreateAsset(asset, path);
            foreach (var mesh in meshesList)
            {
                AssetDatabase.AddObjectToAsset(mesh, path);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return asset;
        }


        // Copy mesh from GameObject if included in BlendShapeDataSO
        public static GeneratedMeshAssetSO CreateMeshAsset(this BlendShapeDataSO so, string path, GameObject target)
        {

            List<Mesh> meshesList = new List<Mesh>(so.m_MeshDataList.Count);

            foreach (var meshData in so.m_MeshDataList)
            {
               

                var targetMesh = target.transform.Find(meshData.m_MeshName)?.GetComponent<SkinnedMeshRenderer>()?.sharedMesh;
                if (targetMesh == null)
                {
                    return null;
                }
                Mesh meshCopy = Object.Instantiate(targetMesh);

                meshCopy.name = meshData.m_MeshName;
                meshesList.Add(meshCopy);
            }

            var asset = ScriptableObject.CreateInstance<GeneratedMeshAssetSO>();

            AssetDatabase.CreateAsset(asset, path);
            foreach (var mesh in meshesList)
            {
                AssetDatabase.AddObjectToAsset(mesh, path);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return asset;
        }

        public static Mesh CreateBlendShapesMesh(MeshData meshBlendShapesData, Mesh target)
        {
            if(target == null)
            {
                return null;
            }

            // Ensure the vertex count of the blend shape data matches the target mesh

            if (meshBlendShapesData.m_VertexCount != target.vertexCount)
            {
                return null;
            }

            // Verify the vertex hash to ensure the target mesh hasn't been altered

            if (meshBlendShapesData.m_VerticesHash != MeshData.GetVerticesHash(target))
            {
                return null;
            }

            // Create a copy of the target mesh to modify

            Mesh newMesh = Object.Instantiate(target);

            // Check if any blend shapes already exist in the target mesh
            bool exist = false;
            foreach (var blendShapeData in meshBlendShapesData.BlendShapes)
            {
                var index = newMesh.GetBlendShapeIndex(blendShapeData.m_ShapeName);
                if (index != -1)
                {
                    exist = true;
                    break;
                }
            }

            // If blend shapes already exist, remove all existing shapes and add back those not defined in meshBlendShapesData
            if (exist)
            {
                List<(string, UnityBlendShapeData)> blendshapes = new List<(string, UnityBlendShapeData)>();

                // Collect existing blend shapes that are not defined in meshBlendShapesData

                for (int i = 0; i < newMesh.blendShapeCount; i++)
                {
                    string name = newMesh.GetBlendShapeName(i);
                    if (!meshBlendShapesData.ContainsBlendShape(name))
                    {
                        blendshapes.Add((name, new UnityBlendShapeData(newMesh, i)));
                    }
                }

                // Clear all blend shapes from the target mesh
                // Add back the collected blend shapes to the target mesh

                newMesh.ClearBlendShapes();
                foreach (var blendShape in blendshapes)
                {
                    foreach (var frame in blendShape.Item2.m_Frames)
                    {
                        frame.AddBlendShapeFrame(ref newMesh, blendShape.Item1);
                    }
                }
            }

            // Apply the new blend shapes from meshBlendShapesData to the target mesh
            foreach (var blendShapeData in meshBlendShapesData.BlendShapes)
            {
                foreach (var frame in blendShapeData.m_UnityBlendShapeData.m_Frames)
                {
                    frame.AddBlendShapeFrame(ref newMesh, blendShapeData.m_ShapeName);
                }
            }
            return newMesh;
        }



        //Autodesk FBX SDK

#if ENABLE_FBX_SDK

        public static FbxBlendShape GetDeformer(this BlendShapeDataSO so, FbxMesh targetMesh, bool create = true)
        {

            if(!string.IsNullOrEmpty(so.m_DeformerID))
            {
                int deformerCount = targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape);


                for (int i = deformerCount - 1; i >= 0; i--)
                {
                    var deformer = targetMesh.GetBlendShapeDeformer(i);
                    if (deformer.GetName() == so.m_DeformerID)
                    {
                        return deformer;
                    }
                }
            }


            if (create)
                return FbxBlendShape.Create(targetMesh, so.m_DeformerID);

            return null;
        }

        public static FbxBlendShapeChannel CreateFbxBlendShapeChannel(string name, FbxMesh mesh, FbxBlendShapeData fbxBlendShapeData)
        {

            FbxBlendShapeChannel fbxBlendShapeChannel = FbxBlendShapeChannel.Create(mesh, name);
            int controlPointCount = mesh.GetControlPointsCount();


            int shapeCount = fbxBlendShapeData.m_Frames.Length;
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {

                FbxShape newShape = FbxShape.Create(mesh, name);
                newShape.InitControlPoints(controlPointCount);

                FbxBlendShapeFrame frame = fbxBlendShapeData.m_Frames[shapeIndex];


                for (int pointIndex = 0; pointIndex < controlPointCount; pointIndex++)
                {
                    var d = frame.GetDeltaControlPointAt(pointIndex);

                    var controlPoint = mesh.GetControlPointAt(pointIndex) + new FbxVector4(d.m_X, d.m_Y, d.m_Z, d.m_W);



                    newShape.SetControlPointAt(controlPoint, pointIndex);
                }


                fbxBlendShapeChannel.AddTargetShape(newShape, 100.0 * (shapeIndex + 1) / shapeCount);
            }
            return fbxBlendShapeChannel;
        }
#endif

        public static bool CreateFbx(this BlendShapeDataSO so, string fbxPath = null)
        {
            return so.CreateFbx(so.m_Original, fbxPath);
        }
        public static bool CreateFbx(this BlendShapeDataSO so, GameObject target, string fbxPath = null)
        {
#if ENABLE_FBX_SDK
            var fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            var scene = FbxScene.Create(fbxManager, target.name);

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Create FBX scene...", 0))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            FbxImporter fbxImporter = FbxImporter.Create(fbxManager, "");
            int pFileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Import source FBX...", 0.1f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(target), pFileFormat, fbxManager.GetIOSettings()))
            {
                return false;
            }



            fbxImporter.Import(scene);
            fbxImporter.Destroy();

            if (EditorUtility.DisplayCancelableProgressBar("TransferFbxBlendshapes", "Getting Root Node...", 0.4f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            var sourceRootNode = scene.GetRootNode();



            foreach (var meshData in so.m_MeshDataList)
            {
                FbxNode node = sourceRootNode.FindChild(meshData.m_MeshName, false);
                FbxMesh targetMesh = node?.GetMesh();
                if (targetMesh == null)
                {
                    Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                    continue;
                }

                so.GetDeformer(targetMesh, false)?.Destroy();

                // remove blend shape define in list
                for (int i = 0; i < targetMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); i++)
                {
                    var d = targetMesh.GetBlendShapeDeformer(i);
                    for (int j = d.GetBlendShapeChannelCount() - 1; j >= 0; j--)
                    {
                        var name = d.GetBlendShapeChannel(j).GetName();
                        if (meshData.ContainsBlendShape(name))
                        {
                            d.RemoveBlendShapeChannel(d.GetBlendShapeChannel(j));
                            Debug.LogWarning($"Warning: The blendshape with the name '{name}' already exists in the node '{node.GetName()}'. The existing blendshape was overwritten.");

                        }
                    }
                }

                var deformer = so.GetDeformer(targetMesh);

                foreach(var blend in meshData.BlendShapes)
                {
                    deformer.AddBlendShapeChannel(CreateFbxBlendShapeChannel(blend.m_ShapeName, targetMesh, blend.m_FbxBlendShapeData));
                }
            }

            var exporter = FbxExporter.Create(fbxManager, "");

            if (string.IsNullOrWhiteSpace(fbxPath))
            {
                fbxPath = AssetDatabase.GetAssetPath(target);
            }
            else
            {
                AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(target), fbxPath);
            }
            AssetDatabase.Refresh();

            if (exporter.Initialize(fbxPath, pFileFormat, fbxManager.GetIOSettings()) == false)
            {
                Debug.LogError("Exporter Initialize failed.");
                return false;
            }
            exporter.Export(scene);
            exporter.Destroy();

            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();
            return true;
#else
            return false;
#endif
        }




        public static bool RemoveBlendShapes(this BlendShapeDataSO so, GameObject target, bool RemoveInAllDeformer = true)
        {
#if ENABLE_FBX_SDK
            var fbxManager = FbxManager.Create();
            var ios = FbxIOSettings.Create(fbxManager, Globals.IOSROOT);
            fbxManager.SetIOSettings(ios);
            var scene = FbxScene.Create(fbxManager, target.name);

            if (EditorUtility.DisplayCancelableProgressBar("RemoveBlendShapes", "Create FBX scene...", 0))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            FbxImporter fbxImporter = FbxImporter.Create(fbxManager, "");
            int pFileFormat = fbxManager.GetIOPluginRegistry().FindWriterIDByDescription("FBX binary (*.fbx)");

            if (EditorUtility.DisplayCancelableProgressBar("RemoveBlendShapes", "Import source FBX...", 0.1f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }

            if (!fbxImporter.Initialize(AssetDatabase.GetAssetPath(target), pFileFormat, fbxManager.GetIOSettings()))
            {
                return false;
            }

            fbxImporter.Import(scene);
            fbxImporter.Destroy();

            if (EditorUtility.DisplayCancelableProgressBar("RemoveBlendShapes", "Getting Root Node...", 0.4f))
            {
                EditorUtility.ClearProgressBar();
                return false;
            }
            var sourceRootNode = scene.GetRootNode();



            foreach (var meshData in so.m_MeshDataList)
            {
                FbxNode node = sourceRootNode.FindChild(meshData.m_MeshName, false);
                FbxMesh sourceMesh = node?.GetMesh();
                if (sourceMesh == null)
                {
                    Debug.LogError($"Can not find mesh: {meshData.m_MeshName} in FBX file");
                    continue;
                }

                if (sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape) < 1)
                {
                    continue;
                }


                so.GetDeformer(sourceMesh, false)?.Destroy();
                if (RemoveInAllDeformer)
                {
                    for (int i = 0; i < sourceMesh.GetDeformerCount(FbxDeformer.EDeformerType.eBlendShape); i++)
                    {
                        var deformer = sourceMesh.GetBlendShapeDeformer(i);
                        for (int j = deformer.GetBlendShapeChannelCount() - 1; j >= 0; j--)
                        {
                            var name = deformer.GetBlendShapeChannel(j).GetName();
                            if (meshData.ContainsBlendShape(name))
                            {
                                deformer.RemoveBlendShapeChannel(deformer.GetBlendShapeChannel(j));

                            }
                        }
                    }
                }

            }

            var exporter = FbxExporter.Create(fbxManager, "");

            if (exporter.Initialize(AssetDatabase.GetAssetPath(target), pFileFormat, fbxManager.GetIOSettings()) == false)
            {
                Debug.LogError("Exporter Initialize failed.");
                return false;
            }
            exporter.Export(scene);
            exporter.Destroy();

            AssetDatabase.Refresh();
            EditorUtility.ClearProgressBar();
            return true;
#else
            return false;
#endif
        }



    }
}

