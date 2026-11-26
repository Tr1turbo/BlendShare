using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.Model.Documents;
using Triturbo.BlendShare.UI.ValueDrawers;
using Triturbo.BlendShare.Pipeline.Skeleton;
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
            var bones = (skeleton?.Nodes ?? new List<SkeletonNodeRecord>()).Where(bone => bone != null).ToArray();
            int anchorCount = skeleton?.Anchors?.Count(a => a != null) ?? 0;
            m_UnitMeters = context?.SpaceProvenance?.MetersPerUnit ?? 1d;
            EnsureValidExpandedBone(bones);
            Add(CreateBonesHeader(bones.Length));
            Add(CreateBoneTree(bones));
            if (anchorCount > 0)
            {
                Add(BlendShareInspectorUi.Row("Calibration Anchors", anchorCount.ToString()));
            }
        }

        private void EnsureValidExpandedBone(IReadOnlyList<SkeletonNodeRecord> bones)
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

        private VisualElement CreateBoneTree(IReadOnlyList<SkeletonNodeRecord> bones)
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

        private Foldout CreateBoneFoldout(SkeletonNodeRecord bone)
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
                    bone.Kind == SkeletonNodeKind.Null ? "Null Node" : "Upsert",
                    StatusKind.Neutral);
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

        private VisualElement CreateBoneDetails(SkeletonNodeRecord bone)
        {
            var details = new VisualElement();
            details.style.marginTop = 3;
            details.style.marginBottom = 4;
            details.style.paddingLeft = 4;

            var transformTitle = new Label(Localization.S("features.skin-weights.armature.status.transform"));
            BlendShareInspectorUi.StyleStrong(transformTitle);
            transformTitle.style.marginBottom = 2;
            details.Add(transformTitle);

            if (bone.Raw != null)
            {
                details.Add(CreateTransformRow(
                    Localization.S("features.skin-weights.armature.position"),
                    new Vector3((float)bone.Raw.LclTranslation.x, (float)bone.Raw.LclTranslation.y, (float)bone.Raw.LclTranslation.z)));
                details.Add(CreateTransformRow(
                    Localization.S("features.skin-weights.armature.rotation"),
                    new Vector3((float)bone.Raw.LclRotation.x, (float)bone.Raw.LclRotation.y, (float)bone.Raw.LclRotation.z)));
                details.Add(CreateTransformRow(
                    Localization.S("features.skin-weights.armature.scale"),
                    new Vector3((float)bone.Raw.LclScaling.x, (float)bone.Raw.LclScaling.y, (float)bone.Raw.LclScaling.z)));
                details.Add(BlendShareInspectorUi.Row("Rotation Order", bone.Raw.RotationOrder.ToString()));
                details.Add(BlendShareInspectorUi.Row("Inherit Mode", bone.Raw.InheritMode.ToString()));
            }
            else
            {
                var local = bone.RestLocal.ToUnityColumnMatrix();
                if (SkeletonTransformMath.TryDecomposeUnityLocalTransform(local, out var position, out var rotation, out var scale, out var error))
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

            details.Add(new BlendShareRecordView(bone.RestLocal, title: "Rest Local Matrix", startExpanded: false, positionUnitMeters: m_UnitMeters));
            details.Add(new BlendShareRecordView(bone.RestGlobal, title: "Rest Global Matrix", startExpanded: false, positionUnitMeters: m_UnitMeters));
            if (bone.HasBaseRest)
            {
                details.Add(new BlendShareRecordView(bone.BaseRestGlobal, title: "Pre-Edit Rest Matrix", startExpanded: false, positionUnitMeters: m_UnitMeters));
            }
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

        private static BoneTreeNode BuildTree(IEnumerable<SkeletonNodeRecord> bones)
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
            public SkeletonNodeRecord Bone { get; set; }
            public List<BoneTreeNode> Children { get; } = new();

            public BoneTreeNode(string name, string path)
            {
                Name = name;
                Path = path;
            }
        }
    }
}
