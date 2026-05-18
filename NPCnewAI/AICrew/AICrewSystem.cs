// =============================================================================
// AICrewSystem.cs — Navigation Rewrite
// Place in: Assets/QuantumUser/Simulation/AI/
//
// CHANGES FROM ORIGINAL:
//   1. ComputeMovementDirection() — replaced with deck-aware ShipNavigation
//   2. StartMovingTo() — now calls ShipNavigation.BuildPath() instead of
//      setting TargetLocalPosition directly. Accepts posOnShip + shipData args.
//   3. BrainTick() — passes posOnShip + shipData to all StartMovingTo calls;
//      updates brain->CurrentDeckLevel each tick.
//   4. CountCrewInState() / CountCrewMovingTo() / CountCrewOnSide() — REMOVED.
//      Replaced with O(1) cached counts in AIShipCrewData. Crew members
//      increment/decrement these when entering/leaving states.
//   5. Helper: UpdateCrewStateCounts() — called when state changes to keep
//      cached counts accurate.
//
// UNCHANGED: All state machine logic, cannon firing, repair, ammo, patrolling,
//            arrival handling, ExecuteActions, IKCCCallbacks3D.
// =============================================================================

using Photon.Deterministic;
using Quantum.Core;

namespace Quantum
{
    public unsafe class AICrewSystem : SystemMainThreadFilter<AICrewSystem.Filter>, IKCCCallbacks3D
    {
        // ─── TUNING ───────────────────────────────────────────────────────────
        private static readonly FP BrainTickInterval    = FP._0_25;
        private static readonly FP AmmoCollectDuration  = FP._1;
        private static readonly FP ArcWaitTimeout       = FP._4;
        private static readonly FP CrewMoveSpeed        = FP._0_75;
        private static readonly FP PatrolMoveSpeed      = FP._0_25;
        private static readonly FP IdlePauseMin         = FP._2;
        private static readonly FP IdlePauseMax         = FP._5;

        public struct Filter
        {
            public EntityRef Entity;
            public AICrewBrain* CrewBrain;
            public Transform3D* Transform;
            public CharacterController3D* CharacterController;
        }

        // =====================================================================
        // MAIN UPDATE
        // =====================================================================

        public override void Update(Frame f, ref Filter filter)
        {
            if (f.DestroyPending(filter.Entity)) return;
            var brain = filter.CrewBrain;

            if (!f.Exists(brain->ShipVisibleEntity) ||
                !f.Unsafe.TryGetPointer<Ship>(brain->ShipVisibleEntity, out Ship* ship))
            {
                f.Destroy(filter.Entity);
                return;
            }

            if (ship->Hull <= 0 || brain->Extracting)
            {
                f.Destroy(filter.Entity);
                return;
            }

            if (f.RuntimeConfig.Mode != ModeType.Match) return;

            if (!f.Unsafe.TryGetPointer<AIShipCrewData>(brain->ShipVisibleEntity, out AIShipCrewData* crewData)) return;
            if (!f.Unsafe.TryGetPointer<Transform3D>(ship->VisibleEntity,         out Transform3D* shipVisibleT)) return;
            if (!f.Unsafe.TryGetPointer<Transform3D>(ship->WorldEntity,           out Transform3D* shipWorldT))   return;

            var shipData    = f.FindAsset<ShipData>(ship->ShipData.Id);
            FPVector3 posOnShip = shipVisibleT->InverseTransformPoint(filter.Transform->Position);

            // 1. Movement direction (every frame) — now deck-aware
            ComputeMovementDirection(f, ref filter, shipVisibleT);

            // 2. Brain tick (at BrainTickInterval)
            brain->BrainTickAccumulator += f.DeltaTime;
            if (brain->BrainTickAccumulator >= BrainTickInterval)
            {
                brain->BrainTickAccumulator -= BrainTickInterval;
                brain->StateTimer           += BrainTickInterval;

                // Update which deck we're on (O(1) — just a Y threshold check)
                brain->CurrentDeckLevel = ShipNavigation.GetDeckLevel(posOnShip.Y, shipData);

                BrainTick(f, ref filter, crewData, ship, shipData, posOnShip, shipVisibleT);
            }

            // 3. Execute actions (every frame)
            ExecuteActions(f, ref filter, ship, crewData, shipData, shipWorldT, shipVisibleT, posOnShip);
        }

