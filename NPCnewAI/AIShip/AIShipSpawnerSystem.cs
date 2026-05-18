// =============================================================================
// AIShipSpawnerSystem.cs
// Place in: Assets/QuantumUser/Simulation/AI/
//
// Manages spawning and respawning of AI-piloted ships.
// Each faction maintains N ships (configurable). When a ship dies,
// it respawns after a configurable delay at a faction spawn point.
//
// This system uses a singleton AIShipSpawnHandler to track all AI ships.
// It spawns real Ship entities (same as player ships) with full AI crew + pilot.
//
// SETUP:
// 1. Add AIShipSpawnHandler singleton entity to your scene/prototype
// 2. Register AIShipSpawnerSystem in SystemSetup (after AIPilotSystem)
// 3. The system auto-initializes on first Update when Mode == Match
//
// TUNING (change via AIShipSpawnHandler fields or call Configure()):
// - ShipsPerFaction: default 2
// - RespawnDelay: default 30 seconds
// - HullPerShip: default 300
// - ShipTypeIndex: default 2 (Clockwork)
// - AICrewPerShip: default 3
// =============================================================================

using Photon.Deterministic;
using UnityEngine;

namespace Quantum
{
    public unsafe class AIShipSpawnerSystem : SystemMainThread
    {
        // Ship data paths matching SpawnSystem pattern
        private static readonly string[] ShipDataPaths = new string[]
        {
            "Resources/DB/PlayersShipData/Raft/RaftData",
            "Resources/DB/PlayersShipData/Dinghy/DinghyData",
            "Resources/DB/PlayersShipData/Clockwork/Ship0Data",
            "Resources/DB/PlayersShipData/Wraith/Ship1Data",
        };
        
        public override void Update(Frame f)
        {
            if (f.RuntimeConfig.Mode != ModeType.Match || f.RuntimeConfig.GameType != GameType.Adventure)
            {
                return;
            }
            
            if (!f.Unsafe.TryGetPointerSingleton<AIShipSpawnHandler>(out var handler))
            {
                return;
            }
            
            var factionsHandler = f.Unsafe.GetPointerSingleton<FactionsHandler>();
            if (factionsHandler->reportedFactionWin)
            {
                return; // game over
            }
            
            if (!handler->IsStarted)
            {
                Configure(f,1,10,100,400,2,3);
                Initialize(f, handler);
            }
            
            // Process both factions
            ProcessFaction(f, handler, PlayerFaction.Red);
            ProcessFaction(f, handler, PlayerFaction.Blue);
        }
        
        private void Initialize(Frame f, AIShipSpawnHandler* handler)
        {
            handler->IsStarted = true;
            
            // Set defaults if not configured
            if (handler->ShipsPerFaction <= 0) handler->ShipsPerFaction = 1;
            if (handler->RespawnDelay <= 0) handler->RespawnDelay = 30;
            if (handler->HullPerShip <= 0) handler->HullPerShip = 300;
            if (handler->AICrewPerShip <= 0) handler->AICrewPerShip = 3;
            if (handler->ShipTypeIndex <= 0) handler->ShipTypeIndex = 2; // Clockwork
            
            // Initialize all slots — spawn initial ships immediately
            for (int i = 0; i < handler->ShipsPerFaction && i < 6; i++)
            {
                handler->RedRespawnTimers[i] = 0; // ready to spawn now
                handler->BlueRespawnTimers[i] = 0;
                handler->RedShips[i] = EntityRef.None;
                handler->BlueShips[i] = EntityRef.None;
            }
            
            // Mark unused slots as inactive
            for (int i = handler->ShipsPerFaction; i < 6; i++)
            {
                handler->RedRespawnTimers[i] = -FP._2; // inactive sentinel
                handler->BlueRespawnTimers[i] = -FP._2;
            }
            
            Log.Info($"AIShipSpawner: Initialized — {handler->ShipsPerFaction} ships/faction, " +
                     $"respawn {handler->RespawnDelay}s, hull {handler->HullPerShip}");
        }
        
