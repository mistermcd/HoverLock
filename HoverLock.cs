// HoverLock - A mod for Cities: Skylines that prevents passenger and cargo helicopters from descending below their takeoff altitude.
// October 2026 - Idea and testing by MisterMcD, coding by Gemini.
// Patches PassengerHelicopterAI and HelicopterAI (for Cargo) with strict transport filtering.

using ColossalFramework;
using HarmonyLib;
using ICities;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HoverLock
{
    public class ModEntry : IUserMod
    {
        public string Name => "HoverLock";
        public string Description => "Prevents passenger and cargo scuba-copters over water while allowing free flight in mountains.";
    }

    public class HoverLockLoadingExtension : LoadingExtensionBase
    {
        private const string HarmonyId = "com.mistermcd.cities.hoverlock";
        private static Harmony _harmony;
        private static bool _isPatched = false;

        public override void OnLevelLoaded(LoadMode mode)
        {
            try
            {
                if (mode == LoadMode.LoadGame || mode == LoadMode.NewGame || mode == LoadMode.LoadMap || mode == LoadMode.NewMap)
                {
                    ApplyPatches();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HoverLock] FATAL Exception in OnLevelLoaded: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public override void OnLevelUnloading()
        {
            try
            {
                UnpatchAll();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HoverLock] Exception in OnLevelUnloading: {ex.Message}");
            }
        }

        private static void ApplyPatches()
        {
            if (_isPatched) return;

            Debug.Log("[HoverLock] ApplyPatches started.");
            _harmony = new Harmony(HarmonyId);
            var postfixMethod = AccessTools.Method(typeof(HelicopterPatch), nameof(HelicopterPatch.Postfix));

            if (ReferenceEquals(postfixMethod, null))
            {
                Debug.LogError("[HoverLock] ERROR: Could not find HelicopterPatch.Postfix method.");
                return;
            }

            int patchedCount = 0;

            // 1. Patch PassengerHelicopterAI (implements its own SimulationStep)
            Type passengerAiType = AccessTools.TypeByName("PassengerHelicopterAI");
            if (!ReferenceEquals(passengerAiType, null))
            {
                MethodInfo passengerMethod = AccessTools.Method(
                    passengerAiType,
                    "SimulationStep",
                    new Type[]
                    {
                        typeof(ushort),
                        typeof(Vehicle).MakeByRefType(),
                        typeof(Vehicle.Frame).MakeByRefType(),
                        typeof(ushort),
                        typeof(Vehicle).MakeByRefType(),
                        typeof(int)
                    }
                );

                if (!ReferenceEquals(passengerMethod, null))
                {
                    _harmony.Patch(passengerMethod, postfix: new HarmonyMethod(postfixMethod));
                    patchedCount++;
                    Debug.Log("[HoverLock] SUCCESS: Successfully patched PassengerHelicopterAI.SimulationStep!");
                }
            }

            // 2. Patch HelicopterAI (base class for CargoHelicopterAI and emergency copters)
            Type baseAiType = AccessTools.TypeByName("HelicopterAI");
            if (!ReferenceEquals(baseAiType, null))
            {
                MethodInfo baseMethod = AccessTools.Method(
                    baseAiType,
                    "SimulationStep",
                    new Type[]
                    {
                        typeof(ushort),
                        typeof(Vehicle).MakeByRefType(),
                        typeof(Vehicle.Frame).MakeByRefType(),
                        typeof(ushort),
                        typeof(Vehicle).MakeByRefType(),
                        typeof(int)
                    }
                );

                if (!ReferenceEquals(baseMethod, null))
                {
                    _harmony.Patch(baseMethod, postfix: new HarmonyMethod(postfixMethod));
                    patchedCount++;
                    Debug.Log("[HoverLock] SUCCESS: Successfully patched HelicopterAI.SimulationStep!");
                }
            }

            if (patchedCount > 0)
            {
                _isPatched = true;
            }
            else
            {
                Debug.LogError("[HoverLock] CRITICAL: Failed to patch any helicopter AI classes!");
            }
        }

        private static void UnpatchAll()
        {
            if (!ReferenceEquals(_harmony, null) && _isPatched)
            {
                _harmony.UnpatchAll(HarmonyId);
                _harmony = null;
                _isPatched = false;
                HelicopterPatch.Cleanup();
                Debug.Log("[HoverLock] Unpatched successfully on level unload.");
            }
        }
    }

    public static class HelicopterPatch
    {
        private static readonly Dictionary<ushort, float> _altitudes = new Dictionary<ushort, float>(256);

        public static void Cleanup()
        {
            _altitudes.Clear();
        }

        public static void Postfix(ushort vehicleID, ref Vehicle vehicleData, ref Vehicle.Frame frameData)
        {
            try
            {
                // STRICT TRANSPORT FILTER: Only process Passenger and Cargo helicopters.
                // Emergency/service choppers (Police, Fire, Medical, Disaster) are safely ignored.
                if (vehicleData.Info == null || vehicleData.Info.m_vehicleAI == null) return;

                string aiTypeName = vehicleData.Info.m_vehicleAI.GetType().Name;
                if (aiTypeName == null || (!aiTypeName.Contains("PassengerHelicopterAI") && !aiTypeName.Contains("CargoHelicopterAI")))
                {
                    return;
                }

                const Vehicle.Flags invalidFlags = Vehicle.Flags.Landing | Vehicle.Flags.Deleted;
                if ((vehicleData.m_flags & invalidFlags) != 0 || (vehicleData.m_flags & Vehicle.Flags.Created) == 0)
                {
                    if (_altitudes.ContainsKey(vehicleID))
                    {
                        _altitudes.Remove(vehicleID);
                    }
                    return;
                }

                Vector3 pos = frameData.m_position;
                bool isTakingOff = (vehicleData.m_flags & Vehicle.Flags.TakingOff) != 0;

                if (!_altitudes.TryGetValue(vehicleID, out float floorAlt))
                {
                    _altitudes[vehicleID] = pos.y;
                    return;
                }

                if (isTakingOff)
                {
                    _altitudes[vehicleID] = pos.y;
                    return;
                }

                // Sample terrain height to handle mountainous terrain check (75m threshold)
                bool allowRatchet = true;
                try
                {
                    if (Singleton<TerrainManager>.exists)
                    {
                        float terrainHeight = TerrainManager.instance.SampleDetailHeight(pos);
                        if (terrainHeight >= 75f)
                        {
                            allowRatchet = false;
                        }
                    }
                }
                catch
                {
                    // Fallback if manager isn't ready
                }

                if (pos.y > floorAlt)
                {
                    if (allowRatchet)
                    {
                        _altitudes[vehicleID] = pos.y; // Dynamically capture cruise altitude climb in low terrain/ocean
                    }
                    // Over mountains (>= 75m), allow climbing freely without updating floorAlt upward
                }
                else if (pos.y < floorAlt)
                {
                    pos.y = floorAlt;
                    frameData.m_position = pos;

                    // Stop downward velocity from fighting the floor clamp (prevents bouncing/speed loss)
                    if (frameData.m_velocity.y < 0f)
                    {
                        frameData.m_velocity.y = 0f;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HoverLock] Error in Postfix for {vehicleID}: {ex.Message}");
            }
        }
    }
}