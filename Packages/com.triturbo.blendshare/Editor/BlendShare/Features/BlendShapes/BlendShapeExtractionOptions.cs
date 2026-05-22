using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using ReaderFbxMatrix = Triturbo.Fbx.FbxMatrix4x4;
using ReaderFbxTransform = Triturbo.Fbx.FbxTransform;
using ReaderUfbxTransform = Triturbo.Fbx.UfbxTransform;
using ReaderVector3d = Triturbo.Fbx.Vector3d;

#if ENABLE_FBX_SDK
using Autodesk.Fbx;
#endif

namespace Triturbo.BlendShare.Features.BlendShapes
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
        public bool ApplyRotation;
        public bool ApplyScale;
        public bool ApplyTranslate;
        public float BlendShapeScale = 1f;
        public bool ApplyTransform => ApplyRotation || ApplyScale || ApplyTranslate;

        /// <inheritdoc />
        public override bool HasSelectedWork(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return Enabled && HasAnySelectedBlendShapeNames(meshes);
        }

        /// <inheritdoc />
        public override bool ShouldExtractMesh(MeshFeatureExtractionMeshRequest mesh)
        {
            return Enabled &&
                   mesh != null &&
                   GetSelectedBlendShapeNames(mesh.Path).Count > 0;
        }

        /// <summary>
        /// Stores the selected blendshape names for one mesh path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        /// <param name="shapeNames">Blendshape names selected for extraction.</param>
        public void SetSelectedBlendShapeNames(
            string path,
            IEnumerable<string> shapeNames)
        {
            selectedBlendShapeNamesByMesh[MeshFeatureExtractionSession.BuildMeshKey(path)] =
                shapeNames?.Where(shapeName => !string.IsNullOrWhiteSpace(shapeName)).Distinct().ToList() ??
                new List<string>();
        }

        /// <summary>
        /// Checks whether this options object has an explicit selection for a mesh path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        /// <returns><c>true</c> when an explicit path-specific selection exists.</returns>
        public bool HasSelectedBlendShapeNames(string path)
        {
            return selectedBlendShapeNamesByMesh.ContainsKey(
                MeshFeatureExtractionSession.BuildMeshKey(path));
        }

        /// <summary>
        /// Gets the selected blendshape names for a mesh path.
        /// </summary>
        /// <param name="path">FBX node path or matching Unity renderer path.</param>
        /// <returns>Path-specific selections, or the global selection list when none exists.</returns>
        public List<string> GetSelectedBlendShapeNames(string path)
        {
            if (selectedBlendShapeNamesByMesh.TryGetValue(
                    MeshFeatureExtractionSession.BuildMeshKey(path),
                    out var meshSpecific))
            {
                return meshSpecific;
            }

            return SelectedBlendShapeNames?
                .Where(shapeName => !string.IsNullOrWhiteSpace(shapeName))
                .Distinct()
                .ToList() ?? new List<string>();
        }

        /// <summary>
        /// Checks whether any requested mesh has at least one selected blendshape.
        /// </summary>
        /// <param name="meshes">Mesh path requests to inspect.</param>
        /// <returns><c>true</c> when at least one mesh has selected blendshapes.</returns>
        public bool HasAnySelectedBlendShapeNames(IEnumerable<MeshFeatureExtractionMeshRequest> meshes)
        {
            return (meshes ?? Enumerable.Empty<MeshFeatureExtractionMeshRequest>())
                .Any(mesh => mesh != null && GetSelectedBlendShapeNames(mesh.Path).Count > 0);
        }

        internal ReaderFbxMatrix GetReaderTransform(ReaderFbxTransform originalTransform, ReaderFbxTransform sourceTransform)
        {
            if (!ApplyTransform)
            {
                return ReaderFbxMatrix.Identity;
            }

            if (ApplyRotation && ApplyScale && ApplyTranslate)
            {
                var originalMatrix = originalTransform.LocalMatrix;
                return originalMatrix.TryInverse(out var originalInverse)
                    ? sourceTransform.LocalMatrix * originalInverse
                    : ReaderFbxMatrix.Identity;
            }

            var translation = ApplyTranslate
                ? sourceTransform.Translation - originalTransform.Translation
                : ReaderVector3d.zero;
            var rotation = ApplyRotation
                ? sourceTransform.Rotation - originalTransform.Rotation
                : ReaderVector3d.zero;
            var scale = ApplyScale
                ? SafeDivide(sourceTransform.Scale, originalTransform.Scale)
                : ReaderVector3d.one;

            return ReaderFbxMatrix.FromTranslationRotationScale(translation, rotation, scale);
        }

        internal ReaderFbxMatrix GetReaderTransform(ReaderUfbxTransform originalTransform, ReaderUfbxTransform sourceTransform)
        {
            if (!ApplyTransform)
            {
                return ReaderFbxMatrix.Identity;
            }

            if (ApplyRotation && ApplyScale && ApplyTranslate)
            {
                var originalMatrix = originalTransform.LocalMatrix;
                return originalMatrix.TryInverse(out var originalInverse)
                    ? sourceTransform.LocalMatrix * originalInverse
                    : ReaderFbxMatrix.Identity;
            }

            var matrix = ReaderFbxMatrix.Identity;
            if (ApplyScale)
            {
                matrix = matrix * ReaderFbxMatrix.Scale(SafeDivide(sourceTransform.Scale, originalTransform.Scale));
            }

            if (ApplyRotation)
            {
                var rotationMatrix =
                    ReaderFbxMatrix.RotateQuaternion(sourceTransform.Rotation) *
                    ReaderFbxMatrix.RotateQuaternion(originalTransform.Rotation.Inverse);
                matrix = matrix * rotationMatrix;
            }

            if (ApplyTranslate)
            {
                matrix = matrix * ReaderFbxMatrix.Translate(sourceTransform.Translation - originalTransform.Translation);
            }

            return matrix;
        }

        private static ReaderVector3d SafeDivide(ReaderVector3d value, ReaderVector3d divisor)
        {
            return new ReaderVector3d(
                System.Math.Abs(divisor.x) > ReaderVector3d.Epsilon ? value.x / divisor.x : 1d,
                System.Math.Abs(divisor.y) > ReaderVector3d.Epsilon ? value.y / divisor.y : 1d,
                System.Math.Abs(divisor.z) > ReaderVector3d.Epsilon ? value.z / divisor.z : 1d);
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
