// =============================================================================
// AIPilotSystem.cs
// Place in: Assets/QuantumUser/Simulation/AI/
//
// The AI Pilot Brain. Runs on Ship entities that have AIPilotBrain component.
// Ticks every ~0.5 simulation seconds for decisions.
// Every frame: applies steer/throttle to Ship.Sails and Ship.Steer,
// fires captain cannons, and writes to CrewSignalBoard.
//
// ENGAGEMENT SOLVER:
// v1 (fallback): Point at enemy → approach → fire captain cannons at close range
// v2 (primary):  Achieve broadside angle → hold distance → fire side cannons
//                Uses lead prediction for intercept course
//
// The pilot WRITES the CrewSignalBoard. Crew READS it. This is the only
// interface between pilot and crew.
// =============================================================================

using Photon.Deterministic;
using UnityEngine;

namespace Quantum
{
    public unsafe class AIPilotSystem : SystemMainThreadFilter<AIPilotSystem.Filter>
    {
        // Brain tick intervals
        private static readonly FP BrainTickInterval = FP._0_50;
        
        // Default tuning (overridden by AIPilotBrain fields if set)
        private static readonly FP DefaultEngagementRange = 120;
        private static readonly FP DefaultCloseRange = 40;
        private static readonly FP DefaultBroadsideTolerance = 20;
        private static readonly FP DefaultDetectionRange = 250;
        private static readonly FP DefaultWaypointRadius = 30;
        
        // Sail adjustment duration
        private static readonly FP SailAdjustDuration = FP._1 + FP._0_50;
        
        // Evade thresholds
        private static readonly FP EvadeHullPercent = FP._0_50 - FP._0_10; // 40%
        private static readonly FP MaxEvadeDuration = 8;
        
        // Steer smoothing rate (per second)
        private static readonly FP SteerLerpRate = FP._2;
        private static readonly FP SailLerpRate = FP._1;
        
        public struct Filter
        {
            public EntityRef Entity;
            public Ship* Ship;
            public AIPilotBrain* Pilot;
        }

        public override void Update(Frame f, ref Filter filter)
        {
            if (!filter.Pilot->IsActive) return;
            if (filter.Ship->Hull <= 0) return;
            
            // ─── Every frame: apply controls to ship ───
            ApplyShipControls(f, ref filter);
            
            // ─── Brain tick at configured interval ───
            filter.Pilot->BrainTickAccumulator += f.DeltaTime;
            if (filter.Pilot->BrainTickAccumulator < BrainTickInterval) return;
            filter.Pilot->BrainTickAccumulator -= BrainTickInterval;
            
            // ─── Brain tick logic ───
            UpdateTargetTracking(f, ref filter);
            UpdateStateMachine(f, ref filter);
            RunEngagementSolver(f, ref filter);
            UpdateSailManagement(f, ref filter);
            WriteCrewSignals(f, ref filter);
        }
        
        // =====================================================================
        // TARGET TRACKING
        // =====================================================================
        
