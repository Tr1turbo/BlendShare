using System;
using System.Collections.Generic;
using System.Reflection;
using Triturbo.BlendShare.Model.Features;
using Triturbo.BlendShare.Model.Serialization;
using Triturbo.BlendShare.Model.Validation;

namespace Triturbo.BlendShare.Container.Serialization
{
    /// <summary>
    /// Deterministic, lazily initialized registry mapping FeatureTypeId to its CLR feature type.
    /// Rejects duplicate keys; does not depend on cross-assembly [InitializeOnLoad] ordering.
    /// </summary>
    public sealed class FeatureSchemaRegistry
    {
        public static readonly FeatureSchemaRegistry Default = CreateDefault();

        private readonly object m_Lock = new object();
        private readonly Dictionary<string, Type> m_ByTypeId = new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly Dictionary<Type, BlendShareFeatureAttribute> m_ByClrType = new Dictionary<Type, BlendShareFeatureAttribute>();

        public void Register(Type featureType)
        {
            if (featureType == null)
            {
                throw new ArgumentNullException(nameof(featureType));
            }

            if (!typeof(BlendShareFeatureData).IsAssignableFrom(featureType) || featureType.IsAbstract)
            {
                throw new ArgumentException($"Feature type '{featureType.FullName}' must be a concrete subclass of {nameof(BlendShareFeatureData)}.", nameof(featureType));
            }

            var attr = featureType.GetCustomAttribute<BlendShareFeatureAttribute>(inherit: false);
            if (attr == null)
            {
                throw new ArgumentException($"Feature type '{featureType.FullName}' is missing [{nameof(BlendShareFeatureAttribute)}].", nameof(featureType));
            }

            lock (m_Lock)
            {
                if (m_ByTypeId.TryGetValue(attr.TypeId, out var existing))
                {
                    if (existing == featureType)
                    {
                        return;
                    }

                    throw new InvalidOperationException(
                        $"Duplicate FeatureTypeId '{attr.TypeId}' registered by '{existing.FullName}' and '{featureType.FullName}'.");
                }

                m_ByTypeId.Add(attr.TypeId, featureType);
                m_ByClrType.Add(featureType, attr);
            }
        }

        public bool TryGetFeatureType(string typeId, out Type featureType)
        {
            if (string.IsNullOrEmpty(typeId))
            {
                featureType = null;
                return false;
            }

            lock (m_Lock)
            {
                return m_ByTypeId.TryGetValue(typeId, out featureType);
            }
        }

        public bool TryGetFeatureAttribute(Type featureType, out BlendShareFeatureAttribute attribute)
        {
            if (featureType == null)
            {
                attribute = null;
                return false;
            }

            lock (m_Lock)
            {
                return m_ByClrType.TryGetValue(featureType, out attribute);
            }
        }

        public IReadOnlyCollection<string> RegisteredTypeIds
        {
            get
            {
                lock (m_Lock)
                {
                    return new List<string>(m_ByTypeId.Keys);
                }
            }
        }

        private static FeatureSchemaRegistry CreateDefault()
        {
            var registry = new FeatureSchemaRegistry();
            registry.Register(typeof(BlendShapeFeature));
            registry.Register(typeof(SkinWeightFeature));
            return registry;
        }
    }

    /// <summary>
    /// Deterministic registry of IFeatureValidator instances keyed by FeatureTypeId.
    /// </summary>
    public sealed class FeatureValidatorRegistry
    {
        public static readonly FeatureValidatorRegistry Default = CreateDefault();

        private readonly object m_Lock = new object();
        private readonly Dictionary<string, IFeatureValidator> m_Validators = new Dictionary<string, IFeatureValidator>(StringComparer.Ordinal);

        public void Register(IFeatureValidator validator)
        {
            if (validator == null)
            {
                throw new ArgumentNullException(nameof(validator));
            }

            if (string.IsNullOrWhiteSpace(validator.FeatureTypeId))
            {
                throw new ArgumentException("Validator FeatureTypeId must be non-empty.", nameof(validator));
            }

            lock (m_Lock)
            {
                if (m_Validators.TryGetValue(validator.FeatureTypeId, out var existing))
                {
                    if (ReferenceEquals(existing, validator))
                    {
                        return;
                    }

                    throw new InvalidOperationException($"Duplicate validator registered for FeatureTypeId '{validator.FeatureTypeId}'.");
                }

                m_Validators.Add(validator.FeatureTypeId, validator);
            }
        }

        public bool TryGetValidator(string typeId, out IFeatureValidator validator)
        {
            if (string.IsNullOrEmpty(typeId))
            {
                validator = null;
                return false;
            }

            lock (m_Lock)
            {
                return m_Validators.TryGetValue(typeId, out validator);
            }
        }

        private static FeatureValidatorRegistry CreateDefault()
        {
            var registry = new FeatureValidatorRegistry();
            registry.Register(new BlendShapeFeatureValidator());
            registry.Register(new SkinWeightFeatureValidator());
            return registry;
        }
    }

    /// <summary>
    /// Registry for optional custom payload codecs keyed by exact (Type, CodecId, CodecVersion) on decode,
    /// and active default writer codec by Type on encode.
    /// </summary>
    public sealed class PayloadCodecRegistry
    {
        public static readonly PayloadCodecRegistry Default = new PayloadCodecRegistry();

        private readonly object m_Lock = new object();
        private readonly Dictionary<Type, object> m_WriterByClrType = new Dictionary<Type, object>();
        private readonly Dictionary<(Type, string, int), object> m_ByExactIdentity = new Dictionary<(Type, string, int), object>();

        public void Register<T>(ICustomBlendShareCodec<T> codec, bool setAsWriterDefault = true) where T : class
        {
            if (codec == null)
            {
                throw new ArgumentNullException(nameof(codec));
            }

            if (string.IsNullOrWhiteSpace(codec.CodecId) || codec.CodecVersion <= 0)
            {
                throw new ArgumentException("Custom codec requires non-empty CodecId and positive CodecVersion.", nameof(codec));
            }

            if (string.Equals(codec.CodecId, BlendShareSerializer.DefaultPayloadCodecId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Custom codec cannot reuse built-in CodecId '{BlendShareSerializer.DefaultPayloadCodecId}'.");
            }

            lock (m_Lock)
            {
                var key = (typeof(T), codec.CodecId, codec.CodecVersion);
                if (m_ByExactIdentity.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        $"Duplicate custom payload codec registered for '{typeof(T).FullName}' ({codec.CodecId}@v{codec.CodecVersion}).");
                }

                m_ByExactIdentity.Add(key, codec);
                if (setAsWriterDefault)
                {
                    m_WriterByClrType[typeof(T)] = codec;
                }
            }
        }

        public bool TryGetWriterCodec<T>(out ICustomBlendShareCodec<T> codec) where T : class
        {
            lock (m_Lock)
            {
                if (m_WriterByClrType.TryGetValue(typeof(T), out var raw))
                {
                    codec = (ICustomBlendShareCodec<T>)raw;
                    return true;
                }

                codec = null;
                return false;
            }
        }

        public bool TryGetDecoderCodec<T>(string codecId, int codecVersion, out ICustomBlendShareCodec<T> codec) where T : class
        {
            if (string.IsNullOrEmpty(codecId) || codecVersion <= 0)
            {
                codec = null;
                return false;
            }

            lock (m_Lock)
            {
                if (m_ByExactIdentity.TryGetValue((typeof(T), codecId, codecVersion), out var raw))
                {
                    codec = (ICustomBlendShareCodec<T>)raw;
                    return true;
                }

                codec = null;
                return false;
            }
        }
    }
}
