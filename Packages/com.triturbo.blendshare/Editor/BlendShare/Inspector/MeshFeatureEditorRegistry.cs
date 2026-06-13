using System;
using System.Collections.Generic;
using System.Linq;
using Triturbo.BlendShare.Core;
using UnityEditor;

namespace Triturbo.BlendShare.Inspector
{
    public static class MeshFeatureEditorRegistry
    {
        private static Dictionary<string, IMeshFeatureObjectEditor> editorsByFeatureId;
        private static Dictionary<Type, IBlendShareEmbeddedEditor> editorsByTargetType;

        public static IMeshFeatureObjectEditor GetEditor(MeshFeatureObject feature)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.FeatureId))
            {
                return null;
            }

            EnsureInitialized();
            return editorsByFeatureId.TryGetValue(feature.FeatureId, out var editor) ? editor : null;
        }

        public static IBlendShareEmbeddedEditor GetEmbeddedEditor(UnityEngine.Object embeddedObject)
        {
            if (embeddedObject == null)
            {
                return null;
            }

            EnsureInitialized();
            if (embeddedObject is MeshFeatureObject feature)
            {
                return GetEditor(feature);
            }

            Type embeddedObjectType = embeddedObject.GetType();
            return editorsByTargetType
                .Where(pair => pair.Key.IsAssignableFrom(embeddedObjectType))
                .OrderByDescending(pair => GetInheritanceDepth(pair.Key))
                .Select(pair => pair.Value)
                .FirstOrDefault();
        }

        private static void EnsureInitialized()
        {
            if (editorsByFeatureId != null && editorsByTargetType != null)
            {
                return;
            }

            editorsByFeatureId = new Dictionary<string, IMeshFeatureObjectEditor>(StringComparer.Ordinal);
            editorsByTargetType = new Dictionary<Type, IBlendShareEmbeddedEditor>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<IMeshFeatureObjectEditor>())
            {
                if (type.IsAbstract || type.IsInterface || typeof(UnityEditor.Editor).IsAssignableFrom(type))
                {
                    continue;
                }

                if (Activator.CreateInstance(type) is not IMeshFeatureObjectEditor editor ||
                    string.IsNullOrWhiteSpace(editor.FeatureId))
                {
                    continue;
                }

                editorsByFeatureId[editor.FeatureId] = editor;
            }

            foreach (var type in TypeCache.GetTypesDerivedFrom<IBlendShareEmbeddedEditor>())
            {
                if (type.IsAbstract || type.IsInterface || typeof(UnityEditor.Editor).IsAssignableFrom(type))
                {
                    continue;
                }

                if (Activator.CreateInstance(type) is not IBlendShareEmbeddedEditor editor || editor.TargetType == null)
                {
                    continue;
                }

                editorsByTargetType[editor.TargetType] = editor;
            }
        }

        private static int GetInheritanceDepth(Type type)
        {
            int depth = 0;
            for (Type current = type; current != null; current = current.BaseType)
            {
                depth++;
            }

            return depth;
        }
    }
}
