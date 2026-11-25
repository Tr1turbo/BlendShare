using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.UI.ValueDrawers;
using Triturbo.BlendShare.Pipeline.Apply;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI.Inspectors
{
    public sealed class SkeletonTreeView : VisualElement
    {
        private const float k_TransformLabelWidth = 82f;
        private const float k_AxisLabelWidth = 14f;
        private string m_ExpandedBonePath;
        private readonly double m_UnitMeters;

        public SkeletonTreeView(SkeletonDocument skeleton, FeatureInspectorContext context)
        {
            var bones = (skeleton?.Bones ?? new List<SkeletonBoneRecord>()).Where(bone => bone != null).ToArray();
            m_UnitMeters = context?.SpaceProvenance?.MetersPerUnit ?? 1d;
            EnsureValidExpandedBone(bones);
            Add(CreateBonesHeader(bones.Length));
            Add(CreateBoneTree(bones));
        }

        private void EnsureValidExpandedBone(IReadOnlyList<SkeletonBoneRecord> bones)
        {
            if (bones.Count == 1)
            {
                m_ExpandedBonePath = bones[0].Path;
                return;
            }

            if (!bones.Any(bone => string.Equals(bone.Path, m_ExpandedBonePath, StringComparison.Ordinal)))
            {
                m_ExpandedBonePath = null;
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

        private VisualElement CreateBoneTree(IReadOnlyList<SkeletonBoneRecord> bones)
        {
            var tree = new VisualElement();
            var boneFoldouts = new Dictionary<string, Foldout>(StringComparer.Ordinal);
            bool synchronizingFoldouts = false;

            void RegisterBoneFoldout(Foldout foldout, string path)
            {
                boneFoldouts[path] = foldout;
                foldout.RegisterValueChangedCallback(evt =>
                {
                    if (evt.target != foldout || synchronizingFoldouts)
                    {
                        return;
                    }

                    if (!evt.newValue)
                    {
                        if (string.Equals(m_ExpandedBonePath, path, StringComparison.Ordinal))
                        {
                            m_ExpandedBonePath = null;
                        }

                        return;
                    }

                    m_ExpandedBonePath = path;
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

        private Foldout CreateBoneFoldout(SkeletonBoneRecord bone)
        {
            var foldout = new Foldout
            {
                text = System.IO.Path.GetFileName(bone.Path),
                tooltip = bone.Path,
                value = string.Equals(m_ExpandedBonePath, bone.Path, StringComparison.Ordinal)
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
                var modeBadge = BlendShareInspectorUi.Badge(
                    bone.CreateIfMissing ? "New" : "Existing",
                    bone.CreateIfMissing ? StatusKind.Success : StatusKind.Neutral);
                modeBadge.style.marginLeft = 6;
                toggle.Add(modeBadge);
            }

            foldout.RegisterValueChangedCallback(evt =>
            {
                if (evt.target != foldout) return;
                foldout.Clear();
                if (evt.newValue) foldout.Add(CreateBoneDetails(bone));
            });
            if (foldout.value) foldout.Add(CreateBoneDetails(bone));
            return foldout;
        }

        private VisualElement CreateBoneDetails(SkeletonBoneRecord bone)
        {
            var details = new VisualElement();
            details.style.marginTop = 3;
            details.style.marginBottom = 4;
            details.style.paddingLeft = 4;

            details.Add(BlendShareInspectorUi.Row("Create If Missing", bone.CreateIfMissing ? "Yes" : "No"));

            var transformTitle = new Label(Localization.S("features.skin-weights.armature.status.transform"));
            BlendShareInspectorUi.StyleStrong(transformTitle);
            transformTitle.style.marginBottom = 2;
            details.Add(transformTitle);

            if (bone.HasRawTransformProperties)
            {
                details.Add(CreateTransformRow(
                    Localization.S("features.skin-weights.armature.position"),
                    new Vector3((float)bone.LclTranslation.x, (float)bone.LclTranslation.y, (float)bone.LclTranslation.z)));
                details.Add(CreateTransformRow(
                    Localization.S("features.skin-weights.armature.rotation"),
                    new Vector3((float)bone.LclRotation.x, (float)bone.LclRotation.y, (float)bone.LclRotation.z)));
                details.Add(CreateTransformRow(
                    Localization.S("features.skin-weights.armature.scale"),
                    new Vector3((float)bone.LclScaling.x, (float)bone.LclScaling.y, (float)bone.LclScaling.z)));
                details.Add(BlendShareInspectorUi.Row("Rotation Order", bone.RotationOrder.ToString()));
                details.Add(BlendShareInspectorUi.Row("Inherit Mode", bone.InheritMode.ToString()));
            }
            else
            {
                var local = bone.LocalBindMatrix.ToUnityColumnMatrix();
                if (ArmatureBindTransformMath.TryDecomposeUnityLocalTransform(local, out var position, out var rotation, out var scale, out var error))
                {
                    details.Add(CreateTransformRow(Localization.S("features.skin-weights.armature.position"), position));
                    details.Add(CreateTransformRow(Localization.S("features.skin-weights.armature.rotation"), rotation.eulerAngles));
                    details.Add(CreateTransformRow(Localization.S("features.skin-weights.armature.scale"), scale));
                }
                else
                {
                    details.Add(new HelpBox(error, HelpBoxMessageType.Info));
                }
            }

            details.Add(new BlendShareRecordView(bone.LocalBindMatrix, title: "Local Bind Matrix", startExpanded: false, positionUnitMeters: m_UnitMeters));
            details.Add(new BlendShareRecordView(bone.GlobalBindMatrix, title: "Global Bind Matrix", startExpanded: false, positionUnitMeters: m_UnitMeters));
            return details;
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
            labelElement.style.width = k_TransformLabelWidth;
            labelElement.style.minWidth = k_TransformLabelWidth;
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
            axisLabel.style.width = k_AxisLabelWidth;
            axisLabel.style.minWidth = k_AxisLabelWidth;
            axisLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            axisLabel.style.marginRight = 3;
            group.Add(axisLabel);

            var field = new FloatField
            {
                value = value
            };
            field.labelElement.style.display = DisplayStyle.None;
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 0;
            field.SetEnabled(false);
            group.Add(field);
            return group;
        }

        private static BoneTreeNode BuildTree(IEnumerable<SkeletonBoneRecord> bones)
        {
            var root = new BoneTreeNode(string.Empty, string.Empty);
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

        private sealed class BoneTreeNode
        {
            public string Name { get; }
            public string Path { get; }
            public SkeletonBoneRecord Bone { get; set; }
            public List<BoneTreeNode> Children { get; } = new();

            public BoneTreeNode(string name, string path)
            {
                Name = name;
                Path = path;
            }
        }
    }
}
