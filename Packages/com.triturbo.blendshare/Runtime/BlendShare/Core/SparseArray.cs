using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triturbo.BlendShare.Core
{
    /// <summary>
    /// Unity-serializable sparse array. T must be serializable by Unity when used in serialized fields.
    /// </summary>
    [Serializable]
    public class SparseArray<T>
    {
        [SerializeField] private List<int> m_Keys = new();
        [SerializeField] private List<T> m_Values = new();

        private Dictionary<int, int> keyToSlot;

        public int Count => Math.Min(m_Keys?.Count ?? 0, m_Values?.Count ?? 0);

        public T this[int index]
        {
            get => Get(index);
            set => Set(index, value);
        }

        public int MaxIndex
        {
            get
            {
                int max = -1;
                int count = Count;
                for (int i = 0; i < count; i++)
                {
                    max = Math.Max(max, m_Keys[i]);
                }

                return max;
            }
        }

        public T Get(int key, T defaultValue = default)
        {
            return TryGetValue(key, out var value) ? value : defaultValue;
        }

        public IEnumerable<KeyValuePair<int, T>> Entries()
        {
            int count = Count;
            for (int i = 0; i < count; i++)
            {
                yield return new KeyValuePair<int, T>(m_Keys[i], m_Values[i]);
            }
        }

        public bool TryGetValue(int key, out T value)
        {
            EnsureLookup();
            if (keyToSlot.TryGetValue(key, out int slot) &&
                m_Values != null &&
                slot >= 0 &&
                slot < m_Values.Count)
            {
                value = m_Values[slot];
                return true;
            }

            value = default;
            return false;
        }

        public void Set(int key, T value)
        {
            if (key < 0)
            {
                return;
            }

            m_Keys ??= new List<int>();
            m_Values ??= new List<T>();
            EnsureLookup();

            if (keyToSlot.TryGetValue(key, out int slot))
            {
                m_Values[slot] = value;
                return;
            }

            keyToSlot[key] = m_Keys.Count;
            m_Keys.Add(key);
            m_Values.Add(value);
        }

        public bool Remove(int key)
        {
            EnsureLookup();
            if (!keyToSlot.TryGetValue(key, out int slot))
            {
                return false;
            }

            m_Keys.RemoveAt(slot);
            m_Values.RemoveAt(slot);
            keyToSlot = null;
            return true;
        }

        public void Clear()
        {
            m_Keys?.Clear();
            m_Values?.Clear();
            keyToSlot = null;
        }

        private void EnsureLookup()
        {
            if (keyToSlot != null)
            {
                return;
            }

            keyToSlot = new Dictionary<int, int>();
            int count = Count;
            for (int i = 0; i < count; i++)
            {
                keyToSlot[m_Keys[i]] = i;
            }
        }
    }
}