        private void UpdateTargetTracking(Frame f, ref Filter filter)
        {
            var pilot = filter.Pilot;
            var ship = filter.Ship;
            var shipTransform = f.Unsafe.GetPointer<Transform3D>(ship->WorldEntity);
            
            FP detectionRange = pilot->DetectionRange > 0 ? pilot->DetectionRange : DefaultDetectionRange;
            FP detectionRangeSqr = detectionRange * detectionRange;
            
            // Check if current target is still valid
            if (pilot->TargetEnemy != EntityRef.None)
            {
                if (!f.Exists(pilot->TargetEnemy))
                {
                    pilot->TargetEnemy = EntityRef.None;
                }
                else
                {
                    // Check if target ship is dead
                    bool targetDead = false;
                    if (f.Unsafe.TryGetPointer<Ship>(pilot->TargetEnemy, out Ship* targetShip))
                    {
                        targetDead = targetShip->Hull <= 0;
                    }
                    if (f.Unsafe.TryGetPointer<NPCShip>(pilot->TargetEnemy, out NPCShip* targetNPC))
                    {
                        targetDead = targetNPC->Hull <= 0;
                    }
                    if (targetDead) pilot->TargetEnemy = EntityRef.None;
                }
            }
            
            // Scan for new target if none
            if (pilot->TargetEnemy == EntityRef.None)
            {
                pilot->TargetEnemy = FindBestTarget(f, ship, shipTransform, filter.Entity, detectionRangeSqr);
            }
            
            // Update distance/angle to target
            if (pilot->TargetEnemy != EntityRef.None)
            {
                Transform3D* targetTransform = null;
                if (f.Unsafe.TryGetPointer<Ship>(pilot->TargetEnemy, out Ship* tShip))
                {
                    targetTransform = f.Unsafe.GetPointer<Transform3D>(tShip->WorldEntity);
                }
                else if (f.Unsafe.TryGetPointer<Transform3D>(pilot->TargetEnemy, out Transform3D* tNPC))
                {
                    targetTransform = tNPC;
                }
                
                if (targetTransform != null)
                {
                    // Lead prediction: estimate enemy velocity
                    FPVector3 enemyPos = targetTransform->Position;
                    pilot->EnemyVelocityEstimate = (enemyPos - pilot->LastEnemyPosition) / BrainTickInterval;
                    pilot->LastEnemyPosition = enemyPos;
                    
                    FPVector3 toEnemy = enemyPos - shipTransform->Position;
                    toEnemy.Y = 0;
                    pilot->DistanceToTarget = toEnemy.Magnitude;
                    pilot->TargetPosition = enemyPos;
                    
                    // Angle to target (signed, positive = target is clockwise from forward)
                    if (pilot->DistanceToTarget > FP._1)
                    {
                        pilot->AngleToTarget = FPVector3.SignedAngle(
                            shipTransform->Forward, toEnemy.Normalized, FPVector3.Up);
                        
                        // Relative bearing in local space
                        FPVector3 localToEnemy = shipTransform->InverseTransformDirection(toEnemy);
                        pilot->RelativeBearing = FPMath.Atan2(localToEnemy.X, localToEnemy.Z) * FP.Rad2Deg;
                    }
                }
            }
            else
            {
                pilot->DistanceToTarget = FP._1000;
                pilot->AngleToTarget = 0;
            }
        }
        
        private EntityRef FindBestTarget(Frame f, Ship* ship, Transform3D* shipTransform, EntityRef self, FP maxDistSqr)
        {
            EntityRef best = EntityRef.None;
            FP bestDistSqr = maxDistSqr;
            
            // Player ships
            var shipFilter = f.Filter<Ship>();
            while (shipFilter.NextUnsafe(out EntityRef entity, out Ship* other))
            {
                if (other->faction == ship->faction) continue;
                if (other->Hull <= 0) continue;
                if (entity == self) continue;
                
                var otherT = f.Unsafe.GetPointer<Transform3D>(other->WorldEntity);
                FPVector3 d = otherT->Position - shipTransform->Position;
                FP distSqr = d.X * d.X + d.Z * d.Z;
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    best = entity;
                }
            }
            
            // NPC ships
            var npcFilter = f.Filter<NPCShip>();
            while (npcFilter.NextUnsafe(out EntityRef entity, out NPCShip* npc))
            {
                if (npc->spawnPoint.faction == ship->faction) continue;
                if (npc->Hull <= 0) continue;
                
                if (f.Unsafe.TryGetPointer<Transform3D>(entity, out Transform3D* npcT))
                {
                    FPVector3 d = npcT->Position - shipTransform->Position;
                    FP distSqr = d.X * d.X + d.Z * d.Z;
                    if (distSqr < bestDistSqr)
                    {
                        bestDistSqr = distSqr;
                        best = entity;
                    }
                }
            }
            
            return best;
        }
        