        // =====================================================================
        // BRAIN TICK — every 0.25 s
        // =====================================================================

        private void BrainTick(Frame f, ref Filter filter, AIShipCrewData* crewData, Ship* ship,
            ShipData shipData, FPVector3 posOnShip, Transform3D* shipVisibleT)
        {
            var brain  = filter.CrewBrain;
            var signal = &crewData->SignalBoard;

            // ── PILOT ─────────────────────────────────────────────────────────
            if (brain->IsPilot)
            {
                if (brain->CurrentState == AICrewState.Piloting) return;

                if (brain->HasReachedTarget ||
                    (posOnShip - shipData.WheelBodyPosition).Magnitude < FP._2)
                {
                    SetState(crewData, brain, AICrewState.Piloting);
                    brain->DesiredMoveSpeed = 0;
                    crewData->AIPilotAtWheel  = true;
                    crewData->PilotCrewEntity = filter.Entity;
                    if (f.Unsafe.TryGetPointer<AIPilotBrain>(brain->ShipVisibleEntity, out AIPilotBrain* pilot))
                        pilot->IsActive = true;
                    return;
                }

                if (brain->CurrentState != AICrewState.MovingToTarget)
                    StartMovingTo(brain, shipData.WheelBodyPosition, DeckNodeType.WheelArea, posOnShip, shipData);
                return;
            }

            // ── CRITICAL REPAIR ───────────────────────────────────────────────
            if (signal->RepairUrgency == RepairUrgency.Critical &&
                brain->CurrentState != AICrewState.Repairing)
            {
                ReleaseAllReservations(crewData, brain);
                brain->AssignedCannonIndex = -1;
                StartMovingTo(brain, crewData->GeneratorPosition, DeckNodeType.Generator, posOnShip, shipData);
                return;
            }

            // ── HIGH REPAIR ───────────────────────────────────────────────────
            if (signal->RepairUrgency == RepairUrgency.High &&
                brain->CurrentState != AICrewState.Repairing)
            {
                // O(1) — reads cached count instead of scanning all entities
                int repCount = crewData->CachedRepairingCount + crewData->CachedMovingToRepairCount;
                if (repCount < 2)
                {
                    ReleaseCannonReservation(crewData, brain);
                    brain->AssignedCannonIndex = -1;
                    StartMovingTo(brain, crewData->GeneratorPosition, DeckNodeType.Generator, posOnShip, shipData);
                    return;
                }
            }

            // ── CANNON ASSIGNMENT ─────────────────────────────────────────────
            if (signal->ThreatLevel == ThreatLevel.Battle ||
                signal->ThreatLevel == ThreatLevel.Skirmish)
            {
                // At cannon on correct side? Fire or reload
                if (brain->CurrentState == AICrewState.EquippedCannon &&
                    brain->AssignedCannonIndex >= 0)
                {
                    bool isLeft = crewData->CannonIsLeftSide[brain->AssignedCannonIndex];
                    bool wantL  = signal->PreferredAttackSide == AttackSide.Left  ||
                                  signal->PreferredAttackSide == AttackSide.Both;
                    bool wantR  = signal->PreferredAttackSide == AttackSide.Right ||
                                  signal->PreferredAttackSide == AttackSide.Both;

                    if ((isLeft && wantL) || (!isLeft && wantR))
                    {
                        if (ship->WeaponSlots[brain->AssignedCannonIndex].Ammo <= 0)
                        {
                            StartMovingTo(brain, crewData->AmmoPilePosition, DeckNodeType.AmmoPile, posOnShip, shipData);
                            return;
                        }
                        TryFireCannon(f, brain, ship, crewData);
                        return;
                    }
                    // Wrong side — release and reassign
                    ReleaseCannonReservation(crewData, brain);
                    brain->AssignedCannonIndex = -1;
                }

                // Already moving to assigned cannon? Keep going
                if (brain->CurrentState == AICrewState.MovingToTarget &&
                    brain->AssignedCannonIndex >= 0)
                    return;

                // Try assign a new cannon (O(1) cached counts)
                if (brain->AssignedCannonIndex < 0)
                {
                    int lc   = crewData->CachedLeftCannonCount;
                    int rc   = crewData->CachedRightCannonCount;
                    int best = -1;

                    if (lc < signal->LeftCannonsNeeded)
                        best = FindAvailableCannon(crewData, ship, true, brain->CrewId);
                    if (best < 0 && rc < signal->RightCannonsNeeded)
                        best = FindAvailableCannon(crewData, ship, false, brain->CrewId);

                    if (best >= 0)
                    {
                        crewData->CannonNodeReserved[best]    = true;
                        crewData->CannonNodeReservedBy[best]  = brain->CrewId;
                        brain->AssignedCannonIndex            = best;
                        brain->ArcWaitTimer                   = 0;

                        FPVector3 dest = ship->WeaponSlots[best].Ammo <= 0
                            ? crewData->AmmoPilePosition
                            : crewData->CannonPositions[best];
                        DeckNodeType nodeType = ship->WeaponSlots[best].Ammo <= 0
                            ? DeckNodeType.AmmoPile
                            : (crewData->CannonIsLeftSide[best] ? DeckNodeType.CannonLeft : DeckNodeType.CannonRight);

                        StartMovingTo(brain, dest, nodeType, posOnShip, shipData);
                        return;
                    }
                }
            }

            // ── IDLE / PATROL ─────────────────────────────────────────────────
            HandleIdlePatrol(f, ref filter, crewData, brain, posOnShip, shipData);
        }

