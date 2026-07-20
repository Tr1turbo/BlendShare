using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShapeShare;
using Triturbo.BlendShare.Core;
using Triturbo.BlendShare.Inspector;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.Features.SkinWeights.Editor
{
    [CustomEditor(typeof(FbxArmatureObject))]
    public sealed class ArmatureObjectEditor : UnityEditor.Editor
    {
        private const float TransformLabelWidth = 82f;
        private const float AxisLabelWidth = 14f;
        private string expandedBonePath;

        /// <inheritdoc />
        public override VisualElement CreateInspectorGUI()
        {
            var root = BlendShareInspectorUi.CreateRoot();
            FbxArmatureObject armature = target as FbxArmatureObject;

            void Rebuild()
            {
                root.Clear();

                if (armature == null)
                {
                    root.Add(new HelpBox(Localization.S("features.skin-weights.armature.missing"), HelpBoxMessageType.Warning));
                    return;
                }

                BlendShareInspectorUi.RegisterDoubleClickAction(root, () =>
                {
                    Selection.activeObject = armature;
                    EditorGUIUtility.PingObject(armature);
                });

                var bones = (armature.Bones ?? Array.Empty<FbxArmatureBoneData>())
                    .Where(bone => bone != null)
                    .ToArray();
                EnsureValidExpandedBone(bones);
                root.Add(CreateBonesHeader(bones.Length));

                if (bones.Length == 0)
                {
                    root.Add(new HelpBox(Localization.S("features.skin-weights.armature.no_bones"), HelpBoxMessageType.Info));
                    return;
                }

                root.Add(CreateBoneTree(bones));
            }

            Rebuild();
            Localization.RebuildOnLanguageChange(root, Rebuild);
            return root;
        }

        private void EnsureValidExpandedBone(IReadOnlyList<FbxArmatureBoneData> bones)
        {
            if (bones.Count == 1)
            {
                expandedBonePath = bones[0].Path;
                return;
            }

            if (!bones.Any(bone => string.Equals(bone.Path, expandedBonePath, StringComparison.Ordinal)))
            {
                expandedBonePath = null;
            }
        }

        private static VisualElement CreateBonesHeader(int boneCount)
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 5;

            var title = new Label(Localization.S("features.skin-weights.bones"));
            BlendShareInspectorUi.StyleHeading(title);
            title.style.flexGrow = 1;
            header.Add(title);

            var count = BlendShareInspectorUi.Badge(boneCount.ToString());
            count.style.marginLeft = 6;
            header.Add(count);
            return header;
        }

        private VisualElement CreateBoneTree(IReadOnlyList<FbxArmatureBoneData> bones)
        {
            var tree = new VisualElement();
            var boneFoldouts = new Dictionary<string, Foldout>(StringComparer.Ordinal);
            bool synchronizingFoldouts = false;

            void RegisterBoneFoldout(Foldout foldout, string path)
            {
                boneFoldouts[path] = foldout;
                foldout.RegisterValueChangedCallback(evt =>
                {
                    if (synchronizingFoldouts)
                    {
                        return;
                    }

                    if (!evt.newValue)
                    {
                        if (string.Equals(expandedBonePath, path, StringComparison.Ordinal))
                        {
                            expandedBonePath = null;
                        }

                        return;
                    }

                    expandedBonePath = path;
                    synchronizingFoldouts = true;
                    foreach (var pair in boneFoldouts)
                    {
                        if (!string.Equals(pair.Key, path, StringComparison.Ordinal))
                        {
                            pair.Value.value = false;
                        }
                    }

                    synchronizingFoldouts = false;
                });
            }

            var rootNode = BuildTree(bones);
            foreach (var child in rootNode.Children)
            {
                tree.Add(CreateTreeElement(child, RegisterBoneFoldout));
            }

            return tree;
        }

        private VisualElement CreateTreeElement(BoneTreeNode node, Action<Foldout, string> registerBoneFoldout)
        {
            if (node.Bone == null)
            {
                var compactedSegments = new List<string> { node.Name };
                var compactedNode = node;
                while (compactedNode.Children.Count == 1 && compactedNode.Children[0].Bone == null)
                {
                    compactedNode = compactedNode.Children[0];
                    compactedSegments.Add(compactedNode.Name);
                }

                var branch = new Foldout
                {
                    text = FormatCompactedPath(compactedSegments),
                    tooltip = compactedNode.Path,
                    value = true
                };
                foreach (var child in compactedNode.Children)
                {
                    branch.Add(CreateTreeElement(child, registerBoneFoldout));
                }

                return branch;
            }

            var container = new VisualElement();
            var boneFoldout = CreateBoneFoldout(node.Bone);
            registerBoneFoldout(boneFoldout, node.Bone.Path);
            container.Add(boneFoldout);

            if (node.Children.Count > 0)
            {
                var children = new VisualElement();
                children.style.marginLeft = 14;
                foreach (var child in node.Children)
                {
                    children.Add(CreateTreeElement(child, registerBoneFoldout));
                }

                container.Add(children);
            }

            return container;
        }

        private Foldout CreateBoneFoldout(FbxArmatureBoneData bone)
        {
            var foldout = new Foldout
            {
                text = bone.Name,
                tooltip = bone.Path,
                value = string.Equals(expandedBonePath, bone.Path, StringComparison.Ordinal)
            };
            foldout.style.marginTop = 1;
            foldout.style.marginBottom = 1;

            var toggle = foldout.Q<Toggle>();
            var title = toggle?.Q<Label>();
            if (title != null)
            {
                title.style.flexGrow = 1;
            }

            if (toggle != null)
            {
                var badge = CreateBoneStatusBadge(bone);
                badge.style.marginLeft = StyleKeyword.Auto;
                badge.style.marginRight = 4;
                toggle.Add(badge);
            }

            var details = new VisualElement();
            details.style.marginTop = 3;
            details.style.marginBottom = 4;
            details.style.paddingLeft = 4;

            var transformTitle = new Label(Localization.S("features.skin-weights.armature.status.transform"));
            BlendShareInspectorUi.StyleStrong(transformTitle);
            transformTitle.style.marginBottom = 2;
            details.Add(transformTitle);
            details.Add(CreateTransformRow(Localization.S("features.skin-weights.armature.position"), bone.LclTranslation.ToVector3()));
            details.Add(CreateTransformRow(Localization.S("features.skin-weights.armature.rotation"), bone.LclRotation.ToVector3()));
            details.Add(CreateTransformRow(Localization.S("features.skin-weights.armature.scale"), bone.LclScaling.ToVector3()));
            foldout.Add(details);
            return foldout;
        }

        private static Label CreateBoneStatusBadge(FbxArmatureBoneData bone)
        {
            string key = bone.m_CreateIfMissing
                ? "features.skin-weights.armature.status.new"
                : "features.skin-weights.armature.status.transform";
            var badge = BlendShareInspectorUi.Badge(
                Localization.S(key),
                bone.m_CreateIfMissing ? StatusKind.Success : StatusKind.Neutral);
            badge.tooltip = Localization.S($"{key}.tooltip");
            return badge;
        }

        private static VisualElement CreateTransformRow(string label, Vector3 value)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 1;
            row.style.marginBottom = 1;
            row.style.flexGrow = 1;
            row.style.flexShrink = 1;
            row.style.minWidth = 0;

            var labelElement = new Label(label);
            labelElement.style.width = TransformLabelWidth;
            labelElement.style.minWidth = TransformLabelWidth;
            labelElement.style.opacity = 0.72f;
            row.Add(labelElement);

            row.Add(CreateAxisField("X", value.x));
            row.Add(CreateAxisField("Y", value.y));
            row.Add(CreateAxisField("Z", value.z));
            return row;
        }

        private static VisualElement CreateAxisField(string axis, float value)
        {
            var group = new VisualElement();
            group.style.flexDirection = FlexDirection.Row;
            group.style.alignItems = Align.Center;
            group.style.marginRight = 6;
            group.style.flexGrow = 1;
            group.style.flexShrink = 1;
            group.style.minWidth = 0;

            var axisLabel = new Label(axis);
            axisLabel.style.width = AxisLabelWidth;
            axisLabel.style.minWidth = AxisLabelWidth;
            axisLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            axisLabel.style.marginRight = 3;
            group.Add(axisLabel);

            var field = new FloatField
            {
                value = NormalizeFloat(value)
            };
            field.labelElement.style.display = DisplayStyle.None;
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 0;
            field.SetEnabled(false);
            group.Add(field);
            return group;
        }

        private static BoneTreeNode BuildTree(IEnumerable<FbxArmatureBoneData> bones)
        {
            var root = new BoneTreeNode(string.Empty, MeshNodePath.Root);
            foreach (var bone in bones.OrderBy(bone => bone.Path, StringComparer.Ordinal))
            {
                var current = root;
                string currentPath = string.Empty;
                foreach (string segment in bone.Path.Split('/'))
                {
                    currentPath = string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}/{segment}";
                    var child = current.Children.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, segment, StringComparison.Ordinal));
                    if (child == null)
                    {
                        child = new BoneTreeNode(segment, currentPath);
                        current.Children.Add(child);
                    }

                    current = child;
                }

                current.Bone = bone;
            }

            SortChildren(root);
            return root;
        }

        private static void SortChildren(BoneTreeNode node)
        {
            node.Children.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            foreach (var child in node.Children)
            {
                SortChildren(child);
            }
        }

        private static string FormatCompactedPath(IReadOnlyList<string> segments)
        {
            return segments.Count <= 3
                ? string.Join(" / ", segments)
                : $"{segments[0]} / {segments[1]} / … / {segments[segments.Count - 1]}";
        }

        private static float NormalizeFloat(float value)
        {
            return Mathf.Abs(value) < 0.0000005f ? 0f : value;
        }

        private sealed class BoneTreeNode
        {
            public string Name { get; }
            public string Path { get; }
            public FbxArmatureBoneData Bone { get; set; }
            public List<BoneTreeNode> Children { get; } = new();

            public BoneTreeNode(string name, string path)
            {
                Name = name;
                Path = path;
            }
        }
    }
}
