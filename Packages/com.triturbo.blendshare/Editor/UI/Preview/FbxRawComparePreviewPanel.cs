using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.UI;
using Triturbo.BlendShare.Fbx;
using Triturbo.BlendShare.Pipeline.Inspection;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.UI.Preview
{
    public enum SolidPreviewMode
    {
        Transparent = 0,
        OpaqueShaded = 1
    }

    public readonly struct FbxPreviewMeshInfo
    {
        public readonly bool Exists;
        public readonly int ControlPointCount;
        public readonly int FaceCount;
        public readonly string TopologyHash;
        public readonly bool HasValidTopology;

        public FbxPreviewMeshInfo(IFbxMesh mesh, FbxTopologyDigest topology)
        {
            Exists = mesh != null;
            ControlPointCount = mesh?.ControlPointCount ?? 0;
            FaceCount = mesh?.FaceCount ?? 0;
            TopologyHash = topology.Hash ?? string.Empty;
            HasValidTopology = topology.IsValid && !string.IsNullOrEmpty(topology.Hash);
        }
    }

    /// <summary>
    /// Interactive 3D raw-FBX comparison and alignment preview panel.
    /// Renders Source geometry pre-transformed by <see cref="FbxInspectionSession.SourceWorldToOriginRaw"/> (<c>W</c>)
    /// into Origin raw space so that manual or auto-fitted <see cref="MeshFeatureSourceOffset"/> values correspond 1:1
    /// to <c>MeshExtractionRequest.SourceToOriginRawOffset</c> (<c>L = W * Offset</c>).
    /// </summary>
    public sealed class FbxRawComparePreviewPanel : IDisposable
    {
        private const float k_MinPreviewHeight = 30f;
        private const float k_PreviewToolbarHeight = 22f;
        private const float k_PreviewControlsHeight = 24f;
        private const float k_MinimumInlinePreviewWidth = 590f;
        private const float k_MinMeshListHeight = 180f;
        private const float k_MinDistance = 0.01f;
        private const float k_AlignedRelativeTolerance = 0.00001f;
        private const float k_MinAlignedTolerance = 0.000001f;
        private const string k_WireShaderPath = "Packages/com.triturbo.blendshare/Editor/UI/Preview/FbxPreviewWire.shader";

        private static readonly int s_PreviewResizeControlHash = "FbxRawComparePreviewResize".GetHashCode();
        private static readonly int s_PreviewInputControlHash = "FbxRawComparePreviewInput".GetHashCode();
        private static readonly Color s_OriginalCompareColor = new Color(0.2f, 0.55f, 1f, 0.58f);
        private static readonly Color s_SourceCompareColor = new Color(1f, 0.5f, 0.12f, 0.58f);
        private static readonly Color s_OriginalCompareWireColor = new Color(0.25f, 0.8f, 1f, 1f);
        private static readonly Color s_SourceCompareWireColor = new Color(1f, 0.72f, 0.2f, 1f);

        private readonly PreviewSlot m_Original = new PreviewSlot();
        private readonly PreviewSlot m_Source = new PreviewSlot();
        private PreviewRenderUtility m_PreviewUtility;
        private Material m_OriginalMaterial;
        private Material m_SourceMaterial;
        private Material m_OriginalOpaqueMaterial;
        private Material m_SourceOpaqueMaterial;
        private Material m_OriginalCompareMaterial;
        private Material m_SourceCompareMaterial;
        private Material m_DepthMaterial;
        private Material m_OriginalWireMaterial;
        private Material m_SourceWireMaterial;
        private Material m_OriginalCompareWireMaterial;
        private Material m_SourceCompareWireMaterial;
        private FbxInspectionSession m_InspectionSession;
        private int m_CompareMeshIndex;
        private bool m_ShowAllMeshes;
        private bool m_AllControlPointPrefixesCoincident;
        private bool m_ShowOriginal = true;
        private bool m_ShowSource = true;
        private bool m_ShowSolid = true;
        private bool m_ShowWire = true;
        private SolidPreviewMode m_SolidMode = SolidPreviewMode.Transparent;
        private Func<string, MeshFeatureSourceOffset> m_SourceOffsetResolver;
        private MeshFeatureSourceOffset m_SourceOffset;
        private bool m_LockScaleProportions = true;
        private Action m_SourceOffsetChanged;
        private Action m_ReloadRequested;
        private Vector2 m_Orbit = new Vector2(25f, -35f);
        private Vector2 m_Pan;
        private Vector3 m_Pivot;
        private float m_Distance = 2f;
        private float m_PreviewHeight = 360f;
        private bool m_ResizingPreview;
        private string m_ErrorCalculationMessage;
        private MessageType m_ErrorCalculationMessageType = MessageType.Info;
        private string m_AlignmentCachePath;
        private IFbxMesh m_AlignmentCacheOriginalMesh;
        private IFbxMesh m_AlignmentCacheSourceMesh;
        private Vector3 m_AlignmentCachePosition;
        private Vector3 m_AlignmentCacheRotation;
        private Vector3 m_AlignmentCacheScale;
        private bool m_AlignmentCacheResult;
        private bool m_AlignmentCacheValid;
        private IFbxMesh m_AlignmentVertexCacheOriginalMesh;
        private IFbxMesh m_AlignmentVertexCacheSourceMesh;
        private FbxInspectionSession m_AlignmentVertexCacheSession;
        private Vector3[] m_AlignmentOriginalVertices = Array.Empty<Vector3>();
        private Vector3[] m_AlignmentSourceVertices = Array.Empty<Vector3>();
        private float m_AlignmentOriginalMeshSize;

        public void Draw(
            FbxInspectionSession inspectionSession,
            float availableHeight,
            Func<string, MeshFeatureSourceOffset> sourceOffsetResolver = null,
            Action sourceOffsetChanged = null,
            Action reloadRequested = null)
        {
            m_SourceOffsetResolver = sourceOffsetResolver;
            m_SourceOffsetChanged = sourceOffsetChanged;
            m_ReloadRequested = reloadRequested;
            EnsureInspectionSession(inspectionSession);
            UpdateSelectedSourceOffset();
            DrawFixedPreview(availableHeight);
        }

        public void Bind(
            FbxInspectionSession inspectionSession,
            Func<string, MeshFeatureSourceOffset> sourceOffsetResolver = null,
            Action sourceOffsetChanged = null,
            Action reloadRequested = null)
        {
            m_SourceOffsetResolver = sourceOffsetResolver;
            m_SourceOffsetChanged = sourceOffsetChanged;
            m_ReloadRequested = reloadRequested;
            EnsureInspectionSession(inspectionSession);
            UpdateSelectedSourceOffset();
        }

        public void DrawFixedPreview(float availableHeight)
        {
            bool useSecondControlRow = EditorGUIUtility.currentViewWidth < k_MinimumInlinePreviewWidth;
            m_PreviewHeight = Mathf.Clamp(
                m_PreviewHeight,
                k_MinPreviewHeight,
                GetMaxPreviewHeight(availableHeight, useSecondControlRow));
            DrawPreviewToolbar(availableHeight, useSecondControlRow);
            if (useSecondControlRow)
            {
                DrawPreviewControls();
            }

            DrawStatus();
            DrawPreview(m_PreviewHeight);
        }

        public void DrawSourceOffsetEditor()
        {
            DrawSourceOffsetControls();
            DrawErrorCalculationControls();
        }

        public void AutoAlignAll()
        {
            int alignedMeshCount = 0;
            int totalVertexCount = 0;
            var sourcePreTransform = GetSourcePreTransform();
            foreach (string path in GetComparablePaths())
            {
                var compatibility = GetTopologyCompatibility(path);
                if (!CanCompareOrderedControlPoints(compatibility))
                {
                    continue;
                }

                var originalMesh = m_Original.FindMesh(path);
                var sourceMesh = m_Source.FindMesh(path);
                var originalMeshVertices = originalMesh?.GetControlPoints();
                var sourceMeshVertices = sourceMesh?.GetControlPoints();
                int count = compatibility.OriginControlPointCount;
                if (count <= 0 || originalMeshVertices == null || sourceMeshVertices == null ||
                    originalMeshVertices.Length < count || sourceMeshVertices.Length < count)
                {
                    continue;
                }

                var originalVertices = new Vector3[count];
                var sourceVertices = new Vector3[count];
                bool applyPre = sourcePreTransform.IsValid && !sourcePreTransform.IsIdentity;
                for (int i = 0; i < count; i++)
                {
                    originalVertices[i] = originalMeshVertices[i].ToVector3();
                    sourceVertices[i] = applyPre
                        ? sourcePreTransform.TransformPoint(sourceMeshVertices[i]).ToVector3()
                        : sourceMeshVertices[i].ToVector3();
                }

                if (!TryFindAutoOffset(
                        originalVertices,
                        sourceVertices,
                        count,
                        out AutoOffsetResult best))
                {
                    continue;
                }

                SetSourceOffset(GetSourceOffset(path), best);
                alignedMeshCount++;
                totalVertexCount += count;
            }

            if (alignedMeshCount == 0)
            {
                SetErrorCalculationMessage(
                    Localization.S("patch_creator.preview.auto_align_all_failed"),
                    MessageType.Warning);
                return;
            }

            UpdateSelectedSourceOffset();
            m_AlignmentCacheValid = false;
            if (m_ShowAllMeshes)
            {
                RebuildPreviewMeshes(false);
            }

            m_SourceOffsetChanged?.Invoke();
            SetErrorCalculationMessage(
                Localization.SF(
                    "patch_creator.preview.auto_align_all_applied",
                    alignedMeshCount,
                    totalVertexCount),
                MessageType.Info);
        }

        public void ResetAllOffsets()
        {
            foreach (string path in GetComparablePaths())
            {
                GetSourceOffset(path).Reset();
            }

            UpdateSelectedSourceOffset();
            m_AlignmentCacheValid = false;
            if (m_ShowAllMeshes)
            {
                RebuildPreviewMeshes(false);
            }

            ClearErrorCalculation();
            m_SourceOffsetChanged?.Invoke();
        }

        public string[] GetMeshPaths()
        {
            return GetComparablePaths();
        }

        public bool IsShownPath(string path)
        {
            return !m_ShowAllMeshes &&
                   string.Equals(GetSelectedComparePath(), path, StringComparison.Ordinal);
        }

        public bool IsShowingAll()
        {
            return m_ShowAllMeshes || GetComparablePaths().Length == 1;
        }

        public void ShowPath(string path, bool frame)
        {
            var paths = GetComparablePaths();
            int index = Array.IndexOf(paths, path);
            if (index < 0)
            {
                return;
            }

            if (!m_ShowAllMeshes && index == m_CompareMeshIndex)
            {
                if (frame)
                {
                    FramePreview();
                }

                return;
            }

            m_ShowAllMeshes = false;
            m_CompareMeshIndex = index;
            UpdateSelectedSourceOffset();
            RebuildPreviewMeshes(frame);
        }

        public void ShowAll(bool frame)
        {
            string[] paths = GetComparablePaths();
            if (paths.Length == 1)
            {
                ShowPath(paths[0], frame);
                return;
            }

            if (paths.Length == 0 || m_ShowAllMeshes)
            {
                return;
            }

            m_ShowAllMeshes = true;
            RebuildPreviewMeshes(frame);
        }

        public FbxPreviewMeshInfo GetOriginalMeshInfo(string path)
        {
            return new FbxPreviewMeshInfo(
                m_Original.FindMesh(path),
                GetTopologyCompatibility(path).OriginDigest);
        }

        public FbxPreviewMeshInfo GetSourceMeshInfo(string path)
        {
            return new FbxPreviewMeshInfo(m_Source.FindMesh(path), FbxTopologyDigest.Invalid);
        }

        public FbxTopologyCompatibilityResult GetTopologyCompatibility(string path)
        {
            return m_InspectionSession?.GetTopologyCompatibility(path) ?? default;
        }

        private static float GetMaxPreviewHeight(float availableHeight, bool useSecondControlRow)
        {
            float controlsHeight = useSecondControlRow ? k_PreviewControlsHeight : 0f;
            return Mathf.Max(
                k_MinPreviewHeight,
                availableHeight - k_MinMeshListHeight - k_PreviewToolbarHeight - controlsHeight);
        }

        public void Dispose()
        {
            m_Original.Dispose();
            m_Source.Dispose();

            if (m_PreviewUtility != null)
            {
                m_PreviewUtility.Cleanup();
                m_PreviewUtility = null;
            }

            DestroyMaterial(ref m_OriginalMaterial);
            DestroyMaterial(ref m_SourceMaterial);
            DestroyMaterial(ref m_OriginalOpaqueMaterial);
            DestroyMaterial(ref m_SourceOpaqueMaterial);
            DestroyMaterial(ref m_OriginalCompareMaterial);
            DestroyMaterial(ref m_SourceCompareMaterial);
            DestroyMaterial(ref m_DepthMaterial);
            DestroyMaterial(ref m_OriginalWireMaterial);
            DestroyMaterial(ref m_SourceWireMaterial);
            DestroyMaterial(ref m_OriginalCompareWireMaterial);
            DestroyMaterial(ref m_SourceCompareWireMaterial);
        }

        private FbxAffineSpaceTransform GetSourcePreTransform()
        {
            return m_InspectionSession != null
                ? m_InspectionSession.SourceWorldToOriginRaw
                : FbxAffineSpaceTransform.Identity;
        }

        private void EnsureInspectionSession(FbxInspectionSession session)
        {
            if (ReferenceEquals(m_InspectionSession, session))
            {
                return;
            }

            m_InspectionSession = session;
            m_AlignmentCacheValid = false;
            m_AlignmentVertexCacheSession = null;
            m_Original.Bind(session?.Origin);
            m_Source.Bind(session?.Source);
            RebuildPreviewMeshes(true);
        }

        private void DrawPreviewToolbar(float availableHeight, bool useSecondControlRow)
        {
            Rect toolbarRect = GUILayoutUtility.GetRect(
                1f,
                EditorGUIUtility.currentViewWidth,
                k_PreviewToolbarHeight,
                k_PreviewToolbarHeight,
                EditorStyles.toolbar);
            GUI.Box(toolbarRect, GUIContent.none, EditorStyles.toolbar);

            float xMax = toolbarRect.xMax - 2f;
            Rect TakeRight(float width)
            {
                xMax -= width;
                var rect = new Rect(xMax, toolbarRect.y, width, toolbarRect.height);
                xMax -= 2f;
                return rect;
            }

            var wireRect = TakeRight(86f);
            m_ShowWire = GUI.Toggle(
                wireRect,
                m_ShowWire,
                Localization.S("patch_creator.preview.wireframe"),
                EditorStyles.toolbarButton);

            var solidRect = TakeRight(112f);
            m_SolidMode = DrawSolidModePopup(solidRect, m_SolidMode);
            m_ShowSolid = true;

            if (!useSecondControlRow)
            {
                DrawPreviewActionControls(TakeRight, true);
            }

            var handleRect = new Rect(
                toolbarRect.x + 6f,
                toolbarRect.y,
                Mathf.Max(24f, xMax - toolbarRect.x - 10f),
                toolbarRect.height);
            DrawPreviewDragHandle(handleRect, availableHeight);
        }

        private void DrawPreviewDragHandle(Rect rect, float availableHeight)
        {
            string label = GetPreviewToolbarLabel();
            var labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 4, 0, 0)
            };
            float maximumLabelWidth = Mathf.Max(0f, rect.width - 28f);
            float labelWidth = Mathf.Min(maximumLabelWidth, labelStyle.CalcSize(new GUIContent(label)).x + 4f);
            GUI.Label(new Rect(rect.x, rect.y, labelWidth, rect.height), label, labelStyle);

            float lineX = rect.x + labelWidth + 4f;
            float lineWidth = rect.xMax - lineX - 2f;
            if (lineWidth > 0f)
            {
                bool hovered = rect.Contains(Event.current.mousePosition);
                Color gripColor;
                if (m_ResizingPreview)
                {
                    gripColor = EditorGUIUtility.isProSkin
                        ? new Color(0.56f, 0.56f, 0.56f)
                        : new Color(0.40f, 0.40f, 0.40f);
                }
                else if (hovered)
                {
                    gripColor = EditorGUIUtility.isProSkin
                        ? new Color(0.46f, 0.46f, 0.46f)
                        : new Color(0.50f, 0.50f, 0.50f);
                }
                else
                {
                    gripColor = EditorGUIUtility.isProSkin
                        ? new Color(0.34f, 0.34f, 0.34f)
                        : new Color(0.58f, 0.58f, 0.58f);
                }

                float centerY = Mathf.Round(rect.center.y);
                EditorGUI.DrawRect(new Rect(lineX, centerY - 2f, lineWidth, 1f), gripColor);
                EditorGUI.DrawRect(new Rect(lineX, centerY + 2f, lineWidth, 1f), gripColor);
            }

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);
            HandleBottomPreviewResize(rect, availableHeight);
        }

        private void DrawPreviewControls()
        {
            Rect rowRect = GUILayoutUtility.GetRect(
                1f,
                EditorGUIUtility.currentViewWidth,
                k_PreviewControlsHeight,
                k_PreviewControlsHeight,
                EditorStyles.toolbar);
            GUI.Box(rowRect, GUIContent.none, EditorStyles.toolbar);

            const float controlsWidth = 70f + 2f + 66f + 2f + 52f + 2f + 58f;

            float x = rowRect.width >= controlsWidth + 120f
                ? rowRect.xMax - controlsWidth - 2f
                : rowRect.x + 2f;
            Rect Take(float width)
            {
                var rect = new Rect(x, rowRect.y + 1f, width, rowRect.height - 2f);
                x += width + 2f;
                return rect;
            }

            DrawPreviewActionControls(Take);
        }

        private void DrawPreviewActionControls(Func<float, Rect> takeRect, bool takeFromRight = false)
        {
            Rect originalRect;
            Rect sourceRect;
            Rect frameRect;
            Rect reloadRect;
            if (takeFromRight)
            {
                reloadRect = takeRect(58f);
                frameRect = takeRect(52f);
                sourceRect = takeRect(66f);
                originalRect = takeRect(70f);
            }
            else
            {
                originalRect = takeRect(70f);
                sourceRect = takeRect(66f);
                frameRect = takeRect(52f);
                reloadRect = takeRect(58f);
            }

            m_ShowOriginal = GUI.Toggle(
                originalRect,
                m_ShowOriginal,
                Localization.S("patch_creator.alignment.original"),
                EditorStyles.toolbarButton);
            m_ShowSource = GUI.Toggle(
                sourceRect,
                m_ShowSource,
                Localization.S("patch_creator.alignment.source"),
                EditorStyles.toolbarButton);

            using (new EditorGUI.DisabledScope(GetFrameBounds().size == Vector3.zero))
            {
                if (GUI.Button(
                        frameRect,
                        Localization.S("patch_creator.preview.frame"),
                        EditorStyles.toolbarButton))
                {
                    FramePreview();
                }
            }

            if (GUI.Button(
                    reloadRect,
                    Localization.S("patch_creator.preview.reload"),
                    EditorStyles.toolbarButton))
            {
                m_ReloadRequested?.Invoke();
            }
        }

        private void HandleBottomPreviewResize(Rect resizeRect, float availableHeight)
        {
            Event evt = Event.current;
            int controlId = GUIUtility.GetControlID(s_PreviewResizeControlHash, FocusType.Passive, resizeRect);
            switch (evt.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when evt.button == 0 && resizeRect.Contains(evt.mousePosition):
                    GUIUtility.hotControl = controlId;
                    m_ResizingPreview = true;
                    evt.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == controlId:
                    m_PreviewHeight = Mathf.Clamp(
                        m_PreviewHeight - evt.delta.y,
                        k_MinPreviewHeight,
                        GetMaxPreviewHeight(
                            availableHeight,
                            EditorGUIUtility.currentViewWidth < k_MinimumInlinePreviewWidth));
                    evt.Use();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == controlId:
                    GUIUtility.hotControl = 0;
                    m_ResizingPreview = false;
                    evt.Use();
                    break;
            }
        }

        private string GetPreviewToolbarLabel()
        {
            if (m_ShowAllMeshes)
            {
                return Localization.S("patch_creator.preview.all_meshes");
            }

            string path = GetSelectedComparePath();
            if (string.IsNullOrEmpty(path))
            {
                return Localization.S("patch_creator.preview.raw_fbx_preview");
            }

            int separatorIndex = path.LastIndexOf('/');
            return separatorIndex >= 0 && separatorIndex < path.Length - 1
                ? path.Substring(separatorIndex + 1)
                : path;
        }

        private static string FormatMeshSummary(IFbxMesh mesh)
        {
            return mesh == null
                ? Localization.S("patch_creator.preview.missing")
                : Localization.SF(
                    "patch_creator.preview.mesh_summary",
                    mesh.ControlPointCount,
                    mesh.FaceCount);
        }

        private static string FormatMeshSummary(PreviewSlot slot, IEnumerable<string> paths)
        {
            var meshes = (paths ?? Enumerable.Empty<string>())
                .Select(slot.FindMesh)
                .Where(mesh => mesh != null)
                .ToArray();
            return meshes.Length == 0
                ? Localization.S("patch_creator.preview.missing")
                : Localization.SF(
                    "patch_creator.preview.mesh_summary",
                    meshes.Sum(mesh => mesh.ControlPointCount),
                    meshes.Sum(mesh => mesh.FaceCount));
        }

        private static SolidPreviewMode DrawSolidModePopup(Rect rect, SolidPreviewMode current)
        {
            var values = new[] { SolidPreviewMode.Transparent, SolidPreviewMode.OpaqueShaded };
            string[] labels =
            {
                Localization.S("patch_creator.preview.transparent"),
                Localization.S("patch_creator.preview.shaded")
            };
            int index = Array.IndexOf(values, current);
            int next = EditorGUI.Popup(rect, Mathf.Max(0, index), labels, EditorStyles.toolbarPopup);
            return next >= 0 && next < values.Length ? values[next] : current;
        }

        private void DrawSourceOffsetControls()
        {
            if (m_SourceOffset == null)
            {
                UpdateSelectedSourceOffset();
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                Localization.S("patch_creator.preview.source_extraction_offset"),
                EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            m_SourceOffset.Position = DrawTransformVectorRow(
                Localization.S("patch_creator.preview.position"),
                m_SourceOffset.Position,
                false);
            m_SourceOffset.Rotation = DrawTransformVectorRow(
                Localization.S("patch_creator.preview.rotation"),
                m_SourceOffset.Rotation,
                false);
            Vector3 previousScale = m_SourceOffset.Scale;
            Vector3 nextScale = DrawTransformVectorRow(
                Localization.S("patch_creator.preview.scale"),
                m_SourceOffset.Scale,
                true);
            m_SourceOffset.Scale = m_LockScaleProportions
                ? ApplyProportionalScale(previousScale, nextScale)
                : nextScale;
            if (EditorGUI.EndChangeCheck())
            {
                NotifySourceOffsetChanged();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(
                        Localization.S("patch_creator.preview.reset_offset"),
                        GUILayout.Width(110f)))
                {
                    m_SourceOffset.Reset();
                    NotifySourceOffsetChanged();
                }
            }
        }

        private Vector3 DrawTransformVectorRow(string label, Vector3 value, bool showScaleLock)
        {
            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float lockWidth = 22f;
            float labelWidth = Mathf.Min(120f, Mathf.Max(72f, row.width * 0.32f));
            var labelRect = new Rect(row.x, row.y, labelWidth - lockWidth, row.height);
            var lockRect = new Rect(labelRect.xMax, row.y, lockWidth, row.height);
            var fieldRect = new Rect(lockRect.xMax + 4f, row.y, row.width - labelWidth - 4f, row.height);

            EditorGUI.LabelField(labelRect, label, showScaleLock ? EditorStyles.label : EditorStyles.boldLabel);
            if (showScaleLock)
            {
                string iconName = m_LockScaleProportions ? "Linked" : "Unlinked";
                if (EditorGUIUtility.isProSkin)
                {
                    iconName = "d_" + iconName;
                }

                GUIContent sourceIcon = EditorGUIUtility.IconContent(iconName);
                var icon = new GUIContent(
                    sourceIcon.image,
                    Localization.S(m_LockScaleProportions
                        ? "patch_creator.preview.scale_linked_tooltip"
                        : "patch_creator.preview.scale_unlinked_tooltip"));
                var iconStyle = new GUIStyle(GUIStyle.none)
                {
                    alignment = TextAnchor.MiddleCenter,
                    imagePosition = ImagePosition.ImageOnly,
                    padding = new RectOffset(1, 1, 1, 1)
                };
                EditorGUIUtility.AddCursorRect(lockRect, MouseCursor.Link);
                m_LockScaleProportions = GUI.Toggle(lockRect, m_LockScaleProportions, icon, iconStyle);
            }

            float labelWidthBefore = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 13f;
            EditorGUI.BeginChangeCheck();
            float x = EditorGUI.FloatField(new Rect(fieldRect.x, fieldRect.y, fieldRect.width / 3f - 4f, fieldRect.height), "X", value.x);
            float y = EditorGUI.FloatField(new Rect(fieldRect.x + fieldRect.width / 3f, fieldRect.y, fieldRect.width / 3f - 4f, fieldRect.height), "Y", value.y);
            float z = EditorGUI.FloatField(new Rect(fieldRect.x + fieldRect.width * 2f / 3f, fieldRect.y, fieldRect.width / 3f, fieldRect.height), "Z", value.z);
            EditorGUIUtility.labelWidth = labelWidthBefore;

            return EditorGUI.EndChangeCheck()
                ? new Vector3(x, y, z)
                : value;
        }

        private static Vector3 ApplyProportionalScale(Vector3 previous, Vector3 next)
        {
            int changedAxis = GetChangedScaleAxis(previous, next);
            if (changedAxis < 0)
            {
                return next;
            }

            float oldValue = previous[changedAxis];
            float newValue = next[changedAxis];
            if (Mathf.Approximately(oldValue, 0f))
            {
                return Vector3.one * newValue;
            }

            float ratio = newValue / oldValue;
            return previous * ratio;
        }

        private static int GetChangedScaleAxis(Vector3 previous, Vector3 next)
        {
            for (int i = 0; i < 3; i++)
            {
                if (!Mathf.Approximately(previous[i], next[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private void DrawErrorCalculationControls()
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(m_Original.Mesh == null || m_Source.Mesh == null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(
                            Localization.S("patch_creator.preview.calculate_error"),
                            GUILayout.Width(130f)))
                    {
                        CalculateError();
                    }

                    if (GUILayout.Button(
                            Localization.S("patch_creator.preview.auto_offset"),
                            GUILayout.Width(110f)))
                    {
                        AutoOffset();
                    }
                }
            }

            if (!string.IsNullOrEmpty(m_ErrorCalculationMessage))
            {
                EditorGUILayout.HelpBox(m_ErrorCalculationMessage, m_ErrorCalculationMessageType);
            }
        }

        private void CalculateError()
        {
            if (!TryGetComparablePreviewVertices(
                    out Vector3[] originalVertices,
                    out Vector3[] sourceVertices,
                    out int compareVertexCount,
                    out string compareNote,
                    out string validationMessage))
            {
                SetErrorCalculationMessage(validationMessage, MessageType.Warning);
                return;
            }

            Matrix4x4 sourceOffsetMatrix = GetSourceOffsetMatrix();
            ErrorMetric error = CalculateError(originalVertices, sourceVertices, compareVertexCount, sourceOffsetMatrix);
            float boundsSize = CalculateBoundsSize(originalVertices, sourceVertices, compareVertexCount, sourceOffsetMatrix);
            SetErrorCalculationMessage(
                FormatErrorMessage(
                    Localization.SF(
                        "patch_creator.preview.index_aligned_error",
                        compareVertexCount,
                        compareNote),
                    error,
                    boundsSize),
                MessageType.Info);
        }

        private void AutoOffset()
        {
            if (!TryGetComparablePreviewVertices(
                    out Vector3[] originalVertices,
                    out Vector3[] sourceVertices,
                    out int compareVertexCount,
                    out string compareNote,
                    out string validationMessage))
            {
                SetErrorCalculationMessage(validationMessage, MessageType.Warning);
                return;
            }

            if (!TryFindAutoOffset(
                    originalVertices,
                    sourceVertices,
                    compareVertexCount,
                    out AutoOffsetResult best))
            {
                SetErrorCalculationMessage(
                    Localization.S("patch_creator.preview.auto_offset_failed"),
                    MessageType.Warning);
                return;
            }

            ApplyAutoOffset(
                best,
                originalVertices,
                sourceVertices,
                compareVertexCount,
                Localization.SF(
                    "patch_creator.preview.auto_offset_applied",
                    compareVertexCount,
                    compareNote,
                    best.Scale.ToString("0.########")));
        }

        private static bool TryFindAutoOffset(
            Vector3[] originalVertices,
            Vector3[] sourceVertices,
            int compareVertexCount,
            out AutoOffsetResult best)
        {
            best = default;
            bool found = false;
            foreach (Quaternion rotation in GetAxisAlignedRotations())
            {
                if (!TryFitUniformScaleAndTranslation(
                        originalVertices,
                        sourceVertices,
                        compareVertexCount,
                        rotation,
                        out float scale,
                        out Vector3 translation))
                {
                    continue;
                }

                var matrix = Matrix4x4.TRS(translation, rotation, Vector3.one * scale);
                ErrorMetric error = CalculateError(originalVertices, sourceVertices, compareVertexCount, matrix);
                if (!found ||
                    error.Rms < best.Error.Rms ||
                    (Math.Abs(error.Rms - best.Error.Rms) < 0.000000001d && error.Max < best.Error.Max))
                {
                    best = new AutoOffsetResult(rotation, scale, translation, error);
                    found = true;
                }
            }

            return found;
        }

        private void ApplyAutoOffset(
            AutoOffsetResult best,
            Vector3[] originalVertices,
            Vector3[] sourceVertices,
            int compareVertexCount,
            string messagePrefix)
        {
            SetSourceOffset(m_SourceOffset, best);
            m_AlignmentCacheValid = false;
            m_SourceOffsetChanged?.Invoke();

            Matrix4x4 bestMatrix = GetSourceOffsetMatrix();
            float boundsSize = CalculateBoundsSize(originalVertices, sourceVertices, compareVertexCount, bestMatrix);
            SetErrorCalculationMessage(
                FormatErrorMessage(messagePrefix, best.Error, boundsSize),
                MessageType.Info);
        }

        private static void SetSourceOffset(MeshFeatureSourceOffset target, AutoOffsetResult best)
        {
            if (target == null)
            {
                return;
            }

            target.Position = best.Translation;
            target.Rotation = NormalizeEuler(best.Rotation.eulerAngles);
            target.Scale = Vector3.one * best.Scale;
        }

        private bool TryGetComparablePreviewVertices(
            out Vector3[] originalVertices,
            out Vector3[] sourceVertices,
            out int compareVertexCount,
            out string compareNote,
            out string message)
        {
            originalVertices = null;
            sourceVertices = null;
            compareVertexCount = 0;
            compareNote = string.Empty;

            string path = GetSelectedComparePath();
            var originalMesh = m_Original.FindMesh(path);
            var sourceMesh = m_Source.FindMesh(path);
            var compatibility = GetTopologyCompatibility(path);
            if (!CanCompareOrderedControlPoints(compatibility))
            {
                message = compatibility.State == FbxTopologyCompatibilityState.SourceHasFewerControlPoints
                    ? Localization.SF(
                        "patch_creator.alignment.source_fewer_control_points",
                        compatibility.SourceControlPointCount,
                        compatibility.OriginControlPointCount)
                    : Localization.S("patch_creator.preview.alignment_requires_compatible_topology");
                return false;
            }

            var originalRawVertices = originalMesh?.GetControlPoints();
            var sourceRawVertices = sourceMesh?.GetControlPoints();
            if (originalRawVertices == null || sourceRawVertices == null ||
                originalRawVertices.Length < compatibility.OriginControlPointCount ||
                sourceRawVertices.Length < compatibility.OriginControlPointCount)
            {
                message = Localization.S("patch_creator.preview.no_vertices");
                return false;
            }

            var preTransform = GetSourcePreTransform();
            bool applyPre = preTransform.IsValid && !preTransform.IsIdentity;

            originalVertices = new Vector3[compatibility.OriginControlPointCount];
            sourceVertices = new Vector3[compatibility.OriginControlPointCount];
            for (int i = 0; i < compatibility.OriginControlPointCount; i++)
            {
                originalVertices[i] = originalRawVertices[i].ToVector3();
                sourceVertices[i] = applyPre
                    ? preTransform.TransformPoint(sourceRawVertices[i]).ToVector3()
                    : sourceRawVertices[i].ToVector3();
            }

            if (originalVertices.Length == 0 || sourceVertices.Length == 0)
            {
                message = Localization.S("patch_creator.preview.no_vertices");
                return false;
            }

            compareVertexCount = compatibility.OriginControlPointCount;
            if (compatibility.HasExtraSourceControlPoints)
            {
                compareNote = Localization.SF(
                    "patch_creator.preview.extra_control_points_note",
                    compatibility.SourceControlPointCount - compatibility.OriginControlPointCount);
            }

            message = null;
            return true;
        }

        private static bool CanCompareOrderedControlPoints(FbxTopologyCompatibilityResult compatibility)
        {
            return compatibility.IsCompatible ||
                   compatibility.State == FbxTopologyCompatibilityState.TopologyMismatch;
        }

        private static ErrorMetric CalculateError(Vector3[] originalVertices, Vector3[] sourceVertices, int count, Matrix4x4 sourceOffset)
        {
            double sumSqr = 0d;
            float max = 0f;
            int maxIndex = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 sourcePosition = sourceOffset.MultiplyPoint3x4(sourceVertices[i]);
                float distanceToOriginal = Vector3.Distance(originalVertices[i], sourcePosition);
                sumSqr += distanceToOriginal * distanceToOriginal;
                if (distanceToOriginal > max)
                {
                    max = distanceToOriginal;
                    maxIndex = i;
                }
            }

            double rms = Math.Sqrt(sumSqr / count);
            return new ErrorMetric(rms, max, maxIndex);
        }

        private static string FormatErrorMessage(string prefix, ErrorMetric error, float boundsSize)
        {
            double relativeRms = error.Rms / boundsSize;
            double relativeMax = error.Max / boundsSize;
            return Localization.SF(
                "patch_creator.preview.error_metrics",
                prefix,
                error.Rms.ToString("0.########"),
                error.Max.ToString("0.########"),
                error.MaxIndex,
                relativeRms.ToString("0.########"),
                relativeMax.ToString("0.########"));
        }

        private static float CalculateBoundsSize(Vector3[] originalVertices, Vector3[] sourceVertices, int count, Matrix4x4 sourceOffset)
        {
            var bounds = new Bounds(originalVertices[0], Vector3.zero);
            for (int i = 0; i < count; i++)
            {
                bounds.Encapsulate(originalVertices[i]);
                bounds.Encapsulate(sourceOffset.MultiplyPoint3x4(sourceVertices[i]));
            }

            return Mathf.Max(bounds.extents.magnitude, k_MinDistance);
        }

        private static bool TryFitUniformScaleAndTranslation(
            Vector3[] originalVertices,
            Vector3[] sourceVertices,
            int count,
            Quaternion rotation,
            out float scale,
            out Vector3 translation)
        {
            scale = 1f;
            translation = Vector3.zero;

            Vector3 originalMean = CalculateMean(originalVertices, count);
            Vector3 sourceMean = CalculateMean(sourceVertices, count);
            double numerator = 0d;
            double denominator = 0d;
            for (int i = 0; i < count; i++)
            {
                Vector3 rotatedSourceDelta = rotation * (sourceVertices[i] - sourceMean);
                Vector3 originalDelta = originalVertices[i] - originalMean;
                numerator += Vector3.Dot(rotatedSourceDelta, originalDelta);
                denominator += Vector3.Dot(rotatedSourceDelta, rotatedSourceDelta);
            }

            if (denominator <= 0.000000000001d)
            {
                return false;
            }

            scale = (float)(numerator / denominator);
            if (!IsFinite(scale) || scale <= 0f)
            {
                return false;
            }

            translation = originalMean - rotation * (sourceMean * scale);
            return IsFinite(translation);
        }

        private static Vector3 CalculateMean(Vector3[] vertices, int count)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                sum += vertices[i];
            }

            return sum / count;
        }

        private static Quaternion[] GetAxisAlignedRotations()
        {
            Vector3[] axes =
            {
                Vector3.right,
                Vector3.left,
                Vector3.up,
                Vector3.down,
                Vector3.forward,
                Vector3.back
            };
            var rotations = new List<Quaternion>(24);
            foreach (Vector3 forward in axes)
            {
                foreach (Vector3 up in axes)
                {
                    if (!Mathf.Approximately(Vector3.Dot(forward, up), 0f))
                    {
                        continue;
                    }

                    Quaternion rotation = Quaternion.LookRotation(forward, up);
                    if (!ContainsRotation(rotations, rotation))
                    {
                        rotations.Add(rotation);
                    }
                }
            }

            return rotations.ToArray();
        }

        private static bool ContainsRotation(List<Quaternion> rotations, Quaternion candidate)
        {
            foreach (Quaternion rotation in rotations)
            {
                if (Mathf.Abs(Quaternion.Dot(rotation, candidate)) > 0.9999f)
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector3 NormalizeEuler(Vector3 euler)
        {
            return new Vector3(NormalizeEuler(euler.x), NormalizeEuler(euler.y), NormalizeEuler(euler.z));
        }

        private static float NormalizeEuler(float value)
        {
            value %= 360f;
            if (value > 180f)
            {
                value -= 360f;
            }

            return Mathf.Abs(value) < 0.0001f ? 0f : value;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void SetErrorCalculationMessage(string message, MessageType messageType)
        {
            m_ErrorCalculationMessage = message;
            m_ErrorCalculationMessageType = messageType;
        }

        private void ClearErrorCalculation()
        {
            m_ErrorCalculationMessage = null;
            m_ErrorCalculationMessageType = MessageType.Info;
        }

        private void DrawStatus()
        {
            DrawSlotStatus(m_Original, Localization.S("patch_creator.alignment.original"));
            DrawSlotStatus(m_Source, Localization.S("patch_creator.alignment.source"));

            if (m_Original.HasMeshes && m_Source.HasMeshes && GetComparablePaths().Length == 0)
            {
                EditorGUILayout.HelpBox(
                    Localization.S("patch_creator.preview.no_shared_meshes"),
                    MessageType.Warning);
            }
        }

        private static void DrawSlotStatus(PreviewSlot slot, string label)
        {
            if (!string.IsNullOrEmpty(slot.ReadMessage) && !slot.HasMeshes)
            {
                EditorGUILayout.HelpBox($"{label}: {slot.ReadMessage}", MessageType.Warning);
                return;
            }

            string buildMessage = GetBuildStatusMessage(slot.BuildResult.Status);
            if (!string.IsNullOrEmpty(buildMessage))
            {
                EditorGUILayout.HelpBox($"{label}: {buildMessage}", MessageType.Warning);
            }
        }

        private void DrawPreview(float previewHeight)
        {
            Rect rect = GUILayoutUtility.GetRect(1f, EditorGUIUtility.currentViewWidth, k_MinPreviewHeight, previewHeight);
            EditorGUI.DrawRect(rect, new Color(0.08f, 0.08f, 0.08f));
            Rect previewRect = new Rect(rect.x + 1f, rect.y + 1f, Mathf.Max(1f, rect.width - 2f), Mathf.Max(1f, rect.height - 2f));
            EditorGUI.DrawRect(previewRect, new Color(0.13f, 0.13f, 0.13f));
            HandlePreviewInput(rect);

            if (!HasAnyPreviewMesh())
            {
                var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 13
                };
                GUI.Label(previewRect, Localization.S("patch_creator.preview.no_preview_mesh"), style);
                return;
            }

            EnsurePreviewUtility();
            m_PreviewUtility.BeginPreview(previewRect, GUIStyle.none);
            m_PreviewUtility.camera.clearFlags = CameraClearFlags.Color;
            m_PreviewUtility.camera.backgroundColor = new Color(0.13f, 0.13f, 0.13f, 1f);
            m_PreviewUtility.lights[0].intensity = 1.15f;
            m_PreviewUtility.lights[0].transform.rotation = Quaternion.Euler(35f, 35f, 0f);
            m_PreviewUtility.lights[1].intensity = 0.55f;

            Quaternion rotation = Quaternion.Euler(m_Orbit.x, m_Orbit.y, 0f);
            Vector3 cameraPivot = m_Pivot + rotation * new Vector3(m_Pan.x, m_Pan.y, 0f);
            m_PreviewUtility.camera.transform.position = cameraPivot + rotation * (Vector3.back * Mathf.Max(m_Distance, k_MinDistance));
            m_PreviewUtility.camera.transform.rotation = rotation;
            m_PreviewUtility.camera.transform.LookAt(cameraPivot);
            ConfigurePreviewClipPlanes(m_PreviewUtility.camera);

            bool controlPointPrefixesCoincident = m_ShowOriginal && m_ShowSource &&
                                                    (m_ShowAllMeshes
                                                        ? m_AllControlPointPrefixesCoincident
                                                        : AreComparableControlPointsCoincident());
            if (controlPointPrefixesCoincident)
            {
                DrawSlotMesh(m_Original, true, Matrix4x4.identity, m_DepthMaterial);
                DrawSlotMesh(m_Source, true, GetSourceOffsetMatrix(), m_DepthMaterial);

                if (m_ShowSolid)
                {
                    if (m_SolidMode == SolidPreviewMode.OpaqueShaded)
                    {
                        DrawSlotMesh(m_Original, true, Matrix4x4.identity, m_OriginalOpaqueMaterial);
                        DrawSlotMesh(m_Source, true, GetSourceOffsetMatrix(), m_SourceOpaqueMaterial);
                    }
                    else
                    {
                        DrawSlotMesh(m_Original, true, Matrix4x4.identity, m_OriginalCompareMaterial);
                        DrawSlotMesh(m_Source, true, GetSourceOffsetMatrix(), m_SourceCompareMaterial);
                    }
                }

                if (m_ShowWire)
                {
                    DrawSlotWire(m_Original, true, Matrix4x4.identity, m_OriginalCompareWireMaterial);
                    DrawSlotWire(m_Source, true, GetSourceOffsetMatrix(), m_SourceCompareWireMaterial);
                }
            }
            else
            {
                if (m_ShowSolid)
                {
                    DrawSlotMesh(m_Original, m_ShowOriginal, Matrix4x4.identity, GetOriginalSolidMaterial());
                    DrawSlotMesh(m_Source, m_ShowSource, GetSourceOffsetMatrix(), GetSourceSolidMaterial());
                }

                if (m_ShowWire)
                {
                    DrawSlotMesh(m_Original, m_ShowOriginal, Matrix4x4.identity, m_DepthMaterial);
                    DrawSlotMesh(m_Source, m_ShowSource, GetSourceOffsetMatrix(), m_DepthMaterial);

                    DrawSlotWire(m_Original, m_ShowOriginal, Matrix4x4.identity, m_OriginalWireMaterial);
                    DrawSlotWire(m_Source, m_ShowSource, GetSourceOffsetMatrix(), m_SourceWireMaterial);
                }
            }

            m_PreviewUtility.camera.Render();
            Texture rendered = m_PreviewUtility.EndPreview();
            GUI.DrawTexture(previewRect, rendered, ScaleMode.StretchToFill, false);
            DrawAxisOverlay(previewRect, rotation);
            DrawPreviewMeshLabel(previewRect);
            DrawPreviewMeshSummary(previewRect);
        }

        private void ConfigurePreviewClipPlanes(Camera camera)
        {
            Bounds bounds = GetFrameBounds();
            float radius = Mathf.Max(bounds.extents.magnitude, k_MinDistance);
            float centerDistance = Vector3.Distance(camera.transform.position, bounds.center);
            float margin = radius * 1.05f;
            camera.nearClipPlane = Mathf.Max(0.001f, centerDistance - margin);
            camera.farClipPlane = Mathf.Max(camera.nearClipPlane + 0.01f, centerDistance + margin);
        }

        private void DrawPreviewMeshLabel(Rect previewRect)
        {
            string path = m_ShowAllMeshes
                ? Localization.S("patch_creator.preview.all_meshes")
                : GetSelectedComparePath();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var style = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                padding = new RectOffset(6, 6, 2, 2)
            };
            style.normal.textColor = new Color(0.9f, 0.9f, 0.9f);

            float width = Mathf.Min(previewRect.width - 80f, style.CalcSize(new GUIContent(path)).x + 12f);
            if (width <= 0f)
            {
                return;
            }

            var labelRect = new Rect(previewRect.x + 8f, previewRect.y + 8f, width, 20f);
            EditorGUI.DrawRect(labelRect, new Color(0.05f, 0.05f, 0.05f, 0.82f));
            GUI.Label(labelRect, path, style);
        }

        private void DrawPreviewMeshSummary(Rect previewRect)
        {
            string originalSummary;
            string sourceSummary;
            if (m_ShowAllMeshes)
            {
                var paths = GetComparablePaths();
                originalSummary = FormatMeshSummary(m_Original, paths);
                sourceSummary = FormatMeshSummary(m_Source, paths);
            }
            else
            {
                string path = GetSelectedComparePath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                originalSummary = FormatMeshSummary(m_Original.FindMesh(path));
                sourceSummary = FormatMeshSummary(m_Source.FindMesh(path));
            }

            if (string.IsNullOrEmpty(originalSummary) && string.IsNullOrEmpty(sourceSummary))
            {
                return;
            }

            string summary = Localization.SF(
                "patch_creator.preview.comparison_summary",
                originalSummary,
                sourceSummary);
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                padding = new RectOffset(6, 6, 2, 2)
            };
            style.normal.textColor = new Color(0.82f, 0.82f, 0.82f);

            float width = Mathf.Min(previewRect.width - 16f, style.CalcSize(new GUIContent(summary)).x + 12f);
            if (width <= 0f)
            {
                return;
            }

            var summaryRect = new Rect(previewRect.x + 8f, previewRect.yMax - 28f, width, 20f);
            EditorGUI.DrawRect(summaryRect, new Color(0.05f, 0.05f, 0.05f, 0.72f));
            GUI.Label(summaryRect, summary, style);
        }

        private void DrawSlotMesh(PreviewSlot slot, bool visible, Matrix4x4 matrix, Material material)
        {
            if (visible && slot.Mesh != null)
            {
                m_PreviewUtility.DrawMesh(slot.Mesh, matrix, material, 0);
            }
        }

        private void DrawSlotWire(PreviewSlot slot, bool visible, Matrix4x4 matrix, Material material)
        {
            if (visible && slot.WireMesh != null)
            {
                m_PreviewUtility.DrawMesh(slot.WireMesh, matrix, material, 0);
            }
        }

        private static void DrawAxisOverlay(Rect previewRect, Quaternion cameraRotation)
        {
            const float size = 54f;
            Rect rect = new Rect(previewRect.xMax - size - 12f, previewRect.y + 12f, size, size);
            Vector2 center = rect.center;
            Quaternion viewRotation = Quaternion.Inverse(cameraRotation);
            DrawAxisLine(center, viewRotation * Vector3.right, new Color(0.95f, 0.25f, 0.25f), "X");
            DrawAxisLine(center, viewRotation * Vector3.up, new Color(0.25f, 0.9f, 0.35f), "Y");
            DrawAxisLine(center, viewRotation * Vector3.forward, new Color(0.35f, 0.55f, 1f), "Z");
        }

        private static void DrawAxisLine(Vector2 center, Vector3 direction, Color color, string label)
        {
            Vector2 end = center + new Vector2(direction.x, -direction.y).normalized * 20f;
            Handles.BeginGUI();
            Color previous = Handles.color;
            Handles.color = color;
            Handles.DrawAAPolyLine(3f, new Vector3(center.x, center.y, 0f), new Vector3(end.x, end.y, 0f));
            Handles.color = previous;
            Handles.EndGUI();

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10
            };
            style.normal.textColor = color;
            GUI.Label(new Rect(end.x - 8f, end.y - 8f, 16f, 16f), label, style);
        }

        private void RebuildPreviewMeshes(bool frame)
        {
            ClearErrorCalculation();
            string[] paths = GetComparablePaths();
            if (m_ShowAllMeshes && paths.Length == 1)
            {
                m_ShowAllMeshes = false;
                m_CompareMeshIndex = 0;
                UpdateSelectedSourceOffset();
            }

            var sourcePre = GetSourcePreTransform();
            if (m_ShowAllMeshes)
            {
                string name = Localization.S("patch_creator.preview.all_meshes");
                m_Original.RebuildPreview(paths, _ => Matrix4x4.identity, name, preTransform: null);
                m_Source.RebuildPreview(
                    paths,
                    path => GetSourceOffset(path).ToUnityMatrix(),
                    name,
                    preTransform: sourcePre);
                m_AllControlPointPrefixesCoincident = paths.All(path =>
                    CalculateControlPointPrefixesCoincident(
                        m_Original.FindMesh(path),
                        m_Source.FindMesh(path),
                        GetSourceOffset(path).ToUnityMatrix()));
            }
            else
            {
                string selectedPath = GetSelectedComparePath();
                m_Original.RebuildPreview(selectedPath, preTransform: null);
                m_Source.RebuildPreview(selectedPath, preTransform: sourcePre);
                m_AllControlPointPrefixesCoincident = false;
            }

            if (frame)
            {
                FramePreview();
            }
        }

        private string[] GetComparablePaths()
        {
            var originalPaths = m_Original.Paths;
            var sourcePaths = m_Source.Paths;
            if (originalPaths.Length == 0 && sourcePaths.Length == 0)
            {
                return Array.Empty<string>();
            }

            if (originalPaths.Length == 0)
            {
                return sourcePaths;
            }

            if (sourcePaths.Length == 0)
            {
                return originalPaths;
            }

            return originalPaths.Intersect(sourcePaths, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        private string GetSelectedComparePath()
        {
            string[] paths = GetComparablePaths();
            if (paths.Length == 0)
            {
                return null;
            }

            m_CompareMeshIndex = Mathf.Clamp(m_CompareMeshIndex, 0, paths.Length - 1);
            return paths[m_CompareMeshIndex];
        }

        private bool HasAnyPreviewMesh()
        {
            return (m_ShowOriginal && (m_Original.Mesh != null || m_Original.WireMesh != null)) ||
                   (m_ShowSource && (m_Source.Mesh != null || m_Source.WireMesh != null));
        }

        private Bounds GetFrameBounds()
        {
            bool hasBounds = false;
            Bounds bounds = default;
            AddBounds(m_Original.Mesh, m_ShowOriginal, Matrix4x4.identity, ref hasBounds, ref bounds);
            AddBounds(m_Source.Mesh, m_ShowSource, GetSourceOffsetMatrix(), ref hasBounds, ref bounds);
            return hasBounds ? bounds : default;
        }

        private static void AddBounds(Mesh mesh, bool visible, Matrix4x4 matrix, ref bool hasBounds, ref Bounds bounds)
        {
            if (!visible || mesh == null)
            {
                return;
            }

            Bounds transformed = TransformBounds(mesh.bounds, matrix);
            if (!hasBounds)
            {
                bounds = transformed;
                hasBounds = true;
                return;
            }

            bounds.Encapsulate(transformed);
        }

        private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
            var result = new Bounds(center, Vector3.zero);
            Vector3 extents = bounds.extents;
            for (int x = -1; x <= 1; x += 2)
            {
                for (int y = -1; y <= 1; y += 2)
                {
                    for (int z = -1; z <= 1; z += 2)
                    {
                        var corner = bounds.center + Vector3.Scale(extents, new Vector3(x, y, z));
                        result.Encapsulate(matrix.MultiplyPoint3x4(corner));
                    }
                }
            }

            return result;
        }

        private void FramePreview()
        {
            Bounds bounds = GetFrameBounds();
            if (bounds.size == Vector3.zero)
            {
                return;
            }

            float radius = Mathf.Max(bounds.extents.magnitude, k_MinDistance);
            m_Pivot = bounds.center;
            m_Distance = radius * 2.8f;
            m_Pan = Vector2.zero;
        }

        private void HandlePreviewInput(Rect rect)
        {
            Event evt = Event.current;
            int controlId = GUIUtility.GetControlID(s_PreviewInputControlHash, FocusType.Passive, rect);
            if (m_ResizingPreview)
            {
                return;
            }

            bool pointerInside = rect.Contains(evt.mousePosition);
            bool ownsPointer = GUIUtility.hotControl == controlId;
            if (!pointerInside && !ownsPointer)
            {
                return;
            }

            if (evt.type == EventType.MouseDown && pointerInside &&
                (evt.button == 0 || evt.button == 2))
            {
                GUIUtility.hotControl = controlId;
                evt.Use();
            }
            else if (evt.type == EventType.MouseUp && ownsPointer)
            {
                GUIUtility.hotControl = 0;
                evt.Use();
            }
            else if (evt.type == EventType.ScrollWheel && pointerInside)
            {
                m_Distance *= 1f + evt.delta.y * 0.06f;
                m_Distance = Mathf.Max(m_Distance, k_MinDistance);
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && ownsPointer && evt.button == 0 && !evt.alt)
            {
                m_Orbit += new Vector2(evt.delta.y * 0.35f, evt.delta.x * 0.35f);
                m_Orbit.x = Mathf.Clamp(m_Orbit.x, -89f, 89f);
                evt.Use();
            }
            else if (evt.type == EventType.MouseDrag && ownsPointer && (evt.button == 2 || evt.alt))
            {
                float scale = Mathf.Max(m_Distance, k_MinDistance) * 0.002f;
                m_Pan += new Vector2(-evt.delta.x * scale, evt.delta.y * scale);
                evt.Use();
            }
        }

        private void EnsurePreviewUtility()
        {
            if (m_PreviewUtility == null)
            {
                m_PreviewUtility = new PreviewRenderUtility();
                m_PreviewUtility.cameraFieldOfView = 30f;
            }

            m_OriginalMaterial ??= CreateMaterial(new Color(0.2f, 0.55f, 1f, 0.42f));
            m_SourceMaterial ??= CreateMaterial(new Color(1f, 0.5f, 0.12f, 0.42f));
            m_OriginalOpaqueMaterial ??= CreateOpaqueMaterial(new Color(0.28f, 0.62f, 1f, 1f));
            m_SourceOpaqueMaterial ??= CreateOpaqueMaterial(new Color(1f, 0.55f, 0.16f, 1f));
            m_OriginalCompareMaterial ??= CreateCompareMaterial(s_OriginalCompareColor);
            m_SourceCompareMaterial ??= CreateCompareMaterial(s_SourceCompareColor);
            m_DepthMaterial ??= CreateDepthMaterial();
            m_OriginalWireMaterial ??= CreateWireMaterial(new Color(0.25f, 0.8f, 1f, 1f));
            m_SourceWireMaterial ??= CreateWireMaterial(new Color(1f, 0.72f, 0.2f, 1f));
            m_OriginalCompareWireMaterial ??= CreateCompareWireMaterial(s_OriginalCompareWireColor);
            m_SourceCompareWireMaterial ??= CreateCompareWireMaterial(s_SourceCompareWireColor);
        }

        private static Material CreateMaterial(Color color)
        {
            var material = new Material(Shader.Find("Hidden/Internal-Colored"))
            {
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 3000
            };
            material.SetColor("_Color", color);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            material.SetInt("_ZWrite", 0);
            return material;
        }

        private static Material CreateDepthMaterial()
        {
            var material = new Material(GetWireShader())
            {
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 3001
            };
            material.SetColor("_Color", Color.clear);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            material.SetInt("_ZWrite", 1);
            material.SetFloat("_DepthOnly", 1f);
            return material;
        }

        private static Material CreateCompareMaterial(Color color)
        {
            var material = new Material(Shader.Find("Hidden/Internal-Colored"))
            {
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 3002
            };
            material.SetColor("_Color", color);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Equal);
            material.SetInt("_ZWrite", 0);
            return material;
        }

        private static Material CreateWireMaterial(Color color)
        {
            var material = new Material(GetWireShader())
            {
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 3002
            };
            material.SetColor("_Color", color);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Equal);
            material.SetInt("_ZWrite", 0);
            material.SetFloat("_LineWidth", 1f);
            material.SetFloat("_DepthOnly", 0f);
            return material;
        }

        private static Material CreateCompareWireMaterial(Color color)
        {
            var material = CreateWireMaterial(color);
            material.renderQueue = 3003;
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            return material;
        }

        private static Shader GetWireShader()
        {
            return AssetDatabase.LoadAssetAtPath<Shader>(k_WireShaderPath)
                   ?? Shader.Find("Hidden/BlendShare/FbxPreviewWire")
                   ?? Shader.Find("Hidden/Internal-Colored");
        }

        private static Material CreateOpaqueMaterial(Color color)
        {
            return new Material(Shader.Find("Standard"))
            {
                hideFlags = HideFlags.HideAndDontSave,
                color = color
            };
        }

        private Material GetOriginalSolidMaterial()
        {
            return m_SolidMode == SolidPreviewMode.OpaqueShaded ? m_OriginalOpaqueMaterial : m_OriginalMaterial;
        }

        private Material GetSourceSolidMaterial()
        {
            return m_SolidMode == SolidPreviewMode.OpaqueShaded ? m_SourceOpaqueMaterial : m_SourceMaterial;
        }

        private bool AreComparableControlPointsCoincident()
        {
            string path = GetSelectedComparePath();
            var originalMesh = m_Original.FindMesh(path);
            var sourceMesh = m_Source.FindMesh(path);
            Vector3 offsetPosition = m_SourceOffset?.Position ?? Vector3.zero;
            Vector3 offsetRotation = m_SourceOffset?.Rotation ?? Vector3.zero;
            Vector3 offsetScale = m_SourceOffset?.Scale ?? Vector3.one;

            if (m_AlignmentCacheValid &&
                string.Equals(m_AlignmentCachePath, path, StringComparison.Ordinal) &&
                ReferenceEquals(m_AlignmentCacheOriginalMesh, originalMesh) &&
                ReferenceEquals(m_AlignmentCacheSourceMesh, sourceMesh) &&
                m_AlignmentCachePosition == offsetPosition &&
                m_AlignmentCacheRotation == offsetRotation &&
                m_AlignmentCacheScale == offsetScale)
            {
                return m_AlignmentCacheResult;
            }

            m_AlignmentCachePath = path;
            m_AlignmentCacheOriginalMesh = originalMesh;
            m_AlignmentCacheSourceMesh = sourceMesh;
            m_AlignmentCachePosition = offsetPosition;
            m_AlignmentCacheRotation = offsetRotation;
            m_AlignmentCacheScale = offsetScale;
            m_AlignmentCacheResult = CalculateControlPointPrefixesCoincident(
                originalMesh,
                sourceMesh,
                GetSourceOffsetMatrix());
            m_AlignmentCacheValid = true;
            return m_AlignmentCacheResult;
        }

        private bool CalculateControlPointPrefixesCoincident(
            IFbxMesh originalMesh,
            IFbxMesh sourceMesh,
            Matrix4x4 sourceMatrix)
        {
            if (originalMesh == null || sourceMesh == null ||
                originalMesh.ControlPointCount <= 0 || sourceMesh.ControlPointCount <= 0)
            {
                return false;
            }

            EnsureAlignmentVertexCache(originalMesh, sourceMesh);
            int count = Mathf.Min(originalMesh.ControlPointCount, sourceMesh.ControlPointCount);
            if (m_AlignmentOriginalVertices.Length < count || m_AlignmentSourceVertices.Length < count)
            {
                return false;
            }

            float tolerance = Mathf.Max(
                m_AlignmentOriginalMeshSize * k_AlignedRelativeTolerance,
                k_MinAlignedTolerance);
            float squaredTolerance = tolerance * tolerance;
            for (int i = 0; i < count; i++)
            {
                Vector3 originalPosition = m_AlignmentOriginalVertices[i];
                Vector3 sourcePosition = sourceMatrix.MultiplyPoint3x4(m_AlignmentSourceVertices[i]);
                if ((originalPosition - sourcePosition).sqrMagnitude > squaredTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private void EnsureAlignmentVertexCache(IFbxMesh originalMesh, IFbxMesh sourceMesh)
        {
            if (!ReferenceEquals(m_AlignmentVertexCacheOriginalMesh, originalMesh))
            {
                m_AlignmentVertexCacheOriginalMesh = originalMesh;
                m_AlignmentOriginalVertices = ToUnityVertices(originalMesh, preTransform: null);
                m_AlignmentOriginalMeshSize = CalculateBoundsMagnitude(m_AlignmentOriginalVertices);
            }

            if (!ReferenceEquals(m_AlignmentVertexCacheSourceMesh, sourceMesh) ||
                !ReferenceEquals(m_AlignmentVertexCacheSession, m_InspectionSession))
            {
                m_AlignmentVertexCacheSourceMesh = sourceMesh;
                m_AlignmentVertexCacheSession = m_InspectionSession;
                m_AlignmentSourceVertices = ToUnityVertices(sourceMesh, GetSourcePreTransform());
            }
        }

        private static Vector3[] ToUnityVertices(IFbxMesh mesh, FbxAffineSpaceTransform? preTransform)
        {
            var vertices = mesh?.GetControlPoints();
            if (vertices == null || vertices.Length == 0)
            {
                return Array.Empty<Vector3>();
            }

            bool applyPre = preTransform.HasValue && preTransform.Value.IsValid && !preTransform.Value.IsIdentity;
            var result = new Vector3[vertices.Length];
            if (applyPre)
            {
                var xform = preTransform.Value;
                for (int i = 0; i < vertices.Length; i++)
                {
                    result[i] = xform.TransformPoint(vertices[i]).ToVector3();
                }
            }
            else
            {
                for (int i = 0; i < vertices.Length; i++)
                {
                    result[i] = vertices[i].ToVector3();
                }
            }

            return result;
        }

        private static float CalculateBoundsMagnitude(Vector3[] vertices)
        {
            if (vertices == null || vertices.Length == 0)
            {
                return 0f;
            }

            var bounds = new Bounds(vertices[0], Vector3.zero);
            for (int i = 1; i < vertices.Length; i++)
            {
                bounds.Encapsulate(vertices[i]);
            }

            return bounds.size.magnitude;
        }

        private Matrix4x4 GetSourceOffsetMatrix()
        {
            return m_ShowAllMeshes
                ? Matrix4x4.identity
                : m_SourceOffset?.ToUnityMatrix() ?? Matrix4x4.identity;
        }

        private MeshFeatureSourceOffset GetSourceOffset(string path)
        {
            return m_SourceOffsetResolver?.Invoke(path) ?? new MeshFeatureSourceOffset();
        }

        private void UpdateSelectedSourceOffset()
        {
            m_SourceOffset = GetSourceOffset(GetSelectedComparePath());
        }

        private void NotifySourceOffsetChanged()
        {
            m_AlignmentCacheValid = false;
            ClearErrorCalculation();
            m_SourceOffsetChanged?.Invoke();
        }

        private static void DestroyMaterial(ref Material material)
        {
            if (material != null)
            {
                UnityEngine.Object.DestroyImmediate(material);
                material = null;
            }
        }

        private static string GetBuildStatusMessage(UfbxPreviewMeshBuildStatus status)
        {
            return status switch
            {
                UfbxPreviewMeshBuildStatus.NullMesh => Localization.S("patch_creator.preview.build_null_mesh"),
                UfbxPreviewMeshBuildStatus.NoVertices => Localization.S("patch_creator.preview.build_no_vertices"),
                UfbxPreviewMeshBuildStatus.NoFaces => Localization.S("patch_creator.preview.build_no_faces"),
                UfbxPreviewMeshBuildStatus.NoValidTriangles => Localization.S("patch_creator.preview.build_no_valid_triangles"),
                _ => null
            };
        }

        private readonly struct ErrorMetric
        {
            public readonly double Rms;
            public readonly float Max;
            public readonly int MaxIndex;

            public ErrorMetric(double rms, float max, int maxIndex)
            {
                Rms = rms;
                Max = max;
                MaxIndex = maxIndex;
            }
        }

        private readonly struct AutoOffsetResult
        {
            public readonly Quaternion Rotation;
            public readonly float Scale;
            public readonly Vector3 Translation;
            public readonly ErrorMetric Error;

            public AutoOffsetResult(Quaternion rotation, float scale, Vector3 translation, ErrorMetric error)
            {
                Rotation = rotation;
                Scale = scale;
                Translation = translation;
                Error = error;
            }
        }

        private sealed class PreviewSlot : IDisposable
        {
            private Dictionary<string, IFbxMesh> m_MeshesByPath = new Dictionary<string, IFbxMesh>(StringComparer.Ordinal);
            private string[] m_Paths = Array.Empty<string>();
            public IFbxMesh[] Meshes = Array.Empty<IFbxMesh>();
            public bool IsAssigned;
            public string ReadMessage;
            public string SelectedPath;
            public IFbxMesh SelectedMesh;
            public Mesh Mesh;
            public Mesh WireMesh;
            public UfbxPreviewMeshBuildResult BuildResult;

            public bool HasMeshes => Meshes.Length > 0;
            public string[] Paths => m_Paths;

            public void Bind(FbxInspectionAsset asset)
            {
                DestroyPreviewMeshes();
                IsAssigned = asset?.FbxObject != null;
                Meshes = Array.Empty<IFbxMesh>();
                m_MeshesByPath = new Dictionary<string, IFbxMesh>(StringComparer.Ordinal);
                m_Paths = Array.Empty<string>();
                SelectedPath = null;
                SelectedMesh = null;
                ReadMessage = null;
                BuildResult = default;

                if (asset?.Scene == null)
                {
                    ReadMessage = asset?.Diagnostics?.FirstOrDefault()
                                  ?? Localization.S("patch_creator.preview.assign_fbx_asset");
                    return;
                }

                Meshes = asset.Scene.Meshes
                    .Where(mesh => mesh != null && mesh.ControlPointCount > 0)
                    .OrderBy(mesh => mesh.PrimaryNode?.Path ?? mesh.Name, StringComparer.Ordinal)
                    .ToArray();

                foreach (var mesh in Meshes)
                {
                    bool addedAnyInstance = false;
                    var instances = mesh.Instances;
                    if (instances != null)
                    {
                        for (int i = 0; i < instances.Count; i++)
                        {
                            string instancePath = FbxNameUtility.NormalizePath(instances[i]?.Path ?? string.Empty);
                            if (!string.IsNullOrEmpty(instancePath))
                            {
                                if (!m_MeshesByPath.ContainsKey(instancePath))
                                {
                                    m_MeshesByPath.Add(instancePath, mesh);
                                }

                                addedAnyInstance = true;
                            }
                        }
                    }

                    if (!addedAnyInstance)
                    {
                        string fallback = FbxNameUtility.NormalizePath(mesh.PrimaryNode?.Path ?? mesh.Name ?? string.Empty);
                        if (!string.IsNullOrEmpty(fallback) && !m_MeshesByPath.ContainsKey(fallback))
                        {
                            m_MeshesByPath.Add(fallback, mesh);
                        }
                    }
                }

                m_Paths = m_MeshesByPath.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
                if (Meshes.Length == 0)
                {
                    ReadMessage = Localization.S("patch_creator.preview.no_raw_meshes");
                }
            }

            public void RebuildPreview(string path, FbxAffineSpaceTransform? preTransform)
            {
                DestroyPreviewMeshes();
                SelectedPath = path;
                SelectedMesh = FindMesh(path);
                if (SelectedMesh == null)
                {
                    BuildResult = default;
                    return;
                }

                BuildResult = UfbxPreviewMeshBuilder.Build(SelectedMesh, preTransform);
                Mesh = BuildResult.Mesh;
                WireMesh = BuildResult.WireMesh;
            }

            public void RebuildPreview(
                IEnumerable<string> paths,
                Func<string, Matrix4x4> transformResolver,
                string name,
                FbxAffineSpaceTransform? preTransform)
            {
                DestroyPreviewMeshes();
                SelectedPath = null;
                SelectedMesh = null;
                var inputs = (paths ?? Enumerable.Empty<string>())
                    .Select(path => new
                    {
                        Path = path,
                        Mesh = FindMesh(path)
                    })
                    .Where(item => item.Mesh != null)
                    .Select(item => new UfbxPreviewMeshBuildInput(
                        item.Mesh,
                        transformResolver?.Invoke(item.Path) ?? Matrix4x4.identity,
                        preTransform));
                BuildResult = UfbxPreviewMeshBuilder.Build(inputs, name);
                Mesh = BuildResult.Mesh;
                WireMesh = BuildResult.WireMesh;
            }

            public void Dispose()
            {
                DestroyPreviewMeshes();
            }

            public IFbxMesh FindMesh(string path)
            {
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                string norm = FbxNameUtility.NormalizePath(path);
                return m_MeshesByPath.TryGetValue(norm, out var mesh) ? mesh : null;
            }

            private void DestroyPreviewMeshes()
            {
                if (Mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(Mesh);
                    Mesh = null;
                }

                if (WireMesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(WireMesh);
                    WireMesh = null;
                }
            }
        }
    }
}
