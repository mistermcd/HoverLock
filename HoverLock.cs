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
        public string Description => "Prevents helicopters from dipping below their cruising altitude when crossing water.";

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
        // Tracks the established cruising altitude floor for each active helicopter
        private static readonly Dictionary<ushort, float> _cruisingAltitudes = new Dictionary<ushort, float>(256);

        public static void Cleanup()
        {
            _cruisingAltitudes.Clear();
        }

        public static void Postfix(ushort vehicleID, ref Vehicle data, ref Vehicle.Frame frameData, uint lodPhysics)
        {
            try
            {
                // If the helicopter is landing, taking off, or deleted, clear tracking so normal vertical operations work
                const Vehicle.Flags invalidFlags = Vehicle.Flags.Landing | Vehicle.Flags.TakingOff | Vehicle.Flags.Deleted;
                if ((data.m_flags & invalidFlags) != 0 || (data.m_flags & Vehicle.Flags.Created) == 0)
                {
                    if (_cruisingAltitudes.ContainsKey(vehicleID))
                    {
                        _cruisingAltitudes.Remove(vehicleID);
                    }
                    return;
                }

                Vector3 position = frameData.m_position;

                // If we don't have a recorded cruising altitude for this copter yet, initialize it
                if (!_cruisingAltitudes.TryGetValue(vehicleID, out float cruisingAlt))
                {
                    _cruisingAltitudes[vehicleID] = position.y;
                    return;
                }

                // If the helicopter is climbing (e.g., avoiding an obstacle or shore structure), 
                // update our cruising altitude floor to match its new higher elevation.
                if (position.y > cruisingAlt)
                {
                    _cruisingAltitudes[vehicleID] = position.y;
                }
                else if (position.y < cruisingAlt)
                {
                    // If the game tries to force it downward (the water-dip bug), 
                    // clamp it so it stays locked at its established cruising height.
                    position.y = cruisingAlt;
                    frameData.m_position = position;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HelicopterAltitudeFix] Exception during SimulationStep for vehicle {vehicleID}: {ex.Message}");
            }
        }
    }
}