        // =====================================================================
        // STATE MACHINE
        // =====================================================================
        
        private void UpdateStateMachine(Frame f, ref Filter filter)
        {
            var pilot = filter.Pilot;
            var ship = filter.Ship;
            //Debug.LogError("current state " + pilot->CurrentState + " - " + pilot->DesiredSailLevel);
            FP hullPercent = ship->Hull / ship->HullMax;
            FP engRange = pilot->EngagementRange > 0 ? pilot->EngagementRange : DefaultEngagementRange;
            FP closeRange = pilot->CloseRange > 0 ? pilot->CloseRange : DefaultCloseRange;
            
            pilot->StateTimer += BrainTickInterval;
            
            // ─── EVADE CHECK (highest priority override) ───
            if (hullPercent < EvadeHullPercent && pilot->CurrentState != PilotState.Evading && 
                pilot->CurrentState != PilotState.Retreating)
            {
                TransitionTo(pilot, PilotState.Evading);
                pilot->EvadeTimer = 0;
                return;
            }
            
            // ─── EVADE EXIT ───
            if (pilot->CurrentState == PilotState.Evading)
            {
                pilot->EvadeTimer += BrainTickInterval;
                if (pilot->EvadeTimer > MaxEvadeDuration || hullPercent > EvadeHullPercent + FP._0_10)
                {
                    // Re-engage or navigate
                    if (pilot->TargetEnemy != EntityRef.None)
                        TransitionTo(pilot, PilotState.ApproachEngage);
                    else
                        TransitionTo(pilot, PilotState.PatrolMove);
                }
                return;
            }
            
            // ─── NO TARGET ───
            if (pilot->TargetEnemy == EntityRef.None)
            {
                if (pilot->CurrentState != PilotState.PatrolMove && 
                    pilot->CurrentState != PilotState.Navigating &&
                    pilot->CurrentState != PilotState.Orbit)
                {
                    TransitionTo(pilot, pilot->HasWaypoint ? PilotState.Navigating : PilotState.PatrolMove);
                }
                return;
            }
            
            // ─── HAS TARGET: determine phase ───
            if (pilot->DistanceToTarget > engRange)
            {
                // Too far — approach
                if (pilot->CurrentState != PilotState.ApproachEngage)
                {
                    TransitionTo(pilot, PilotState.ApproachEngage);
                }
            }
            else if (pilot->DistanceToTarget < closeRange)
            {
                // Very close — captain cannons
                if (pilot->CurrentState != PilotState.CaptainCannonFire)
                {
                    TransitionTo(pilot, PilotState.CaptainCannonFire);
                }
            }
            else
            {
                // In engagement range — broadside or approach based on solver version
                if (pilot->UseBroadsideSolver)
                {
                    FP tolerance = pilot->BroadsideTolerance > 0 ? pilot->BroadsideTolerance : DefaultBroadsideTolerance;
                    FP absRelBearing = FPMath.Abs(pilot->RelativeBearing);
                    
                    // Are we at roughly 90 degrees (broadside)?
                    if (absRelBearing > (90 - tolerance) && absRelBearing < (90 + tolerance))
                    {
                        if (pilot->CurrentState != PilotState.BroadsideStrafe)
                        {
                            TransitionTo(pilot, PilotState.BroadsideStrafe);
                        }
                    }
                    else
                    {
                        // Need to adjust angle
                        if (pilot->CurrentState != PilotState.ApproachEngage)
                        {
                            TransitionTo(pilot, PilotState.ApproachEngage);
                        }
                    }
                }
                else
                {
                    // v1: just keep approaching until close range
                    if (pilot->CurrentState != PilotState.ApproachEngage)
                    {
                        TransitionTo(pilot, PilotState.ApproachEngage);
                    }
                }
            }
        }
        
