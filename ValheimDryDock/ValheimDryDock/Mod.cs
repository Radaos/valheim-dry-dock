using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace ValheimDryDock
{
    // ==========================================
    // CORE PLUGIN ENTRY POINT
    // ==========================================
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    [BepInProcess("valheim.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGUID = "com.github.seniormodder.valheimdrydock";
        public const string ModName = "ValheimDryDock";
        public const string ModVersion = "1.1.0";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource ModLogger;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            ModLogger = Logger;

            // Initialize Configuration Systems
            Configuration.BindConfig(Config);

            // Execute Hooks
            _harmony = new Harmony(ModGUID);
            _harmony.PatchAll();

            ModLogger.LogInfo($"{ModName} Structural System Module v{ModVersion} Initialized.");
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || Chat.instance?.HasFocus() == true || Menu.IsVisible() || Console.IsVisible())
                return;

            if (DateTime.Now < Configuration.LastActionTime.AddMilliseconds(600))
                return;

            if (Input.GetKeyDown(Configuration.DockHotkey.Value))
            {
                Configuration.LastActionTime = DateTime.Now;
                DryDockController.ExecuteDock();
            }

            if (Input.GetKeyDown(Configuration.UndockHotkey.Value))
            {
                Configuration.LastActionTime = DateTime.Now;
                DryDockController.ExecuteUndock();
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    // ==========================================
    // CONFIGURATION TREE DEFINITIONS
    // ==========================================
    public static class Configuration
    {
        public static ConfigEntry<KeyCode> DockHotkey { get; private set; }
        public static ConfigEntry<KeyCode> UndockHotkey { get; private set; }
        public static ConfigEntry<float> SafetyClearanceBuffer { get; private set; }
        public static ConfigEntry<float> RaycastDistance { get; private set; }
        public static ConfigEntry<string> VerticalPostPrefab { get; private set; }
        public static ConfigEntry<string> AngledBracePrefab { get; private set; }

        public static DateTime LastActionTime = DateTime.MinValue;

        public static void BindConfig(ConfigFile config)
        {
            DockHotkey = config.Bind("Controls", "DockHotkey", KeyCode.Y, "Press to lift and dock the targeted ship.");
            UndockHotkey = config.Bind("Controls", "UndockHotkey", KeyCode.H, "Press to restore and release the targeted ship into water.");
            SafetyClearanceBuffer = config.Bind("Mechanics", "SafetyClearanceBuffer", 3.0f, "Vertical buffer clearance in meters added directly above the highest local wave crest.");
            RaycastDistance = config.Bind("Mechanics", "RaycastDistance", 30.0f, "Maximum distance targeting vector for ship selection scans.");
            VerticalPostPrefab = config.Bind("Assets", "VerticalPostPrefab", "wood_pole2", "Vanilla prefab used to build the center support columns.");
            AngledBracePrefab = config.Bind("Assets", "AngledBracePrefab", "wood_beam2_26", "Vanilla prefab used to build the dynamic angled side hull supports.");
        }
    }

    // ==========================================
    // DRY DOCK STRUCTURAL RUNTIME CONTROLLER
    // ==========================================
    public static class DryDockController
    {
        private const string ZdoKeyDocked = "DryDock_IsDocked";
        private const string ZdoKeyOrigPos = "DryDock_OrigPos";
        private const string ZdoKeyOrigRot = "DryDock_OrigRot";

        public static void ExecuteDock()
        {
            try
            {
                Ship ship = FindTargetShip();
                if (ship == null)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "No ship targeted.");
                    return;
                }

                // Explicit type cast parsing to eliminate CS0411 ambiguity
                ZNetView nview = ship.gameObject.GetComponent(typeof(ZNetView)) as ZNetView;
                if (nview == null || !nview.IsValid()) return;

                if (!PrivateArea.CheckAccess(ship.transform.position, 0f, flash: true))
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Building access restricted by Ward protection.");
                    return;
                }

                if (!nview.IsOwner())
                {
                    nview.ClaimOwnership();
                }

                if (!nview.IsOwner())
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Network link sync deferred. Try again.");
                    return;
                }

                if (nview.GetZDO().GetInt(ZdoKeyDocked, 0) == 1)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Targeted ship is already docked.");
                    return;
                }

                // Passed 0f as a plain float wave multiplier to match the signature parameters
                Vector3 currentPos = ship.transform.position;
                float liquidLevel = Floating.GetLiquidLevel(currentPos, 0f);
                float requiredLift = (liquidLevel - currentPos.y) + Configuration.SafetyClearanceBuffer.Value;

                if (requiredLift < Configuration.SafetyClearanceBuffer.Value)
                {
                    requiredLift = Configuration.SafetyClearanceBuffer.Value;
                }

                nview.GetZDO().Set(ZdoKeyOrigPos, currentPos);
                nview.GetZDO().Set(ZdoKeyOrigRot, ship.transform.rotation.eulerAngles);

                Rigidbody rb = ship.gameObject.GetComponent(typeof(Rigidbody)) as Rigidbody;
                if (rb != null)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.isKinematic = true;
                }

                Bounds hullBounds = CalculateHullBounds(ship);
                float length = hullBounds.size.z;
                float width = hullBounds.size.x;

                Vector3 targetDockPosition = currentPos + new Vector3(0, requiredLift, 0);
                ship.transform.position = targetDockPosition;
                nview.GetZDO().SetPosition(targetDockPosition);

                ConstructCradle(ship, length, width);

                nview.GetZDO().Set(ZdoKeyDocked, 1);
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, $"Dry Dock Secure: Raised +{requiredLift:F1}m above sea level.");
            }
            catch (Exception ex)
            {
                Plugin.ModLogger.LogError($"Exception during ExecuteDock processing sequence: {ex}");
            }
        }

        public static void ExecuteUndock()
        {
            try
            {
                Ship ship = FindTargetShip();
                if (ship == null)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "No ship targeted.");
                    return;
                }

                ZNetView nview = ship.gameObject.GetComponent(typeof(ZNetView)) as ZNetView;
                if (nview == null || !nview.IsValid()) return;

                if (!PrivateArea.CheckAccess(ship.transform.position, 0f, flash: true))
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Building access restricted by Ward protection.");
                    return;
                }

                if (!nview.IsOwner()) nview.ClaimOwnership();
                if (!nview.IsOwner()) return;

                if (nview.GetZDO().GetInt(ZdoKeyDocked, 0) == 0)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Targeted ship is not currently docked.");
                    return;
                }

                Vector3 originalPosition = nview.GetZDO().GetVec3(ZdoKeyOrigPos, Vector3.zero);
                Vector3 originalRotationEuler = nview.GetZDO().GetVec3(ZdoKeyOrigRot, Vector3.zero);

                if (originalPosition == Vector3.zero)
                {
                    Plugin.ModLogger.LogWarning("ZDO reference data empty. Falling back to dynamic water calculations.");
                    originalPosition = ship.transform.position;
                    originalPosition.y = Floating.GetLiquidLevel(originalPosition, 0f);
                }

                DemolishCradleSubstructures(ship);

                ship.transform.position = originalPosition;
                ship.transform.rotation = Quaternion.Euler(originalRotationEuler);
                nview.GetZDO().SetPosition(originalPosition);

                Rigidbody rb = ship.gameObject.GetComponent(typeof(Rigidbody)) as Rigidbody;
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.WakeUp();
                }

                nview.GetZDO().Set(ZdoKeyDocked, 0);
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Ship returned safely to the sea.");
            }
            catch (Exception ex)
            {
                Plugin.ModLogger.LogError($"Exception during ExecuteUndock processing sequence: {ex}");
            }
        }

        private static Ship FindTargetShip()
        {
            Transform camTransform = GameCamera.instance.transform;
            Ray ray = new Ray(camTransform.position, camTransform.forward);
            int layerMask = LayerMask.GetMask("piece", "item", "viewblock", "vehicle");
            if (Physics.Raycast(ray, out RaycastHit hit, Configuration.RaycastDistance.Value, layerMask))
            {
                Ship ship = hit.collider.gameObject.GetComponentInParent(typeof(Ship)) as Ship;
                if (ship != null) return ship;
                Collider[] adjacentColliders = Physics.OverlapSphere(hit.point, 4.0f, LayerMask.GetMask("vehicle"));
                foreach (Collider col in adjacentColliders)
                {
                    Ship fallbackShip = col.gameObject.GetComponentInParent(typeof(Ship)) as Ship;
                    if (fallbackShip != null) return fallbackShip;
                }
            }
            return null;
        }
        private static Bounds CalculateHullBounds(Ship ship)
        {
            Bounds combinedBounds = new Bounds(ship.transform.position, Vector3.zero);
            Component[] colliders = ship.gameObject.GetComponentsInChildren(typeof(Collider));
            bool established = false;
            foreach (Component comp in colliders)
            {
                Collider col = comp as Collider;
                if (col == null || col.isTrigger) continue;
                if (!established)
                {
                    combinedBounds = col.bounds;
                    established = true;
                }
                else
                {
                    combinedBounds.Encapsulate(col.bounds);
                }
            }
            return combinedBounds;
        }
        private static void ConstructCradle(Ship ship, float length, float width)
        {
            GameObject verticalPrefab = ZNetScene.instance.GetPrefab(Configuration.VerticalPostPrefab.Value);
            GameObject angledPrefab = ZNetScene.instance.GetPrefab(Configuration.AngledBracePrefab.Value);
            if (verticalPrefab == null || angledPrefab == null)
            {
                Plugin.ModLogger.LogError("Configured cradle build prefabs are missing from the game's internal ZNetScene database.");
                return;
            }
            int supportRows = Mathf.Max(4, Mathf.CeilToInt(length / 1.8f));
            float sliceInterval = length / (supportRows - 1);
            for (int i = 0; i < supportRows; i++)
            {
                float relativeZ = -(length / 2f) + (i * sliceInterval);
                Vector3 centerRelOffset = new Vector3(0f, -0.4f, relativeZ);
                Vector3 centerWorldPos = ship.transform.TransformPoint(centerRelOffset);
                BuildPieceServerSide(verticalPrefab, centerWorldPos, ship.transform.rotation);
                Vector3 portRelOffset = new Vector3(-(width * 0.45f), -0.2f, relativeZ);
                Vector3 portWorldPos = ship.transform.TransformPoint(portRelOffset);
                Quaternion portRot = ship.transform.rotation * Quaternion.Euler(0f, 0f, -26f);
                BuildPieceServerSide(angledPrefab, portWorldPos, portRot);
                Vector3 starboardRelOffset = new Vector3(width * 0.45f, -0.2f, relativeZ);
                Vector3 starboardWorldPos = ship.transform.TransformPoint(starboardRelOffset);
                Quaternion starboardRot = ship.transform.rotation * Quaternion.Euler(0f, 0f, 26f);
                BuildPieceServerSide(angledPrefab, starboardWorldPos, starboardRot);
            }
        }
        private static void BuildPieceServerSide(GameObject prefab, Vector3 worldPosition, Quaternion rotation)
        {
            Piece pieceComponent = prefab.GetComponent(typeof(Piece)) as Piece;
            if (pieceComponent == null) return;
            Player localPlayer = Player.m_localPlayer;
            GameObject placedObject = UnityEngine.Object.Instantiate(prefab, worldPosition, rotation);
            ZNetView nview = placedObject.GetComponent(typeof(ZNetView)) as ZNetView;
            if (nview != null && nview.IsValid())
            {
                nview.GetZDO().SetOwner(ZNet.GetUID());
                Piece piece = placedObject.GetComponent(typeof(Piece)) as Piece;
                if (piece != null)
                {
                    PlatformUserID emptyUserToken = new PlatformUserID();
                    piece.SetCreator(localPlayer.GetPlayerID(), emptyUserToken);
                }
                WearNTear wear = placedObject.GetComponent(typeof(WearNTear)) as WearNTear;
                if (wear != null)
                {
                    wear.OnPlaced();
                }
            }
        }
        private static void DemolishCradleSubstructures(Ship ship)
        {
            int layerMask = LayerMask.GetMask("piece");
            Collider[] surroundingPieces = Physics.OverlapSphere(ship.transform.position, 12.0f, layerMask);
            foreach (Collider col in surroundingPieces)
            {
                Piece piece = col.gameObject.GetComponentInParent(typeof(Piece)) as Piece;
                if (piece == null) continue;
                string cleanName = piece.gameObject.name.Replace("(Clone)", "").Trim();
                if (cleanName == Configuration.VerticalPostPrefab.Value || cleanName == Configuration.AngledBracePrefab.Value)
                {
                    ZNetView pieceView = piece.gameObject.GetComponent(typeof(ZNetView)) as ZNetView;
                    if (pieceView != null && pieceView.IsValid())
                    {
                        if (!pieceView.IsOwner()) pieceView.ClaimOwnership();
                        if (pieceView.IsOwner())
                        {
                            ZNetScene.instance.Destroy(piece.gameObject);
                        }
                    }
                }
            }
        }
    }
    // ==========================================
    // HARMONY NETWORK INJECTOR PATCHES
    // ==========================================
    [HarmonyPatch]
    public static class DryDockNetworkPatches
    {
        [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
        [HarmonyPrefix]
        public static bool PrefixShipPhysicsUpdate(Ship __instance, float fixedDeltaTime)
        {
            if (__instance == null) return true;
            ZNetView nview = __instance.gameObject.GetComponent(typeof(ZNetView)) as ZNetView;
            if (nview != null && nview.IsValid())
            {
                if (nview.GetZDO().GetInt("DryDock_IsDocked", 0) == 1)
                {
                    Rigidbody rb = __instance.gameObject.GetComponent(typeof(Rigidbody)) as Rigidbody;
                    if (rb != null && !rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.isKinematic = true;
                    }
                    return false;
                }
            }
            return true;
        }
    }
}