        private void ProcessFaction(Frame f, AIShipSpawnHandler* handler, PlayerFaction faction)
        {
            bool isRed = faction == PlayerFaction.Red;
            
            for (int i = 0; i < handler->ShipsPerFaction && i < 6; i++)
            {
                EntityRef shipEntity = isRed ? handler->RedShips[i] : handler->BlueShips[i];
                FP timer = isRed ? handler->RedRespawnTimers[i] : handler->BlueRespawnTimers[i];
                
                // ─── SHIP IS ALIVE: check if it died ───
                if (shipEntity != EntityRef.None)
                {
                    bool isDead = false;
                    
                    if (!f.Exists(shipEntity))
                    {
                        isDead = true;
                    }
                    else if (f.Unsafe.TryGetPointer<Ship>(shipEntity, out Ship* ship))
                    {
                        isDead = ship->Hull <= 0;
                    }
                    
                    if (isDead)
                    {
                        // Ship died — start respawn timer
                        if (isRed)
                        {
                            handler->RedShips[i] = EntityRef.None;
                            handler->RedRespawnTimers[i] = handler->RespawnDelay;
                        }
                        else
                        {
                            handler->BlueShips[i] = EntityRef.None;
                            handler->BlueRespawnTimers[i] = handler->RespawnDelay;
                        }
                        
                        Log.Info($"AIShipSpawner: {faction} ship slot {i} died — respawning in {handler->RespawnDelay}s");
                    }
                    continue;
                }
                
                // ─── NO SHIP: count down respawn timer ───
                if (timer > 0)
                {
                    timer -= f.DeltaTime;
                    if (isRed)
                        handler->RedRespawnTimers[i] = timer;
                    else
                        handler->BlueRespawnTimers[i] = timer;
                    continue;
                }
                // Skip inactive slots (sentinel value)
                if (timer < 0) continue;
                
                // ─── TIMER READY: spawn new ship ───
                EntityRef newShip = SpawnAIShip(f, handler, faction, i);
                if (newShip != EntityRef.None)
                {
                    if (isRed)
                    {
                        handler->RedShips[i] = newShip;
                        handler->RedRespawnTimers[i] = -FP._1; // alive sentinel
                    }
                    else
                    {
                        handler->BlueShips[i] = newShip;
                        handler->BlueRespawnTimers[i] = -FP._1;
                    }
                }
            }
        }
        
        private EntityRef SpawnAIShip(Frame f, AIShipSpawnHandler* handler, PlayerFaction faction, int slotIndex)
        {
            // Get ship data
            int typeIndex = FPMath.Clamp(handler->ShipTypeIndex, 0, ShipDataPaths.Length - 1);
            var shipData = f.FindAsset<ShipData>(ShipDataPaths[typeIndex]);
            if (shipData == null)
            {
                Log.Error("AIShipSpawner: ShipData not found at " + ShipDataPaths[typeIndex]);
                return EntityRef.None;
            }
            
            // Create entities (same triple-entity pattern as SpawnSystem.SpawnPlayerShip)
            EntityRef shipVisibleEntity = f.Create(shipData.ShipVisible);
            EntityRef shipImpactEntity = f.Create(shipData.ShipImpact);
            EntityRef shipWorldEntity = f.Create(shipData.ShipWorld);
            f.Add(shipWorldEntity, new ShipWorld());
            
            // Impact link
            var shipImpact = f.Unsafe.GetPointer<ShipImpact>(shipImpactEntity);
            shipImpact->ShipVisible = shipVisibleEntity;
            
            // Initialize Ship component
            var ship = f.Unsafe.GetPointer<Ship>(shipVisibleEntity);
            ship->ShipData = shipData;
            ship->Index = f.Global->CurrentShipIndex;
            f.Global->CurrentShipIndex++;
            ship->Leader = default;         // no player leader
            ship->LeaderEntity = default;
            ship->faction = faction;
            ship->Hull = handler->HullPerShip;
            ship->HullMax = handler->HullPerShip;
            ship->HullStart = handler->HullPerShip;
            if (shipData.HaveShield && handler->ShieldPerShip > 0)
            {
                ship->ShieldMax = handler->ShieldPerShip;
                ship->Shield = handler->ShieldPerShip;
            }
            ship->WorldEntity = shipWorldEntity;
            ship->VisibleEntity = shipVisibleEntity;
            ship->ImpactEntity = shipImpactEntity;
            ship->ShipPlayers = f.AllocateList<PlayerResult>();
            ship->RefinedResources = f.AllocateDictionary<int, int>();
            ship->BountyPayors = f.AllocateList<int>();
            ship->lastDamageData = f.AllocateDictionary<int, LastDamageData>();
            ship->InPVPMode = true;
            ship->IsVulnerable = true;
            ship->IndexOfLastShipToDealDamage = -1;
            
            // Position at faction spawn point
            PositionAtFactionSpawn(f, ship, shipWorldEntity, shipVisibleEntity, faction, shipData, slotIndex);
            
            // Initialize weapons
            if (shipData.CanShoot)
            {
                for (int i = 0; i < shipData.WeaponSlots.Length && i < 6; i++)
                {
                    var weaponType = WeaponType.DefaultCannon;
                    var weaponData = ConfigAssetsHelper.GetWeaponDataByWeaponType(f, weaponType);
                    ship->WeaponSlots[i] = new WeaponSlot()
                    {
                        MaxAmmo = weaponData.MaxAmmo,
                        Ammo = weaponData.MaxAmmo,
                        Health = 100,
                        IsDestroyed = false,
                        WeaponType = weaponType,
                        WeaponData = weaponData,
                        Cooldown = weaponData.Cooldown,
                        CameraOffset = weaponData.CameraPos,
                        Index = (byte)i
                    };
                }
            }
            
            // Initialize sails
            if (shipData.Sails != null)
            {
                int blockIdx = 0;
                for (int i = 0; i < shipData.Sails.Length; i++)
                {
                    ship->SailSlots[i] = new SailSlot() { Health = ShipSystem.SailsMaxHealth, IsDestroyed = false };
                    for (int j = 0; j < shipData.Sails[i].Blocks.Length; j++)
                    {
                        ship->BlockSlots[blockIdx] = new BlockSlot()
                        {
                            CorrespondingSailIndex = (byte)i,
                            BlockIndexWithinSail = j,
                        };
                        blockIdx++;
                    }
                }
            }
            
            // Initialize AI systems (crew + pilot)
            AIShipInitSystem.InitializeAIShip(f, shipVisibleEntity, handler->AICrewPerShip, AIShipMode.FullAI);
            
            Log.Info($"AIShipSpawner: Spawned {faction} AI ship slot {slotIndex}, index {ship->Index}");
            
            return shipVisibleEntity;
        }
        