        private void TransitionTo(AIPilotBrain* pilot, PilotState newState)
        {
            if (pilot->CurrentState != newState)
            {
                pilot->PreviousState = pilot->CurrentState;
                pilot->CurrentState = newState;
                pilot->StateTimer = 0;
            }
        }
        
        // =====================================================================
        // ENGAGEMENT SOLVER
        // =====================================================================
        
        private void RunEngagementSolver(Frame f, ref Filter filter)
        {
            var pilot = filter.Pilot;
            var ship = filter.Ship;
            
            // Reset output
            pilot->Engagement.FireCaptainCannons = false;
            pilot->Engagement.FireSideCannons = false;
            pilot->Engagement.WhichSide = AttackSide.None;
            
            if (pilot->TargetEnemy == EntityRef.None)
            {
                pilot->Engagement.Phase = EngagementPhase.None;
                return;
            }
            
            if (pilot->UseBroadsideSolver)
            {
                SolveBroadside_V2(f, ref filter);
            }
            else
            {
                SolveApproachRam_V1(f, ref filter);
            }
        }
        
        // ─── V1: Simple approach + captain cannon fire ───
        private void SolveApproachRam_V1(Frame f, ref Filter filter)
        {
            var pilot = filter.Pilot;
            var ship = filter.Ship;
            var shipTransform = f.Unsafe.GetPointer<Transform3D>(ship->WorldEntity);
            
            FP closeRange = pilot->CloseRange > 0 ? pilot->CloseRange : DefaultCloseRange;
            
            if (pilot->CurrentState == PilotState.Evading)
            {
                // Steer away from enemy
                FP awayAngle = pilot->AngleToTarget > 0 ? -FP._1 : FP._1;
                pilot->DesiredSteer = awayAngle;
                pilot->DesiredSailLevel = FP._1; // full speed escape
                pilot->Engagement.Phase = EngagementPhase.Disengage;
                return;
            }
            
            // Steer toward enemy
            FP steerToward = FPMath.Clamp(pilot->AngleToTarget / 90, -FP._1, FP._1);
            pilot->DesiredSteer = steerToward;
            pilot->DesiredSailLevel = FP._1; // full speed approach
            
            if (pilot->DistanceToTarget < closeRange)
            {
                pilot->Engagement.Phase = EngagementPhase.CloseRange;
                pilot->Engagement.FireCaptainCannons = true;
                pilot->DesiredSailLevel = FP._0_50; // slow down at close range
            }
            else
            {
                pilot->Engagement.Phase = EngagementPhase.Approach;
            }
        }
        