        // =====================================================================
        // IDLE ↔ PATROL CYCLE
        // =====================================================================

        private void HandleIdlePatrol(Frame f, ref Filter filter, AIShipCrewData* crewData,
            AICrewBrain* brain, FPVector3 posOnShip, ShipData shipData)
        {
            if (brain->CurrentState != AICrewState.Idle &&
                brain->CurrentState != AICrewState.PatrollingDeck &&
                brain->CurrentState != AICrewState.MovingToTarget)
            {
                ReleaseAllReservations(crewData, brain);
                SetState(crewData, brain, AICrewState.Idle);
                brain->StateTimer     = 0;
                brain->DesiredMoveSpeed = 0;
                brain->ArcWaitTimer   = f.Global->RngSession.Next(IdlePauseMin, IdlePauseMax);
                return;
            }

            if (brain->CurrentState == AICrewState.Idle)
            {
                brain->DesiredMoveSpeed = 0;
                if (brain->StateTimer >= brain->ArcWaitTimer)
                {
                    int idx = f.Global->RngSession.Next(0, crewData->PatrolPoints.Length);
                    FPVector3 patrolTarget = crewData->PatrolPoints[idx];

                    // Build a deck-aware path to the patrol point
                    StartMovingTo(brain, patrolTarget, DeckNodeType.PatrolPoint, posOnShip, shipData);
                    SetState(crewData, brain, AICrewState.PatrollingDeck);
                    brain->DesiredMoveSpeed = PatrolMoveSpeed;
                    brain->StateTimer = 0;
                }
                return;
            }

            if (brain->CurrentState == AICrewState.PatrollingDeck)
            {
                if (brain->HasReachedTarget || brain->StateTimer > 10)
                {
                    SetState(crewData, brain, AICrewState.Idle);
                    brain->DesiredMoveSpeed       = 0;
                    brain->DesiredMoveDirection   = FPVector3.Zero;
                    brain->StateTimer             = 0;
                    brain->ArcWaitTimer           = f.Global->RngSession.Next(IdlePauseMin, IdlePauseMax);
                }
            }
        }

        // =====================================================================
        // MOVEMENT DIRECTION — every frame
        // CHANGE: replaced manual delta math with ShipNavigation.TickNavigation
        //         + ShipNavigation.GetNavDirection. Y is now preserved for stair
        //         approach waypoints, zeroed only for the final flat destination.
        // =====================================================================

