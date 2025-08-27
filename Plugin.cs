using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ChildrenOfMorta_GemsSharing
{
    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.adrensnyder.com.gemssharing";
        public const string PLUGIN_NAME = "CoM Gems Sharing";
        public const string PLUGIN_VERSION = "0.1.0.0";
    }

    /// <summary>
    /// CoM Gems Sharing: keeps a shared "souls" wallet across players and tracks active player roots.
    /// </summary>
    [BepInPlugin($"{PluginInfo.PLUGIN_GUID}", $"{PluginInfo.PLUGIN_NAME}", $"{PluginInfo.PLUGIN_VERSION}")]
    public class Plugin : BaseUnityPlugin
    {
        private readonly HashSet<GameObject> _activePlayerRoots = new HashSet<GameObject>();
        private readonly Dictionary<string, GameObject> _labelToRoot = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        internal static new ManualLogSource Logger;
        internal static Plugin Instance;
        private Harmony _harmony;

        private readonly Dictionary<GameObject, Func<float>> _hpReaders = new Dictionary<GameObject, Func<float>>();
        private readonly Dictionary<GameObject, Func<float>> _maxHpReaders = new Dictionary<GameObject, Func<float>>();
        private readonly Dictionary<GameObject, string> _playerLabel = new Dictionary<GameObject, string>();

        private readonly Dictionary<GameObject, float> _lastHp = new Dictionary<GameObject, float>();
        private readonly Dictionary<GameObject, float> _lastMax = new Dictionary<GameObject, float>();

        private static readonly Regex RX_HEALTH = new Regex("(health|hp|life)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RX_MAX = new Regex("(max.*(health|hp|life)|(health|hp|life).*max)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Gems Sharing reflection cache
        private static Type _playerBaseType;
        private static MethodInfo _miModifySoulCount;
        private static PropertyInfo _piSoulsCount;

        private const float INTEGRITY_PERIOD = 1.0f;
        private bool _didInitialSoulsMerge = false;

        private void Awake()
        {
            Logger = base.Logger;
            Instance = this;

            _harmony = new Harmony($"{PluginInfo.PLUGIN_GUID}");

            TryPatchByMethodName("Zyklus.UI.PlayerHUDComponent", "InitializePlayerHUD");
            TryPatchByMethodName("Zyklus.Managers.PlayerManager", "AcquirePlayersGameobjects");

            _playerBaseType = AccessTools.TypeByName("Zyklus.Player.PlayerBase");
            _miModifySoulCount = _playerBaseType != null ? AccessTools.Method(_playerBaseType, "ModifySoulCount", new Type[] { typeof(int) }) : null;
            _piSoulsCount = _playerBaseType != null ? AccessTools.Property(_playerBaseType, "pSoulsCount") : null;

            try
            {
                _harmony.PatchAll(typeof(Patch_GemsSharing_ModifySoulCount));
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Harmony] Gems Sharing patch failed: " + ex.Message);
            }

            StartCoroutine(ScanPlayersLoop());
            // StartCoroutine(HealthWatchdogLoop()); // Disabled: used only for debugging to verify players were correctly discovered.
            StartCoroutine(SharedSoulsIntegrityLoop());

            Logger.LogInfo($"[{PluginInfo.PLUGIN_NAME}] started v{PluginInfo.PLUGIN_VERSION}");
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            Instance = null;
        }

        /// <summary>
        /// Periodically scans the scene to bind player roots and set up health readers.
        /// </summary>
        private IEnumerator ScanPlayersLoop()
        {
            var wait = new WaitForSeconds(2f);
            while (true)
            {
                try { ScanAndBindPlayers(); } catch (Exception ex) { Logger.LogError("Scan error: " + ex); }
                yield return wait;
            }
        }

        private void ScanAndBindPlayers()
        {
            if (_activePlayerRoots.Count == 0) return;

            foreach (var go in _activePlayerRoots.ToArray())
            {
                if (go == null) continue;

                if (!_hpReaders.ContainsKey(go))
                {
                    Func<float> hp, mh;
                    if (TryMakeHealthReaders(go, out hp, out mh))
                    {
                        _hpReaders[go] = hp;
                        _maxHpReaders[go] = mh ?? (() => -1f);

                        string label = LabelFromPlayerIndex(go);
                        foreach (var kv in _labelToRoot)
                            if (kv.Value == go) label = kv.Key;
                        if (string.IsNullOrEmpty(label)) label = InferPlayerLabel(go);

                        _playerLabel[go] = string.IsNullOrEmpty(label) ? null : label.ToUpperInvariant();

                        float h0 = Safe(hp), m0 = Safe(_maxHpReaders[go]);
                        _lastHp[go] = h0; _lastMax[go] = m0;

                        Logger.LogInfo("[PLAYER BOUND] " + Label(go) + " " + DescribeGO(go) + " — HP: " + h0 + "/" + m0);
                    }
                }
            }
        }

        private void RefreshActivePlayersFromManager(Component playerManager)
        {
            try
            {
                _activePlayerRoots.Clear();
                _labelToRoot.Clear();

                if (playerManager == null) return;

                var objs = new List<UnityEngine.Object>();
                var t = playerManager.GetType();

                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    var f = fields[i];
                    CollectUnityCollections(f.GetValue(playerManager), objs);
                }
                var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < props.Length; i++)
                {
                    var p = props[i];
                    if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                    CollectUnityCollections(p.GetValue(playerManager, null), objs);
                }

                var roots = new List<GameObject>();
                foreach (var o in objs)
                {
                    var go = o as GameObject ?? (o as Component)?.gameObject ?? (o as Transform)?.gameObject;
                    if (go == null) continue;

                    var root = FindCharacterRoot(go);
                    if (root != null && LooksLikePlayerRoot(root))
                    {
                        if (!_activePlayerRoots.Contains(root))
                            _activePlayerRoots.Add(root);
                    }
                }

                var ordered = rootsFromSet(_activePlayerRoots)
                    .OrderBy(g => PlayerIndexOrInf(g))
                    .ToList();

                if (ordered.Count > 0) _labelToRoot["P1"] = ordered[0];
                if (ordered.Count > 1) _labelToRoot["P2"] = ordered[1];

                foreach (var kv in _labelToRoot)
                    Logger.LogInfo("[PM MAP] " + kv.Key + " → " + kv.Value.name);

                _didInitialSoulsMerge = false;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("RefreshActivePlayersFromManager error: " + ex.Message);
            }
        }

        private static IEnumerable<GameObject> rootsFromSet(HashSet<GameObject> set)
        {
            foreach (var g in set) if (g != null) yield return g;
        }

        private static void CollectUnityCollections(object value, List<UnityEngine.Object> addTo)
        {
            if (value == null) return;

            var arr = value as Array;
            if (arr != null)
            {
                foreach (var e in arr)
                {
                    var u = e as UnityEngine.Object;
                    if (u != null) addTo.Add(u);
                }
                return;
            }

            var ilist = value as IEnumerable;
            if (ilist != null && !(value is string))
            {
                foreach (var e in ilist)
                {
                    var u = e as UnityEngine.Object;
                    if (u != null) addTo.Add(u);
                }
                return;
            }

            var single = value as UnityEngine.Object;
            if (single != null) addTo.Add(single);
        }

        private static GameObject FindCharacterRoot(GameObject start)
        {
            Transform t = start.transform;
            while (t.parent != null)
            {
                if (t.parent.name.IndexOf("Instantiate Parent", StringComparison.OrdinalIgnoreCase) >= 0)
                    return t.gameObject;
                t = t.parent;
            }
            return start;
        }

        private string LabelFromPlayerIndex(GameObject root)
        {
            int idx = PlayerIndexOrInf(root);
            if (idx == 0) return "P1";
            if (idx == 1) return "P2";
            return null;
        }

        private static readonly Regex RX_INDEX = new Regex("(player(Index|Id)|slot|index)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private int PlayerIndexOrInf(GameObject root)
        {
            int found = -1;

            var comps = root.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                var t = c.GetType();

                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int j = 0; j < fields.Length; j++)
                {
                    var f = fields[j];
                    if (f.FieldType == typeof(int) && RX_INDEX.IsMatch(f.Name))
                    {
                        try { found = (int)f.GetValue(c); if (found >= 0) return found; } catch { }
                    }
                }

                var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int j = 0; j < props.Length; j++)
                {
                    var p = props[j];
                    if (p.PropertyType == typeof(int) && p.CanRead && p.GetIndexParameters().Length == 0 && RX_INDEX.IsMatch(p.Name))
                    {
                        try { found = (int)p.GetValue(c, null); if (found >= 0) return found; } catch { }
                    }
                }
            }
            return found;
        }

        private bool LooksLikePlayerRoot(GameObject go)
        {
            Transform t = go.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c == null) continue;
                if (c.name.IndexOf("Player Number Indicator", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Optional watchdog to log HP changes (disabled by default; debugging aid).
        /// </summary>
        private IEnumerator HealthWatchdogLoop()
        {
            var wait = new WaitForSeconds(0.15f);
            while (true)
            {
                try
                {
                    foreach (var kv in _hpReaders)
                    {
                        var go = kv.Key;
                        float hp = Safe(kv.Value);
                        float mh = _maxHpReaders.ContainsKey(go) ? Safe(_maxHpReaders[go]) : -1f;

                        float prevHp = _lastHp.ContainsKey(go) ? _lastHp[go] : hp;
                        float prevMax = _lastMax.ContainsKey(go) ? _lastMax[go] : mh;

                        if (!Approximately(hp, prevHp) || !Approximately(mh, prevMax))
                        {
                            float delta = hp - prevHp;
                            string tag = delta < 0 ? "[DMG]" : "[HEAL]";
                            Logger.LogInfo(tag + " " + Label(go) + " " + go.name + " => " + hp + "/" + mh + " (" + (delta >= 0 ? "+" : "") + delta + ")");

                            _lastHp[go] = hp;
                            _lastMax[go] = mh;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("HealthWatchdog error: " + ex.Message);
                }
                yield return wait;
            }
        }

        private bool TryMakeHealthReaders(GameObject go, out Func<float> hpGetter, out Func<float> maxGetter)
        {
            hpGetter = null; maxGetter = null;

            var comps = go.GetComponentsInChildren<Component>(true)
                          .Where(c => c != null && c.gameObject == go)
                          .ToArray();

            var t = go.transform.parent;
            if (comps.Length == 0 && t != null)
                comps = t.GetComponents<Component>();

            var ordered = comps.OrderByDescending(c => c.GetType().Name.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 :
                                                      c.GetType().FullName.IndexOf(".Player", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0);

            foreach (var c in ordered)
            {
                Func<float> hp, mh;
                if (TryBuildNumericReaders(c, out hp, out mh))
                {
                    if (hpGetter == null && hp != null) hpGetter = hp;
                    if (maxGetter == null && mh != null) maxGetter = mh;
                    if (hpGetter != null && maxGetter != null) break;
                }
            }

            return hpGetter != null;
        }

        private bool TryBuildNumericReaders(Component comp, out Func<float> hpGetter, out Func<float> maxGetter)
        {
            hpGetter = null; maxGetter = null;
            var t = comp.GetType();

            var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (!p.CanRead) continue;
                if (!IsNumericType(p.PropertyType)) continue;

                if (RX_HEALTH.IsMatch(p.Name) && hpGetter == null)
                    hpGetter = () => ToSingleSafe(p.GetValue(comp, null));

                if (RX_MAX.IsMatch(p.Name) && maxGetter == null)
                    maxGetter = () => ToSingleSafe(p.GetValue(comp, null));
            }

            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (!IsNumericType(f.FieldType)) continue;

                if (RX_HEALTH.IsMatch(f.Name) && hpGetter == null)
                    hpGetter = () => ToSingleSafe(f.GetValue(comp));

                if (RX_MAX.IsMatch(f.Name) && maxGetter == null)
                    maxGetter = () => ToSingleSafe(f.GetValue(comp));
            }

            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.GetParameters().Length != 0) continue;
                if (!IsNumericType(m.ReturnType)) continue;

                var n = m.Name.ToLowerInvariant();
                if ((n.Contains("get") || n.Contains("current") || n.Contains("calc")) && (n.Contains("health") || n.Contains("hp") || n.Contains("life")))
                {
                    if (hpGetter == null) hpGetter = () => ToSingleSafe(m.Invoke(comp, null));
                }
                if (n.Contains("max") && (n.Contains("health") || n.Contains("hp") || n.Contains("life")))
                {
                    if (maxGetter == null) maxGetter = () => ToSingleSafe(m.Invoke(comp, null));
                }
            }

            return hpGetter != null || maxGetter != null;
        }

        /// <summary>
        /// Keeps all players' souls in sync; performs an initial merge and then mirrors the max value.
        /// </summary>
        private IEnumerator SharedSoulsIntegrityLoop()
        {
            var wait = new WaitForSeconds(INTEGRITY_PERIOD);
            while (true)
            {
                bool skipRestThisCycle = false;

                try
                {
                    var players = GetAllPlayerBases();
                    if (players.Count > 1)
                    {
                        var counts = new List<KeyValuePair<Component, int>>();
                        for (int i = 0; i < players.Count; i++)
                        {
                            var pb = players[i];
                            int c = Souls(pb);
                            counts.Add(new KeyValuePair<Component, int>(pb, c));
                        }

                        if (!_didInitialSoulsMerge)
                        {
                            bool anyPositive = false;
                            int sum = 0;
                            for (int i = 0; i < counts.Count; i++)
                            {
                                int v = counts[i].Value;
                                if (v > 0) anyPositive = true;
                                if (v > 0) sum += v;
                            }

                            if (anyPositive)
                            {
                                Patch_GemsSharing_ModifySoulCount.Suppress = true;
                                for (int i = 0; i < counts.Count; i++)
                                {
                                    var kv = counts[i];
                                    int cur = kv.Value < 0 ? 0 : kv.Value;
                                    int delta = sum - cur;
                                    if (delta != 0) CallModify(kv.Key, delta);
                                }
                                Patch_GemsSharing_ModifySoulCount.Suppress = false;

                                _didInitialSoulsMerge = true;
                                Logger.LogInfo($"[WALLET INIT] initial merge: sum={sum}, applied to all players.");
                                skipRestThisCycle = true;
                            }
                        }

                        if (!skipRestThisCycle)
                        {
                            int max = 0;
                            for (int i = 0; i < counts.Count; i++)
                            {
                                int v = counts[i].Value;
                                if (v > max) max = v;
                            }
                            if (max < 0) max = 0;

                            bool mismatch = counts.Any(kv => kv.Value != max);
                            if (mismatch)
                            {
                                Patch_GemsSharing_ModifySoulCount.Suppress = true;
                                foreach (var kv in counts)
                                {
                                    int cur = kv.Value < 0 ? 0 : kv.Value;
                                    int delta = max - cur;
                                    if (delta != 0) CallModify(kv.Key, delta);
                                }
                                Patch_GemsSharing_ModifySoulCount.Suppress = false;

                                Logger.LogInfo($"[WALLET SYNC] aligned all players to {max} souls.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[WALLET SYNC] error: " + ex.Message);
                }

                yield return wait;
            }
        }

        /// <summary>
        /// Returns all PlayerBase components from known player roots, with a scene fallback.
        /// </summary>
        internal List<Component> GetAllPlayerBases()
        {
            var list = new List<Component>();
            if (_playerBaseType == null) return list;

            foreach (var root in _activePlayerRoots)
            {
                if (root == null) continue;
                var pb = root.GetComponentInChildren(_playerBaseType, true) as Component;
                if (pb != null && !list.Contains(pb)) list.Add(pb);
            }

            if (list.Count == 0)
            {
                var any = GameObject.FindObjectsOfType(typeof(Component))
                                    .OfType<Component>()
                                    .Where(c => c != null && _playerBaseType.IsAssignableFrom(c.GetType()))
                                    .ToArray();
                foreach (var pb in any) if (!list.Contains(pb)) list.Add(pb);
            }

            return list;
        }

        internal static int Souls(Component playerBase)
        {
            if (playerBase == null || _piSoulsCount == null) return -1;
            try { return Convert.ToInt32(_piSoulsCount.GetValue(playerBase, null)); } catch { return -1; }
        }

        internal static void CallModify(Component playerBase, int delta)
        {
            if (playerBase == null || _miModifySoulCount == null) return;
            try { _miModifySoulCount.Invoke(playerBase, new object[] { delta }); } catch { }
        }

        internal string Label(GameObject go)
        {
            string lab;
            if (_playerLabel.TryGetValue(go, out lab) && !string.IsNullOrEmpty(lab))
                return "[" + lab + "]";
            foreach (var kv in _labelToRoot)
                if (kv.Value == go) return "[" + kv.Key.ToUpperInvariant() + "]";
            return "[P?]";
        }

        internal string LabelFromComponent(Component comp)
        {
            var root = ResolveRootFromComponent(comp);
            return root != null ? Label(root) : "[P?]";
        }

        internal GameObject ResolveRootFromComponent(Component comp)
        {
            if (comp == null) return null;

            var t = comp.transform;
            for (int i = 0; i < 10 && t != null; i++, t = t.parent)
            {
                if (_activePlayerRoots.Contains(t.gameObject)) return t.gameObject;
            }

            t = comp.transform;
            for (int i = 0; i < 10 && t != null; i++, t = t.parent)
            {
                if (LooksLikePlayerRoot(t.gameObject)) return t.gameObject;
            }

            return null;
        }

        private string InferPlayerLabel(GameObject root)
        {
            var child = root.transform.Find("Player Number Indicator");
            if (child == null)
            {
                for (int i = 0; i < root.transform.childCount; i++)
                {
                    var c = root.transform.GetChild(i);
                    if (c.name.IndexOf("Player Number", StringComparison.OrdinalIgnoreCase) >= 0) { child = c; break; }
                }
            }
            if (child == null) return null;

            var uiText = child.GetComponentInChildren<Text>(true);
            if (uiText != null && !string.IsNullOrEmpty(uiText.text))
            {
                string lab = ExtractP1P2(uiText.text);
                if (!string.IsNullOrEmpty(lab)) return lab;
            }

            var tmp = child.GetComponentsInChildren<Component>(true)
                           .FirstOrDefault(c => c != null && c.GetType().Name.Contains("TMP_Text"));
            if (tmp != null)
            {
                var prop = tmp.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null)
                {
                    var txt = prop.GetValue(tmp, null) as string;
                    string lab = ExtractP1P2(txt);
                    if (!string.IsNullOrEmpty(lab)) return lab;
                }
            }

            var img = child.GetComponentInChildren<Image>(true);
            if (img != null && img.sprite != null)
            {
                string lab = ExtractP1P2(img.sprite.name);
                if (!string.IsNullOrEmpty(lab)) return lab;
            }
            var sr = child.GetComponentInChildren<SpriteRenderer>(true);
            if (sr != null && sr.sprite != null)
            {
                string lab = ExtractP1P2(sr.sprite.name);
                if (!string.IsNullOrEmpty(lab)) return lab;
            }

            return null;
        }

        private void MapHudToPlayer(string label, Component hudComp)
        {
            var refs = CollectUnityRefs(hudComp);
            if (refs.Count == 0) { Logger.LogWarning("[MAP HUD] No refs on " + hudComp.name + " for " + label); return; }

            foreach (var r in refs)
            {
                GameObject go = r as GameObject ?? (r as Component)?.gameObject ?? (r as Transform)?.gameObject;
                if (go == null) continue;

                foreach (var root in _activePlayerRoots)
                {
                    if (root != null && IsDescendantOf(go.transform, root.transform))
                    {
                        _labelToRoot[label.ToUpperInvariant()] = root;
                        _playerLabel[root] = label.ToUpperInvariant();
                        Logger.LogInfo("[MAP HUD] " + label + " → " + root.name);
                        return;
                    }
                }
            }

            Logger.LogWarning("[MAP HUD] Not mapped: " + label);
        }

        private static bool IsDescendantOf(Transform child, Transform potentialRoot)
        {
            var t = child;
            while (t != null) { if (t == potentialRoot) return true; t = t.parent; }
            return false;
        }

        private static List<UnityEngine.Object> CollectUnityRefs(Component comp)
        {
            var list = new List<UnityEngine.Object>();
            var t = comp.GetType();

            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType))
                { try { var v = f.GetValue(comp) as UnityEngine.Object; if (v) list.Add(v); } catch { } }
            }

            var props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];
                if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                if (typeof(UnityEngine.Object).IsAssignableFrom(p.PropertyType))
                { try { var v = p.GetValue(comp, null) as UnityEngine.Object; if (v) list.Add(v); } catch { } }
            }

            return list;
        }

        private static string ExtractP1P2(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.ToUpperInvariant();
            if (s.Contains("P1")) return "P1";
            if (s.Contains("P2")) return "P2";
            if (s.Contains("PLAYER 1")) return "P1";
            if (s.Contains("PLAYER 2")) return "P2";
            return null;
        }

        private static string DescribeGO(GameObject go)
        {
            if (go == null) return "<null>";
            return go.name + " (path: " + GetPath(go.transform) + ")";
        }

        private static string GetPath(Transform t)
        {
            var stack = new List<string>();
            while (t != null) { stack.Add(t.name); t = t.parent; }
            stack.Reverse();
            return string.Join("/", stack.ToArray());
        }

        /// <summary>
        /// Patches by type + method name, and hooks HUD/PlayerManager to map P1/P2 consistently.
        /// </summary>
        private void TryPatchByMethodName(string typeFullName, string methodName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(x => x != null).ToArray(); }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (typeFullName != null && !string.Equals(t.FullName, typeFullName, StringComparison.Ordinal)) continue;

                    var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                              .Where(m => m.Name == methodName);
                    foreach (var m in ms)
                    {
                        try
                        {
                            var postfix = new HarmonyMethod(typeof(Plugin).GetMethod("GenericPostfixLog", BindingFlags.Static | BindingFlags.NonPublic));
                            _harmony.Patch(m, postfix: postfix);

                            if (t.FullName == "Zyklus.UI.PlayerHUDComponent" && methodName == "InitializePlayerHUD")
                            {
                                var hudPostfix = new HarmonyMethod(typeof(Plugin).GetMethod("PlayerHUDPostfix", BindingFlags.Static | BindingFlags.NonPublic));
                                _harmony.Patch(m, postfix: hudPostfix);
                            }

                            if (t.FullName == "Zyklus.Managers.PlayerManager" && methodName == "AcquirePlayersGameobjects")
                            {
                                var pmPostfix = new HarmonyMethod(typeof(Plugin).GetMethod("PlayerManagerAcquirePostfix", BindingFlags.Static | BindingFlags.NonPublic));
                                _harmony.Patch(m, postfix: pmPostfix);
                            }

                            Logger.LogInfo("Patched: " + t.FullName + "." + m.Name + "()");
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning("Patch failed on " + t.FullName + "." + m.Name + ": " + ex.Message);
                        }
                    }
                }
            }
        }

        private static void PlayerManagerAcquirePostfix(object __instance)
        {
            try
            {
                var plugin = Instance;
                if (plugin == null) return;
                plugin.RefreshActivePlayersFromManager(__instance as Component);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning("PlayerManagerAcquirePostfix error: " + ex.Message);
            }
        }

        private static void PlayerHUDPostfix(object __instance)
        {
            try
            {
                var hud = __instance as Component;
                if (hud == null) return;

                string label = hud.gameObject != null ? hud.gameObject.name : null;
                if (string.IsNullOrEmpty(label)) return;

                var plugin = Instance;
                if (plugin == null) return;

                plugin.MapHudToPlayer(label, hud);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning("PlayerHUDPostfix error: " + ex.Message);
            }
        }

        private static void GenericPostfixLog(MethodBase __originalMethod, object __instance)
        {
            try
            {
                Logger?.LogInfo("[HUD/PLAYERS HOOK] " + __originalMethod.DeclaringType.FullName + "." + __originalMethod.Name + " invoked. Instance: " + __instance);
            }
            catch { }
        }

        private static bool IsNumericType(Type t)
        {
            return t == typeof(float) || t == typeof(double) || t == typeof(int) || t == typeof(uint) ||
                   t == typeof(short) || t == typeof(ushort) || t == typeof(byte) || t == typeof(sbyte) ||
                   t == typeof(long) || t == typeof(ulong) || t == typeof(decimal);
        }

        private static float ToSingleSafe(object v)
        {
            try
            {
                if (v == null) return -1f;
                if (v is float) return (float)v;
                if (v is IConvertible) return Convert.ToSingle(v);
            }
            catch { }
            return -1f;
        }

        private static float Safe(Func<float> f) { try { return f != null ? f() : -1f; } catch { return -1f; } }

        private static bool Approximately(float a, float b) { return Mathf.Abs(a - b) <= 0.001f; }
    }

    /// <summary>
    /// Harmony patch that mirrors soul changes to all players (Gems Sharing).
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_GemsSharing_ModifySoulCount
    {
        internal static bool Suppress = false;

        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Zyklus.Player.PlayerBase");
            return AccessTools.Method(t, "ModifySoulCount", new Type[] { typeof(int) });
        }

        static void Postfix(object __instance, int mod_number)
        {
            if (Suppress) return;

            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return;

                var source = __instance as Component;
                if (source == null) return;

                int afterSource = Plugin.Souls(source);
                if (afterSource < 0) afterSource = 0;

                var players = plugin.GetAllPlayerBases();
                if (players.Count <= 1) return;

                Suppress = true;
                foreach (var pb in players)
                {
                    if (pb == null) continue;
                    if (pb == source) continue;

                    int cur = Plugin.Souls(pb);
                    if (cur < 0) cur = 0;

                    int delta = afterSource - cur;
                    if (delta != 0)
                        Plugin.CallModify(pb, delta);
                }
                Suppress = false;

                Plugin.Logger.LogInfo($"[WALLET MIRROR] {plugin.LabelFromComponent(source)} all → {afterSource}");
            }
            catch (Exception ex)
            {
                Suppress = false;
                Plugin.Logger.LogWarning("[WALLET MIRROR] error: " + ex.Message);
            }
        }
    }
}
