using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ICities;

namespace mistermcd.HelicopterAltitudeFix
{
    public class HelicopterAltitudeFixMod : IUserMod
    {
        public string Name => "HoverLock";
        public string Description => "Prevents Scuba-Copters!";

        private const string HarmonyId = "com.mistermcd.cities.helicopteraltitudefloor";
        private Harmony _harmony;

        public void OnEnabled()
        {
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(HelicopterAltitudeFixMod).Assembly);
                Debug.Log("[HoverLock] Successfully applied Harmony patches.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HoverLock] Failed to apply Harmony patches: {ex.Message}");
            }
        }

        public void OnDisabled()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
                _harmony = null;
                HelicopterAISimulationStepPatch.Cleanup();
                Debug.Log("[HoverLock] Successfully unpatched Harmony.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[HoverLock] Error during unpatching: {ex.Message}");
            }
        }
    }

    // State container for each individual helicopter instance
    public class CopterFlightState
    {
        public float FloorAltitude = 0f;
        public bool IsCruising = false;
        public bool WasTakingOff = false;
    }

    [HarmonyPatch(typeof(HelicopterAI))]
    [HarmonyPatch("SimulationStep", new Type[] { typeof(ushort), typeof(Vehicle), typeof(Vehicle.Frame), typeof(uint) })]
    public static class HelicopterAISimulationStepPatch
    {
        private static readonly Dictionary<ushort, CopterFlightState> _copterStates = new Dictionary<ushort, CopterFlightState>(256);

        public static void Cleanup()
        {
            _copterStates.Clear();
        }

        public static void Postfix(ushort vehicleID, ref Vehicle data, ref Vehicle.Frame frameData, uint lodPhysics)
        {
            try
            {
                // If landing, deleted, or not created, clear state so normal ground/pad operations work
                const Vehicle.Flags invalidFlags = Vehicle.Flags.Landing | Vehicle.Flags.Deleted;
                if ((data.m_flags & invalidFlags) != 0 || (data.m_flags & Vehicle.Flags.Created) == 0)
                {
                    if (_copterStates.ContainsKey(vehicleID))
                    {
                        _copterStates.Remove(vehicleID);
                    }
                    return;
                }

                Vector3 position = frameData.m_position;

                // Get or create state tracker for this specific helicopter
                if (!_copterStates.TryGetValue(vehicleID, out var state))
                {
                    state = new CopterFlightState();
                    _copterStates[vehicleID] = state;
                }

                bool isTakingOff = (data.m_flags & Vehicle.Flags.TakingOff) != 0;

                if (isTakingOff)
                {
                    // While taking off, continuously update the baseline height as it ascends
                    state.FloorAltitude = position.y;
                    state.IsCruising = false;
                    state.WasTakingOff = true;
                }
                else
                {
                    // Check if we just transitioned out of takeoff into cruise flight
                    if (state.WasTakingOff && !state.IsCruising)
                    {
                        // THIS IS THE EXACT MOMENT: Takeoff complete, cruising begins!
                        // Lock the floor altitude right here.
                        state.FloorAltitude = position.y;
                        state.IsCruising = true;
                        state.WasTakingOff = false;
                    }
                    else if (!state.IsCruising)
                    {
                        // Fallback if it spawned directly in mid-air
                        state.FloorAltitude = position.y;
                        state.IsCruising = true;
                    }

                    // We are in cruise flight!
                    // Enforce the floor: never go below FloorAltitude, but allow rising above it for obstacles.
                    if (position.y < state.FloorAltitude)
                    {
                        position.y = state.FloorAltitude;
                        frameData.m_position = position;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[HoverLock] Exception during SimulationStep for vehicle {vehicleID}: {ex.Message}");
            }
        }
    }
}