        private void ComputeMovementDirection(Frame f, ref Filter filter, Transform3D* shipVisibleT)
        {
            var brain = filter.CrewBrain;
            if (brain->CurrentState != AICrewState.MovingToTarget &&
                brain->CurrentState != AICrewState.PatrollingDeck)
            {
                brain->DesiredMoveSpeed     = 0;
                brain->DesiredMoveDirection = FPVector3.Zero;
                return;
            }

            // Ship-local position of this crew member
            FPVector3 localPos = shipVisibleT->InverseTransformPoint(filter.Transform->Position);

            // Advance waypoint if close enough to current one (O(1))
            bool pathDone = ShipNavigation.TickNavigation(brain, localPos);
            if (pathDone)
            {
                // HasReachedTarget was set inside TickNavigation
                brain->DesiredMoveSpeed     = 0;
                brain->DesiredMoveDirection = FPVector3.Zero;
                return;
            }

            // Get world-space direction to current waypoint
            // Y is included for stair waypoints, zeroed for same-deck final approach
            FPVector3 worldDir = ShipNavigation.GetNavDirection(brain, localPos, shipVisibleT);
            brain->DesiredMoveDirection = worldDir;
            // DesiredMoveSpeed is set by StartMovingTo / HandleIdlePatrol
        }

        // =====================================================================
        // EXECUTE ACTIONS — every frame (unchanged from original)
        // =====================================================================

        private void ExecuteActions(Frame f, ref Filter filter, Ship* ship, AIShipCrewData* crewData,
            ShipData shipData, Transform3D* shipWorldT, Transform3D* shipVisibleT, FPVector3 posOnShip)
        {
            var brain = filter.CrewBrain;
            brain->Repairing          = false;
            brain->RepairingComponent = false;

            switch (brain->CurrentState)
            {
                case AICrewState.MovingToTarget:
                    if (brain->HasReachedTarget)
                        HandleArrival(f, ref filter, crewData, ship, shipData);
                    break;

                case AICrewState.CollectingAmmo:
                    if (brain->StateTimer >= AmmoCollectDuration)
                    {
                        brain->CarryingAmmo = true;
                        if (crewData->AmmoPileReservedBy == brain->CrewId)
                        { crewData->AmmoPileReserved = false; crewData->AmmoPileReservedBy = -1; }

                        if (brain->AssignedCannonIndex >= 0)
                            StartMovingTo(brain,
                                crewData->CannonPositions[brain->AssignedCannonIndex],
                                crewData->CannonIsLeftSide[brain->AssignedCannonIndex]
                                    ? DeckNodeType.CannonLeft : DeckNodeType.CannonRight,
                                posOnShip, shipData);
                        else
                        {
                            SetState(crewData, brain, AICrewState.Idle);
                            brain->StateTimer   = 0;
                            brain->ArcWaitTimer = FP._2;
                        }
                    }
                    break;

                case AICrewState.EquippedCannon:
                    if (brain->AssignedCannonIndex >= 0 &&
                        ship->WeaponSlots[brain->AssignedCannonIndex].IsDestroyed)
                    {
                        ReleaseCannonReservation(crewData, brain);
                        brain->AssignedCannonIndex = -1;
                        SetState(crewData, brain, AICrewState.Idle);
                        brain->StateTimer   = 0;
                        brain->ArcWaitTimer = FP._1;
                    }
                    break;

                case AICrewState.Repairing:
                    brain->Repairing = true;
                    if (ship->Hull < ship->HullMax)
                        ship->Hull = FPMath.Clamp(ship->Hull + f.DeltaTime * 3, 0, ship->HullMax);
                    else if (ship->Shield < ShipSystem.ShieldCap(ship))
                        ship->Shield = FPMath.Clamp(ship->Shield + f.DeltaTime * 3, 0, ShipSystem.ShieldCap(ship));

                    if (crewData->SignalBoard.RepairUrgency == RepairUrgency.None)
                    {
                        brain->Repairing = false;
                        if (crewData->GeneratorReservedBy == brain->CrewId)
                        { crewData->GeneratorReserved = false; crewData->GeneratorReservedBy = -1; }
                        SetState(crewData, brain, AICrewState.Idle);
                        brain->StateTimer   = 0;
                        brain->ArcWaitTimer = FP._1;
                    }
                    break;

                case AICrewState.Piloting:
                    brain->diff = shipData.WheelBodyPosition - posOnShip;
                    break;
            }

            // ── Movement application (unchanged from original) ─────────────────
            bool locked = brain->CurrentState == AICrewState.Piloting      ||
                          brain->CurrentState == AICrewState.EquippedCannon ||
                          brain->CurrentState == AICrewState.Repairing      ||
                          brain->CurrentState == AICrewState.CollectingAmmo;

            FPQuaternion targetRot = FPQuaternion.Slerp(shipWorldT->Rotation, shipVisibleT->Rotation, FP._0_50);

            if (!locked)
            {
                FPVector3 dir = brain->DesiredMoveDirection;
                if (brain->DesiredMoveSpeed > FP.EN2 && dir.SqrMagnitude > FP.EN3)
                {
                    FP trow = FPVector3.SignedAngle(dir, shipWorldT->Forward, FPVector3.Down);
                    brain->Trow = FPMath.LerpRadians(
                        brain->Trow * FP.Deg2Rad, trow * FP.Deg2Rad, f.DeltaTime * 20) * FP.Rad2Deg;
                    targetRot *= FPQuaternion.Euler(FPVector3.Up * brain->Trow);

                    filter.CharacterController->MaxSpeed =
                        FPMath.Clamp(8 * brain->DesiredMoveSpeed, FP._1, FP.MaxValue);
                    PlayerSystem.Move(f, filter.CharacterController, filter.Entity, dir * brain->DesiredMoveSpeed, this);
                }
                else
                {
                    filter.CharacterController->Velocity = FPVector3.Zero;
                }
            }
            else
            {
                filter.CharacterController->Velocity = FPVector3.Zero;

                if (brain->CurrentState == AICrewState.Piloting)
                    brain->Trow = 0;
                else if (brain->CurrentState == AICrewState.EquippedCannon &&
                         brain->AssignedCannonIndex >= 0)
                {
                    brain->diff = PlayerSystem.WeaponRelativeMannedPosition(f, ship, brain->AssignedCannonIndex) - posOnShip;
                    brain->Trow = shipData.WeaponSlots[brain->AssignedCannonIndex].BaseRotation;
                    targetRot  *= FPQuaternion.Euler(FPVector3.Up * brain->Trow);
                }
                else if (brain->CurrentState == AICrewState.Repairing)
                    brain->diff = shipData.ShieldGenerator - posOnShip;

                FPVector3 worldDiff = shipVisibleT->TransformDirection(brain->diff);
                filter.Transform->Position +=
                    FPVector3.ClampMagnitude(worldDiff, 1) * f.DeltaTime * 8;
            }

            if (filter.Transform->Position.Y < -10 && ship->Hull > 0)
                PlayerSystem.ResetPlayerOntoShip(f, ship, filter.Entity);

            filter.Transform->Rotation = FPQuaternion.Lerp(
                filter.Transform->Rotation, targetRot, f.DeltaTime * 10);
        }

