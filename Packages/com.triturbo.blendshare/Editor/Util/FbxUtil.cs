#if ENABLE_FBX_SDK
using Autodesk.Fbx;

namespace Triturbo.BlendShapeShare.Util
{
    public static class FbxUtil
    {
        private static FbxNode FindChildByPath(this FbxNode rootNode, string path)
        {
            if (rootNode == null || string.IsNullOrEmpty(path))
            {
                return rootNode;
            }

            string[] parts = path.Split('/');
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
                    for (int i = 0; i < currentNode.GetChildCount(); i++)
                    {
                        FbxNode child = currentNode.GetChild(i);
                    }
                    return null; // Node not found in hierarchy
                }
            }
            return currentNode;
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
    }
}

#endif
