// =============================================================================
// AIShipInitSystem.cs
// Place in: Assets/QuantumUser/Simulation/AI/
//
// Handles initialization of AI-controlled ships:
// - Populates AIShipCrewData with deck node positions from ShipData
// - Spawns AI crew member entities with AICrewBrain components
// - Sets default signal board for testing (Phase 1)
//
// Call AIShipInitSystem.InitializeAIShip() from your ship spawning code
// after the Ship component is fully set up.
// =============================================================================

using Photon.Deterministic;

namespace Quantum
{
    public static unsafe class AIShipInitSystem
    {
        // How many AI crew to spawn per ship (configurable)
        public const int DefaultAICrewCount = 1;
        
        /// <summary>
        /// Call this after a Ship entity is fully initialized to add AI crew.
        /// Adds AIShipCrewData component to the ship and spawns AI crew entities.
        /// </summary>
        /// <param name="f">Current frame</param>
        /// <param name="shipVisibleEntity">The ship's visible entity (has Ship component)</param>
        /// <param name="crewCount">Number of AI crew to spawn</param>
        /// <param name="crewPrototype">Entity prototype path for AI crew members</param>
        /// <summary>
        /// Call this after a Ship entity is fully initialized to add AI crew.
        /// Adds AIShipCrewData component to the ship and spawns AI crew entities.
        /// </summary>
        /// <param name="f">Current frame</param>
        /// <param name="shipVisibleEntity">The ship's visible entity (has Ship component)</param>
        /// <param name="crewCount">Number of AI crew to spawn</param>
        /// <param name="mode">FullAI for AI-only ships, PlayerPilotAICrew for player ships with AI helpers</param>
        /// <param name="crewPrototype">Entity prototype path for AI crew members</param>
        public static void InitializeAIShip(Frame f, EntityRef shipVisibleEntity, int crewCount = DefaultAICrewCount, 
            AIShipMode mode = AIShipMode.FullAI,
            string crewPrototype = "Resources/DB/AI/AICrewEntityPrototype")
        {
            if (!f.Unsafe.TryGetPointer<Ship>(shipVisibleEntity, out Ship* ship))
            {
                Log.Error("AIShipInitSystem: Ship component not found on entity");
                return;
            }
            
            var shipData = f.FindAsset<ShipData>(ship->ShipData.Id);
            if (shipData == null)
            {
                Log.Error("AIShipInitSystem: ShipData asset not found");
                return;
            }
            
            
            
            // ─── Add AIShipCrewData to the ship entity ───
            if (!f.Has<AIShipCrewData>(shipVisibleEntity))
            {
                f.Add<AIShipCrewData>(shipVisibleEntity);
            }
            
            var crewData = f.Unsafe.GetPointer<AIShipCrewData>(shipVisibleEntity);
            crewData->IsAIControlled = true;
            crewData->AICrewCount = crewCount;
            crewData->ShipMode = mode;
            crewData->LastHullSnapshot = ship->Hull;
            
            
            // ─── Populate deck node positions from ShipData ───
            PopulateDeckNodes(f, crewData, ship, shipData);
            
            // ─── Set default signal board (for Phase 1 testing) ───
            SetDefaultSignalBoard(crewData);
            
            
            // ─── Spawn AI crew member entities ───
            SpawnAICrew(f, shipVisibleEntity, ship, crewData, crewCount, mode, crewPrototype);
            // ─── Initialize AI Pilot ───
            AIPilotSystem.InitializePilot(f, shipVisibleEntity, useBroadside: true);
            Log.Info($"AIShipInitSystem: Initialized AI ship {ship->Index} with {crewCount} crew members");
        }
        
