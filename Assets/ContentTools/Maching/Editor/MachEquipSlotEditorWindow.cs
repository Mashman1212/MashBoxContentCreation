using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace ContentTools.Maching
{
    public class MachEquipManagerWindow : EditorWindow
    {
        private const float IconSize = 80f;
        private const float IconPadding = 12f;
        private const int Columns = 4;

        private Vector2 scrollPos;
        private readonly Dictionary<string, Texture2D> iconCache = new();
        private readonly Dictionary<MachVehicle, bool> vehicleFoldout = new();
        private List<GameObject> machVehiclePrefabs = new();
        private MachVehicle[] sceneVehicles;
        private double lastVehicleScanTime;

        [MenuItem("Tools/Mach Equip Manager")]
        public static void ShowWindow() => GetWindow<MachEquipManagerWindow>("Mach Equip Manager");

        private void OnEnable()
        {
            RefreshPrefabList();
            RefreshSceneVehicles();
        }

        private void OnFocus()
        {
            if (EditorApplication.timeSinceStartup - lastVehicleScanTime > 5)
                RefreshPrefabList();

            RefreshSceneVehicles();
        }

        private void OnHierarchyChange() => RefreshSceneVehicles();

        // --- REFRESH LOGIC ---
        private void RefreshPrefabList()
        {
            machVehiclePrefabs.Clear();
            string[] guids = AssetDatabase.FindAssets("t:Prefab");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<MachVehicle>() != null)
                    machVehiclePrefabs.Add(prefab);
            }

            machVehiclePrefabs.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
            lastVehicleScanTime = EditorApplication.timeSinceStartup;
        }

        private void RefreshSceneVehicles()
        {
            sceneVehicles = FindObjectsOfType<MachVehicle>(true);
            Repaint();
        }

        // --- GUI ---
        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("⚙️ Mach Equipment Manager", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This tool manages MachVehicles and their equipment slots.\n\n" +
                "• Use the spawn buttons below to add MachVehicles to your scene.\n" +
                "• Expand a vehicle’s foldout to manage its MachEquipSlots.\n" +
                "• Drag prefabs directly onto slot icons to equip them.\n" +
                "• Click an icon to ping and select that equipped item.",
                MessageType.Info
            );

            EditorGUILayout.Space(6);
            DrawVehicleSpawnButtons();
            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("🧩 Scene Mach Vehicles", EditorStyles.boldLabel);

            if (sceneVehicles == null || sceneVehicles.Length == 0)
            {
                EditorGUILayout.HelpBox("No MachVehicle objects found in the scene.", MessageType.Warning);
                return;
            }

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            foreach (MachVehicle vehicle in sceneVehicles)
            {
                if (vehicle == null) continue;

                if (!vehicleFoldout.ContainsKey(vehicle))
                    vehicleFoldout[vehicle] = true;

                vehicleFoldout[vehicle] = EditorGUILayout.Foldout(vehicleFoldout[vehicle], vehicle.name, true);

                if (vehicleFoldout[vehicle])
                    DrawEquipSlotsForVehicle(vehicle);

                EditorGUILayout.Space(4);
            }

            EditorGUILayout.EndScrollView();
        }

        // --- VEHICLE SPAWN SECTION ---
        private void DrawVehicleSpawnButtons()
        {
            if (machVehiclePrefabs == null || machVehiclePrefabs.Count == 0)
            {
                EditorGUILayout.HelpBox("No MachVehicle prefabs found in the project.", MessageType.None);
                if (GUILayout.Button("🔍 Rescan MachVehicle Prefabs"))
                    RefreshPrefabList();
                return;
            }

            EditorGUILayout.LabelField("🚗 Spawnable MachVehicles", EditorStyles.boldLabel);
            EditorGUILayout.Space(3);

            EditorGUILayout.BeginHorizontal();
            foreach (GameObject prefab in machVehiclePrefabs)
            {
                string buttonText = $"Spawn {prefab.name}";
                Texture2D icon = GetPrefabIcon(prefab);

                if (GUILayout.Button(new GUIContent(buttonText, icon), GUILayout.Height(24)))
                {
                    SpawnVehiclePrefab(prefab);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void SpawnVehiclePrefab(GameObject prefab)
        {
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Spawn MachVehicle");
            Selection.activeObject = instance;
            SceneView.lastActiveSceneView?.FrameSelected();
            Debug.Log($"[MachEquipManager] Spawned MachVehicle: {prefab.name}");
            RefreshSceneVehicles();
        }

        // --- VEHICLE EQUIP SLOT UI ---
        private void DrawEquipSlotsForVehicle(MachVehicle vehicle)
        {
            MachEquipSlot[] slots = vehicle.GetComponentsInChildren<MachEquipSlot>(true);
            if (slots.Length == 0)
            {
                EditorGUILayout.LabelField("No EquipSlots found.", EditorStyles.miniLabel);
                return;
            }

            int count = 0;
            EditorGUILayout.BeginHorizontal();

            foreach (MachEquipSlot slot in slots)
            {
                DrawSlotTile(slot);
                count++;

                if (count % Columns == 0)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.Space();
                    EditorGUILayout.BeginHorizontal();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // --- EQUIP SLOT TILE ---
        private void DrawSlotTile(MachEquipSlot slot)
        {
            GUILayout.BeginVertical(GUILayout.Width(IconSize + IconPadding));
            Rect rect = GUILayoutUtility.GetRect(IconSize, IconSize, GUILayout.ExpandWidth(false));

            Texture2D icon = GetSlotIcon(slot);

            GUI.Box(rect, GUIContent.none);
            if (icon != null)
                GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit, true);

            HandlePingOnClick(rect, slot);
            HandleDragAndDrop(rect, slot);

            GUILayout.Label(slot.name, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(IconSize));
            GUILayout.EndVertical();
        }

        // --- CLICK TO PING LOGIC ---
        private void HandlePingOnClick(Rect rect, MachEquipSlot slot)
        {
            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
            {
                GameObject equipped = slot.GetEquippedItem();
                if (equipped != null)
                {
                    // If it’s a prefab asset, ping the asset; otherwise ping the scene object
                    Object target = PrefabUtility.GetCorrespondingObjectFromSource(equipped) ?? equipped;
                    EditorGUIUtility.PingObject(target);
                    Selection.activeObject = target;
                }
                evt.Use();
            }
        }

        // --- DRAG & DROP ---
        private void HandleDragAndDrop(Rect rect, MachEquipSlot slot)
        {
            Event evt = Event.current;
            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!rect.Contains(evt.mousePosition))
                        return;

                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (Object draggedObj in DragAndDrop.objectReferences)
                        {
                            if (draggedObj is GameObject go)
                            {
                                Undo.RegisterCompleteObjectUndo(slot, "Equip Item");
                                slot.Equip(go);
                                EditorUtility.SetDirty(slot);
                                break;
                            }
                        }
                    }

                    evt.Use();
                    break;
            }
        }

        // --- ICON HELPERS ---
        private Texture2D GetSlotIcon(MachEquipSlot slot)
        {
            GameObject item = slot.GetEquippedItem();
            if (item == null)
                return EditorGUIUtility.IconContent("Prefab Icon").image as Texture2D;

            string key = item.name;
            if (iconCache.TryGetValue(key, out Texture2D cached))
                return cached;

            Texture2D icon = FindItemIcon(item.name);
            if (icon == null)
                icon = AssetPreview.GetAssetPreview(item) ?? AssetPreview.GetMiniThumbnail(item);

            if (icon == null)
                icon = EditorGUIUtility.IconContent("Prefab Icon").image as Texture2D;

            iconCache[key] = icon;
            return icon;
        }

        private Texture2D GetPrefabIcon(GameObject prefab)
        {
            if (prefab == null) return null;
            if (iconCache.TryGetValue(prefab.name, out Texture2D cached)) return cached;

            Texture2D icon = AssetPreview.GetMiniThumbnail(prefab) ?? AssetPreview.GetAssetPreview(prefab);
            if (icon == null)
                icon = EditorGUIUtility.IconContent("Prefab Icon").image as Texture2D;

            iconCache[prefab.name] = icon;
            return icon;
        }

        private Texture2D FindItemIcon(string itemName)
        {
            if (iconCache.TryGetValue(itemName, out Texture2D cached))
                return cached;

            string iconName = itemName + "_Icon";
            string[] guids = AssetDatabase.FindAssets(iconName);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Object asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset is Texture2D tex)
                    return tex;
                if (asset is Sprite sprite)
                    return sprite.texture;
            }

            return null;
        }
    }
}
