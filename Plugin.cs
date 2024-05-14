using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HotbarScroll
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class HotbarScrollPlugin : BaseUnityPlugin
    {
        internal const string ModName = "HotbarScroll";
        internal const string ModVersion = "1.0.2";
        internal const string Author = "Azumatt";
        private const string ModGUID = $"{Author}.{ModName}";
        private static string ConfigFileName = $"{ModGUID}.cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        private readonly Harmony _harmony = new(ModGUID);
        public static readonly ManualLogSource HotbarScrollLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);

        public enum Toggle
        {
            On = 1,
            Off = 0
        }

        public void Awake()
        {
            ModifierKey = config("1 - General", "Modifier Key", new KeyboardShortcut(KeyCode.LeftAlt), new ConfigDescription("The key that must be held to scroll the hotbar.", new AcceptableShortcuts()));
            InvertedScroll = config("1 - General", "Inverted Scroll", Toggle.Off, "Invert the scroll direction of the hotbar.");
            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            SetupWatcher();
        }

        private void OnDestroy()
        {
            Config.Save();
        }

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
                HotbarScrollLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
            }
            catch
            {
                HotbarScrollLogger.LogError($"There was an issue loading your {ConfigFileName}");
                HotbarScrollLogger.LogError("Please check your config entries for spelling and format!");
            }
        }


        #region ConfigOptions

        internal static ConfigEntry<KeyboardShortcut> ModifierKey = null!;
        internal static ConfigEntry<Toggle> InvertedScroll = null!;

        private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description)
        {
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

            return configEntry;
        }

        private ConfigEntry<T> config<T>(string group, string name, T value, string description)
        {
            return config(group, name, value, new ConfigDescription(description));
        }

        private class ConfigurationManagerAttributes
        {
            [UsedImplicitly] public int? Order = null!;
            [UsedImplicitly] public bool? Browsable = null!;
            [UsedImplicitly] public string? Category = null!;
            [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
        }

        class AcceptableShortcuts : AcceptableValueBase
        {
            public AcceptableShortcuts() : base(typeof(KeyboardShortcut))
            {
            }

            public override object Clamp(object value) => value;
            public override bool IsValid(object value) => true;

            public override string ToDescriptionString() => $"# Acceptable values: {string.Join(", ", UnityInput.Current.SupportedKeyCodes)}";
        }

        #endregion
    }

    public static class KeyboardExtensions
    {
        public static bool IsKeyDown(this KeyboardShortcut shortcut)
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
        }

        public static bool IsKeyHeld(this KeyboardShortcut shortcut)
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
        }
    }

    [HarmonyPatch(typeof(Menu), nameof(Menu.IsVisible))]
    static class MenuIsVisiblePatch
    {
        static void Postfix(Menu __instance, ref bool __result)
        {
            if (HudUpdatePatch.ShouldBlockCameraScroll)
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(HotkeyBar), nameof(HotkeyBar.OnEnable))]
    static class HotkeyBarOnEnablePatch
    {
        public const string HotbarScrollSelection = "HotbarScrollSelection";

        static void Postfix(HotkeyBar __instance)
        {
            HudUpdatePatch.CreateSelectionObjects(__instance);
        }
    }

    [HarmonyPatch(typeof(HotkeyBar), nameof(HotkeyBar.Update))]
    static class HudUpdatePatch
    {
        public static bool ShouldBlockCameraScroll = false;

        [HarmonyPriority(Priority.VeryHigh)]
        static void Prefix(HotkeyBar __instance)
        {
            if (__instance.name != "HotKeyBar" || __instance.m_elements.Count == 0) return;

            Player player = Player.m_localPlayer;
            if (player == null) return;

            if (InventoryGui.IsVisible() || GameCamera.InFreeFly() || Minimap.IsOpen() || Hud.IsPieceSelectionVisible() || StoreGui.IsVisible() || Console.IsVisible() || Chat.instance.HasFocus() || PlayerCustomizaton.IsBarberGuiVisible() || Hud.InRadial())
                return;

            if (HotbarScrollPlugin.ModifierKey.Value.IsKeyHeld())
            {
                float scrollDelta = ZInput.GetMouseScrollWheel();
                if (HotbarScrollPlugin.InvertedScroll.Value == HotbarScrollPlugin.Toggle.On)
                {
                    scrollDelta *= -1;
                }

                if (scrollDelta == 0) return;
                ScrollHotbar(__instance, scrollDelta);
                Input.ResetInputAxes();
                ShouldBlockCameraScroll = true;
            }
            else if (HotbarScrollPlugin.ModifierKey.Value.IsUp())
            {
                ShouldBlockCameraScroll = false;
                UseSelectedItem(__instance);
            }
        }

        private static void ScrollHotbar(HotkeyBar hotkeyBar, float scrollDelta)
        {
            int newIndex = hotkeyBar.m_selected + (scrollDelta > 0 ? 1 : -1);
            if (newIndex >= hotkeyBar.m_elements.Count)
                newIndex = 0;
            else if (newIndex < 0)
                newIndex = hotkeyBar.m_elements.Count - 1;

            hotkeyBar.m_selected = newIndex;
            UpdateSelection(hotkeyBar, newIndex);
        }

        private static void UseSelectedItem(HotkeyBar hotkeyBar)
        {
            Player player = Player.m_localPlayer;
            if (player == null || hotkeyBar.m_selected < 0 || hotkeyBar.m_selected >= hotkeyBar.m_elements.Count) return;
            player.UseHotbarItem(hotkeyBar.m_selected + 1);
            DeactivateSelection(hotkeyBar, hotkeyBar.m_selected);
        }

        private static void UpdateSelection(HotkeyBar hotkeyBar, int index)
        {
            bool needsCreation = false;

            for (int i = 0; i < hotkeyBar.m_elements.Count; ++i)
            {
                HotkeyBar.ElementData? element = hotkeyBar.m_elements[i];
                GameObject selection = element.m_go.transform.Find(HotkeyBarOnEnablePatch.HotbarScrollSelection)?.gameObject!;

                if (selection != null) continue;
                needsCreation = true;
                break;
            }

            if (needsCreation)
            {
                CreateSelectionObjects(hotkeyBar);
            }

            for (int i = 0; i < hotkeyBar.m_elements.Count; ++i)
            {
                HotkeyBar.ElementData? element = hotkeyBar.m_elements[i];
                GameObject selection = element.m_go.transform.Find(HotkeyBarOnEnablePatch.HotbarScrollSelection).gameObject;
                selection.SetActive(i == index);
            }
        }

        public static void CreateSelectionObjects(HotkeyBar hotkeyBar)
        {
            if (hotkeyBar.m_elements.Count <= 0 || hotkeyBar.m_elements[0].m_selection == null) return;
            GameObject originalSelection = hotkeyBar.m_elements[0].m_selection;

            for (int i = 0; i < hotkeyBar.m_elements.Count; ++i)
            {
                HotkeyBar.ElementData element = hotkeyBar.m_elements[i];
                GameObject newSelection = Object.Instantiate(originalSelection, element.m_go.transform);
                newSelection.name = HotkeyBarOnEnablePatch.HotbarScrollSelection;
                newSelection.SetActive(false);
            }
        }

        private static void DeactivateSelection(HotkeyBar hotkeyBar, int index)
        {
            GameObject selection = hotkeyBar.m_elements[index].m_go.transform.Find(HotkeyBarOnEnablePatch.HotbarScrollSelection)?.gameObject!;
            if (selection != null) selection.SetActive(false);
        }
    }
}