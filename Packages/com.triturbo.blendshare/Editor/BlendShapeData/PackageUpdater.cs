using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Triturbo.BlendShapeShare
{
    public class PackageUpdater: EditorWindow
    {
        private const string githubOwner = "Tr1turbo";
        private const string githubRepo = "BlendShare";
        private const string packageJsonPath = "Packages/com.triturbo.blendshare/package.json";
        
        
       [MenuItem("Tools/BlendShare/Check for Update")]
        public static async void CheckAndImportLatest()
        {
            try
            {
                string localVersion = ReadLocalVersion();
                string apiUrl = $"https://api.github.com/repos/{githubOwner}/{githubRepo}/releases/latest";

                using (UnityWebRequest request = UnityWebRequest.Get(apiUrl))
                {
                    request.SetRequestHeader("User-Agent", "UnityEditor");

                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                        await Task.Yield();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        EditorUtility.DisplayDialog(
                            Localization.S("updater.update_title"),
                            Localization.SF("updater.update_check_failed", request.error),
                            "OK"
                        );
                        return;
                    }

                    var json = request.downloadHandler.text;
                    var release = JsonUtility.FromJson<GitHubRelease>(json);

                    if (release == null || string.IsNullOrEmpty(release.tag_name))
                    {
                        EditorUtility.DisplayDialog(
                            Localization.S("updater.update_title"),
                            Localization.S("updater.release_parse_failed"),
                            "OK"
                        );
                        return;
                    }

                    string latestVersion = release.tag_name.TrimStart('v');
                    if (!IsNewerVersion(localVersion, latestVersion))
                    {
                        EditorUtility.DisplayDialog(
                            Localization.S("updater.update_title"),
                            Localization.SF("updater.latest_version", localVersion),
                            "OK"
                        );
                        return;
                    }

                    // --- Ask user ---
                    int choice = EditorUtility.DisplayDialogComplex(
                        Localization.S("updater.update_available_title"),
                        Localization.SF("updater.update_available_body", localVersion, latestVersion),
                        Localization.S("updater.dialog_yes_update"),
                        Localization.S("updater.dialog_cancel"),
                        Localization.S("updater.dialog_view_release")
                    );

                    if (choice == 1) // Cancel
                        return;

                    if (choice == 2) // View Release
                    {
                        Application.OpenURL($"https://github.com/{githubOwner}/{githubRepo}/releases/latest");
                        return;
                    }

                    // --- Proceed with download ---
                    GitHubAsset unityPackage = null;
                    foreach (var asset in release.assets)
                    {
                        if (asset.name.EndsWith(".unitypackage"))
                        {
                            unityPackage = asset;
                            break;
                        }
                    }
                    if (unityPackage == null)
                    {
                        EditorUtility.DisplayDialog(
                            Localization.S("updater.update_title"),
                            Localization.S("updater.update_no_unitypackage"),
                            "OK"
                        );
                        return;
                    }

                    string savePath = Path.Combine(Application.dataPath, "../Temp", unityPackage.name);
                    Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                    await DownloadFile(unityPackage.browser_download_url, savePath);
                    AssetDatabase.ImportPackage(savePath, true);
                }
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog(
                    Localization.S("updater.update_title"),
                    Localization.SF("updater.update_error", ex.Message),
                    "OK"
                );
            }
        }

        
        private static string ReadLocalVersion()
        {
            if (!File.Exists(packageJsonPath))
            {
                Debug.LogWarning($"package.json not found at {packageJsonPath}");
                return "0.0.0";
            }

            string json = File.ReadAllText(packageJsonPath);
            // A tiny lightweight parser that just finds "version"
            int start = json.IndexOf("\"version\"");
            if (start >= 0)
            {
                int colon = json.IndexOf(':', start);
                int quote1 = json.IndexOf('"', colon + 1);
                int quote2 = json.IndexOf('"', quote1 + 1);
                if (quote1 >= 0 && quote2 > quote1)
                {
                    return json.Substring(quote1 + 1, quote2 - quote1 - 1).Trim();
                }
            }

            return "0.0.0";
        }
        
        private static bool IsNewerVersion(string current, string latest)
        {
            try
            {
                System.Version v1 = new System.Version(current);
                System.Version v2 = new System.Version(latest);
                return v2 > v1;
            }
            catch
            {
                // fallback if tag name format is weird
                return current != latest;
            }
        }
        
        
        private static async Task DownloadFile(string url, string savePath)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.downloadHandler = new DownloadHandlerFile(savePath);
                var operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    EditorUtility.DisplayProgressBar("Downloading Package", $"Progress: {request.downloadProgress * 100f:F1}%", request.downloadProgress);
                    await Task.Yield();
                }

                EditorUtility.ClearProgressBar();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"Download failed: {request.error}");
                }
            }
        }

        // --- GitHub JSON Models ---
        [System.Serializable]
        private class GitHubRelease
        {
            public string tag_name;
            public GitHubAsset[] assets;
        }

        [System.Serializable]
        private class GitHubAsset
        {
            public string name;
            public string browser_download_url;
        }
    }

}
