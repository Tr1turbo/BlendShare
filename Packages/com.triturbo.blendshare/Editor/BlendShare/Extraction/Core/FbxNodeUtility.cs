#if ENABLE_FBX_SDK
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Fbx;

namespace Triturbo.BlendShare.Core
{
    public static class FbxNodeUtility
    {
        /// <summary>
        /// Finds a mesh node by its exact slash-delimited path from the supplied root node.
        /// </summary>
        public static FbxNode FindMeshChildByPath(this FbxNode rootNode, string path)
        {
            if (rootNode == null)
            {
                return null;
            }

            string normalizedPath = NormalizeNodePath(path);
            if (normalizedPath == ".")
            {
                return rootNode.GetMesh() != null ? rootNode : null;
            }

            string[] parts = normalizedPath.Split('/');
            FbxNode currentNode = rootNode;
            foreach (string part in parts)
            {
                bool found = false;
                for (int i = 0; i < currentNode.GetChildCount(); i++)
                {
                    FbxNode child = currentNode.GetChild(i);
                    if (child.GetName() == part)
                    {
                        currentNode = child;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return null;
                }
            }

            return currentNode?.GetMesh() != null ? currentNode : null;
        }

        public static FbxNode FindMeshChildByPathOrUniqueName(this FbxNode rootNode, string path)
        {
            var byPath = rootNode.FindMeshChildByPath(path);
            if (byPath != null)
            {
                return byPath;
            }

            string nodeName = LeafName(path);
            return nodeName == "." ? null : rootNode.FindUniqueMeshChildByName(nodeName);
        }

        public static FbxNode FindUniqueMeshChildByName(this FbxNode rootNode, string name)
        {
            if (rootNode == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var matches = new List<FbxNode>();
            CollectMeshChildrenByName(rootNode, name, matches);
            return matches.Count == 1 ? matches[0] : null;
        }

        public static FbxNode FindMeshChild(this FbxNode rootNode, string name)
        {
            if (rootNode.GetName() == name && rootNode.GetMesh() != null)
            {
                return rootNode;
            }

            for (int i = 0; i < rootNode.GetChildCount(); i++)
            {
                FbxNode child = rootNode.GetChild(i);
                if (child.GetName() == name && child.GetMesh() != null)
                {
                    return child;
                }
            }

            for (int i = 0; i < rootNode.GetChildCount(); i++)
            {
                return rootNode.GetChild(i).FindMeshChild(name);
            }

            return null;
        }

        private static void CollectMeshChildrenByName(FbxNode node, string name, List<FbxNode> matches)
        {
            if (node == null)
            {
                return;
            }

            if (node.GetMesh() != null && node.GetName() == name)
            {
                matches.Add(node);
            }

            for (int i = 0; i < node.GetChildCount(); i++)
            {
                CollectMeshChildrenByName(node.GetChild(i), name, matches);
            }
        }

        private static string NormalizeNodePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == ".")
            {
                return ".";
            }

            string normalized = string.Join(
                "/",
                path.Replace('\\', '/')
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0 && part != "."));

            return string.IsNullOrEmpty(normalized) ? "." : normalized;
        }

        private static string LeafName(string path)
        {
            string normalized = NormalizeNodePath(path);
            if (normalized == ".")
            {
                return ".";
            }

            int separator = normalized.LastIndexOf('/');
            return separator >= 0 ? normalized.Substring(separator + 1) : normalized;
        }
    }
}
#endif
