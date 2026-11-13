using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using HarmonyLib;
using Blocksters.MathLib;
using Blocksters.Terrain.chunk;
using Blocksters.Terrain;
using BlockStoryCore;
using BlockStoryMultiplayer;
using UnityEngine;

namespace BlockStoryMod
{
    [BepInPlugin("com.BlueKitsunex777x.blockstory.biggersafebox", "Bigger Safebox", "1.1.0")]
    [BepInDependency(Core.Guid)]
    public class SafeboxSizePlugin : BaseUnityPlugin
    {
        public const int Columns = 6, Rows = 6;
        public const int Slots = Columns * Rows;
        // what the game itself gives a safebox. online the game's server keeps these, the rest go to the server app
        public const int GameSlots = 16;

        public static bool Enabled = PlayerPrefs.GetInt("SafeboxSize_Enabled", 1) != 0;
        // online it needs a Block Story Server to keep the extra slots, anywhere else the safebox stays as the game made it
        public static bool Active => Enabled && (!BSWorld.Online || (BSServerStore.Ready && !BSWorld.InRealm));

        private void Awake()
        {
            if (!BSMKCheck.Passed(this)) return;   // added: only runs on the real BSMK Core
            try { new Harmony("com.BlueKitsunex777x.blockstory.biggersafebox").PatchAll(); L.Info("loaded, safebox " + Columns + "x" + Rows); }
            catch (Exception e) { L.Err("PatchAll", e); }

            ModRegistry.Register(new ModInfo
            {
                Name = "Bigger Safebox",
                Description = "Safeboxes get 6x6 slots. Online it needs a Block Story Server (BSMK), on other servers safeboxes stay normal. One player at a time gets the extra slots. Changes from the main menu only. Before turning it off, take items out of the bottom slots: a safebox with something past slot 16 won't open without this mod.",
                GetEnabled = () => Enabled,
                SetEnabled = on =>
                {
                    // the storage is built when the world loads, it can't change size under you
                    if (BSWorld.InWorld) { L.Warn("on/off ignored, in a world"); return; }
                    Enabled = on; PlayerPrefs.SetInt("SafeboxSize_Enabled", on ? 1 : 0); PlayerPrefs.Save(); L.Info("enabled = " + on);
                },
            });
        }

        private void Update()
        {
            try { SafeboxOnline.Tick(); }
            catch (Exception e) { L.Err("online tick", e); }
        }

        private void OnGUI()
        {
            try { SafeboxOnline.Draw(); }
            catch (Exception e) { L.Err("online draw", e); }
        }
    }

    // the game pads every saved chest out to the storage size, and without this mod it can't load a list longer than 16.
    // so the file only keeps what's really used, then taking the mod away leaves normal safeboxes behind
    [HarmonyPatch(typeof(ChestStorage), "ItemChanged")]
    internal static class SafeboxTrimPatch
    {
        private const int GameSize = 16;

        private static void Postfix(ChestStorage __instance, List<LootItem> ___lootList, Vector3i ___position, bool ___loading)
        {
            try
            {
                if (___loading || !SafeboxSizePlugin.Active || BSWorld.Online || ___lootList == null || ___lootList.Count <= GameSize) return;
                int keep = ___lootList.Count;
                while (keep > GameSize && (___lootList[keep - 1].count <= 0 || string.IsNullOrEmpty(___lootList[keep - 1].name))) keep--;
                if (keep < ___lootList.Count) __instance.terrain.world.SetChestContent(___position, ___lootList.GetRange(0, keep));
            }
            catch (Exception e) { L.Err("trim", e); }
        }
    }

