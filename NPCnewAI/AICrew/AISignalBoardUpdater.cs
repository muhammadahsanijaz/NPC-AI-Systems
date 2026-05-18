// =============================================================================
// AISignalBoardUpdater.cs
// Place in: Assets/QuantumUser/Simulation/AI/
//
// Updates the CrewSignalBoard based on ship state and captain type.
//
// TWO MODES:
// 
// FullAI mode:
//   Scans for enemies, determines threat side purely from geometry.
//   In Phase 2, the AI Pilot will override this and write signals directly.
//
// PlayerPilotAICrew mode:
//   Reads what the human captain is doing and infers intent:
//   - Player is steering → predict which side will face enemy
//   - Player fired captain cannons → confirm engagement, crew battle stations
//   - Ship taking damage → auto bump repair urgency
//   - Player manning a crew cannon → AI avoids that cannon + prefers same side
//   - No player at wheel → fall back to FullAI-style auto signals
//
// Repair urgency is always auto-calculated from hull % in both modes.
//
// CANNON CONFLICT RESOLUTION:
//   If a human player walks toward an AI-reserved cannon, the AI yields
//   and finds another task. Human players always have priority.
// =============================================================================

using Photon.Deterministic;

namespace Quantum
{
    public unsafe class AISignalBoardUpdater : SystemMainThreadFilter<AISignalBoardUpdater.Filter>
    {
        // Engagement range thresholds (squared for fast comparison)
        private static readonly FP EngagementRangeSqr = 150 * 150;
        private static readonly FP CloseRangeSqr = 50 * 50;
        
        // How long after captain fires cannons we consider the ship "in combat"
        private static readonly FP CaptainFireMemoryDuration = FP._5;
        
        // Distance at which a human player approaching a cannon causes AI to yield
        private static readonly FP PlayerCannonYieldDistSqr = FP._3 * FP._3;

        public struct Filter
        {
            public EntityRef Entity;
            public Ship* Ship;
            public AIShipCrewData* CrewData;
        }

        public override void Update(Frame f, ref Filter filter)
        {
            if (!filter.CrewData->IsAIControlled) return;
            if (filter.Ship->Hull <= 0) return;
            
            // ─── ALWAYS: Update repair urgency from hull state ───
            UpdateRepairUrgency(filter.Ship, filter.CrewData);
            
            // ─── ALWAYS: Detect hull damage between ticks ───
            UpdateDamageTracking(filter.Ship, filter.CrewData);
            
            // ─── Route to correct signal logic based on mode ───
            if (filter.CrewData->ShipMode == AIShipMode.PlayerPilotAICrew)
            {
                UpdatePlayerPilotSignals(f, ref filter);
            }
            else
            {
                UpdateFullAISignals(f, ref filter);
            }
            
            // ─── ALWAYS: Handle AI yielding cannons to human players ───
            HandlePlayerCannonConflicts(f, ref filter);
        }
        
        // =====================================================================
        // REPAIR URGENCY (same for both modes)
        // =====================================================================
        
        private void UpdateRepairUrgency(Ship* ship, AIShipCrewData* crewData)
        {
            FP hullPercent = ship->Hull / ship->HullMax;
            
            if (hullPercent < FP._0_20)
            {
                crewData->SignalBoard.RepairUrgency = RepairUrgency.Critical;
            }
            else if (hullPercent < FP._0_50 - FP._0_10) // < 40%
            {
                crewData->SignalBoard.RepairUrgency = RepairUrgency.High;
            }
            else if (hullPercent < FP._0_75)
            {
                crewData->SignalBoard.RepairUrgency = RepairUrgency.Low;
            }
            else
            {
                crewData->SignalBoard.RepairUrgency = RepairUrgency.None;
            }
        }
        
        private void UpdateDamageTracking(Ship* ship, AIShipCrewData* crewData)
        {
            crewData->ShipTakingDamage = ship->Hull < crewData->LastHullSnapshot;
            crewData->LastHullSnapshot = ship->Hull;
        }
        
        // =====================================================================
        // PLAYER PILOT + AI CREW MODE
        // =====================================================================
        
