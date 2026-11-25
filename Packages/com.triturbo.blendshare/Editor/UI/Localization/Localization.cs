using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShare.UI
{
    public static class Localization
    {
        private const string k_DefaultLocale = "en-US";
        private const string k_LocalePreferenceKey = "com.triturbo.blendshare.localization.locale";
        private const string k_LocalizationFolderGuid = "42b02b3f92611074a9fab944b5ec1054";
        private const string k_FallbackLocalizationFolderPath = "Packages/com.triturbo.blendshare/Editor/UI/Localization";

        private static readonly Dictionary<string, LocalizationAsset> s_LanguageAssets =
            new Dictionary<string, LocalizationAsset>(StringComparer.Ordinal);
        private static LocalizationAsset s_CurrentAsset;
        private static LocalizationAsset s_FallbackAsset;
        private static string s_CurrentLanguage = k_DefaultLocale;
        private static bool s_Loaded;

        public static event Action LanguageChanged;

        public static string CurrentLanguage
        {
            get
            {
                EnsureLoaded();
                return s_CurrentLanguage;
            }
        }

        public static IReadOnlyCollection<string> AvailableLanguages
        {
            get
            {
                EnsureLoaded();
                return s_LanguageAssets.Keys;
            }
        }

        public static bool IsCjkLanguage
        {
            get
            {
                string language = CurrentLanguage;
                return language.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ||
                       language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                       language.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void LoadAvailableLanguages()
        {
            s_LanguageAssets.Clear();
            s_Loaded = true;

            string localizationPathRoot = AssetDatabase.GUIDToAssetPath(k_LocalizationFolderGuid);
            if (string.IsNullOrEmpty(localizationPathRoot) || !AssetDatabase.IsValidFolder(localizationPathRoot))
            {
                localizationPathRoot = k_FallbackLocalizationFolderPath;
            }

            if (string.IsNullOrEmpty(localizationPathRoot) || !AssetDatabase.IsValidFolder(localizationPathRoot))
            {
                s_FallbackAsset = null;
                s_CurrentAsset = null;
                return;
            }

            string[] assetGuids = AssetDatabase.FindAssets(string.Empty, new[] { localizationPathRoot });
            for (int i = 0; i < assetGuids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(assetGuids[i]);
                if (string.IsNullOrEmpty(assetPath) ||
                    !assetPath.EndsWith(".po", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(assetPath).StartsWith("._", StringComparison.Ordinal))
                {
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath<LocalizationAsset>(assetPath);
                if (asset != null)
                {
                    s_LanguageAssets[Path.GetFileNameWithoutExtension(assetPath)] = asset;
                }
            }

            s_LanguageAssets.TryGetValue(k_DefaultLocale, out s_FallbackAsset);
            if (!s_LanguageAssets.TryGetValue(s_CurrentLanguage, out s_CurrentAsset))
            {
                s_CurrentAsset = s_FallbackAsset;
                s_CurrentLanguage = k_DefaultLocale;
            }
        }

        public static void LoadLanguage(string langKey)
        {
            EnsureLoaded();

            string previousLanguage = s_CurrentLanguage;
            if (string.IsNullOrEmpty(langKey) || !s_LanguageAssets.TryGetValue(langKey, out s_CurrentAsset))
            {
                s_CurrentAsset = s_FallbackAsset;
                langKey = k_DefaultLocale;
            }

            s_CurrentLanguage = langKey;
            EditorPrefs.SetString(k_LocalePreferenceKey, s_CurrentLanguage);
            if (!string.Equals(previousLanguage, s_CurrentLanguage, StringComparison.Ordinal))
            {
                LanguageChanged?.Invoke();
            }
        }

        public static T LocalizedEnumPopup<T>(GUIContent label, T enumValue, string enumKey) where T : Enum
        {
            string[] names = Enum.GetNames(typeof(T));
            GUIContent[] displayedOptions = names
                .Select(name => G($"{enumKey}.{name.Replace(" ", "_").ToLowerInvariant()}"))
                .ToArray();

            int currentIndex = Array.IndexOf(names, enumValue.ToString());
            int newIndex = EditorGUILayout.Popup(label, currentIndex, displayedOptions);

            if (newIndex >= 0 && newIndex < names.Length)
            {
                return (T)Enum.Parse(typeof(T), names[newIndex]);
            }

            return enumValue;
        }

        public static void LocalizedEnumPropertyField(Rect rect, SerializedProperty property, GUIContent label, string enumKey)
        {
            if (property.propertyType != SerializedPropertyType.Enum)
            {
                EditorGUI.LabelField(rect, label.text, "Not an enum");
                return;
            }

            label = EditorGUI.BeginProperty(rect, label, property);

            GUIContent[] displayedOptions = property.enumDisplayNames
                .Select(name => G($"{enumKey}.{name.Replace(" ", "_").ToLowerInvariant()}"))
                .ToArray();

            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUI.Popup(rect, label, property.enumValueIndex, displayedOptions);
            if (EditorGUI.EndChangeCheck() && newIndex != property.enumValueIndex)
            {
                property.enumValueIndex = newIndex;
            }

            EditorGUI.EndProperty();
        }

        public static bool DisplayDialog(string key, string ok = "OK", string cancel = "")
        {
            string title = S(key + ".title", key);
            string message = S(key + ".message", "(message missing)");
            return EditorUtility.DisplayDialog(title, message, ok, cancel);
        }

        public static string S(string key)
        {
            return S(key, key);
        }

        public static string S(string key, string defaultValue)
        {
            EnsureLoaded();

            string localized = s_CurrentAsset != null ? s_CurrentAsset.GetLocalizedString(key) : key;
            if (localized != key)
            {
                return localized;
            }

            localized = s_FallbackAsset != null ? s_FallbackAsset.GetLocalizedString(key) : key;
            return localized != key ? localized : defaultValue;
        }

        public static string SF(string key, params object[] format)
        {
            return string.Format(S(key), format);
        }

        public static string FeatureName(string featureId, string defaultValue = null)
        {
            if (!string.IsNullOrEmpty(featureId))
            {
                string directKey = $"features.{featureId}.name";
                string direct = S(directKey, null);
                if (direct != null)
                {
                    return direct;
                }

                string normalized = NormalizeFeatureLocalizationKey(featureId);
                if (!string.IsNullOrEmpty(normalized))
                {
                    string mapped = S($"features.{normalized}.name", null);
                    if (mapped != null)
                    {
                        return mapped;
                    }
                }
            }

            return defaultValue ?? featureId ?? string.Empty;
        }

        public static GUIContent G(string key)
        {
            string tooltip = S(key + ".tooltip", null);
            return tooltip != null ? new GUIContent(S(key), tooltip) : new GUIContent(S(key));
        }

        public static GUIContent GF(string key, params object[] format)
        {
            string tooltip = S(key + ".tooltip", null);
            string text = string.Format(S(key), format);
            return tooltip != null ? new GUIContent(text, string.Format(tooltip, format)) : new GUIContent(text);
        }

        public static void RebuildOnLanguageChange(VisualElement element, Action rebuild)
        {
            if (element == null || rebuild == null)
            {
                return;
            }

            bool subscribed = false;
            void Update()
            {
                if (element.panel != null)
                {
                    rebuild();
                }
            }

            void Subscribe()
            {
                if (subscribed)
                {
                    return;
                }

                LanguageChanged += Update;
                subscribed = true;
            }

            void Unsubscribe()
            {
                if (!subscribed)
                {
                    return;
                }

                LanguageChanged -= Update;
                subscribed = false;
            }

            element.RegisterCallback<AttachToPanelEvent>(_ => Subscribe());
            element.RegisterCallback<DetachFromPanelEvent>(_ => Unsubscribe());
            if (element.panel != null)
            {
                Subscribe();
            }
        }

        public static void BindText(TextElement element, string key)
        {
            BindText(element, key, null);
        }

        public static void BindText(TextElement element, string key, string defaultValue)
        {
            if (element == null)
            {
                return;
            }

            void Update() => element.text = defaultValue == null ? S(key) : S(key, defaultValue);
            Update();
            RebuildOnLanguageChange(element, Update);
        }

        public static void BindLabel<TValue>(BaseField<TValue> field, string key)
        {
            BindLabel(field, key, null);
        }

        public static void BindLabel<TValue>(BaseField<TValue> field, string key, string defaultValue)
        {
            if (field == null)
            {
                return;
            }

            void Update() => field.label = defaultValue == null ? S(key) : S(key, defaultValue);
            Update();
            RebuildOnLanguageChange(field, Update);
        }

        public static void BindFoldout(Foldout foldout, string key)
        {
            BindFoldout(foldout, key, null);
        }

        public static void BindFoldout(Foldout foldout, string key, string defaultValue)
        {
            if (foldout == null)
            {
                return;
            }

            void Update() => foldout.text = defaultValue == null ? S(key) : S(key, defaultValue);
            Update();
            RebuildOnLanguageChange(foldout, Update);
        }

        public static void BindTooltip(VisualElement element, string key)
        {
            BindTooltip(element, key, null);
        }

        public static void BindTooltip(VisualElement element, string key, string defaultValue)
        {
            if (element == null)
            {
                return;
            }

            void Update() => element.tooltip = defaultValue == null ? S(key) : S(key, defaultValue);
            Update();
            RebuildOnLanguageChange(element, Update);
        }

        public static void DrawLanguageSelection()
        {
            EnsureLoaded();

            string[] langKeys = s_LanguageAssets.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            if (langKeys.Length == 0)
            {
                EditorGUILayout.Popup(G("common.language"), 0, new[] { "No Locale Available" });
                return;
            }

            string[] langDisplayNames = langKeys.Select(GetLanguageDisplayName).ToArray();
            int index = Array.IndexOf(langKeys, s_CurrentLanguage);
            if (index < 0)
            {
                index = 0;
            }

            int newIndex = EditorGUILayout.Popup(G("common.language"), index, langDisplayNames);
            if (newIndex != index && newIndex >= 0 && newIndex < langKeys.Length)
            {
                LoadLanguage(langKeys[newIndex]);
            }
        }

        public static PopupField<string> CreateLanguageDropdown()
        {
            EnsureLoaded();

            var langKeys = s_LanguageAssets.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
            if (langKeys.Count == 0)
            {
                langKeys.Add(k_DefaultLocale);
            }

            string initial = langKeys.Contains(s_CurrentLanguage) ? s_CurrentLanguage : langKeys[0];
            var dropdown = new PopupField<string>(
                S("common.language", "Language"),
                langKeys,
                initial,
                GetLanguageDisplayName,
                GetLanguageDisplayName);
            dropdown.RegisterValueChangedCallback(evt =>
            {
                if (!string.IsNullOrEmpty(evt.newValue))
                {
                    LoadLanguage(evt.newValue);
                }
            });
            return dropdown;
        }

        private static string NormalizeFeatureLocalizationKey(string raw)
        {
            switch (raw)
            {
                case "blendshare.blendshape":
                case "blendshare.blend-shapes":
                case "blendshape":
                case "blendshapes":
                case "blend-shapes":
                    return "blend-shapes";
                case "blendshare.skinweight":
                case "blendshare.skin-weights":
                case "skinweight":
                case "skinweights":
                case "skin-weights":
                    return "skin-weights";
                case "blendshare.uv":
                case "uvs":
                case "uv":
                    return "uv";
                default:
                    if (raw.StartsWith("blendshare.", StringComparison.Ordinal))
                    {
                        return raw.Substring("blendshare.".Length);
                    }

                    return null;
            }
        }

        private static void EnsureLoaded()
        {
            if (!s_Loaded)
            {
                LoadAvailableLanguages();
                LoadLanguage(EditorPrefs.GetString(k_LocalePreferenceKey, k_DefaultLocale));
            }
        }

        public static string GetLanguageDisplayName(string langKey)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(langKey) || !s_LanguageAssets.TryGetValue(langKey, out var asset) || asset == null)
            {
                return langKey ?? string.Empty;
            }

            string localeNameKey = $"locale:{langKey}";
            string localeName = asset.GetLocalizedString(localeNameKey);
            return localeName != localeNameKey ? localeName : langKey;
        }
    }
}