        private static void PopulateDeckNodes(Frame f, AIShipCrewData* crewData, Ship* ship, ShipData shipData)
        {
            // Ammo pile position (from ShipData)
            crewData->AmmoPilePosition = shipData.AmmoPilePosition;
            crewData->AmmoPileReserved = false;
            crewData->AmmoPileReservedBy = -1;
            
            // Shield generator / repair position (from ShipData)
            crewData->GeneratorPosition = shipData.ShieldGenerator;
            crewData->GeneratorReserved = false;
            crewData->GeneratorReservedBy = -1;
            var shipTransform = f.Unsafe.GetPointer<Transform3D>(ship->VisibleEntity);
            // Cannon positions - derive from ShipData.WeaponSlots
            for (int i = 0; i < ship->WeaponSlots.Length && i < 6; i++)
            {
                // Use the weapon slot's manned position
                // PlayerSystem uses WeaponRelativeActionPosition for this
                // We'll store the base position from ShipData's WeaponSlot data
                if (i < shipData.WeaponSlots.Length)
                {
                    var weaponSlotData = shipData.WeaponSlots[i];
                    var weaponData = f.FindAsset<WeaponData>(ship->WeaponSlots[i].WeaponData.Id);
                    // The body position where crew stands when manning this cannon
                    FPVector3 cannonPos = new FPVector3(weaponData.BodyPositionX, 0, weaponData.BodyPositionZ);
                    var cannonLocalPos = shipTransform->InverseTransformPoint(PlayerSystem.WeaponLocalOffsetToGlobalPosition(f,ship,i,cannonPos));

                    crewData->CannonPositions[i] = cannonLocalPos;
                    // Determine side based on BaseRotation
                    // Negative BaseRotation = left side, Positive = right side
                    // (matching your existing PlayerSystem cannon rotation logic)
                    crewData->CannonIsLeftSide[i] = weaponSlotData.BaseRotation < 0;
                }
                
                crewData->CannonNodeReserved[i] = false;
                crewData->CannonNodeReservedBy[i] = -1;
            }
            
            // Patrol waypoints - simple positions around the deck
            // These are approximate and can be tuned per ship type
            SetupPatrolPoints(crewData, shipData);
        }
        
        private static void SetupPatrolPoints(AIShipCrewData* crewData, ShipData shipData)
        {
            crewData->PatrolPoints[0] = shipData.ShieldGenerator;
            crewData->PatrolPoints[1] = shipData.AmmoPilePosition;
            crewData->PatrolPoints[2] = crewData->CannonPositions[0];
            crewData->PatrolPoints[3] = crewData->CannonPositions[1];
            crewData->PatrolPoints[4] = crewData->CannonPositions[2];
            crewData->PatrolPoints[5] = crewData->CannonPositions[3];
            crewData->PatrolPoints[6] = crewData->CannonPositions[4];
            crewData->PatrolPoints[7] = crewData->CannonPositions[5];
            
        }
        
        private static void SetDefaultSignalBoard(AIShipCrewData* crewData)
        {
            // Default: peaceful, no combat
            // Change these values for Phase 1 testing
            crewData->SignalBoard = new CrewSignalBoard
            {
                LeftCannonsNeeded = 0,
                RightCannonsNeeded = 0,
                RepairUrgency = RepairUrgency.None,
                PreferredAttackSide = AttackSide.None,
                ThreatLevel = ThreatLevel.Peaceful
            };
        }
        
        private static void SpawnAICrew(Frame f, EntityRef shipVisibleEntity, Ship* ship, AIShipCrewData* crewData, 
            int crewCount, AIShipMode mode, string crewPrototype)
        {
            var shipData = f.FindAsset<ShipData>(ship->ShipData.Id);
            var shipTransform = f.Unsafe.GetPointer<Transform3D>(shipVisibleEntity);
            var prototype = f.FindAsset<EntityPrototype>(crewPrototype);
            ship->ShipAICrew = f.AllocateList<EntityRef>();
            var shipAICrew = f.ResolveList(ship->ShipAICrew);
            for (int i = 0; i < crewCount; i++)
            {
                EntityRef crewEntity = f.Create(prototype);
                
                var crewTransform = f.Unsafe.GetPointer<Transform3D>(crewEntity);
                
                // Initialize the AI brain
                var brain = f.Unsafe.GetPointer<AICrewBrain>(crewEntity);
                brain->CrewId = i;
                brain->ShipIndex = ship->Index;
                brain->ShipVisibleEntity = shipVisibleEntity;
                brain->Faction = ship->faction;
                brain->CurrentState = AICrewState.Idle;
                brain->AssignedCannonIndex = -1;
                brain->TargetNodeIndex = -1;
                brain->CarryingAmmo = false;
                brain->HasReachedTarget = false;
                brain->StateTimer = 0;
                brain->ArcWaitTimer = 0;
                brain->BrainTickAccumulator = FP.FromRaw(FP.Raw._0_10) * i; // stagger brain ticks
                brain->DesiredMoveSpeed = 0;
                brain->DesiredFire = false;
                brain->DesiredInteract = false;
                
                
                // First crew member is the pilot/captain
                brain->IsPilot = (i == 0 && mode == AIShipMode.FullAI);
                var position = brain->IsPilot ? 
                    shipTransform->TransformPoint(shipData.SpawnCaptain) : 
                    new FPVector3((i - crewCount / 2) * FP._1, 0,0);
                
                crewTransform->Position = position;
                brain->LocalPositionOnShip = crewTransform->Position;
                
                // Position crew on deck (spread them out initially)
                
                if (brain->IsPilot)
                {
                    crewData->PilotCrewEntity = crewEntity;
                    crewData->AIPilotAtWheel = false;
                }
                shipAICrew.Add(crewEntity);
            }
        }
        