        private void UpdatePlayerPilotSignals(Frame f, ref Filter filter)
        {
            var ship = filter.Ship;
            var crewData = filter.CrewData;
            
            // ─── Check if a human is actually at the wheel ───
            crewData->PlayerIsPiloting = ship->PlayerOnWheel != default;
            
            // ─── Track captain cannon fire ───
            // CaptainCannonCooldown resets when the captain fires. If it was recently 
            // reset (positive and below max), the player fired recently.
            if (ship->CaptainCannonCooldown > 0 && !crewData->PlayerFiredCaptainCannon)
            {
                crewData->PlayerFiredCaptainCannon = true;
                crewData->PlayerFireCooldownTracker = CaptainFireMemoryDuration;
            }
            
            if (crewData->PlayerFireCooldownTracker > 0)
            {
                crewData->PlayerFireCooldownTracker -= f.DeltaTime;
            }
            else
            {
                crewData->PlayerFiredCaptainCannon = false;
            }
            
            // ─── Snapshot steer value ───
            crewData->PlayerSteerValue = ship->Steer;
            
            // ─── If no player at wheel, fall back to auto signals ───
            if (!crewData->PlayerIsPiloting)
            {
                UpdateFullAISignals(f, ref filter);
                return;
            }
            
            // ─── Find nearest enemy (we still need to know where enemies are) ───
            var shipTransform = f.Unsafe.GetPointer<Transform3D>(ship->WorldEntity);
            FPVector3 closestEnemyPos;
            FP closestDistSqr;
            bool foundEnemy = FindNearestEnemy(f, ship, shipTransform, filter.Entity, 
                out closestEnemyPos, out closestDistSqr);
            
            if (!foundEnemy || closestDistSqr > EngagementRangeSqr)
            {
                // No enemy in range
                if (crewData->PlayerFiredCaptainCannon || crewData->ShipTakingDamage)
                {
                    // Player fired or we're taking damage — something is happening
                    // even if we can't see the enemy clearly. Go to alert.
                    crewData->SignalBoard.ThreatLevel = ThreatLevel.Skirmish;
                    
                    // Use ship's steer to guess which side player is exposing
                    InferSideFromSteer(ship, crewData);
                }
                else
                {
                    crewData->SignalBoard.ThreatLevel = ThreatLevel.Peaceful;
                    crewData->SignalBoard.PreferredAttackSide = AttackSide.None;
                    crewData->SignalBoard.LeftCannonsNeeded = 0;
                    crewData->SignalBoard.RightCannonsNeeded = 0;
                }
                return;
            }
            
            // ─── Enemy in range: determine side using BOTH enemy position AND player intent ───
            FPVector3 toEnemy = closestEnemyPos - shipTransform->Position;
            FPVector3 localToEnemy = shipTransform->InverseTransformDirection(toEnemy);
            bool enemyCurrentlyOnLeft = localToEnemy.X < 0;
            
            // Start with geometry-based prediction
            bool predictedSideIsLeft = enemyCurrentlyOnLeft;
            
            // Check if player is actively steering — if so, predict where the enemy 
            // will end up relative to the ship after the turn completes
            if (FPMath.Abs(ship->Steer) > FP._0_10)
            {
                // Player is turning. 
                // Steer > 0 = turning right = presenting left broadside
                // Steer < 0 = turning left = presenting right broadside
                //
                // Only override prediction if the enemy is roughly forward (not already
                // clearly on a side), because the player is actively maneuvering.
                bool enemyRoughlyAhead = FPMath.Abs(localToEnemy.X) < FPMath.Abs(localToEnemy.Z);
                
                if (enemyRoughlyAhead)
                {
                    if (ship->Steer > FP._0_10)
                    {
                        predictedSideIsLeft = true; // Turning right → left broadside
                    }
                    else if (ship->Steer < -FP._0_10)
                    {
                        predictedSideIsLeft = false; // Turning left → right broadside
                    }
                }
            }
            
            // ─── Strongest signal: check if any human player is manning a cannon ───
            AttackSide playerCannonSide = GetHumanOccupiedCannonSide(f, ship, crewData);
            if (playerCannonSide == AttackSide.Left)
            {
                predictedSideIsLeft = true;
            }
            else if (playerCannonSide == AttackSide.Right)
            {
                predictedSideIsLeft = false;
            }
            // AttackSide.Both or None — keep existing prediction
            
            // ─── Set threat level based on distance + player actions ───
            if (closestDistSqr < CloseRangeSqr || crewData->PlayerFiredCaptainCannon)
            {
                crewData->SignalBoard.ThreatLevel = ThreatLevel.Battle;
            }
            else
            {
                crewData->SignalBoard.ThreatLevel = ThreatLevel.Skirmish;
            }
            
            // ─── Write cannon assignments ───
            if (closestDistSqr < CloseRangeSqr)
            {
                crewData->SignalBoard.PreferredAttackSide = AttackSide.Both;
                crewData->SignalBoard.LeftCannonsNeeded = 2;
                crewData->SignalBoard.RightCannonsNeeded = 2;
            }
            else
            {
                if (predictedSideIsLeft)
                {
                    crewData->SignalBoard.PreferredAttackSide = AttackSide.Left;
                    crewData->SignalBoard.LeftCannonsNeeded = 3;
                    crewData->SignalBoard.RightCannonsNeeded = 0;
                }
                else
                {
                    crewData->SignalBoard.PreferredAttackSide = AttackSide.Right;
                    crewData->SignalBoard.LeftCannonsNeeded = 0;
                    crewData->SignalBoard.RightCannonsNeeded = 3;
                }
            }
        }
        