    // chests and safeboxes share one storage and one window, built once. so the storage and the grid are built big enough
    // for the safebox, then the layout is switched every time a block is opened
    [HarmonyPatch(typeof(ItemStorage), "Init")]
    internal static class SafeboxStoragePatch
    {
        private static void Prefix(ItemStorage __instance)
        {
            try
            {
                // built when the world loads, before anyone knows whether a server keeps the extra slots, so it's always built big
                if (!SafeboxSizePlugin.Enabled || !(__instance is ChestStorage) || __instance.size >= SafeboxSizePlugin.Slots) return;
                L.Info("chest storage size " + __instance.size + " -> " + SafeboxSizePlugin.Slots);
                __instance.size = SafeboxSizePlugin.Slots;
            }
            catch (Exception e) { L.Err("size", e); }
        }
    }

    internal class Layout
    {
        public Vector3[] pos;
        public int count;
        public Vector3 bgScale;
        public Vector3 scale;
        public float fit = 1f;
    }

    [HarmonyPatch(typeof(UIItemStorage), "Awake")]
    internal static class SafeboxGridPatch
    {
        internal static readonly Dictionary<UIItemStorage, float> Fit = new Dictionary<UIItemStorage, float>();
        internal static readonly Dictionary<UIItemStorage, Layout> Original = new Dictionary<UIItemStorage, Layout>();

        private static void Prefix(UIItemStorage __instance)
        {
            try
            {
                if (!SafeboxSizePlugin.Enabled || !(__instance.storage is ChestStorage) || __instance.template == null) return;
                // the frame is drawn for the original grid, so the 6x6 has to be squeezed into the same room
                Fit[__instance] = Mathf.Min((float)__instance.maxColumns / SafeboxSizePlugin.Columns, (float)__instance.maxRows / SafeboxSizePlugin.Rows);
                int have = Mathf.Min(__instance.maxItemCount, __instance.maxRows * __instance.maxColumns);
                L.Info("chest grid '" + __instance.name + "' rows " + __instance.maxRows + " cols " + __instance.maxColumns + " items " + __instance.maxItemCount
                    + " spacing " + __instance.spacing + " padding " + __instance.padding + " offset " + __instance.offset + " slotIndex " + __instance.slotIndex);
                if (have >= SafeboxSizePlugin.Slots) return;
                // 6 columns wide keeps the rows tidy for the safebox, the chest layout is put back by position afterwards
                __instance.maxColumns = Mathf.Max(__instance.maxColumns, SafeboxSizePlugin.Columns);
                __instance.maxRows = Mathf.Max(__instance.maxRows, SafeboxSizePlugin.Rows);
                __instance.maxItemCount = Mathf.Max(__instance.maxItemCount, SafeboxSizePlugin.Slots);
            }
            catch (Exception e) { L.Err("grid", e); }
        }

        private static void Postfix(UIItemStorage __instance)
        {
            try
            {
                if (!SafeboxSizePlugin.Enabled || !(__instance.storage is ChestStorage) || __instance.template == null) return;
                // this runs after the grid is built with the big numbers, so the chest look has to be rebuilt by hand
                var lay = new Layout { fit = Fit.TryGetValue(__instance, out float f) ? Mathf.Min(1f, f) : 1f, scale = __instance.transform.localScale, count = __instance.Count, pos = new Vector3[__instance.Count] };
                for (int i = 0; i < lay.count; i++) lay.pos[i] = __instance[i].transform.localPosition;
                if (__instance.background != null) lay.bgScale = __instance.background.transform.localScale;
                Original[__instance] = lay;
                // the window can be built after the first open (it wakes up when first shown), so the open that just happened has to be applied now
                SafeboxOpenPatch.Apply(__instance, lay, SafeboxOpenPatch.Last);
                L.Info("chest grid built with " + lay.count + " slots");
            }
            catch (Exception e) { L.Err("grid post", e); }
        }
    }

    internal enum Look { Game, Safebox, Plain }

