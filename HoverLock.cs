using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ICities;

namespace mistermcd.HelicopterAltitudeFix
{
    public class HelicopterAltitudeFixMod : IUserMod
    {
        public string Name => "Helicopter Water Altitude Floor";
        public string Description => "Prevents helicopters from dipping below their last known land height when crossing water.";

        private const string HarmonyId = "com.mistermcd.cities.helicopteraltitudefloor";
        private Harmony _harmony;

        public void OnEnabled()
        {
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(HelicopterAltitudeFixMod).Assembly);
                Debug.Log("[HelicopterAltitudeFix] Successfully applied Harmony patches.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HelicopterAltitudeFix] Failed to apply Harmony patches: {ex.Message}");
            }
        }

        public void OnDisabled()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
                _harmony = null;
                HelicopterAISimulationStepPatch.Cleanup();
                Debug.Log("[HelicopterAltitudeFix] Successfully unpatched Harmony.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HelicopterAltitudeFix] Error during unpatching: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(HelicopterAI))]
    [HarmonyPatch("SimulationStep", new Type[] { typeof(ushort), typeof(Vehicle), typeof(Vehicle.Frame), typeof(uint) })]
    public static class HelicopterAISimulationStepPatch
    {
        private static readonly Dictionary<ushort, float> _lastOverLandAltitudes = new Dictionary<ushort, float>(256);

        public static void Cleanup()
        {
            _lastOverLandAltitudes.Clear();
        }

        public static void Postfix(ushort vehicleID, ref Vehicle data, ref Vehicle.Frame frameData, uint lodPhysics)
        {
            try
            {
                const Vehicle.Flags invalidFlags = Vehicle.Flags.Landing | Vehicle.Flags.TakingOff | Vehicle.Flags.Deleted;
                if ((data.m_flags & invalidFlags) != 0 || (data.m_flags & Vehicle.Flags.Created) == 0)
                {
                    if (_lastOverLandAltitudes.ContainsKey(vehicleID))
                    {
                        _lastOverLandAltitudes.Remove(vehicleID);
                    }
                    return;
                }

                Vector3 position = frameData.m_position;

                // Cast a ray straight down from the helicopter to see what surface it is flying over
                // Layer mask can target water or terrain, or we can check raycast hit tag/layer.
                bool isOverWater = false;
                RaycastHit hit;
                // Raycast downward 500 meters from current position
                if (Physics.Raycast(position + Vector3.up * 10f, Vector3.down, out hit, 500f))
                {
                    // Check if the hit object belongs to water layer or has a water indicator name
                    string colliderName = hit.collider.gameObject.name.ToLower();
                    if (colliderName.Contains("water") || hit.collider.gameObject.layer == LayerMask.NameToLayer("Water"))
                    {
                        isOverWater = true;
                    }
                }

                if (!isOverWater)
                {
                    // Over land: Update and save current flight altitude
                    _lastOverLandAltitudes[vehicleID] = position.y;
                }
                else
                {
                    // Over water: Enforce minimum altitude floor based on the last known land height
                    if (_lastOverLandAltitudes.TryGetValue(vehicleID, out float minAltitude))
                    {
                        if (position.y < minAltitude)
                        {
                            position.y = minAltitude;
                            frameData.m_position = position;
                        }
                    }
                    else
                    {
                        _lastOverLandAltitudes[vehicleID] = position.y;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HelicopterAltitudeFix] Exception during SimulationStep for vehicle {vehicleID}: {ex.Message}");
            }
        }
    }
}