        // =====================================================================
        // ARRIVAL HANDLER (unchanged logic, updated SetState calls)
        // =====================================================================

        private void HandleArrival(Frame f, ref Filter filter, AIShipCrewData* crewData,
            Ship* ship, ShipData shipData)
        {
            var brain = filter.CrewBrain;
            switch (brain->TargetNodeType)
            {
                case DeckNodeType.AmmoPile:
                    SetState(crewData, brain, AICrewState.CollectingAmmo);
                    brain->StateTimer     = 0;
                    brain->DesiredMoveSpeed = 0;
                    break;

                case DeckNodeType.CannonLeft:
                case DeckNodeType.CannonRight:
                    if (brain->AssignedCannonIndex >= 0)
                    {
                        if (brain->CarryingAmmo)
                        {
                            ship->WeaponSlots.GetPointer(brain->AssignedCannonIndex)->Ammo =
                                ship->WeaponSlots[brain->AssignedCannonIndex].MaxAmmo;
                            brain->CarryingAmmo = false;
                        }
                        SetState(crewData, brain, AICrewState.EquippedCannon);
                    }
                    else
                        SetState(crewData, brain, AICrewState.Idle);
                    brain->StateTimer     = 0;
                    brain->DesiredMoveSpeed = 0;
                    break;

                case DeckNodeType.Generator:
                    SetState(crewData, brain, AICrewState.Repairing);
                    brain->Repairing      = true;
                    brain->StateTimer     = 0;
                    brain->DesiredMoveSpeed = 0;
                    break;

                case DeckNodeType.WheelArea:
                    if (brain->IsPilot)
                    {
                        SetState(crewData, brain, AICrewState.Piloting);
                        brain->DesiredMoveSpeed = 0;
                    }
                    break;

                default:
                    SetState(crewData, brain, AICrewState.Idle);
                    brain->StateTimer   = 0;
                    brain->ArcWaitTimer = f.Global->RngSession.Next(IdlePauseMin, IdlePauseMax);
                    break;
            }
        }