        // ─── V2: Full broadside with lead prediction ───
        private void SolveBroadside_V2(Frame f, ref Filter filter)
        {
            var pilot = filter.Pilot;
            var ship = filter.Ship;
            var shipTransform = f.Unsafe.GetPointer<Transform3D>(ship->WorldEntity);
            
            FP engRange = pilot->EngagementRange > 0 ? pilot->EngagementRange : DefaultEngagementRange;
            FP closeRange = pilot->CloseRange > 0 ? pilot->CloseRange : DefaultCloseRange;
            FP tolerance = pilot->BroadsideTolerance > 0 ? pilot->BroadsideTolerance : DefaultBroadsideTolerance;
            
            if (pilot->CurrentState == PilotState.Evading)
            {
                FP awayAngle = pilot->AngleToTarget > 0 ? -FP._1 : FP._1;
                pilot->DesiredSteer = awayAngle;
                pilot->DesiredSailLevel = FP._1;
                pilot->Engagement.Phase = EngagementPhase.Disengage;
                return;
            }
            
            // Lead prediction: predict where enemy will be
            FPVector3 predictedPos = pilot->TargetPosition;
            if (pilot->DistanceToTarget > FP._10)
            {
                // Time to reach = distance / our approximate speed
                // Ship speed is roughly: Sails * SailPowerMultiplier * 72 * windPower / 40
                // Simplified: assume ~50 units/sec at full sail
                FP approxSpeed = 50;
                FP timeToReach = pilot->DistanceToTarget / approxSpeed;
                timeToReach = FPMath.Clamp(timeToReach, 0, FP._3); // cap prediction to 3 seconds
                predictedPos = pilot->TargetPosition + pilot->EnemyVelocityEstimate * timeToReach;
            }
            
            FPVector3 toPredicted = predictedPos - shipTransform->Position;
            toPredicted.Y = 0;
            
            FP absRelBearing = FPMath.Abs(pilot->RelativeBearing);
            bool enemyOnLeft = pilot->RelativeBearing < 0;
            
            // ─── PHASE: Close range override ───
            if (pilot->DistanceToTarget < closeRange)
            {
                pilot->Engagement.Phase = EngagementPhase.CloseRange;
                pilot->Engagement.FireCaptainCannons = true;
                pilot->Engagement.FireSideCannons = true;
                pilot->Engagement.WhichSide = enemyOnLeft ? AttackSide.Left : AttackSide.Right;
                
                // At close range, try to circle the enemy
                FP circleSteer = enemyOnLeft ? FP._0_50 : -FP._0_50;
                pilot->DesiredSteer = circleSteer;
                pilot->DesiredSailLevel = FP._0_50 - FP._0_10; // slow, controlled
                return;
            }
            
            // ─── PHASE: Too far, approach ───
            if (pilot->DistanceToTarget > engRange)
            {
                pilot->Engagement.Phase = EngagementPhase.Approach;
                
                // Steer toward predicted position
                FP angleToPredict = FPVector3.SignedAngle(
                    shipTransform->Forward, toPredicted.Normalized, FPVector3.Up);
                pilot->DesiredSteer = FPMath.Clamp(angleToPredict / 60, -FP._1, FP._1);
                pilot->DesiredSailLevel = FP._1; // full speed
                return;
            }
            
            // ─── PHASE: In range, check broadside angle ───
            if (absRelBearing > (90 - tolerance) && absRelBearing < (90 + tolerance))
            {
                // BROADSIDE ACHIEVED
                pilot->Engagement.Phase = EngagementPhase.Broadside;
                pilot->Engagement.FireSideCannons = true;
                pilot->Engagement.WhichSide = enemyOnLeft ? AttackSide.Left : AttackSide.Right;
                
                // Hold the broadside: steer to maintain ~90 degree bearing
                // If enemy is drifting forward of our beam, steer toward them slightly
                // If enemy is drifting behind our beam, steer away slightly
                FP bearingError = FPMath.Abs(pilot->RelativeBearing) - 90;
                FP correctionSteer = bearingError / 45; // gentle correction
                
                // Flip correction based on which side enemy is on
                if (enemyOnLeft)
                    pilot->DesiredSteer = -correctionSteer; // if enemy moving forward of beam, turn left
                else
                    pilot->DesiredSteer = correctionSteer;
                
                pilot->DesiredSteer = FPMath.Clamp(pilot->DesiredSteer, -FP._0_50, FP._0_50);
                pilot->DesiredSailLevel = FP._0_50 - FP._0_10; // partial throttle to hold distance
                return;
            }
            
            // ─── PHASE: In range but wrong angle — adjust ───
            pilot->Engagement.Phase = EngagementPhase.AngleAdjust;
            
            // We need the enemy at 90 degrees off our bow.
            // To achieve this, we steer PERPENDICULAR to the line between us and the enemy.
            // If enemy is roughly ahead (bearing ~0), we need to turn hard to expose our side.
            // If enemy is roughly behind (bearing ~180), we need to turn to bring them to our beam.
            
            FP desiredBearing = enemyOnLeft ? -90 : 90;
            FP bearingDiff = desiredBearing - pilot->RelativeBearing;
            
            // Normalize to -180..180
            while (bearingDiff > 180) bearingDiff -= 360;
            while (bearingDiff < -180) bearingDiff += 360;
            
            pilot->DesiredSteer = FPMath.Clamp(bearingDiff / 60, -FP._1, FP._1);
            pilot->DesiredSailLevel = FP._0_75; // moderate speed while adjusting
        }
        
