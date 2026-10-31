# Technical Analysis: HoverLock Mod for Cities: Skylines

**Author:** MisterMcD & Gemini

**Target Domain:** *Cities: Skylines* C# Modding (Harmony Patching, Vehicle AI)

**Target Classes:** `PassengerHelicopterAI`, `CargoHelicopterAI`

---

## 1. Executive Summary

In *Cities: Skylines*, passenger and cargo helicopters frequently suffer from an artificial altitude-loss phenomenon commonly referred to as "scuba-diving," where aircraft descend toward sea level over deep water or open ocean tiles, often clipping water surfaces or stalling. 

While attempting to prevent this descent by dynamically tracking takeoff and cruise altitudes, early iterations of our mod logic inadvertently introduced an internal **"Ratchet Effect"** (the High-Water Mark problem). When flying over high mountain ranges, the mod's initial altitude-tracking code continuously updated the minimum flight floor upward to match mountain peaks. When helicopters attempted to descend down the other side, they became trapped in mid-air at the mountain's peak elevation, breaking the game's movement state machine and paralyzing aircraft at speeds of ~1 km/h.

The final **HoverLock** mod resolves both the native "scuba-diving" bug and our development iteration's "ratchet trap" through a lightweight, terrain-aware Harmony postfix patch that dynamically manages altitude floors without interfering with emergency service vehicles (`HelicopterAI`) or natural mountain descents.

---

## 2. Architectural Overview & Scope

### 2.1 Scope & Target Selection

To protect core transit routes while preserving native gameplay behavior for emergency responders (Police, Fire, Medical, Disaster), HoverLock exclusively patches transport-class helicopter AI:

* `PassengerHelicopterAI`
* `CargoHelicopterAI`

Emergency service helicopters (`HelicopterAI`) remain completely unpatched to ensure their internal pathfinding and emergency response state machines remain 100% vanilla.

### 2.2 Harmony Patching Mechanism

The mod hooks into the `SimulationStep` method of the target AI classes using a Harmony `Postfix` injection:

```csharp
public static void Postfix(ushort vehicleID, ref Vehicle vehicleData, ref Vehicle.Frame frameData)
```

This intercepts the vehicle's state and frame data at the end of each simulation tick, allowing real-time positional adjustments to `frameData.m_position` and `frameData.m_velocity`.

---

## 3. Core Logic & Mechanics

### 3.1 State Management & Dictionary Lookup

HoverLock tracks active vehicles using a lightweight, pooled dictionary:

```csharp
private static readonly Dictionary<ushort, float> _altitudes = new Dictionary<ushort, float>(256);
```

* **Key:** `ushort vehicleID` (The game's internal unique vehicle handle).
* **Value:** `float floorAlt` (The established minimum cruising floor altitude).
* **Cleanup:** Invalidated or deleted vehicles are immediately purged from the dictionary. Global cleanup occurs on level unload.

### 3.2 Solving the "Ratchet Effect" via Terrain-Aware Guardrails

During early testing, an unconstrained upward update check was used:

```csharp
// EARLY FLAWED LOGIC: Caused the Ratchet Effect
if (pos.y > floorAlt) {
    _altitudes[vehicleID] = pos.y; // Trapped helicopters at mountain peak height!
}
```

To eliminate this flaw while retaining the ability to capture initial cruising altitude during takeoff over coastal bluffs, HoverLock implements **conditional ratcheting** governed by underlying terrain elevation:

1. **Takeoff Initialization:** When a helicopter takes off (`isTakingOff` flag active), its current world altitude (`pos.y`) is captured as the initial `floorAlt`.

2. **Low Terrain / Ocean Flight (`terrainHeight < 75m`):**
   * As the helicopter climbs from its coastal depot to its cruising altitude, `pos.y > floorAlt` is true.
   * The mod permits upward ratcheting: `_altitudes[vehicleID] = pos.y`. This ensures `floorAlt` successfully transitions from pad height to full cruise height.

3. **High Mountain Flight (`terrainHeight >= 75m`):**
   * When flying over elevated terrain or mountain ridges, `TerrainManager.instance.SampleDetailHeight(pos)` detects high ground ($\ge 75\text{m}$).
   * The mod sets `allowRatchet = false`.
   * Consequently, `floorAlt` is **frozen** and cannot ratchet upward to match mountain peaks.

4. **The Downward Clamp:**
   * If the helicopter's position drops below the established floor (`pos.y < floorAlt`), the mod locks its height: `pos.y = floorAlt`.
   * To prevent downward momentum from fighting the clamp (which causes rubberbanding and speed loss), vertical velocity is zeroed out if negative:
     ```csharp
     if (frameData.m_velocity.y < 0f) {
         frameData.m_velocity.y = 0f;
     }
     ```

---

## 4. Test Scenarios & Behavioral Analysis

### 4.1 Test Case A: Coastal / Ocean City

* **Environment:** Map features inland passenger/cargo depots situated near coastlines, with flight paths crossing expansive deep-water bays.
* **Vanilla Game Bug:** Copters take off, drift outward over deep water, and follow dipping seabed contours down toward sea level ("scuba-diving").
* **HoverLock Behavior:**
  1. Copters take off and climb to cruise altitude (e.g., $Y = 50\text{m}$).
  2. The upward ratchet captures $Y = 50\text{m}$ as `floorAlt` while over low coastal terrain.
  3. Upon entering deep water, terrain height drops below $75\text{m}$, keeping `allowRatchet = true`.
  4. When the game engine attempts to pull the helicopter down toward the seabed, the `pos.y < floorAlt` clamp engages, locking flight at a flat $Y = 50\text{m}$ floor.
* **Outcome:** Zero scuba-diving; smooth, level over-water cruising at full speed.

### 4.2 Test Case B: Mountainous City

* **Environment:** Map features rugged terrain with valley depots and high mountain ridges exceeding $300\text{m}$ elevation.
* **Early Mod Iteration Flaw (Unconstrained Ratchet):** Climbing a $350\text{m}$ mountain locked `floorAlt` to $350\text{m}$, trapping the helicopter when descending into the opposite valley and causing severe speed degradation down to $1\text{km/h}$.
* **HoverLock Final Behavior:**
  1. Copter takes off in a valley ($Y = 30\text{m}$).
  2. Copter climbs up a mountain ridge. As terrain height surpasses $75\text{m}$, `allowRatchet` switches to `false`.
  3. `floorAlt` remains at its initial valley cruise height and does **not** ratchet up to the peak.
  4. Upon crossing the peak and descending into the target valley, `pos.y` naturally decreases below the mountain height without hitting an artificial ceiling.
* **Outcome:** Full freedom of movement over mountains; no speed reduction, stalling, or mid-air trapping.

---

## 5. Performance and Optimization

HoverLock maintains a negligible CPU footprint through several design principles:

* **$O(1)$ Time Complexity:** All state checks utilize direct dictionary hash-lookups by `ushort` ID.
* **Sparse Entity Count:** Restricted strictly to active transport helicopters (typically $< 40$ instances map-wide at any given time).
* **Early-Out Guards:** Invalid, deleted, or inactive vehicles bypass execution immediately.
* **Native Integration:** Leverages Unity and *Cities: Skylines* internal singleton managers (`TerrainManager`) directly in C++ memory layers with minimal overhead.