    [HarmonyPatch]
    internal static class SafeboxOpenPatch
    {
        internal static Look Last = Look.Game;

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ChestStorage), "LoadChest");
            yield return AccessTools.Method(typeof(ChestStorage), "LoadWorldChest");
        }

        private static void Postfix(ChestStorage __instance)
        {
            try
            {
                if (!SafeboxSizePlugin.Enabled) return;
                var pos = (Vector3i)Traverse.Create(__instance).Field("position").GetValue();
                ushort id = __instance.terrain.world[pos.X, pos.Y, pos.Z].id;
                bool safe = id == BlockTypes.Safebox.id;
                L.Info("opened block id " + id + " at " + pos + (safe ? " = safebox" : " = not a safebox"));
                Look look;
                if (!BSWorld.Online) look = SafeboxSizePlugin.Active && safe ? Look.Safebox : Look.Game;
                else
                {
                    // online the game's server only ever gets 16 slots, so nothing past 16 may be on show
                    // until the server app has said this player can have the rest
                    look = Look.Plain;
                    if (safe && SafeboxSizePlugin.Active) SafeboxOnline.Opened(__instance, pos);
                }
                Show(__instance, look);
            }
            catch (Exception e) { L.Err("open", e); }
        }

        internal static void Show(ChestStorage storage, Look look)
        {
            Last = look;
            foreach (var kv in SafeboxGridPatch.Original)
            {
                if (kv.Key == null || kv.Key.storage != storage) continue;
                Apply(kv.Key, kv.Value, look);
            }
        }

        internal static void Apply(UIItemStorage ui, Layout lay, Look look)
        {
            int cols = look == Look.Safebox ? SafeboxSizePlugin.Columns : 4;
            int shownSlots = look == Look.Safebox ? SafeboxSizePlugin.Slots : SafeboxSizePlugin.GameSlots;
            int n = ui.Count;
            for (int i = 0; i < n; i++)
            {
                var slot = ui[i];
                if (look == Look.Game) { slot.transform.localPosition = lay.pos[i]; slot.gameObject.SetActive(true); continue; }
                bool shown = i < shownSlots;
                slot.gameObject.SetActive(shown);
                if (!shown) continue;
                int row = i / cols, col = i % cols;
                slot.transform.localPosition = new Vector3(ui.padding + (col + ui.offset + 0.5f) * ui.spacing, -ui.padding - (row + 0.5f) * ui.spacing, 0f);
            }
            bool big = look == Look.Safebox;
            ui.transform.localScale = big ? lay.scale * lay.fit : lay.scale;
            if (ui.background != null)
                ui.background.transform.localScale = big
                    ? new Vector3(ui.padding * 2f + SafeboxSizePlugin.Columns * ui.spacing, ui.padding * 2f + SafeboxSizePlugin.Rows * ui.spacing, 0f)
                    : lay.bgScale;
        }
    }

    // ---------- online, on a Block Story Server ----------

    // Slots 1-16 stay with the game's server like always. Slots 17-36 are kept by the server app, and only the
    // player who has the safebox open can change them, so two people can't overwrite each other.
    internal static class SafeboxOnline
    {
        private const string Mod = "Bigger Safebox";
        private const float Renew = 5f;

        private static ChestStorage _storage;
        private static Vector3i _pos;
        private static string _key, _held;
        // what slots 17+ hold right now, kept so a refresh from the game's server (which blanks them) can be undone
        private static string _extras;
        private static float _renewAt;
        private static string _note;
        private static float _noteUntil;
        private static GUIStyle _style;

        internal static string KeyOf(Vector3i p) => p.X + "," + p.Y + "," + p.Z;

        internal static void Opened(ChestStorage storage, Vector3i pos)
        {
            string key = KeyOf(pos);
            if (_held != null && _held != key) Let();
            _storage = storage;
            _pos = pos;
            _key = key;
            BSServerStore.Claim(Mod, key, r =>
            {
                if (_key != key || _storage == null || !_storage.IsAtPosition(pos)) { if (r.Ok) BSServerStore.Release(Mod, key); return; }
                if (r.Busy) { Say((r.Holder.Length > 0 ? r.Holder : "Someone") + " is using this safebox, you get the top 16 slots"); return; }
                if (!r.Ok) { Say("The server didn't answer, you get the top 16 slots"); return; }
                _held = key;
                _extras = r.Value;
                _renewAt = Time.unscaledTime + Renew;
                // nothing saved yet means whatever the game's server sent stays, so a safebox filled before this update keeps its items
                if (r.Value != null) Fill(_storage, r.Value);
                else _extras = Write(_storage);
                SafeboxOpenPatch.Show(_storage, Look.Safebox);
            });
        }

        internal static void Tick()
        {
            if (_held == null) return;
            bool open = false;
            foreach (var kv in SafeboxGridPatch.Original)
                if (kv.Key != null && kv.Key.storage == _storage && kv.Key.gameObject.activeInHierarchy) open = true;
            if (!open || !BSWorld.Online || _storage == null || !_storage.IsAtPosition(_pos)) { Let(); return; }
            if (Time.unscaledTime < _renewAt) return;
            _renewAt = Time.unscaledTime + Renew;
            string key = _held;
            BSServerStore.Claim(Mod, key, r =>
            {
                if (_held != key || !r.Busy) return;
                // the claim ran out (a long freeze) and someone else got it. stop writing before anything gets overwritten
                _held = null;
                if (_storage != null) SafeboxOpenPatch.Show(_storage, Look.Plain);
                Say("Lost the safebox to " + r.Holder);
            });
        }

        private static void Let()
        {
            if (_held != null) BSServerStore.Release(Mod, _held);
            _held = null;
            _key = null;
            _extras = null;
        }

        internal static bool Holding(ChestStorage storage) => _held != null && storage == _storage && storage.IsAtPosition(_pos);

        internal static void Changed(ChestStorage storage)
        {
            if (!Holding(storage)) return;
            _extras = Write(storage);
            BSServerStore.Put(Mod, _held, _extras, r => { if (r.Busy) L.Warn("extra slots not saved, " + r.Holder + " has the safebox"); });
        }

        // one line per slot from 17 on, blank line for an empty slot, so items stay where they were put
        private static string Write(ChestStorage storage)
        {
            var sb = new StringBuilder();
            for (int i = SafeboxSizePlugin.GameSlots; i < storage.size; i++)
            {
                InvGameItem it = storage[i].item;
                if (it != null && it.baseItem != null && it.count > 0)
                    sb.Append(it.baseItem.name).Append('\t').Append(it.data).Append('\t').Append(it.paintData).Append('\t').Append(it.count);
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        internal static void Refreshed(ChestStorage storage)
        {
            if (Holding(storage) && _extras != null) Fill(storage, _extras);
        }

        internal static List<LootItem> Read(string text)
        {
            var list = new List<LootItem>();
            foreach (string line in (text ?? "").Split('\n'))
            {
                string[] p = line.Split('\t');
                if (p.Length == 4 && ushort.TryParse(p[1], out ushort data) && ushort.TryParse(p[2], out ushort paint) && int.TryParse(p[3], out int count))
                    list.Add(new LootItem { name = p[0], data = data, paintData = paint, count = count });
                else list.Add(new LootItem { name = "" });
            }
            return list;
        }

        private static void Fill(ChestStorage storage, string text)
        {
            List<LootItem> items = Read(text);
            var t = Traverse.Create(storage).Field("loading");
            t.SetValue(true);
            try
            {
                for (int i = SafeboxSizePlugin.GameSlots; i < storage.size; i++)
                {
                    int at = i - SafeboxSizePlugin.GameSlots;
                    LootItem it = at < items.Count ? items[at] : new LootItem { name = "" };
                    storage[i].item = it.count > 0 && !string.IsNullOrEmpty(it.name) && InvDatabase.FindByName(it.name) != null
                        ? InvDatabase.CreateItem(it.name, it.data, it.count, it.paintData) : null;
                }
            }
            finally { t.SetValue(false); }
        }

        internal static void Say(string m) { _note = m; _noteUntil = Time.unscaledTime + 5f; L.Info(m); }

        internal static void Draw()
        {
            if (_note == null || Time.unscaledTime > _noteUntil) return;
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16, fontStyle = FontStyle.Bold };
            var r = new Rect(0f, Screen.height * 0.12f, Screen.width, 26f);
            _style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), _note, _style);
            _style.normal.textColor = new Color(0.95f, 0.8f, 0.4f);
            GUI.Label(r, _note, _style);
        }
    }

    // a change in slot 17 or later goes to the server app, the game's own sync only ever carries the top 16
    [HarmonyPatch(typeof(ChestStorage), "ItemChanged")]
    internal static class SafeboxOnlineChangePatch
    {
        private static void Postfix(ChestStorage __instance, ItemSlot slot, bool ___loading)
        {
            try
            {
                if (___loading || !BSWorld.Online || slot == null || slot.Key < SafeboxSizePlugin.GameSlots) return;
                SafeboxOnline.Changed(__instance);
            }
            catch (Exception e) { L.Err("online change", e); }
        }
    }

    [HarmonyPatch(typeof(ChestStorage), nameof(ChestStorage.ApplyServerState))]
    internal static class SafeboxRefreshPatch
    {
        private static void Postfix(ChestStorage __instance)
        {
            try { if (BSWorld.Online) SafeboxOnline.Refreshed(__instance); }
            catch (Exception e) { L.Err("refresh", e); }
        }
    }

    // the game's server is only ever sent 16 slots. whether it would keep more is unknown, and a normal
    // server must never see a safebox it can't load
    [HarmonyPatch(typeof(ChestStorage), nameof(ChestStorage.TryBuildUpdateInfo))]
    internal static class SafeboxSendPatch
    {
        private static void Postfix(ref ContainerUpdateInfo info)
        {
            try
            {
                if (!BSWorld.Online || info.Slots == null || info.Slots.Length <= SafeboxSizePlugin.GameSlots) return;
                Array.Resize(ref info.Slots, SafeboxSizePlugin.GameSlots);
            }
            catch (Exception e) { L.Err("send", e); }
        }
    }

    // a world chest rolls loot for every slot and sends the lot to the server, so online it rolls for the game's 16 only
    [HarmonyPatch(typeof(ChestStorage), nameof(ChestStorage.TryClaimWorldChest))]
    internal static class SafeboxClaimPatch
    {
        private static void Prefix(ChestStorage __instance, ref int __state)
        {
            __state = __instance.size;
            if (BSWorld.Online && __instance.size > SafeboxSizePlugin.GameSlots) __instance.size = SafeboxSizePlugin.GameSlots;
        }

        private static void Postfix(ChestStorage __instance, int __state) => __instance.size = __state;
    }

    // breaking a safebox online: the game's server drops the top 16, this drops the rest. Take hands them to one player only
    [HarmonyPatch(typeof(Digger), "DropLootFromSafe")]
    internal static class SafeboxBreakPatch
    {
        private static void Postfix(Digger __instance, Vector3i currentBlock)
        {
            try
            {
                if (!BSWorld.Online || !BSServerStore.Ready || BSWorld.InRealm) return;
                Vector3i at = currentBlock;
                BSServerStore.Take("Bigger Safebox", SafeboxOnline.KeyOf(at), r =>
                {
                    if (!r.Ok || r.Value == null) { if (r.Busy) SafeboxOnline.Say(r.Holder + " still has the safebox open, its bottom slots stay saved"); return; }
                    var drop = new Vector3(at.X, at.Y, at.Z);
                    int n = 0;
                    foreach (LootItem it in SafeboxOnline.Read(r.Value))
                    {
                        if (it.count <= 0 || string.IsNullOrEmpty(it.name) || it.name == "Diamond" || it.name == "Cheat Diamond") continue;
                        InvBaseItem b = InvDatabase.FindByName(it.name);
                        if (b == null) continue;
                        __instance.inventoryCollector?.DropLoot(drop, b, it.data, it.count, true, 1f, it.paintData);
                        n++;
                    }
                    L.Info("safebox broken, dropped " + n + " stacks from the bottom slots");
                });
            }
            catch (Exception e) { L.Err("break", e); }
        }
    }
}
