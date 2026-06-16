using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Triturbo.BlendShapeShare
{
    public static class Localization
    {
        private const string DefaultLocale = "en-US";
        private const string LocalePreferenceKey = "com.triturbo.blendshare.localization.locale";
        private const string LocalizationFolderGuid = "42b02b3f92611074a9fab944b5ec1054";

        private static readonly Dictionary<string, LocalizationAsset> LanguageAssets = new();
        private static LocalizationAsset currentAsset;
        private static LocalizationAsset fallbackAsset;
        private static string currentLanguage = DefaultLocale;
        private static bool loaded;

        public static event Action LanguageChanged;

        public static string CurrentLanguage
        {
            get
            {
                EnsureLoaded();
                return currentLanguage;
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
            LanguageAssets.Clear();
            loaded = true;

            string localizationPathRoot = AssetDatabase.GUIDToAssetPath(LocalizationFolderGuid);
            if (string.IsNullOrEmpty(localizationPathRoot) || !Directory.Exists(localizationPathRoot))
            {
                fallbackAsset = null;
                currentAsset = null;
                return;
            }

            foreach (string file in Directory.GetFiles(localizationPathRoot, "*.po"))
            {
                if (Path.GetFileName(file).StartsWith("._", StringComparison.Ordinal))
                {
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath<LocalizationAsset>(file);
                if (asset != null)
                {
                    LanguageAssets[Path.GetFileNameWithoutExtension(file)] = asset;
                }
            }

            LanguageAssets.TryGetValue(DefaultLocale, out fallbackAsset);
            if (!LanguageAssets.TryGetValue(currentLanguage, out currentAsset))
            {
                currentAsset = fallbackAsset;
                currentLanguage = DefaultLocale;
            }
        }

        public static void LoadLanguage(string langKey)
        {
            EnsureLoaded();

            string previousLanguage = currentLanguage;
            if (!LanguageAssets.TryGetValue(langKey, out currentAsset))
            {
                currentAsset = fallbackAsset;
                langKey = DefaultLocale;
            }

            currentLanguage = langKey;
            EditorPrefs.SetString(LocalePreferenceKey, currentLanguage);
            if (!string.Equals(previousLanguage, currentLanguage, StringComparison.Ordinal))
            {
                LanguageChanged?.Invoke();
            }
        }

        public static T LocalizedEnumPopup<T>(GUIContent label, T enumValue, string enumKey) where T : Enum
        {
            string[] names = Enum.GetNames(typeof(T));
            GUIContent[] displayedOptions = names
                .Select(name => G($"{enumKey}.{name.Replace(" ", "_").ToLower()}"))
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
                .Select(name => G($"{enumKey}.{name.Replace(" ", "_").ToLower()}"))
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
            var title = S(key + ".title", key);
            var message = S(key + ".message", "(message missing)");

            return EditorUtility.DisplayDialog(title, message, ok, cancel);
        }

        public static string S(string key)
        {
            return S(key, key);
        }

        public static string S(string key, string defaultValue)
        {
            EnsureLoaded();

            string localized = currentAsset != null ? currentAsset.GetLocalizedString(key) : key;
            if (localized != key)
            {
                return localized;
            }

            localized = fallbackAsset != null ? fallbackAsset.GetLocalizedString(key) : key;
            return localized != key ? localized : defaultValue;
        }

        public static string SF(string key, params object[] format)
        {
            return string.Format(S(key), format);
        }

        public static string FeatureName(string featureId, string defaultValue = null)
        {
            return S($"features.{featureId}.name", defaultValue ?? featureId);
        }

        public static GUIContent G(string key)
        {
            var tooltip = S(key + ".tooltip", null);
            return tooltip != null ? new GUIContent(S(key), tooltip) : new GUIContent(S(key));
        }

        public static GUIContent GF(string key, params object[] format)
        {
            var tooltip = S(key + ".tooltip", null);
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

            var langKeys = LanguageAssets.Keys.OrderBy(key => key).ToArray();
            if (langKeys.Length == 0)
            {
                EditorGUILayout.Popup(G("common.language"), 0, new[] { "No Locale Available" });
                return;
            }

            string[] langDisplayNames = langKeys.Select(GetLanguageDisplayName).ToArray();
            int index = Array.IndexOf(langKeys, currentLanguage);
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

        private static void EnsureLoaded()
        {
            if (!loaded)
            {
                LoadAvailableLanguages();
                LoadLanguage(EditorPrefs.GetString(LocalePreferenceKey, DefaultLocale));
            }
        }

        private static string GetLanguageDisplayName(string langKey)
        {
            if (!LanguageAssets.TryGetValue(langKey, out var asset) || asset == null)
            {
                return langKey;
            }

            string localeNameKey = $"locale:{langKey}";
            string localeName = asset.GetLocalizedString(localeNameKey);
            return localeName != localeNameKey ? localeName : langKey;
        }
    }
}
