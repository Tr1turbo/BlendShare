using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using nadena.dev.ndmf.localization;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Triturbo.BlendShapeShare.Ndmf.Editor
{
    [InitializeOnLoad]
    internal static class Localization
    {
        public static event Action OnLangChange;
        private const string FallbackLanguage = "en";

        private const string LocalizationPathGuid = "5c1396f6b1c94907a6e32bccada2929a";
        private static readonly string LocalizationPathRoot = AssetDatabase.GUIDToAssetPath(LocalizationPathGuid);

        private static readonly ImmutableDictionary<string, string> SupportedLanguageDisplayNames
            = ImmutableDictionary<string, string>.Empty
                .Add("en-US", "English");

        private static readonly ImmutableList<string>
            SupportedLanguages = new[] {"en-US"}.ToImmutableList();

        private static string[] _displayNames = SupportedLanguages.Select(l => 
            CollectionExtensions.GetValueOrDefault(SupportedLanguageDisplayNames, l, l))
            .ToArray();

        private static Dictionary<string, ImmutableDictionary<string, string>> _cache = new();

        internal static string OverrideLanguage { get; set; } = null;

        public static Localizer L { get; }

        static Localization()
        {
            var localizer = new Localizer(SupportedLanguages[0], () =>
            {
                return SupportedLanguages.Select(lang => (lang, LanguageLookup(lang))).ToList();
            });
            
            L = localizer;
            
            LanguagePrefs.RegisterLanguageChangeCallback(typeof(Localization), _ => OnLangChange?.Invoke());
        }

        private static Func<string,string> LanguageLookup(string lang)
        {
            var filename = LocalizationPathRoot + "/" + lang + ".json";

            try
            {
                var langData = File.ReadAllText(filename);
                var langMap = JsonConvert.DeserializeObject<Dictionary<string, string>>(langData);

                return key => langMap.GetValueOrDefault(key);
            }
            catch (Exception e)
            {
                Debug.LogError("Failed to load language file " + filename);
                Debug.LogException(e);
                return _ => null;
            }
        }

        public static GUIContent G(string key)
        {
            var tooltip = S(key + ".tooltip", null);
            return tooltip != null ? new GUIContent(S(key), tooltip) : new GUIContent(S(key));
        }

        public static string S(string key)
        {
            return S(key, key);
        }
        
        public static string S_f(string key, params string[] format)
        {
            try
            {
                return string.Format(S(key, key), format);
            }
            catch (FormatException)
            {
                return S(key, key) + "(" + string.Join(", ", format) + ")";
            }
        }

        public static string S(string key, string defValue)
        {
            return L.TryGetLocalizedString(key, out var val) ? val : defValue;
        }
    }
}