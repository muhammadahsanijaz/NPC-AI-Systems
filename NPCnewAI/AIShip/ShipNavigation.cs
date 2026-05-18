// =============================================================================
// ShipNavigation.cs
// Place in: Assets/QuantumUser/Simulation/AI/
//
// DECK-AWARE WAYPOINT NAVIGATION FOR AI CREW
//
// DESIGN GOALS:
//   - Zero per-frame allocation
//   - Path built ONCE when target changes (in StartMovingTo)
//   - Per-frame cost: array index lookup + vector math only
//   - Max 6 waypoints: sufficient for any 3-deck ship routing
//   - Deterministic (pure FP math, no Unity physics queries)
//
// HOW IT WORKS:
//   1. BuildPath() determines which deck the crew is on and which deck the
//      target is on using Y-threshold values defined in ShipData.
//   2. If crossing decks, it inserts stair approach/exit waypoints from
//      ShipData.Stairs[] before the final destination.
//   3. Per-frame, TickNavigation() moves the crew toward the current
//      waypoint. When reached, it advances to the next. Y is included
//      in direction for stair waypoints, zeroed for final flat approach.
//
// CONFIGURATION (in ShipData):
//   - DeckYThresholds: float array, e.g. [1.5, 3.5]
//       Y < 1.5  → deck 0 (lower)
//       Y < 3.5  → deck 1 (mid)
//       Y >= 3.5 → deck 2 (upper)
//   - Stairs: ShipStairData[], one entry per physical staircase
//       Each stair defines LowerApproach, UpperApproach, which decks it connects
//
// STAIR MODELING NOTE:
//   If your stairs are ramp/slope geometry, the KCC auto-climbs when the crew
//   moves toward the StairUpperApproach waypoint (Y is included in direction).
//   If stairs use step geometry, ensure KCC.StepHeight is configured to match.
//   If your game uses trigger teleport volumes between decks, replace the stair
//   waypoints with your teleport trigger positions and the system still works —
//   just make sure LowerApproach/UpperApproach are the trigger entry points.
// =============================================================================

using Photon.Deterministic;

namespace Quantum
{
    public static unsafe class ShipNavigation
    {
        // ── Constants ────────────────────────────────────────────────────────
        
        /// <summary>
        /// Must match array<FPVector3>[N] NavPath size in AICrewComponents.qtn
        /// </summary>
        public const int MaxNavPathLength = 6;

        // XZ-only arrival radius squared for the final same-deck destination
        private static readonly FP FlatArrivalSqr = FP._1 + FP._0_50; // 1.5 units²

        // Full 3D arrival radius squared for intermediate stair waypoints
        // Slightly larger — the crew just needs to be "near" the stair to proceed
        private static readonly FP StairArrivalSqr = FP._2;            // ~1.41 unit radius


        // =====================================================================
        // PUBLIC API
        // =====================================================================

        /// <summary>
        /// Determines the deck index (0=lower, 1=mid, 2=upper) for a given
        /// local-space Y position using ShipData.DeckYThresholds.
        /// O(1) — threshold array is tiny (2 values for 3 decks).
        /// </summary>
        public static int GetDeckLevel(FP localY, ShipData shipData)
        {
            // DeckYThresholds example: [1.5, 3.5]
            //   localY < 1.5  → 0 (lower deck)
            //   localY < 3.5  → 1 (mid deck)
            //   localY >= 3.5 → 2 (upper deck)
            for (int i = 0; i < shipData.DeckYThresholds.Length; i++)
            {
                if (localY < FP.FromFloat_UNSAFE(shipData.DeckYThresholds[i]))
                    return i;
            }
            return shipData.DeckYThresholds.Length;
        }

        /// <summary>
        /// Builds a waypoint path from fromLocal to toLocal in ship-local space.
        /// Inserts stair waypoints if the source and destination are on different decks.
        /// 
        /// Call this ONCE when assigning a new target — not every frame.
        /// Path is stored directly in brain->NavPath (fixed array, no allocation).
        /// </summary>
        public static void BuildPath(
            AICrewBrain* brain,
            FPVector3 fromLocal,
            FPVector3 toLocal,
            ShipData shipData)
        {
            brain->NavPathIndex  = 0;
            brain->HasReachedTarget = false;

            int fromDeck = GetDeckLevel(fromLocal.Y, shipData);
            int toDeck   = GetDeckLevel(toLocal.Y,   shipData);

            if (fromDeck == toDeck)
            {
                // ── Same deck: single direct waypoint ────────────────────────
                brain->NavPath[0] = toLocal;
                brain->NavPathLength = 1;
                return;
            }

            // ── Different decks: insert stair waypoints ───────────────────────
            BuildStairedPath(brain, fromLocal, toLocal, fromDeck, toDeck, shipData);
        }