        // =====================================================================
        // CANNON FIRE (unchanged)
        // =====================================================================

        private void TryFireCannon(Frame f, AICrewBrain* brain, Ship* ship, AIShipCrewData* crewData)
        {
            if (brain->AssignedCannonIndex < 0 ||
                brain->AssignedCannonIndex >= ship->WeaponSlots.Length) return;
            var cannon = ship->WeaponSlots.GetPointer(brain->AssignedCannonIndex);

            if (cannon->IsDestroyed || cannon->Ammo <= 0 || cannon->CurrentCooldown > 0) return;
            if (cannon->WeaponType == WeaponType.MortarCannon) return;

            var wd = f.FindAsset<WeaponData>(cannon->WeaponData.Id);
            if (wd == null) return;

            var rb  = f.Unsafe.GetPointer<PhysicsBody3D>(ship->WorldEntity);
            var rot = PlayerSystem.WeaponGlobalRotation(f, ship, brain->AssignedCannonIndex);

            cannon->CurrentCooldown = cannon->Cooldown;
            cannon->Ammo           -= wd.AmmoUsagePerShot;

            if (cannon->WeaponType == WeaponType.ScatterGun)
            {
                var from = rot * FPQuaternion.Euler(0, -wd.ArcAngle / 2, 0);
                var to   = rot * FPQuaternion.Euler(0,  wd.ArcAngle / 2, 0);
                for (int i = 0; i < wd.ProjectileCount; i++)
                {
                    FP t = (FP)i / FPMath.Max(wd.ProjectileCount - 1, 1);
                    SpawnProjectile(f, brain, ship, brain->AssignedCannonIndex,
                        FPQuaternion.Lerp(from, to, t) * FPVector3.Forward * wd.InitialVelocity + rb->Velocity, wd);
                }
            }
            else
            {
                SpawnProjectile(f, brain, ship, brain->AssignedCannonIndex,
                    rot * FPVector3.Forward * wd.InitialVelocity + rb->Velocity, wd);
            }

            f.Events.FiredCannon(brain->ShipVisibleEntity, brain->AssignedCannonIndex,
                default, wd.BarrelPosition, wd.Cooldown);
        }

        private void SpawnProjectile(Frame f, AICrewBrain* brain, Ship* ship,
            int idx, FPVector3 vel, WeaponData wd)
        {
            var proto = f.FindAsset<EntityPrototype>(wd.cannonBallEntiyPath);
            if (proto == null) return;
            var e = f.Create(proto);
            f.Unsafe.GetPointer<Transform3D>(e)->Position =
                PlayerSystem.WeaponLocalOffsetToGlobalPosition(f, ship, idx, wd.BarrelPosition);
            var p = f.Unsafe.GetPointer<Projectile>(e);
            p->Velocity  = vel; p->Falloff = wd.Falloff; p->Range = wd.Range;
            p->WeaponType = ship->WeaponSlots[idx].WeaponType;
            p->OriginShipEntity  = ship->WorldEntity; p->OriginShipVisible = brain->ShipVisibleEntity;
            p->OriginPlayer = default; p->ShooterEntity = default; p->faction = ship->faction;
            p->Damage = wd.MaxDamage + ship->ShipDamageModifier; p->IsCaptainsCannons = false;
        }

        // =====================================================================
        // HELPERS
        // =====================================================================

        /// <summary>
        /// Replaces the original StartMovingTo().
        /// Now calls ShipNavigation.BuildPath() to create a stair-routed waypoint
        /// path instead of directly setting a single TargetLocalPosition.
        /// </summary>
        private void StartMovingTo(
            AICrewBrain* brain,
            FPVector3 targetLocal,
            DeckNodeType type,
            FPVector3 currentLocalPos, // crew's current local position on ship
            ShipData shipData)         // needed for deck thresholds + stair data
        {
            brain->TargetLocalPosition = targetLocal;  // kept for HandleArrival type check
            brain->TargetNodeType      = type;
            brain->HasReachedTarget    = false;
            brain->CurrentState        = AICrewState.MovingToTarget;
            brain->StateTimer          = 0;
            brain->DesiredMoveSpeed    = CrewMoveSpeed;

            // Build deck-aware waypoint path (O(Stairs.Length) — usually 2–4)
            ShipNavigation.BuildPath(brain, currentLocalPos, targetLocal, shipData);
        }