        // =====================================================================
        // TESTING HELPERS - Use these to test crew behavior in Phase 1
        // =====================================================================
        
        /// <summary>
        /// Sets the signal board to simulate a battle on the left side.
        /// Call from a test command or debug system.
        /// </summary>
        public static void TestSignal_BattleLeft(Frame f, EntityRef shipVisibleEntity)
        {
            if (f.Unsafe.TryGetPointer<AIShipCrewData>(shipVisibleEntity, out AIShipCrewData* crewData))
            {
                crewData->SignalBoard = new CrewSignalBoard
                {
                    LeftCannonsNeeded = 3,
                    RightCannonsNeeded = 0,
                    RepairUrgency = RepairUrgency.None,
                    PreferredAttackSide = AttackSide.Left,
                    ThreatLevel = ThreatLevel.Battle
                };
            }
        }
        
        /// <summary>
        /// Sets the signal board to simulate critical damage - all crew repair.
        /// </summary>
        public static void TestSignal_CriticalRepair(Frame f, EntityRef shipVisibleEntity)
        {
            if (f.Unsafe.TryGetPointer<AIShipCrewData>(shipVisibleEntity, out AIShipCrewData* crewData))
            {
                crewData->SignalBoard = new CrewSignalBoard
                {
                    LeftCannonsNeeded = 0,
                    RightCannonsNeeded = 0,
                    RepairUrgency = RepairUrgency.Critical,
                    PreferredAttackSide = AttackSide.None,
                    ThreatLevel = ThreatLevel.Overwhelmed
                };
            }
        }
        
        /// <summary>
        /// Sets the signal board to simulate mixed scenario: 2 left cannons + 1 repair.
        /// </summary>
        public static void TestSignal_MixedBattle(Frame f, EntityRef shipVisibleEntity)
        {
            if (f.Unsafe.TryGetPointer<AIShipCrewData>(shipVisibleEntity, out AIShipCrewData* crewData))
            {
                crewData->SignalBoard = new CrewSignalBoard
                {
                    LeftCannonsNeeded = 2,
                    RightCannonsNeeded = 0,
                    RepairUrgency = RepairUrgency.High,
                    PreferredAttackSide = AttackSide.Left,
                    ThreatLevel = ThreatLevel.Battle
                };
            }
        }
        
        /// <summary>
        /// Resets signal board to peaceful idle.
        /// </summary>
        public static void TestSignal_Peaceful(Frame f, EntityRef shipVisibleEntity)
        {
            if (f.Unsafe.TryGetPointer<AIShipCrewData>(shipVisibleEntity, out AIShipCrewData* crewData))
            {
                crewData->SignalBoard = new CrewSignalBoard
                {
                    LeftCannonsNeeded = 0,
                    RightCannonsNeeded = 0,
                    RepairUrgency = RepairUrgency.None,
                    PreferredAttackSide = AttackSide.None,
                    ThreatLevel = ThreatLevel.Peaceful
                };
            }
        }
    }
}