        /// <summary>
        /// Called every frame during movement states.
        /// Checks if the crew has reached the current waypoint and advances the path.
        /// Returns true when the entire path is complete (final destination reached).
        /// 
        /// O(1) — pure array index + distance check.
        /// </summary>
        public static bool TickNavigation(AICrewBrain* brain, FPVector3 currentLocalPos)
        {
            if (brain->NavPathLength == 0) return true;

            int idx = brain->NavPathIndex;
            FPVector3 waypoint = brain->NavPath[idx];
            bool isFinalWaypoint = (idx == brain->NavPathLength - 1);

            FPVector3 delta = waypoint - currentLocalPos;

            if (isFinalWaypoint)
            {
                // Final destination: XZ-only arrival (tolerant of small Y errors)
                FP xzSqr = delta.X * delta.X + delta.Z * delta.Z;
                if (xzSqr < FlatArrivalSqr)
                {
                    brain->HasReachedTarget = true;
                    brain->NavPathIndex     = 0;
                    brain->NavPathLength    = 0;
                    return true;
                }
            }
            else
            {
                // Stair waypoint: 3D arrival (crew must reach the correct height)
                FP sqr3d = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
                if (sqr3d < StairArrivalSqr)
                {
                    brain->NavPathIndex++;
                    // Don't return — continue moving toward the NEW waypoint this same frame
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the world-space movement direction toward the current waypoint.
        /// Preserves the Y component for stair waypoints (so the KCC climbs).
        /// Zeroes Y for the final flat-deck destination (stable ground movement).
        /// 
        /// O(1).
        /// </summary>
        public static FPVector3 GetNavDirection(
            AICrewBrain* brain,
            FPVector3 currentLocalPos,
            Transform3D* shipVisibleT)
        {
            if (brain->NavPathLength == 0) return FPVector3.Zero;

            int idx = brain->NavPathIndex;
            bool isFinalWaypoint = (idx == brain->NavPathLength - 1);
            FPVector3 waypoint = brain->NavPath[idx];
            FPVector3 delta    = waypoint - currentLocalPos;

            // Zero Y for same-deck final approach (prevents wobbling on flat deck)
            if (isFinalWaypoint)
                delta.Y = FP._0;

            FP sqrMag = delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z;
            if (sqrMag < FP.EN4) return FPVector3.Zero;

            FPVector3 localDir = delta * (FP._1 / FPMath.Sqrt(sqrMag));
            FPVector3 worldDir = shipVisibleT->TransformDirection(localDir);

            if (isFinalWaypoint)
                worldDir.Y = FP._0;

            FP worldSqr = worldDir.X * worldDir.X + worldDir.Y * worldDir.Y + worldDir.Z * worldDir.Z;
            if (worldSqr > FP.EN3)
                worldDir = worldDir * (FP._1 / FPMath.Sqrt(worldSqr));

            return worldDir;
        }


        // =====================================================================
        // PRIVATE HELPERS
        // =====================================================================

        private static void BuildStairedPath(
            AICrewBrain* brain,
            FPVector3 fromLocal,
            FPVector3 toLocal,
            int fromDeck,
            int toDeck,
            ShipData shipData)
        {
            int waypointCount = 0;
            int step          = toDeck > fromDeck ? 1 : -1;
            int currentDeck   = fromDeck;

            // Walk floor-by-floor, inserting one stair pair per transition
            // 3 decks max → at most 2 transitions → at most 4 stair waypoints + 1 dest = 5 total
            while (currentDeck != toDeck && waypointCount < MaxNavPathLength - 1)
            {
                int nextDeck = currentDeck + step;

                // Pick the staircase that minimises detour for THIS leg
                ShipStairData stair = FindBestStair(shipData, currentDeck, nextDeck, fromLocal, toLocal);

                if (step > 0)
                {
                    // Going UP: stand at lower approach → walk up to upper exit
                    brain->NavPath[waypointCount++] = stair.LowerApproach;
                    brain->NavPath[waypointCount++] = stair.UpperApproach;
                }
                else
                {
                    // Going DOWN: stand at upper approach → walk down to lower exit
                    brain->NavPath[waypointCount++] = stair.UpperApproach;
                    brain->NavPath[waypointCount++] = stair.LowerApproach;
                }

                currentDeck = nextDeck;
            }

            // Final destination (always last)
            brain->NavPath[waypointCount++] = toLocal;
            brain->NavPathLength = waypointCount;
        }

        /// <summary>
        /// Finds the staircase that connects fromDeck ↔ toDeck and is closest
        /// to the straight-line midpoint of the journey. This naturally picks
        /// the "least detour" stair without any expensive path search.
        /// O(Stairs.Length) — typically 2–4 stairs per ship.
        /// </summary>
        private static ShipStairData FindBestStair(
            ShipData shipData,
            int fromDeck,
            int toDeck,
            FPVector3 fromLocal,
            FPVector3 toLocal)
        {
            // XZ midpoint between origin and destination (Y irrelevant for proximity)
            FP midX = (fromLocal.X + toLocal.X) * FP._0_50;
            FP midZ = (fromLocal.Z + toLocal.Z) * FP._0_50;

            ShipStairData best    = default;
            FP            bestSqr = FP.MaxValue;

            int lower = fromDeck < toDeck ? fromDeck : toDeck;
            int upper = fromDeck < toDeck ? toDeck   : fromDeck;

            for (int i = 0; i < shipData.Stairs.Length; i++)
            {
                var s = shipData.Stairs[i];

                // Must connect the exact deck pair we need
                if (s.LowerDeckIndex != lower || s.UpperDeckIndex != upper)
                    continue;

                // Distance from midpoint to stair base (XZ only)
                FP dx  = s.LowerApproach.X - midX;
                FP dz  = s.LowerApproach.Z - midZ;
                FP sqr = dx * dx + dz * dz;

                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best    = s;
                }
            }

            return best;
        }
    }
}
