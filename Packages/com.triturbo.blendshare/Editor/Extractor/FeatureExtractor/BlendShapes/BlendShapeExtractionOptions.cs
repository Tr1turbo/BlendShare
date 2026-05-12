using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShapeShare.Extractor
{
    public enum BlendShapeBaseMesh
    {
        Source,
        Original
    }

    public sealed class BlendShapeExtractionOptions : MeshFeatureExtractionOptions
    {
        private readonly Dictionary<string, List<string>> selectedBlendShapeNamesByMesh = new();

        public override string FeatureId => BlendShapeFeatureObject.Id;

        public List<string> SelectedBlendShapeNames = new();
        public BlendShapeBaseMesh BaseMesh = BlendShapeBaseMesh.Source;
        public bool WeldVertices = true;
        public bool ApplyRotation;
        public bool ApplyScale;
        public bool ApplyTranslate;
        public float BlendShapeScale = 1f;
        public bool ApplyTransform => ApplyRotation || ApplyScale || ApplyTranslate;

        public void SetSelectedBlendShapeNames(
            string meshPath,
            string meshName,
            IEnumerable<string> shapeNames)
        {
            selectedBlendShapeNamesByMesh[MeshFeatureExtractionSession.BuildMeshKey(meshPath, meshName)] =
                shapeNames?.Where(shapeName => !string.IsNullOrWhiteSpace(shapeName)).Distinct().ToList() ??
                new List<string>();
        }

        public bool HasSelectedBlendShapeNames(string meshPath, string meshName)
        {
            return selectedBlendShapeNamesByMesh.ContainsKey(
                MeshFeatureExtractionSession.BuildMeshKey(meshPath, meshName));
        }

        public List<string> GetSelectedBlendShapeNames(string meshPath, string meshName)
        {
            if (selectedBlendShapeNamesByMesh.TryGetValue(
                    MeshFeatureExtractionSession.BuildMeshKey(meshPath, meshName),
                    out var meshSpecific))
            {
                return meshSpecific;
            }

            return SelectedBlendShapeNames?
                .Where(shapeName => !string.IsNullOrWhiteSpace(shapeName))
                .Distinct()
                .ToList() ?? new List<string>();
        }

        public bool HasAnySelectedBlendShapeNames(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return (meshes ?? Enumerable.Empty<MeshFeatureExtractionMeshRequest>())
                .Any(mesh => mesh != null && GetSelectedBlendShapeNames(mesh.MeshPath, mesh.MeshName).Count > 0);
        }

#if ENABLE_FBX_SDK
        internal FbxAMatrix GetTransform(FbxAMatrix originalMatrix, FbxAMatrix sourceMatrix)
        {
            if (originalMatrix == null)
            {
                originalMatrix = new FbxAMatrix();
                originalMatrix.SetIdentity();
            }

            if (!ApplyTransform)
            {
                var identity = new FbxAMatrix();
                identity.SetIdentity();
                return identity;
            }

            if (sourceMatrix == null)
            {
                sourceMatrix = new FbxAMatrix();
                sourceMatrix.SetIdentity();
            }

            var relativeTransform = sourceMatrix * originalMatrix.Inverse();

            if (!ApplyScale)
            {
                relativeTransform.SetS(new FbxVector4(1, 1, 1, 1));
            }

            if (!ApplyRotation)
            {
                relativeTransform.SetR(new FbxVector4(0, 0, 0, 0));
            }

            if (!ApplyTranslate)
            {
                relativeTransform.SetT(new FbxVector4(0, 0, 0, 0));
            }

            return relativeTransform;
        }
#endif
    }
}
