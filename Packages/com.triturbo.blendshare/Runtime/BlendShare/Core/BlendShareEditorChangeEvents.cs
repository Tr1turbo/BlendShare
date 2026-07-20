#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Triturbo.BlendShare.Editor
{
    [Flags]
    internal enum BlendShareEditorChangeKind
    {
        None = 0,
        Hierarchy = 1 << 0,
        SerializedObject = 1 << 1,
        UndoRedo = 1 << 2,
        Project = 1 << 3,
        Explicit = 1 << 4,
        All = Hierarchy | SerializedObject | UndoRedo | Project | Explicit
    }

    internal readonly struct BlendShareEditorChange
    {
        internal BlendShareEditorChange(
            BlendShareEditorChangeKind kinds,
            IReadOnlyList<Object> changedObjects)
        {
            Kinds = kinds;
            ChangedObjects = changedObjects ?? Array.Empty<Object>();
        }

        public BlendShareEditorChangeKind Kinds { get; }
        public IReadOnlyList<Object> ChangedObjects { get; }
    }

    /// <summary>Publishes coalesced, source-level Unity editor change notifications.</summary>
    [InitializeOnLoad]
    internal static class BlendShareEditorChangeEvents
    {
        private const BlendShareEditorChangeKind TargetedKinds =
            BlendShareEditorChangeKind.SerializedObject |
            BlendShareEditorChangeKind.Explicit;

        private static readonly List<Subscription> Subscriptions = new();
        private static readonly Dictionary<Object, BlendShareEditorChangeKind> PendingObjectKinds = new();
        private static BlendShareEditorChangeKind pendingKinds;
        private static bool publishScheduled;

        static BlendShareEditorChangeEvents()
        {
            EditorApplication.hierarchyChanged += () => NotifyIfSubscribed(BlendShareEditorChangeKind.Hierarchy);
            EditorApplication.projectChanged += () => NotifyIfSubscribed(BlendShareEditorChangeKind.Project);
            Undo.undoRedoPerformed += () => NotifyIfSubscribed(BlendShareEditorChangeKind.UndoRedo);
            Undo.postprocessModifications += OnPostprocessModifications;
        }

        internal static IDisposable Subscribe(
            BlendShareEditorChangeKind kinds,
            Action<BlendShareEditorChange> callback,
            params Type[] interestedTypes)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            var subscription = new Subscription(kinds, interestedTypes, callback);
            Subscriptions.Add(subscription);
            return subscription;
        }

        internal static void NotifyChanged(
            BlendShareEditorChangeKind kind,
            params Object[] changedObjects)
        {
            if (kind == BlendShareEditorChangeKind.None)
            {
                return;
            }

            pendingKinds |= kind;
            foreach (var changedObject in changedObjects ?? Array.Empty<Object>())
            {
                if (ReferenceEquals(changedObject, null))
                {
                    continue;
                }

                PendingObjectKinds.TryGetValue(changedObject, out var objectKinds);
                PendingObjectKinds[changedObject] = objectKinds | kind;
            }

            if (publishScheduled)
            {
                return;
            }

            publishScheduled = true;
            EditorApplication.delayCall += Publish;
        }

        private static UndoPropertyModification[] OnPostprocessModifications(
            UndoPropertyModification[] modifications)
        {
            if (!HasSubscribers(BlendShareEditorChangeKind.SerializedObject))
            {
                return modifications;
            }

            var changedObjects = (modifications ?? Array.Empty<UndoPropertyModification>())
                .Select(modification =>
                    modification.currentValue?.target ?? modification.previousValue?.target)
                .Where(target => !ReferenceEquals(target, null))
                .Distinct()
                .ToArray();
            if (changedObjects.Length > 0)
            {
                NotifyChanged(BlendShareEditorChangeKind.SerializedObject, changedObjects);
            }

            return modifications;
        }

        private static void NotifyIfSubscribed(BlendShareEditorChangeKind kind)
        {
            if (HasSubscribers(kind))
            {
                NotifyChanged(kind);
            }
        }

        private static bool HasSubscribers(BlendShareEditorChangeKind kind)
        {
            return Subscriptions.Any(subscription => subscription.Includes(kind));
        }

        private static void Publish()
        {
            publishScheduled = false;
            var kinds = pendingKinds;
            var objectKinds = PendingObjectKinds.ToArray();
            pendingKinds = BlendShareEditorChangeKind.None;
            PendingObjectKinds.Clear();
            if (kinds == BlendShareEditorChangeKind.None)
            {
                return;
            }

            foreach (var subscription in Subscriptions.ToArray())
            {
                subscription.Publish(kinds, objectKinds);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private readonly BlendShareEditorChangeKind kinds;
            private readonly Type[] interestedTypes;
            private readonly Action<BlendShareEditorChange> callback;
            private bool disposed;

            public Subscription(
                BlendShareEditorChangeKind kinds,
                IEnumerable<Type> interestedTypes,
                Action<BlendShareEditorChange> callback)
            {
                this.kinds = kinds;
                this.interestedTypes = (interestedTypes ?? Array.Empty<Type>())
                    .Where(type => type != null)
                    .Distinct()
                    .ToArray();
                this.callback = callback;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                Subscriptions.Remove(this);
            }

            public bool Includes(BlendShareEditorChangeKind kind)
            {
                return !disposed && (kinds & kind) != 0;
            }

            public void Publish(
                BlendShareEditorChangeKind publishedKinds,
                IReadOnlyList<KeyValuePair<Object, BlendShareEditorChangeKind>> objectKinds)
            {
                if (disposed)
                {
                    return;
                }

                var matchedKinds = publishedKinds & kinds;
                if (matchedKinds == BlendShareEditorChangeKind.None)
                {
                    return;
                }

                var broadKinds = matchedKinds & ~TargetedKinds;
                var targetedKinds = matchedKinds & TargetedKinds;
                var matchedEntries = (objectKinds ?? Array.Empty<KeyValuePair<Object, BlendShareEditorChangeKind>>())
                    .Where(entry => (entry.Value & targetedKinds) != 0 && MatchesType(entry.Key))
                    .ToArray();
                if (broadKinds == BlendShareEditorChangeKind.None && matchedEntries.Length == 0)
                {
                    return;
                }

                var deliveredKinds = broadKinds | matchedEntries.Aggregate(
                        BlendShareEditorChangeKind.None,
                        (current, entry) => current | (entry.Value & targetedKinds));
                var matchedObjects = matchedEntries.Select(entry => entry.Key).ToArray();
                try
                {
                    callback(new BlendShareEditorChange(deliveredKinds, matchedObjects));
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            private bool MatchesType(Object changedObject)
            {
                return !ReferenceEquals(changedObject, null) &&
                       (interestedTypes.Length == 0 ||
                        interestedTypes.Any(type => type.IsInstanceOfType(changedObject)));
            }
        }
    }
}
#endif