        /// <summary>
        /// When no enemy is visible but the player is steering, infer attack side from steer direction.
        /// </summary>
        private void InferSideFromSteer(Ship* ship, AIShipCrewData* crewData)
        {
            if (ship->Steer > FP._0_10)
            {
                // Turning right → likely presenting left broadside
                crewData->SignalBoard.PreferredAttackSide = AttackSide.Left;
                crewData->SignalBoard.LeftCannonsNeeded = 2;
                crewData->SignalBoard.RightCannonsNeeded = 0;
            }
            else if (ship->Steer < -FP._0_10)
            {
                crewData->SignalBoard.PreferredAttackSide = AttackSide.Right;
                crewData->SignalBoard.LeftCannonsNeeded = 0;
                crewData->SignalBoard.RightCannonsNeeded = 2;
            }
            else
            {
                crewData->SignalBoard.PreferredAttackSide = AttackSide.Both;
                crewData->SignalBoard.LeftCannonsNeeded = 1;
                crewData->SignalBoard.RightCannonsNeeded = 1;
            }
        }
        
        /// <summary>
        /// Checks if any human player on this ship is currently manning a cannon.
        /// Returns which side they're on so AI crew can support the same side.
        /// </summary>
        private AttackSide GetHumanOccupiedCannonSide(Frame f, Ship* ship, AIShipCrewData* crewData)
        {
            bool humanOnLeft = false;
            bool humanOnRight = false;
            
            for (int i = 0; i < ship->WeaponSlots.Length && i < 6; i++)
            {
                if (ship->WeaponSlots[i].Player != default)
                {
                    if (crewData->CannonIsLeftSide[i])
                        humanOnLeft = true;
                    else
                        humanOnRight = true;
                }
            }
            
            if (humanOnLeft && humanOnRight) return AttackSide.Both;
            if (humanOnLeft) return AttackSide.Left;
            if (humanOnRight) return AttackSide.Right;
            return AttackSide.None;
        }
        
        // =====================================================================
        // FULL AI MODE (no player captain)
        // =====================================================================
        
        private void UpdateFullAISignals(Frame f, ref Filter filter)
        {
            if (f.Unsafe.TryGetPointer<AIPilotBrain>(filter.Entity, out AIPilotBrain* pilot))
            {
                if (pilot->IsActive) return;
            }
            var ship = filter.Ship;
            var crewData = filter.CrewData;
            var shipTransform = f.Unsafe.GetPointer<Transform3D>(ship->WorldEntity);
            
            FPVector3 closestEnemyPos;
            FP closestDistSqr;
            bool foundEnemy = FindNearestEnemy(f, ship, shipTransform, filter.Entity, 
                out closestEnemyPos, out closestDistSqr);
            
            if (!foundEnemy || closestDistSqr > EngagementRangeSqr)
            {
                crewData->SignalBoard.ThreatLevel = ThreatLevel.Peaceful;
                crewData->SignalBoard.PreferredAttackSide = AttackSide.None;
                crewData->SignalBoard.LeftCannonsNeeded = 0;
                crewData->SignalBoard.RightCannonsNeeded = 0;
                return;
            }
            
            FPVector3 toEnemy = closestEnemyPos - shipTransform->Position;
            FPVector3 localToEnemy = shipTransform->InverseTransformDirection(toEnemy);
            bool enemyOnLeft = localToEnemy.X < 0;
            
            if (closestDistSqr < CloseRangeSqr)
            {
                crewData->SignalBoard.ThreatLevel = ThreatLevel.Battle;
                crewData->SignalBoard.PreferredAttackSide = AttackSide.Both;
                crewData->SignalBoard.LeftCannonsNeeded = 2;
                crewData->SignalBoard.RightCannonsNeeded = 2;
            }
            else
            {
                crewData->SignalBoard.ThreatLevel = ThreatLevel.Skirmish;
                
                if (enemyOnLeft)
                {
                    crewData->SignalBoard.PreferredAttackSide = AttackSide.Left;
                    crewData->SignalBoard.LeftCannonsNeeded = 3;
                    crewData->SignalBoard.RightCannonsNeeded = 0;
                }
                else
                {
                    crewData->SignalBoard.PreferredAttackSide = AttackSide.Right;
                    crewData->SignalBoard.LeftCannonsNeeded = 0;
                    crewData->SignalBoard.RightCannonsNeeded = 3;
                }
            }
        }
        