        // =====================================================================
        // SAIL MANAGEMENT
        // =====================================================================
        
        private void UpdateSailManagement(Frame f, ref Filter filter)
        {
            var pilot = filter.Pilot;
            
            // Sail level is set by the engagement solver based on state
            // Here we just handle the ADJUSTING_SAIL sub-state if needed
            
            switch (pilot->CurrentState)
            {
                case PilotState.ApproachEngage:
                    pilot->DesiredSailLevel = FP._1; // full speed
                    break;
                    
                case PilotState.BroadsideStrafe:
                    pilot->DesiredSailLevel = FP._0_50 - FP._0_10; // slow, controlled
                    break;
                    
                case PilotState.CaptainCannonFire:
                    pilot->DesiredSailLevel = FP._0_50; // moderate
                    break;
                    
                case PilotState.Evading:
                    pilot->DesiredSailLevel = FP._1; // max escape speed
                    break;
                    
                case PilotState.Orbit:
                    pilot->DesiredSailLevel = FP._0_50;
                    break;
                    
                case PilotState.PatrolMove:
                case PilotState.Navigating:
                    pilot->DesiredSailLevel = FP._0_75;
                    break;
                    
                case PilotState.Idle:
                    pilot->DesiredSailLevel = 0;
                    break;
            }
            
            // Patrol/Navigate: steer toward waypoint
            if ((pilot->CurrentState == PilotState.PatrolMove || pilot->CurrentState == PilotState.Navigating) 
                && pilot->TargetEnemy == EntityRef.None)
            {
                if (pilot->HasWaypoint)
                {
                    var shipT = f.Unsafe.GetPointer<Transform3D>(filter.Ship->WorldEntity);
                    FPVector3 toWP = pilot->CurrentWaypoint - shipT->Position;
                    toWP.Y = 0;
                    if (toWP.SqrMagnitude > FP._1)
                    {
                        FP angleToWP = FPVector3.SignedAngle(shipT->Forward, toWP.Normalized, FPVector3.Up);
                        pilot->DesiredSteer = FPMath.Clamp(angleToWP / 45, -FP._1, FP._1);
                        
                        // Check arrival
                        FP waypointRadius = pilot->WaypointArrivalRadius > 0 ? pilot->WaypointArrivalRadius : DefaultWaypointRadius;
                        if (toWP.Magnitude < waypointRadius)
                        {
                            pilot->HasWaypoint = false;
                        }
                    }
                }
                else
                {
                    pilot->DesiredSteer = 0;
                }
            }
        }
        
        // =====================================================================
        // APPLY CONTROLS TO SHIP (every frame)
        // =====================================================================
        
        private void ApplyShipControls(Frame f, ref Filter filter)
        {
            var pilot = filter.Pilot;
            var ship = filter.Ship;
            
            if (ship->spawningData.shipSpawned == ShipSpawn.Spawning) return;
            
            // Pilot can only control ship when the pilot crew member has reached the wheel
            if (f.Unsafe.TryGetPointer<AIShipCrewData>(filter.Entity, out AIShipCrewData* crewData))
            {
                if (!crewData->AIPilotAtWheel) return;
            }
            
            // Smoothly lerp Ship.Sails toward desired level
            FP sailDelta = pilot->DesiredSailLevel - ship->Sails;
            FP sailStep = SailLerpRate * f.DeltaTime;
            if (FPMath.Abs(sailDelta) < sailStep)
                ship->Sails = pilot->DesiredSailLevel;
            else
                ship->Sails += FPMath.Sign(sailDelta) * sailStep;
            ship->Sails = FPMath.Clamp(ship->Sails, 0, FP._1);
            
            // Smoothly lerp Ship.Steer toward desired steer
            FP steerDelta = pilot->DesiredSteer - ship->Steer;
            FP steerStep = SteerLerpRate * f.DeltaTime;
            if (FPMath.Abs(steerDelta) < steerStep)
                ship->Steer = pilot->DesiredSteer;
            else
                ship->Steer += FPMath.Sign(steerDelta) * steerStep;
            ship->Steer = FPMath.Clamp(ship->Steer, -FP._1, FP._1);
            
            // Mark ship as having moved (needed for spawn system)
            ship->spawningData.shipSpawnedStop = true;
            
            // ─── FIRE CAPTAIN CANNONS ───
            if (pilot->Engagement.FireCaptainCannons && ship->CaptainCannonCooldown <= 0 && ship->Hull > 0)
            {
                var shipData = f.FindAsset<ShipData>(ship->ShipData.Id);
                if (shipData.CanShootCaptainCannon)
                {
                    FireCaptainCannons(f, ship, shipData);
                }
            }
        }
        
