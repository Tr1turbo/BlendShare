using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Triturbo.BlendShapeShare.BlendShapeData;
using Triturbo.Fbx;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShare.Editor.DebugTools
{
    public sealed class FbxReaderSpeedComparisonWindow : EditorWindow
    {
        private const int MaxMeshRows = 30;

        private GameObject fbxPrefab;
        private string concernPath = string.Empty;
        private int iterations = 5;
        private Vector2 scroll;
        private readonly List<FbxMeshGeometry> meshes = new();
        private readonly List<TimingRow> results = new();
        private string assetPath = string.Empty;
        private string refreshMessage = string.Empty;

        [MenuItem("Tools/BlendShare/Debug/FBX Reader Speed Comparison")]
        public static void ShowWindow()
        {
            GetWindow<FbxReaderSpeedComparisonWindow>("FBX Reader Speed Comparison");
        }

        private void OnGUI()
        {
            DrawInputs();
            EditorGUILayout.Space(8);
            DrawMeshList();
            EditorGUILayout.Space(8);
            DrawResults();
        }

        private void DrawInputs()
        {
            EditorGUILayout.LabelField("FBX Reader Speed Comparison", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            fbxPrefab = EditorWidgets.FBXGameObjectField(new GUIContent("FBX Prefab", "FBX prefab asset to measure."), fbxPrefab);
            if (EditorGUI.EndChangeCheck())
            {
                results.Clear();
                RefreshMeshes();
            }

            concernPath = EditorGUILayout.TextField("Concern Path", concernPath);
            iterations = Mathf.Clamp(EditorGUILayout.IntField("Iterations", iterations), 1, 100);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(fbxPrefab == null))
                {
                    if (GUILayout.Button("Refresh Meshes"))
                    {
                        RefreshMeshes();
                    }
                }

                using (new EditorGUI.DisabledScope(!CanRun()))
                {
                    if (GUILayout.Button("Run Comparison"))
                    {
                        RunComparison();
                    }
                }
            }

            if (!string.IsNullOrEmpty(refreshMessage))
            {
                EditorGUILayout.HelpBox(refreshMessage, MessageType.Info);
            }
        }

        private void DrawMeshList()
        {
            EditorGUILayout.LabelField("Mesh Paths", EditorStyles.boldLabel);

            if (meshes.Count == 0)
            {
                EditorGUILayout.HelpBox("Assign an FBX prefab and refresh to list mesh paths.", MessageType.Info);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(260));
            foreach (var mesh in meshes.Take(MaxMeshRows))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string nodePath = mesh.OwnerNode?.Path ?? mesh.Name;
                    if (GUILayout.Button(nodePath, EditorStyles.linkLabel, GUILayout.ExpandWidth(true)))
                    {
                        concernPath = nodePath;
                        GUI.FocusControl(null);
                    }

                    GUILayout.Label(
                        $"{mesh.ControlPointCount} cps",
                        GUILayout.Width(90));
                }
            }

            if (meshes.Count > MaxMeshRows)
            {
                EditorGUILayout.LabelField($"+{meshes.Count - MaxMeshRows} more meshes");
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawResults()
        {
            if (results.Count == 0)
            {
                return;
            }

            EditorGUILayout.LabelField("Timing Results", EditorStyles.boldLabel);
            double baseline = results.FirstOrDefault(row => row.Success)?.AverageMilliseconds ?? 0d;
            foreach (var row in results)
            {
                using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                {
                    EditorGUILayout.LabelField(row.Label, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("Status", row.Status);
                    EditorGUILayout.LabelField("Average Time", FormatMilliseconds(row.AverageMilliseconds));
                    EditorGUILayout.LabelField("Last Time", FormatMilliseconds(row.LastMilliseconds));
                    if (baseline > 0d && row.Success)
                    {
                        EditorGUILayout.LabelField("Relative Time", FormatRelative(row.AverageMilliseconds / baseline));
                    }

                    EditorGUILayout.LabelField("Managed Memory Delta", EditorUtility.FormatBytes(Math.Max(0, row.ManagedMemoryDeltaBytes)));
                    EditorGUILayout.LabelField("Nodes", row.NodeCount.ToString());
                    EditorGUILayout.LabelField("Meshes", row.MeshCount.ToString());
                    EditorGUILayout.LabelField("Materialized Meshes", row.MaterializedMeshCount.ToString());
                    EditorGUILayout.LabelField("Concern Mesh", row.MeshSummary);

                    if (!string.IsNullOrEmpty(row.Message))
                    {
                        EditorGUILayout.HelpBox(row.Message, row.Success ? MessageType.Info : MessageType.Warning);
                    }
                }
            }
        }

        private bool CanRun()
        {
            return fbxPrefab != null && !string.IsNullOrWhiteSpace(concernPath);
        }

        private void RefreshMeshes()
        {
            meshes.Clear();
            refreshMessage = string.Empty;
            assetPath = fbxPrefab != null ? AssetDatabase.GetAssetPath(fbxPrefab) : string.Empty;

            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
            {
                return;
            }

            var result = FbxDocumentReader.Read(assetPath);
            if (!result.Success)
            {
                refreshMessage = result.Message;
                return;
            }

            meshes.AddRange(result.Value.ListMeshes().OrderBy(mesh => mesh.OwnerNode?.Path ?? mesh.Name, StringComparer.Ordinal));
            if (string.IsNullOrWhiteSpace(concernPath) && meshes.Count > 0)
            {
                concernPath = meshes[0].OwnerNode?.Path ?? meshes[0].Name;
            }

            refreshMessage = string.Format(
                "{0}: {1}, {2}: {3}",
                "Nodes",
                result.Value.Nodes.Count,
                "Meshes",
                meshes.Count);
        }

        private void RunComparison()
        {
            results.Clear();
            assetPath = AssetDatabase.GetAssetPath(fbxPrefab);
            string path = NormalizePath(concernPath);
            var requestedPaths = new[] { path };

            results.Add(MeasureDocumentCase(
                "Our reader - full file",
                () => BinaryFbxDocumentReader.Read(assetPath),
                path));

            results.Add(MeasureDocumentCase(
                "ufbx - full file",
                () => UfbxDocumentReader.Read(assetPath),
                path));

            results.Add(MeasureDocumentCase(
                "Our reader - selected mesh",
                () => BinaryFbxDocumentReader.Read(assetPath, requestedPaths),
                path));

            results.Add(MeasureDocumentCase(
                "ufbx - selected mesh",
                () => UfbxDocumentReader.Read(assetPath, requestedPaths),
                path));

        }

        private TimingRow MeasureDocumentCase(
            string label,
            Func<FbxReadResult<FbxDocument>> read,
            string path)
        {
            _ = read();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var timings = new double[iterations];
            FbxReadResult<FbxDocument> lastResult = null;
            long beforeMemory = GC.GetTotalMemory(true);
            long afterMemory = beforeMemory;

            for (int i = 0; i < iterations; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                lastResult = read();
                stopwatch.Stop();
                timings[i] = stopwatch.Elapsed.TotalMilliseconds;
                afterMemory = GC.GetTotalMemory(false);
            }

            return BuildTimingRow(label, lastResult, path, timings, afterMemory - beforeMemory);
        }

        private static TimingRow BuildTimingRow(
            string label,
            FbxReadResult<FbxDocument> result,
            string path,
            IReadOnlyList<double> timings,
            long memoryDeltaBytes)
        {
            var row = new TimingRow
            {
                Label = label,
                Success = result?.Success == true,
                Status = result?.Status.ToString() ?? FbxReadStatus.ParseError.ToString(),
                Message = result?.Message ?? string.Empty,
                AverageMilliseconds = timings.Count > 0 ? timings.Average() : 0d,
                LastMilliseconds = timings.Count > 0 ? timings[timings.Count - 1] : 0d,
                ManagedMemoryDeltaBytes = memoryDeltaBytes,
                MeshSummary = "No mesh"
            };

            if (result?.Value == null)
            {
                return row;
            }

            var document = result.Value;
            row.NodeCount = document.Nodes.Count;
            row.MeshCount = document.ListMeshes().Count;
            row.MaterializedMeshCount = document.Meshes.Count;

            var meshResult = document.TryFindMesh(path);
            if (meshResult.Success)
            {
                row.MeshSummary = BuildMeshSummary(meshResult.Value);
            }
            else if (string.IsNullOrEmpty(row.Message))
            {
                row.Message = meshResult.Message;
            }

            row.MaterializedMeshCount = document.Meshes.Count;
            return row;
        }

        private static string BuildMeshSummary(FbxMeshGeometry mesh)
        {
            if (mesh == null)
            {
                return "No mesh";
            }

            int blendShapeChannels = mesh.BlendShapeDeformers.Sum(deformer => deformer.Channels.Count);
            int blendShapeFrames = mesh.BlendShapeDeformers
                .SelectMany(deformer => deformer.Channels)
                .Sum(channel => channel.Frames.Count);
            int bones = mesh.SkinDeformers.Sum(skin => skin.Bones.Count);

            return string.Format(
                "{0} | cps {1}, loaded {2}, normals {3}, tangents {4}, blendshape channels {5}, frames {6}, bones {7}",
                mesh.OwnerNode?.Path ?? mesh.Name,
                mesh.ControlPointCount,
                mesh.ControlPoints.Count,
                mesh.ControlPointNormals.Count,
                mesh.ControlPointTangents.Count,
                blendShapeChannels,
                blendShapeFrames,
                bones);
        }

        private static string NormalizePath(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return string.Join(
                "/",
                value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part =>
                    {
                        int separator = part.IndexOf("::", StringComparison.Ordinal);
                        return separator >= 0 ? part.Substring(separator + 2) : part;
                    }));
        }

        private static string FormatMilliseconds(double milliseconds)
        {
            return milliseconds.ToString("0.###") + " ms";
        }

        private static string FormatRelative(double ratio)
        {
            return ratio.ToString("0.##") + "x";
        }

        private sealed class TimingRow
        {
            public string Label;
            public bool Success;
            public string Status;
            public string Message;
            public double AverageMilliseconds;
            public double LastMilliseconds;
            public long ManagedMemoryDeltaBytes;
            public int NodeCount;
            public int MeshCount;
            public int MaterializedMeshCount;
            public string MeshSummary;
        }
    }
}