        // =====================================================================
        // CANNON CONFLICT RESOLUTION
        // If a human player walks toward an AI-reserved cannon, AI yields.
        // =====================================================================
        
        private void HandlePlayerCannonConflicts(Frame f, ref Filter filter)
        {
            var ship = filter.Ship;
            var crewData = filter.CrewData;
            
            if (!f.Unsafe.TryGetPointer<Transform3D>(ship->VisibleEntity, out Transform3D* shipTransform))
                return;
            
            // Iterate all human players on this ship
            var shipPlayers = f.ResolveList(ship->ShipPlayers);
            foreach (var playerResult in shipPlayers)
            {
                if (!f.Exists(playerResult.PlayerEntity)) continue;
                var playerTransform = f.Unsafe.GetPointer<Transform3D>(playerResult.PlayerEntity);
                
                FPVector3 playerLocalPos = shipTransform->InverseTransformPoint(playerTransform->Position);
                
                for (int i = 0; i < ship->WeaponSlots.Length && i < 6; i++)
                {
                    if (!crewData->CannonNodeReserved[i]) continue;
                    int reservedByCrewId = crewData->CannonNodeReservedBy[i];
                    if (reservedByCrewId < 0) continue;
                    
                    // Is a human player close to this AI-reserved cannon?
                    FPVector3 cannonPos = crewData->CannonPositions[i];
                    FPVector3 delta = playerLocalPos - cannonPos;
                    FP distSqr = delta.X * delta.X + delta.Z * delta.Z;
                    
                    if (distSqr < PlayerCannonYieldDistSqr)
                    {
                        // Human approaching — AI yields this cannon
                        crewData->CannonNodeReserved[i] = false;
                        crewData->CannonNodeReservedBy[i] = -1;
                        
                        // Find the AI crew member and reset their assignment
                        var crewFilter = f.Filter<AICrewBrain>();
                        while (crewFilter.NextUnsafe(out _, out AICrewBrain* crew))
                        {
                            if (crew->ShipVisibleEntity == ship->VisibleEntity && 
                                crew->AssignedCannonIndex == i &&
                                crew->CrewId == reservedByCrewId)
                            {
                                crew->AssignedCannonIndex = -1;
                                crew->CurrentState = AICrewState.Idle;
                                crew->DesiredFire = false;
                                crew->DesiredInteract = false;
                                crew->DesiredMoveSpeed = 0;
                                break;
                            }
                        }
                    }
                }
            }
        }
        
        // =====================================================================
        // SHARED: Enemy scanning (used by both modes)
        // =====================================================================
        
        private bool FindNearestEnemy(Frame f, Ship* ship, Transform3D* shipTransform, EntityRef selfEntity,
            out FPVector3 closestEnemyPos, out FP closestDistSqr)
        {
            closestDistSqr = FP._1000 * FP._1000;
            closestEnemyPos = FPVector3.Zero;
            bool found = false;
            
            // Player ships
            var shipFilter = f.Filter<Ship>();
            while (shipFilter.NextUnsafe(out EntityRef shipEntity, out Ship* otherShip))
            {
                if (otherShip->faction == ship->faction) continue;
                if (otherShip->Hull <= 0) continue;
                if (shipEntity == selfEntity) continue;
                
                var otherTransform = f.Unsafe.GetPointer<Transform3D>(otherShip->WorldEntity);
                FPVector3 delta = otherTransform->Position - shipTransform->Position;
                FP distSqr = delta.X * delta.X + delta.Z * delta.Z;
                
                if (distSqr < closestDistSqr)
                {
                    closestDistSqr = distSqr;
                    closestEnemyPos = otherTransform->Position;
                    found = true;
                }
            }
            
            // NPC ships
            var npcFilter = f.Filter<NPCShip>();
            while (npcFilter.NextUnsafe(out EntityRef npcEntity, out NPCShip* npcShip))
            {
                if (npcShip->spawnPoint.faction == ship->faction) continue;
                
                if (f.Unsafe.TryGetPointer<Transform3D>(npcEntity, out Transform3D* npcTransform))
                {
                    FPVector3 delta = npcTransform->Position - shipTransform->Position;
                    FP distSqr = delta.X * delta.X + delta.Z * delta.Z;
                    
                    if (distSqr < closestDistSqr)
                    {
                        closestDistSqr = distSqr;
                        closestEnemyPos = npcTransform->Position;
                        found = true;
                    }
                }
            }
            
            return found;
        }
    }
}