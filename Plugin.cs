using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using LocalizationManager;
using ServerSync;
using UnityEngine;

namespace Rebuilt
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class RebuiltPlugin : BaseUnityPlugin
    {
        internal const string ModName = "Rebuilt";
        internal const string ModVersion = "1.0.0";
        internal const string Author = "RustyMods";
        private const string ModGUID = Author + "." + ModName;
        private static readonly string ConfigFileName = ModGUID + ".cfg";
        private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        internal static string ConnectionError = "";
        private readonly Harmony _harmony = new(ModGUID);
        public static readonly ManualLogSource RebuiltLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
        private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };
        private static readonly Dictionary<string, AssetBundle> m_bundles = new();
    
        public static AssetBundle GetAssetBundle(string fileName)
        {
            if (m_bundles.TryGetValue(fileName, out AssetBundle bundle)) return bundle;
            Assembly execAssembly = Assembly.GetExecutingAssembly();
            string resourceName = execAssembly.GetManifestResourceNames().Single(str => str.EndsWith(fileName));
            using Stream? stream = execAssembly.GetManifestResourceStream(resourceName);
            var assetBundle = AssetBundle.LoadFromStream(stream);
            m_bundles[fileName] = assetBundle;
            return assetBundle;
        }
        
        private static ConfigEntry<Toggle> _serverConfigLocked = null!;
        public static ConfigEntry<Toggle> _enabled = null!;
        public static ConfigEntry<Toggle> _requireResources = null!;
        public static ConfigEntry<float> _transparency = null!;
        public static ConfigEntry<Toggle> _ghostSupports = null!;
        public static ConfigEntry<string> _blacklist = null!;
        public static ConfigEntry<Toggle> _hasCreator = null!;
        public static ConfigEntry<Toggle> _requireWard = null!;
        public enum Toggle { On = 1, Off = 0 }

        public void Awake()
        {
            Localizer.Load();
            _serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
            _requireResources = config("2 - Settings", "Require Resources", Toggle.On, "If on, rebuilding a piece will consume resources, and ghosting it will return those resources.");
            _transparency = config("2 - Settings", "Transparency", 0.5f, new ConfigDescription("Adjust the transparency level of ghost pieces. Set between 0 (fully invisible) and 1 (fully opaque) host piece", new AcceptableValueRange<float>(0f, 1f)));
            _enabled = config("2 - Settings", "Enabled", Toggle.On, "If turned off, all ghost pieces will automatically get removed");
            _enabled.SettingChanged += GhostPiece.OnEnableConfigChange;
            _ghostSupports = config("2 - Settings", "Supports", Toggle.Off, "If on, ghost pieces will provide structural support and affect stability of real pieces");
            _ghostSupports.SettingChanged += GhostPiece.OnSupportConfigChange;
            _blacklist = config("3 - Blacklist", "Prefab Names", "", new ConfigDescription(
                "Comma-separated list of prefab names that will be excluded from ghosting.", null, new ConfigurationManagerAttributes()
                {
                    Category = "3 - Blacklist",
                    CustomDrawer = SerializedNameList.Draw
                }));
            _hasCreator = config("2 - Settings", "Require Creator", Toggle.On, "If on, a piece must have a creator assigned in order to become a ghost");
            _requireWard = config("2 - Settings", "Require Ward", Toggle.Off, "If on, a ward must be present and active in the area for a piece to become a ghost.");
            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();
        }

        private void OnDestroy() => Config.Save();

        private void SetupWatcher()
        {
            FileSystemWatcher watcher = new(Paths.ConfigPath, ConfigFileName);
            watcher.Changed += ReadConfigValues;
            watcher.Created += ReadConfigValues;
            watcher.Renamed += ReadConfigValues;
            watcher.IncludeSubdirectories = true;
            watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            if (!File.Exists(ConfigFileFullPath)) return;
            try
            {
                RebuiltLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
            }
            catch
            {
                RebuiltLogger.LogError($"There was an issue loading your {ConfigFileName}");
                RebuiltLogger.LogError("Please check your config entries for spelling and format!");
            }
        }
        private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description,
            bool synchronizedSetting = true)
        {
            ConfigDescription extendedDescription =
                new(
                    description.Description +
                    (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
                    description.AcceptableValues, description.Tags);
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
            SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true)
        {
            return config(group, name, value, new ConfigDescription(description), synchronizedSetting);
        }

        private class ConfigurationManagerAttributes
        {
            [UsedImplicitly] public int? Order;
            [UsedImplicitly] public bool? Browsable;
            [UsedImplicitly] public string? Category;
            [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer;
        }
        
        public class SerializedNameList
        {
            public readonly List<string> m_names;

            public SerializedNameList(List<string> prefabs) => m_names = prefabs;

            public SerializedNameList(params string[] prefabs) => m_names = prefabs.ToList(); 

            public SerializedNameList(string config) => m_names = config.Split(',').ToList();
            
            public override string ToString() => string.Join(",", m_names);

            public static void Draw(ConfigEntryBase cfg)
            {
                bool locked = cfg.Description.Tags
                    .Select(a =>
                        a.GetType().Name == "ConfigurationManagerAttributes"
                            ? (bool?)a.GetType().GetField("ReadOnly")?.GetValue(a)
                            : null).FirstOrDefault(v => v != null) ?? false;
                bool wasUpdated = false;
                List<string> prefabs = new();
                GUILayout.BeginVertical();
                foreach (var prefab in new SerializedNameList((string)cfg.BoxedValue).m_names)
                {
                    GUILayout.BeginHorizontal();
                    var prefabName = prefab;
                    var nameField = GUILayout.TextField(prefab);
                    if (nameField != prefab && !locked)
                    {
                        wasUpdated = true;
                        prefabName = nameField;
                    }

                    if (GUILayout.Button("x", new GUIStyle(GUI.skin.button) { fixedWidth = 21 }) && !locked)
                    {
                        wasUpdated = true;
                    }
                    else
                    {
                        prefabs.Add(prefabName);
                    }

                    if (GUILayout.Button("+", new GUIStyle(GUI.skin.button) { fixedWidth = 21 }) && !locked)
                    {
                        prefabs.Add("");
                        wasUpdated = true;
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
                if (wasUpdated)
                {
                    cfg.BoxedValue = new SerializedNameList(prefabs).ToString();
                }
            }
        }
    }
}