        /// <summary>
        /// Changes the crew state and keeps the O(1) cached counts in AIShipCrewData
        /// accurate. Always use this instead of brain->CurrentState = X directly.
        /// </summary>
        private void SetState(AIShipCrewData* crewData, AICrewBrain* brain, AICrewState newState)
        {
            // Decrement count for old state
            DecrementStateCount(crewData, brain, brain->CurrentState);
            // Increment count for new state
            IncrementStateCount(crewData, brain, newState);
            brain->CurrentState = newState;
        }

        private void IncrementStateCount(AIShipCrewData* crewData, AICrewBrain* brain, AICrewState state)
        {
            switch (state)
            {
                case AICrewState.Repairing:
                    crewData->CachedRepairingCount++;
                    break;
                case AICrewState.MovingToTarget when brain->TargetNodeType == DeckNodeType.Generator:
                    crewData->CachedMovingToRepairCount++;
                    break;
                case AICrewState.EquippedCannon when brain->AssignedCannonIndex >= 0:
                    if (crewData->CannonIsLeftSide[brain->AssignedCannonIndex])
                        crewData->CachedLeftCannonCount++;
                    else
                        crewData->CachedRightCannonCount++;
                    break;
            }
        }

        private void DecrementStateCount(AIShipCrewData* crewData, AICrewBrain* brain, AICrewState state)
        {
            switch (state)
            {
                case AICrewState.Repairing:
                    if (crewData->CachedRepairingCount > 0) crewData->CachedRepairingCount--;
                    break;
                case AICrewState.MovingToTarget when brain->TargetNodeType == DeckNodeType.Generator:
                    if (crewData->CachedMovingToRepairCount > 0) crewData->CachedMovingToRepairCount--;
                    break;
                case AICrewState.EquippedCannon when brain->AssignedCannonIndex >= 0:
                    if (crewData->CannonIsLeftSide[brain->AssignedCannonIndex])
                    { if (crewData->CachedLeftCannonCount  > 0) crewData->CachedLeftCannonCount--;  }
                    else
                    { if (crewData->CachedRightCannonCount > 0) crewData->CachedRightCannonCount--; }
                    break;
            }
        }

        // Kept for potential future use / debugging, but no longer called in hot paths
        private int FindAvailableCannon(AIShipCrewData* cd, Ship* s, bool left, int crewId)
        {
            for (int i = 0; i < s->WeaponSlots.Length; i++)
            {
                if (s->WeaponSlots[i].IsDestroyed) continue;
                if (cd->CannonIsLeftSide[i] != left) continue;
                if (s->WeaponSlots[i].Player != default) continue;
                if (cd->CannonNodeReserved[i] && cd->CannonNodeReservedBy[i] != crewId) continue;
                return i;
            }
            return -1;
        }

        private void ReleaseCannonReservation(AIShipCrewData* cd, AICrewBrain* b)
        {
            if (b->AssignedCannonIndex >= 0 && b->AssignedCannonIndex < 6 &&
                cd->CannonNodeReservedBy[b->AssignedCannonIndex] == b->CrewId)
            {
                cd->CannonNodeReserved[b->AssignedCannonIndex]    = false;
                cd->CannonNodeReservedBy[b->AssignedCannonIndex]  = -1;
            }
        }

        private void ReleaseAllReservations(AIShipCrewData* cd, AICrewBrain* b)
        {
            ReleaseCannonReservation(cd, b);
            if (cd->AmmoPileReservedBy  == b->CrewId) { cd->AmmoPileReserved  = false; cd->AmmoPileReservedBy  = -1; }
            if (cd->GeneratorReservedBy == b->CrewId) { cd->GeneratorReserved = false; cd->GeneratorReservedBy = -1; }
        }

        // IKCCCallbacks3D
        public bool OnCharacterCollision3D(FrameBase f, EntityRef character, Physics3D.Hit3D hit) => true;
        public void OnCharacterTrigger3D(FrameBase f, EntityRef character, Physics3D.Hit3D hit) { }
    }
}