        private void PositionAtFactionSpawn(Frame f, Ship* ship, EntityRef worldEntity, EntityRef visibleEntity,
            PlayerFaction faction, ShipData shipData, int slotIndex)
        {
            var gameplay = f.Unsafe.GetPointerSingleton<Gameplay>();
            
            var spawnpoints = faction == PlayerFaction.Red ? gameplay->redSpawnPoints : gameplay->blueSpawnPoints;
            
            // Use slot index to pick a spawn point (cycle through available points)
            int spawnIdx = slotIndex+1 % spawnpoints.Length;
            var spawnPointTransform = f.Unsafe.GetPointer<Transform3D>(spawnpoints[spawnIdx]);
            
            // Set up spawning animation (same as player ships)
            ship->spawningData.landingPosition = spawnPointTransform->Position + FPVector3.Up * shipData.heightOffset;
            ship->spawningData.spawningStartWait = 2;
            ship->spawningData.shipSpawnpointIndex = spawnIdx;
            
            FP heightOffset = shipData.SpawningYOffset;
            var worldT = f.Unsafe.GetPointer<Transform3D>(worldEntity);
            var visibleT = f.Unsafe.GetPointer<Transform3D>(visibleEntity);
            
            worldT->Position = spawnPointTransform->Position + FPVector3.Up * heightOffset;
            worldT->Rotation = FPQuaternion.Euler(0, spawnPointTransform->EulerAngles.Y, 0);
            visibleT->Position = worldT->Position;
            visibleT->Rotation = worldT->Rotation;
        }
        
        // =====================================================================
        // PUBLIC CONFIGURATION API
        // Call from your game mode setup to change defaults before first Update
        // =====================================================================
        
        /// <summary>
        /// Configure the AI ship spawner. Call before the first frame or during OnInit.
        /// </summary>
        public static void Configure(Frame f, int shipsPerFaction = 2, FP respawnDelay = default, 
            int hullPerShip = 300, int shieldPerShip = 0, int shipTypeIndex = 2, int crewPerShip = 3)
        {
            if (!f.Unsafe.TryGetPointerSingleton<AIShipSpawnHandler>(out var handler))
            {
                Log.Error("AIShipSpawner: No AIShipSpawnHandler singleton found. Add one to your scene.");
                return;
            }
            
            handler->ShipsPerFaction = FPMath.Clamp(shipsPerFaction, 1, 6);
            handler->RespawnDelay = respawnDelay > 0 ? respawnDelay : 30;
            handler->HullPerShip = hullPerShip;
            handler->ShieldPerShip = shieldPerShip;
            handler->ShipTypeIndex = shipTypeIndex;
            handler->AICrewPerShip = crewPerShip;
            
            Log.Info($"AIShipSpawner: Configured — {shipsPerFaction} ships/faction, " +
                     $"respawn {handler->RespawnDelay}s, hull {hullPerShip}, type {shipTypeIndex}");
        }
        
        /// <summary>
        /// Change ships per faction at runtime. New slots spawn on next tick.
        /// Excess ships are NOT destroyed — they just won't be replaced when they die.
        /// </summary>
        public static void SetShipsPerFaction(Frame f, int count)
        {
            if (!f.Unsafe.TryGetPointerSingleton<AIShipSpawnHandler>(out var handler)) return;
            
            int prev = handler->ShipsPerFaction;
            handler->ShipsPerFaction = FPMath.Clamp(count, 0, 6);
            
            // Activate new slots
            for (int i = prev; i < handler->ShipsPerFaction && i < 6; i++)
            {
                handler->RedRespawnTimers[i] = 0; // ready to spawn
                handler->BlueRespawnTimers[i] = 0;
                handler->RedShips[i] = EntityRef.None;
                handler->BlueShips[i] = EntityRef.None;
            }
            
            // Deactivate removed slots (don't kill existing ships)
            for (int i = handler->ShipsPerFaction; i < 6; i++)
            {
                handler->RedRespawnTimers[i] = -FP._2; // inactive sentinel
                handler->BlueRespawnTimers[i] = -FP._2;
            }
        }
        
        /// <summary>
        /// Change respawn delay at runtime.
        /// </summary>
        public static void SetRespawnDelay(Frame f, FP delay)
        {
            if (!f.Unsafe.TryGetPointerSingleton<AIShipSpawnHandler>(out var handler)) return;
            handler->RespawnDelay = delay;
        }
    }
}
