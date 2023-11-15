using HarmonyLib;
using Newtonsoft.Json;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityModManagerNet;
using static UnityModManagerNet.UnityModManager;

namespace WorkshopInstaller {
    static class Main {
        internal static Harmony HarmonyInstance;
        internal static UnityModManager.ModEntry.ModLogger log;
        internal static string persistentPath;
        public static Settings settings;

        static bool Load(UnityModManager.ModEntry modEntry) {
            log = modEntry.Logger;
            settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUnload = OnUnload;
            HarmonyInstance = new Harmony(modEntry.Info.Id);
            HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
            persistentPath = Application.persistentDataPath;
            if (!SteamManager.Initialized) {
                log.Log("Steamworks not initialized; Can't handle subscribed items");
                return false;
            }
            HandleSubscribedItems();
            return true;
        }
        private static Callback<DownloadItemResult_t> downloadItemResultCallback;
        public static void HandleSubscribedItems() {
            try {
                EnsureDirectories();
                Callback<DownloadItemResult_t>.Create(OnDownloadItemResult);
                var count = SteamUGC.GetNumSubscribedItems();
                PublishedFileId_t[] subscribedItems = new PublishedFileId_t[count];
                uint trueCount = SteamUGC.GetSubscribedItems(subscribedItems, count);
                if (trueCount != count) {
                    log.Log($"GetNumSubscribedItems returned {count} but actually fetched {trueCount} items!");
                }
                log.Log($"Fetched {trueCount} subscribed workshop items!");
                // Check for leftover files of unsubscribed stuff
                foreach (var item in settings.installedItems.Keys.ToList()) {
                    var IsSubscribed = (SteamUGC.GetItemState(item) & 1) > 0;
                    if (!IsSubscribed) {
                        UninstallLocally(item);
                    }
                }
                // Check for new mods/needed updates
                for (int i = 0; i < trueCount; i++) {
                    var item = subscribedItems[i];
                    log.Log($"Checking Workshop Item with ID {item}");
                    var state = SteamUGC.GetItemState(item);
                    // https://partner.steamgames.com/doc/api/ISteamUGC#EItemState
                    bool IsSubscribed = (state & 1) > 0;
                    bool IsInstalled = (state & 4) > 0;
                    bool NeedsUpdate = (state & 8) > 0;
                    log.Log($"{IsSubscribed} : {IsInstalled} : {NeedsUpdate} : {Convert.ToString(state, 2)}");
                    if (IsSubscribed) {
                        if (!IsInstalled) {
                            log.Log($"Trying to install Workshop item with ID: {item}");
                            SteamUGC.DownloadItem(item, true);
                        } else if (NeedsUpdate) {
                            log.Log($"Trying to update Workshop item with ID: {item}");
                            SteamUGC.DownloadItem(item, true);
                        } else {
                            uint MaxPathLength = 256;
                            bool isProperlyInstalled = SteamUGC.GetItemInstallInfo(item, out var size, out var DirectoryPath, MaxPathLength, out var TimeStamp);
                            if (isProperlyInstalled) {
                                log.Log($"Path to installed files is: {DirectoryPath}");
                                InstallLocally(item, DirectoryPath);
                            } else {
                                log.Log($"Download succeeded but GetItemInstallInfo returned false, meaning item is not properly installed?");
                            }
                        }
                    } else {
                        if (IsInstalled) {
                            log.Log($"Trying to uninstall Workshop item with ID: {item}");
                            SteamUGC.UnsubscribeItem(item);
                            UninstallLocally(item);
                        }
                    }
                }
            } catch (Exception ex) {
                log.Log(ex.ToString());
            }
        }
        public static void OnDownloadItemResult(DownloadItemResult_t callback) {
            var currentApp = new AppId_t(2186680);
            if (callback.m_unAppID == currentApp) {
                /* This might cause problems when updating an Owlcat Template Mod that's already loaded? Instead updating is delayed until after restart.
                if (callback.m_eResult == EResult.k_EResultOK) {
                    log.Log($"Download Succeeded for ItemID: {callback.m_nPublishedFileId}");
                    uint MaxPathLength = 256;
                    bool isProperlyInstalled = SteamUGC.GetItemInstallInfo(callback.m_nPublishedFileId, out var size, out var DirectoryPath, MaxPathLength, out var TimeStamp);
                    if (isProperlyInstalled) {
                        log.Log($"Path to installed files is: {DirectoryPath}");
                        InstallLocally(callback.m_nPublishedFileId, DirectoryPath);
                    } else {
                        log.Log($"Download succeeded but GetItemInstallInfo returned false, meaning item is not properly installed?");
                    }
                } else {
                    log.Log($"Download Failed for ItemID: {callback.m_nPublishedFileId} with result: {callback.m_eResult}");
                }
                */
            }
        }
        public static void InstallLocally(PublishedFileId_t item, string pathToFiles) {
            var dir = new DirectoryInfo(Path.Combine(new FileInfo(Assembly.GetExecutingAssembly().Location).Directory.FullName, "temp"));
            log.Log($"Creating temp dir at {dir} because executed in: {new FileInfo(Assembly.GetExecutingAssembly().Location).Directory.FullName}");
            if (dir.Exists) {
                dir.Delete(true);
            }
            dir.Create();
            try {
                try {
                    string zipFile;
                    if (pathToFiles.EndsWith(@"_legacy.bin")) {
                        log.Log("Steam _legacy.bin file discovered!");
                        zipFile = pathToFiles;
                    } else {
                        zipFile = Path.Combine(pathToFiles, Directory.GetFiles(pathToFiles, "*.zip")[0]);
                    }
                    ZipFile.ExtractToDirectory(zipFile, dir.FullName);
                } catch (IndexOutOfRangeException ex) {
                    foreach (var file in Directory.GetFiles(pathToFiles)) {
                        File.Copy(Path.Combine(pathToFiles, file), Path.Combine(dir.FullName, Path.GetFileName(file)), true);
                    }
                }
                OwlcatTemplateClass modInfo = null;
                try {
                    modInfo = JsonConvert.DeserializeObject<OwlcatTemplateClass>(File.ReadAllText(Path.Combine(dir.FullName, "OwlcatModificationManifest.json")));
                    if (modInfo == null) {
                        throw new Exception("Deserialization of Modinfo resulted in Null");
                    }
                } catch (Exception ex) {
                    log.Log($"Can't read manifest of mod with id {item} at {Path.Combine(dir.FullName, "OwlcatModificationManifest.json")}");
                    log.Log(ex.ToString());
                    return;
                }
                bool isUMM = new FileInfo(Path.Combine(dir.FullName, "Info.json")).Exists;
                string ModManagerPath;
                if (isUMM) {
                    ModManagerPath = Path.Combine(persistentPath, "UnityModManager");
                } else {
                    ModManagerPath = Path.Combine(persistentPath, "Modifications");
                }
                DirectoryInfo targetDir = new DirectoryInfo(Path.Combine(ModManagerPath, modInfo.UniqueName));
                targetDir.Create();
                foreach (var file in Directory.GetFiles(dir.FullName)) {
                    File.Copy(Path.Combine(dir.FullName, file), Path.Combine(targetDir.FullName, Path.GetFileName(file)), true);
                }
                settings.installedItems[item] = modInfo.UniqueName;
                if (!isUMM) HandleManagerSettings(true, item);
            } catch (Exception ex) {
                log.Log(ex.ToString());
            } finally {
                dir.Delete(true);
            }
        }
        public static void UninstallLocally(PublishedFileId_t item) {
            try {
                var UMMDir = Path.Combine(persistentPath, "UnityModManager");
                var OwlcatTemplateDir = Path.Combine(persistentPath, "Modifications");
                foreach (var directory in Directory.GetDirectories(UMMDir)) {
                    if (Path.GetFileName(directory) == settings.installedItems[item]) {
                        new DirectoryInfo(Path.Combine(UMMDir, Path.GetFileName(directory))).Delete(true);
                    }
                }
                foreach (var directory in Directory.GetDirectories(OwlcatTemplateDir)) {
                    if (Path.GetFileName(directory) == settings.installedItems[item]) {
                        new DirectoryInfo(Path.Combine(OwlcatTemplateDir, Path.GetFileName(directory))).Delete(true);
                        HandleManagerSettings(false, item);
                    }
                }
                settings.installedItems.Remove(item);
            } catch (Exception ex) {
                log.Log(ex.ToString());
            }
        }
        public static void HandleManagerSettings(bool install, PublishedFileId_t item) {
            var filePath = Path.Combine(persistentPath, "OwlcatModificationManagerSettings.json");
            ModificationManagerSettings ModManagerSettings = JsonConvert.DeserializeObject<ModificationManagerSettings>(File.ReadAllText(filePath));
            if (install) {
                if (!ModManagerSettings.EnabledModifications.Contains(settings.installedItems[item])) {
                    ModManagerSettings.EnabledModifications.Add(settings.installedItems[item]);
                }
            } else if (ModManagerSettings.EnabledModifications.Contains(settings.installedItems[item])) {
                ModManagerSettings.EnabledModifications.Remove(settings.installedItems[item]);
            }
            File.WriteAllText(filePath, JsonConvert.SerializeObject(ModManagerSettings, Formatting.Indented));
        }
        public static void EnsureDirectories() {
            var filePath = Path.Combine(persistentPath, "OwlcatModificationManagerSettings.json");
            ModificationManagerSettings ModManagerSettings = null;
            bool needInit = true;
            if (File.Exists(filePath)) {
                ModManagerSettings = JsonConvert.DeserializeObject<ModificationManagerSettings>(File.ReadAllText(filePath));
                if (ModManagerSettings != null && ModManagerSettings.EnabledModifications != null) {
                    needInit = false;
                    if (ModManagerSettings.SourceDirectory == null || ModManagerSettings.SourceDirectory?.Count < 2) {
                        ModManagerSettings.SourceDirectory = new() { "UnityModManager", "Modifications" };
                    }
                }
            }
            if (needInit) {
                ModManagerSettings = new();
                ModManagerSettings.SourceDirectory = new() { "UnityModManager", "Modifications" };
                ModManagerSettings.EnabledModifications = new();
            }
            File.WriteAllText(filePath, JsonConvert.SerializeObject(ModManagerSettings, Formatting.Indented));
            new DirectoryInfo(Path.Combine(persistentPath, "UnityModManager")).Create();
            new DirectoryInfo(Path.Combine(persistentPath, "Modifications")).Create();
        }
        public static void OnSaveGUI(UnityModManager.ModEntry modEntry) {
            settings.Save(modEntry);
        }
        public static bool OnUnload(UnityModManager.ModEntry modEntry) {
            downloadItemResultCallback.Dispose();
            return true;
        }
        [Serializable]
        public class ModificationManagerSettings {
            [JsonProperty]
            public List<string> SourceDirectory;
            [JsonProperty]
            public List<string> EnabledModifications;
        }
        [Serializable]
        public class OwlcatTemplateClass {
            [JsonProperty]
            public string UniqueName;
            [JsonProperty]
            public string Version;
            [JsonProperty]
            public string DisplayName;
            [JsonProperty]
            public string Description;
            [JsonProperty]
            public string Author;
            [JsonProperty]
            public string ImageName;
            [JsonProperty]
            public string WorkshopId;
            [JsonProperty]
            public string Repository;
            [JsonProperty]
            public string HomePage;
            [JsonProperty]
            public IEnumerable<IDictionary<string, string>> Dependencies;
        }
    }
}