        private void FireCaptainCannons(Frame f, Ship* ship, ShipData shipData)
        {
            var shipVisibleTransform = f.Unsafe.GetPointer<Transform3D>(ship->VisibleEntity);
            var shipWorldRigidbody = f.Unsafe.GetPointer<PhysicsBody3D>(ship->WorldEntity);
            
            // Set cooldown (matching PlayerSystem captain cannon pattern)
            ship->CaptainCannonCooldown = FP._2;
            
            // Get default weapon data for projectile stats (speed, falloff, range, damage)
            var defaultWeaponData = ConfigAssetsHelper.GetWeaponDataByWeaponType(f, WeaponType.DefaultCannon);
            
            // Fire each captain cannon
            for (int i = 0; i < shipData.CaptainsCannonsData.Length; i++)
            {
                var cannonData = shipData.CaptainsCannonsData[i];
                
                var cannonballPrototype = f.FindAsset<EntityPrototype>("Resources/DB/Cannonballs/CannonballEntityPrototype");
                if (cannonballPrototype == null) continue;
                
                EntityRef cannonballEntity = f.Create(cannonballPrototype);
                Transform3D* cannonballTransform = f.Unsafe.GetPointer<Transform3D>(cannonballEntity);
                
                // Position at cannon position on ship
                cannonballTransform->Position = shipVisibleTransform->TransformPoint(cannonData.CannonPosition);
                
                // Direction is the local-space fire direction from CaptainsCannonData
                FPVector3 worldDirection = shipVisibleTransform->TransformDirection(cannonData.Direction.Normalized);
                FPVector3 velocity = worldDirection * defaultWeaponData.InitialVelocity + shipWorldRigidbody->Velocity;
                
                var projectile = f.Unsafe.GetPointer<Projectile>(cannonballEntity);
                projectile->Velocity = velocity;
                projectile->Falloff = defaultWeaponData.Falloff;
                projectile->Range = defaultWeaponData.Range;
                projectile->WeaponType = WeaponType.DefaultCannon;
                projectile->OriginShipEntity = ship->WorldEntity;
                projectile->OriginShipVisible = ship->VisibleEntity;
                projectile->OriginPlayer = default;
                projectile->Damage = defaultWeaponData.MaxDamage + ship->ShipDamageModifier;
                projectile->IsCaptainsCannons = true;
                projectile->faction = ship->faction;
            }
            
            // Fire event for VFX
            f.Events.FiredCannon(ship->VisibleEntity, 99, default, FPVector3.Zero, FP._2);
        }
        
        // =====================================================================
        // CREW SIGNAL BOARD WRITING
        // =====================================================================
        
