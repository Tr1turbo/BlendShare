using System;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.Runtime.Assets;
using Triturbo.BlendShare.UI.ValueDrawers;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Ui = Triturbo.BlendShare.Inspector.BlendShareInspectorUi;

namespace Triturbo.BlendShare.UI.Inspectors
{
    [CustomEditor(typeof(BlendShareMeshAsset))]
    public sealed class BlendShareMeshAssetEditor : BlendShareDocumentEditor
    {
        protected override BlendShareAsset Patch => (target as BlendShareMeshAsset)?.Root;

        protected override void Build(VisualElement root, PatchDocument document, Func<MeshDocument, FeatureInspectorContext> context)
        {
            var handle = (BlendShareMeshAsset)target;
            var mesh = document.Meshes.FirstOrDefault(m => m.MeshId == handle.MeshId);
            if (mesh == null)
            {
                root.Add(new HelpBox(Localization.S("patch.mesh_data.missing"), HelpBoxMessageType.Warning));
                return;
            }
            var ctx = context(mesh);
            var fbx = ctx.Buffer?.Settings.DefaultTargetFbx;
            var card = Ui.Box();
            card.Add(Ui.Header(mesh.Name, mesh.NodePath));
            AddCompatibility(card, fbx, mesh);
            string path = FbxNameUtility.NormalizePath(mesh.NodePath);
            var node = !fbx ? null : string.IsNullOrEmpty(path) ? fbx.transform : fbx.transform.Find(path);
            var unityMesh = node?.GetComponent<SkinnedMeshRenderer>()?.sharedMesh ?? node?.GetComponent<MeshFilter>()?.sharedMesh;
            card.Add(Ui.Row(Localization.S("patch.mapping.unity_mesh"), unityMesh ? unityMesh.name : Localization.S("common.none")));
            card.Add(Ui.SmallButton(Localization.S("common.select") + " · " + Localization.S("patch.mesh_data.parent_patch"),
                () => Selection.activeObject = handle.Root));
            root.Add(card);
            AddFeatures(root, mesh, ctx, false);
            var advanced = ctx.Advanced(mesh);
            advanced.Insert(0, new BlendShareRecordView(mesh.Topology, ctx.PayloadSource, Localization.S("patch.mesh_data.topology_fingerprint"), false));
            advanced.Insert(1, new BlendShareRecordView(mesh.SpaceProvenance ?? new MeshSpaceProvenance(), ctx.PayloadSource, "Space Provenance", false));
            advanced.Insert(2, new BlendShareRecordView(mesh.OriginBinding, ctx.PayloadSource, "Origin Binding", false));
            root.Add(advanced);
        }
    }
}