        private void WriteCrewSignals(Frame f, ref Filter filter)
        {
            if (!f.Unsafe.TryGetPointer<AIShipCrewData>(filter.Entity, out AIShipCrewData* crewData))
                return;
            
            // Only write signals if we're in FullAI mode — PlayerPilotAICrew mode
            // has its own signal logic from AISignalBoardUpdater
            if (crewData->ShipMode != AIShipMode.FullAI) return;
            
            var pilot = filter.Pilot;
            var ship = filter.Ship;
            var signal = &crewData->SignalBoard;
            
            // Repair urgency (always computed from hull)
            FP hullPercent = ship->Hull / ship->HullMax;
            if (hullPercent < FP._0_20)
                signal->RepairUrgency = RepairUrgency.Critical;
            else if (hullPercent < FP._0_50 - FP._0_10)
                signal->RepairUrgency = RepairUrgency.High;
            else if (hullPercent < FP._0_75)
                signal->RepairUrgency = RepairUrgency.Low;
            else
                signal->RepairUrgency = RepairUrgency.None;
            
            // Threat level from pilot state
            switch (pilot->CurrentState)
            {
                case PilotState.BroadsideStrafe:
                case PilotState.CaptainCannonFire:
                    signal->ThreatLevel = ThreatLevel.Battle;
                    break;
                case PilotState.ApproachEngage:
                    signal->ThreatLevel = ThreatLevel.Skirmish;
                    break;
                case PilotState.Evading:
                    signal->ThreatLevel = ThreatLevel.Overwhelmed;
                    break;
                default:
                    signal->ThreatLevel = ThreatLevel.Peaceful;
                    break;
            }
            
            // Attack side from engagement solver
            signal->PreferredAttackSide = pilot->Engagement.WhichSide;
            
            switch (pilot->Engagement.WhichSide)
            {
                case AttackSide.Left:
                    signal->LeftCannonsNeeded = 3;
                    signal->RightCannonsNeeded = 0;
                    break;
                case AttackSide.Right:
                    signal->LeftCannonsNeeded = 0;
                    signal->RightCannonsNeeded = 3;
                    break;
                case AttackSide.Both:
                    signal->LeftCannonsNeeded = 2;
                    signal->RightCannonsNeeded = 2;
                    break;
                default:
                    signal->LeftCannonsNeeded = 0;
                    signal->RightCannonsNeeded = 0;
                    break;
            }
        }
        
        // =====================================================================
        // INITIALIZATION HELPER
        // =====================================================================
        
        /// <summary>
        /// Call this after creating a ship to set up the pilot brain.
        /// For FullAI ships: pilot starts INACTIVE. The pilot crew member walks to the wheel
        /// and activates it when they arrive (handled by AICrewInputProvider).
        /// For PlayerPilotAICrew ships: pilot stays inactive (human controls the ship).
        /// </summary>
        public static void InitializePilot(Frame f, EntityRef shipVisibleEntity, bool useBroadside = true)
        {
            if (!f.Has<AIPilotBrain>(shipVisibleEntity))
            {
                f.Add<AIPilotBrain>(shipVisibleEntity);
            }
            
            var pilot = f.Unsafe.GetPointer<AIPilotBrain>(shipVisibleEntity);
            // Always start inactive — pilot crew member activates when reaching wheel
            pilot->IsActive = false;
            pilot->CurrentState = PilotState.Idle;
            pilot->PreviousState = PilotState.Idle;
            pilot->UseBroadsideSolver = useBroadside;
            pilot->TargetEnemy = EntityRef.None;
            pilot->HasWaypoint = false;
            
            // Default tuning (can be overridden per ship type)
            pilot->EngagementRange = DefaultEngagementRange;
            pilot->CloseRange = DefaultCloseRange;
            pilot->BroadsideTolerance = DefaultBroadsideTolerance;
            pilot->DetectionRange = DefaultDetectionRange;
            pilot->WaypointArrivalRadius = DefaultWaypointRadius;
            
            pilot->DesiredSailLevel = 0;
            pilot->DesiredSteer = 0;
            pilot->BrainTickAccumulator = 0;
            
            Log.Info($"AIPilotSystem: Initialized pilot on ship, broadside={useBroadside} (waiting for crew to reach wheel)");
        }
    }
}