using System;
using Photon.Deterministic;

namespace Quantum
{
    public unsafe class NPCShipSystem : SystemMainThreadFilter<NPCShipSystem.Filter>, ISignalOnCollisionEnter3D
    {
        public struct Filter
        {
            public EntityRef Entity;
            public NPCShip* npcShip;
            public Transform3D* transform;
        }

        public static int XYToIndex(int x, int y)
        {
            return x * Constants.MAX_NPC_SHIPS + y;
        }

        public static void SpawnNPCShipAtFaction(Frame f, int factionIndex, FPVector3 spawnPosition,
            PlayerFaction faction)
        {
            var npcShipData = f.FindAsset<NPCShipGeneralData>("Resources/DB/NPCShipDataAssets/NPCShipGeneralData");
            var patrolPathAssets = f.FindAsset<PatrolPathAssets>("Resources/DB/NPCShipDataAssets/PatrolPathsData");
            FPVector3 centerPoint = new(spawnPosition.X, npcShipData.oceanOffset.Y, spawnPosition.Z);
            f.Global->NpcShipsSpawnPoints[factionIndex].Position = centerPoint;
            f.Global->NpcShipsSpawnPoints[factionIndex].faction = faction;
            f.Global->NpcShipsSpawnPoints[factionIndex].Agressor = false;
            for (int i = 0; i < f.Global->NpcShipsSpawnPoints[factionIndex].NpcShipEntity.Length; i++)
            {
                f.Global->NpcShipsSpawnPoints[factionIndex].NpcShipEntity[i] = SpawnNPCShip(f,
                    f.Global->NpcShipsSpawnPoints[factionIndex], f.Global->NpcShipsSpawnPoints[factionIndex].Position,
                    patrolPathAssets.PatrolPaths[0], i * 2);
            }
        }

        public static EntityRef SpawnNPCShip(Frame f, NPCsShipSpawnPointData spawnPoint, FPVector3 spawnPosition,
            AssetRef<PatrolPath> assetRefPatrolPath, int spawnindex = -1, bool isSiegeShip = false)
        {
            //Visible
            var shipToSpawn = 1;
            if (spawnPoint.faction == PlayerFaction.Blue)
            {
                shipToSpawn = 3;
            }

            var npcGeneralShipData =
                f.FindAsset<NPCShipGeneralData>("Resources/DB/NPCShipDataAssets/NPCShipGeneralData");
            // var patrolPathAssets = f.FindAsset<PatrolPathAssets>("Resources/DB/NPCShipDataAssets/PatrolPathsData");
            PatrolPath patrolPath = f.FindAsset<PatrolPath>(assetRefPatrolPath.Id);
            AssetRef<EntityPrototype> npcShipVisiblePrototype = npcGeneralShipData.NPCShipVisible[shipToSpawn];
            if (isSiegeShip) npcShipVisiblePrototype = npcGeneralShipData.SiegeShipVisible;
            EntityRef npcShipVisibleEntity = f.Create(npcShipVisiblePrototype);
            //Impact
            AssetRef<EntityPrototype> npcShipImpactPrototype = npcGeneralShipData.NPCShipImpact[shipToSpawn];
            if (isSiegeShip) npcShipImpactPrototype = npcGeneralShipData.SiegeShipImpact;
            EntityRef npcShipImpactEntity = f.Create(npcShipImpactPrototype);
            //World
            AssetRef<EntityPrototype> npcShipWorldPrototype = npcGeneralShipData.NPCShipWorld[shipToSpawn];
            if (isSiegeShip) npcShipWorldPrototype = npcGeneralShipData.SiegeShipWorld;
            EntityRef npcShipWorldEntity = f.Create(npcShipWorldPrototype);
            //Transform
            ShipWorld* shipWorld = f.Unsafe.GetPointer<ShipWorld>(npcShipWorldEntity);
            shipWorld->ShipVisible = npcShipVisibleEntity;
            Transform3D* worldTransform = f.Unsafe.GetPointer<Transform3D>(npcShipWorldEntity);
            Transform3D* viewTransform = f.Unsafe.GetPointer<Transform3D>(npcShipVisibleEntity);
            //worldTransform->Position = centerPoint;
            if (spawnindex == -1) spawnindex = f.Global->RngSession.Next(0, patrolPath.path.Length);
            var currentPosition = patrolPath.GetCurrentPoint(spawnindex, patrolPath.centerOffset) + spawnPosition;
            worldTransform->Position = currentPosition;
            var angle = GetYAngle(currentPosition,
                patrolPath.GetCurrentPoint((spawnindex + 1) % patrolPath.path.Length, patrolPath.centerOffset) +
                spawnPosition);
            worldTransform->Rotation = FPQuaternion.Euler(0, angle, 0);
            viewTransform->Position = worldTransform->Position;
            viewTransform->Rotation = worldTransform->Rotation;
            //NPC Ship Component
            NPCShip* npcShip = f.Unsafe.GetPointer<NPCShip>(npcShipVisibleEntity);
            f.AllocateDictionary(out npcShip->damageTakenFromPlayers);

            npcShip->VisibleEntity = npcShipVisibleEntity;
            npcShip->spawnPoint = spawnPoint;
            npcShip->ImpactEntity = npcShipImpactEntity;
            npcShip->WorldEntity = npcShipWorldEntity;
            NPCShipData npcShipData = f.FindAsset<NPCShipData>(npcShip->npcShipData.Id);
            npcShip->sail.maxForce = npcShipData.maxForce;
            npcShip->sail.rateOfRotation = npcShipData.rateOfRotation;
            npcShip->navigator.currentIndex = spawnindex;
            npcShip->navigator.turnSpeed = npcShipData.turnSpeed;
            npcShip->navigator.targetRadius = npcShipData.targetRadius;
            //npcShip->navigator.targetSightRadius = npcShipData.targetSightRadius;
            //npcShip->navigator.targetRangeRadius = npcShipData.targetRangeRadius;
            npcShip->navigator.lerpRate = npcShipData.lerpRate;
            npcShip->navigator.patrolDistance = npcShipData.patrolDistance;
            npcShip->navigator.attackDistance = npcShipData.attackDistance;
            npcShip->navigator.chaseSpeed = npcShipData.chaseSpeed;
            npcShip->navigator.centerPoint = spawnPoint.Position;
            npcShip->navigator.speedRegulator = 1;
            npcShip->navigator.patrolPath.Id = assetRefPatrolPath.Id;

            npcShip->navigator.forcePatrol = f.RNG->Next() < npcGeneralShipData.forcePatrolRate;
            var weaponData = ConfigAssetsHelper.GetWeaponDataByWeaponType(f, WeaponType.DefaultCannon);

            for (int i = 0; i < npcShipData.WeaponSlots.Length; i++)
            {
                npcShip->WeaponSlots[i] = new WeaponSlot()
                {
                    Ammo = 999,
                    WeaponData = weaponData,
                    Index = (byte)i
                };
            }

            //Max
            npcShip->HullMax = npcShipData.HullMax;
            npcShip->Hull = npcShipData.HullMax;
            npcShip->ShieldMax = npcShipData.ShieldMax;
            npcShip->Shield = npcShipData.ShieldMax;
            return npcShipVisibleEntity;
        }

        static FP GetYAngle(FPVector3 from, FPVector3 to)
        {
            var direction = to - from;
            direction.Y = 0; // ignore vertical difference

            if (direction.SqrMagnitude < FP.FromFloat_UNSAFE(0.0001f))
                return FP._0; // same position, no angle

            // angle around Y axis in degrees
            return FPMath.Atan2(direction.X, direction.Z) * FP.Rad2Deg;
        }

        public void AdaptiveDifficulty(Frame f)
        {
            /*ComponentFilter<Ship> ships = f.Filter<Ship>();
            while (ships.Next(out EntityRef shipEntity, out Ship shipComponent))
            {
                //f.Global->NumberNPCDestroyed += (shipComponent.Kills + shipComponent.NPCKills);
            }

            switch (f.Global->NumberNPCDestroyed)
            {
                case <= 2:
                    f.currentStage = 0; // First Stage - Easy difficulty
                    break;
                case <= 5:
                    f.currentStage = 1; // Second Stage - Moderate difficulty
                    break;
                case <= 8:
                    f.currentStage = 2; // Third Stage - Hard difficulty
                    break;
                default:
                    f.currentStage = 3; // Fourth Stage - Very hard difficulty
                    break;
            }*/
        }

        public FP GetWindPower(NPCShip* npcShip)
        {
            return 1;
        }

        private int SailsFor(NavigationMode mode)
        {
            // Ensure at least some forward power while maneuvering
            switch (mode)
            {
                case NavigationMode.Manovering: return 1; // keep a trickle of thrust
                case NavigationMode.Patrol: return 2;
                case NavigationMode.Pursuing: return 3;
                case NavigationMode.Flanking: return 3;
                case NavigationMode.Attacking: return 2; // up to you: 2–3 depending on balance
                case NavigationMode.Ramming: return 3;
                default: return 2;
            }
        }

        public FP GetSailPower(NPCShip* npcShip)
        {
            return npcShip->sail.numberOfSailsOpened * FP._1_50;
        }

        public FPVector3 GetSailForce(NPCShip* npcShip)
        {
            return FPVector3.Forward * GetWindPower(npcShip) * GetSailPower(npcShip);
        }

        public FP GetSailRotationDifference(NPCShip* npcShip, Transform3D* transform)
        {
            FP currentYawDeg = transform->Rotation.AsEuler.Y; // same basis as where you write it back
            // short-arc diff
            FP diff = npcShip->sail.sailRotation - currentYawDeg;
            while (diff > 180) diff -= 360;
            while (diff < -180) diff += 360;
            return diff;
        }

        /*public FP GetTargetAngle(NPCShipNavigator navigator, Transform3D* transform, Transform3D* targetTransform)
        {
            FP anglePerpendicularToTarget = CalculateAngleToTarget(transform, targetTransform);
            FP angleToTarget = CalculateAngleToTarget(transform, targetTransform != null ? targetTransform->Position : FPVector3.Zero);
            bool isWithinRange = GetIsWithinRange(transform, targetTransform, navigator);
            bool isWithingSight = GetIsWithinSight(transform, targetTransform, navigator);
            return (isWithingSight || isWithinRange) && navigator.hasReachedTarget ? anglePerpendicularToTarget : angleToTarget;
        }

        public FP GetChaseAngle(Transform3D* transform, FPVector3 Position)
        {
            return CalculateAngleToTarget(transform->Position, Position);
        }
        */

        public FPVector3 GetPatrolPoint(NPCShip* npcShip, PatrolPath patrolPath)
        {
            return patrolPath?.GetCurrentPoint(npcShip->navigator.currentIndex, npcShip->navigator.centerPoint) ??
                   FPVector3.Zero;
        }

        public FP GetPatrolPathAngle(NPCShip* npcShip, PatrolPath patrolPath, Transform3D* transform)
        {
            return CalculateAngleToTarget(transform->Position, GetPatrolPoint(npcShip, patrolPath));
        }


        public static void DamageShip(Frame f, PlayerRef playerDealing, Ship* shipDealing, NPCShip* shipDamaged,
            FP damageDealt, WeaponType weaponType, bool causedByCaptainQuitting)
        {
            FP prevHealthShipDamaged = shipDamaged->Hull;
            FP damageToShields = FPMath.Min(shipDamaged->Shield, damageDealt);
            FP damageToHull = damageDealt - damageToShields;
            damageToHull = FPMath.Min(damageToHull, shipDamaged->Hull);
            shipDamaged->lastTimeAttackedByPlayerShip += 15;
            shipDamaged->lastTargetShip = shipDealing->VisibleEntity;

            if (weaponType == WeaponType.MortarCannon)
            {
                var damagedVisibleTransform = f.Unsafe.GetPointer<Transform3D>(shipDamaged->VisibleEntity);
                f.Events.MortarDamageRecieved(playerDealing, damagedVisibleTransform->Position, damageDealt.AsInt);
            }

            int hullNuggiesToAddToChestForQuitter = 0;
            if (causedByCaptainQuitting)
            {
                FP hullPercent = (FP)damageToHull / shipDamaged->HullMax;
                hullNuggiesToAddToChestForQuitter =
                    FPMath.FloorToInt(hullPercent * shipDamaged->FullRepairCost * ShipSystem.NuggiesBonus);
            }

            shipDamaged->Shield = FPMath.Max(shipDamaged->Shield - damageToShields, 0);
            shipDamaged->Hull = FPMath.Max(shipDamaged->Hull - damageToHull, 0);

            if (shipDealing != default)
            {
                shipDealing->Damage += FPMath.CeilToInt(damageDealt * 100);
            }

            ModifyDamageTakenData(f, damageDealt, shipDamaged->VisibleEntity, shipDealing->VisibleEntity);

            if (prevHealthShipDamaged > 0 && shipDamaged->Hull <= 0)
            {
                if (shipDealing != default)
                {
                    shipDealing->NPCKills++;
                }
            }

            DropLoot(f, prevHealthShipDamaged, shipDamaged, shipDealing, playerDealing);

            if (shipDealing != default && prevHealthShipDamaged > 0 && shipDamaged->Hull < prevHealthShipDamaged)
            {
                FP hullDamagePercent = (FP)damageToHull / shipDamaged->HullMax;
                shipDealing->NuggiesDamage +=
                    FPMath.FloorToInt(hullDamagePercent * shipDamaged->FullRepairCost * ShipSystem.NuggiesBonus);
            }

            f.Events.ShipHit(shipDamaged->VisibleEntity, shipDealing->VisibleEntity, WeaponType.DefaultCannon);

            foreach (var playerLink in f.Unsafe.GetComponentBlockIterator<PlayerLink>())
            {
                if (playerLink.Component->Player == playerDealing)
                {
                    NPCShipData npcShipData = f.FindAsset<NPCShipData>(shipDamaged->npcShipData.Id);
                    var roundedDamage = FPMath.RoundToInt((damageToShields + damageToHull));
                    playerLink.Component->ScoreboardNPCDamage += roundedDamage;
                    playerLink.Component->NPCEnemyKillsDamage[(int)npcShipData.enemyType] += roundedDamage;

                    if (shipDamaged->Hull <= 0 && prevHealthShipDamaged > 0)
                    {
                        playerLink.Component->ScoreboardNPCKills++;
                        playerLink.Component->NPCEnemyKills[(int)npcShipData.enemyType]++;
                        //var tranform = f.Unsafe.GetPointer<Transform3D>(playerLink.Component->Entity);
                        //EnemySystem.CheckAndSpawnPerkLoot(f,tranform->Position,EnemyType.Raider);
                    }

                    break;
                }
            }
        }

        public static void DamageShip(Frame f, NPCShip* shipDealing, NPCShip* shipDamaged, FP damageDealt,
            PlayerRef playerDealing, WeaponType weaponType)
        {
            NPCShipData npcShipData = f.FindAsset<NPCShipData>(shipDealing->npcShipData.Id);
            FP prevHealthShipDamaged = shipDamaged->Hull;
            FP damageToShields = FPMath.Min(shipDamaged->Shield, damageDealt);
            FP damageToHull = damageDealt - damageToShields;
            damageToHull = FPMath.Min(damageToHull, shipDamaged->Hull);

            /*int hullNuggiesToAddToChestForQuitter = 0;
            if (causedByCaptainQuitting)
            {
                FP hullPercent = (FP)damageToHull / shipDamaged->HullMax;
                hullNuggiesToAddToChestForQuitter = FPMath.FloorToInt(hullPercent * shipDamaged->FullRepairCost * ShipSystem.NuggiesBonus);
            }
            */
            shipDamaged->Shield = FPMath.Max(shipDamaged->Shield - damageToShields, 0);
            shipDamaged->Hull = FPMath.Max(shipDamaged->Hull - damageToHull, 0);
            var npcShipPosition = f.Unsafe.GetPointer<Transform3D>(shipDamaged->VisibleEntity)->Position;
            
            if (shipDamaged->Hull <= 0 && prevHealthShipDamaged > 0)
            {
                ReportNpcKillEncounterByNpcShip(f, npcShipData.enemyTier, npcShipPosition, shipDamaged->VisibleEntity);
            }

            if (weaponType == WeaponType.MortarCannon)
            {
                var damagedVisibleTransform = f.Unsafe.GetPointer<Transform3D>(shipDamaged->VisibleEntity);
                f.Events.MortarDamageRecieved(playerDealing, damagedVisibleTransform->Position, damageDealt.AsInt);
            }

            if (shipDealing != default)
            {
                shipDealing->Damage += FPMath.CeilToInt(damageDealt * npcShipData.DamageMultiplier);
            }

            if (shipDealing != default && prevHealthShipDamaged > 0 && shipDamaged->Hull < prevHealthShipDamaged)
            {
                FP hullDamagePercent = (FP)damageToHull / shipDamaged->HullMax;
                shipDealing->NuggiesDamage +=
                    FPMath.FloorToInt(hullDamagePercent * shipDamaged->FullRepairCost * ShipSystem.NuggiesBonus);
            }

            f.Events.ShipHit(shipDamaged->VisibleEntity, shipDealing->VisibleEntity, WeaponType.DefaultCannon);
        }

        public static void DamageShip(Frame f, NPCShip* shipDealing, Ship* shipDamaged, FP damageDealt,
            PlayerRef playerRef, WeaponType weaponType, bool causedByCaptainQuitting)
        {
            NPCShipData npcShipData = f.FindAsset<NPCShipData>(shipDealing->npcShipData.Id);
            FP prevHealthShipDamaged = shipDamaged->Hull;
            FP damage = damageDealt * (npcShipData.DamageMultiplier / FP.FromFloat_UNSAFE(8.5f));
            FP damageToShields = FPMath.Min(shipDamaged->Shield, damage);
            FP damageToHull = damage - damageToShields;
            damageToHull = FPMath.Min(damageToHull, shipDamaged->Hull);

            int hullNuggiesToAddToChestForQuitter = 0;
            if (causedByCaptainQuitting)
            {
                FP hullPercent = (FP)damageToHull / shipDamaged->HullMax;
                hullNuggiesToAddToChestForQuitter =
                    FPMath.FloorToInt(hullPercent * shipDamaged->FullRepairCost * ShipSystem.NuggiesBonus);
            }

            if (weaponType == WeaponType.MortarCannon)
            {
                var damagedVisibleTransform = f.Unsafe.GetPointer<Transform3D>(shipDamaged->VisibleEntity);
                f.Events.MortarDamageRecieved(playerRef, damagedVisibleTransform->Position, damageDealt.AsInt);
            }

            shipDamaged->Shield = FPMath.Max(shipDamaged->Shield - damageToShields, 0);
            shipDamaged->Hull = FPMath.Max(shipDamaged->Hull - damageToHull, 0);

            if (shipDealing != default)
            {
                shipDealing->Damage += FPMath.CeilToInt(damageDealt * npcShipData.DamageMultiplier);
            }

            //DropLoot(f, prevHealthShipDamaged, shipDamaged, hullNuggiesToAddToChestForQuitter);

            if (shipDealing != default && prevHealthShipDamaged > 0 && shipDamaged->Hull < prevHealthShipDamaged)
            {
                FP hullDamagePercent = (FP)damageToHull / shipDamaged->HullMax;
                shipDealing->NuggiesDamage +=
                    FPMath.FloorToInt(hullDamagePercent * shipDamaged->FullRepairCost * ShipSystem.NuggiesBonus);
            }

            f.Events.ShipHit(shipDamaged->VisibleEntity, shipDealing->VisibleEntity, WeaponType.DefaultCannon);

            ShipSystem.CheckPlayerShipSunk(f, shipDamaged, prevHealthShipDamaged, causedByCaptainQuitting);
        }

        // Damage by gauntlet project
        public static void DamageShip(Frame f, EntityRef shooterEntity, NPCShip* shipDamaged, FP damageDealt)
        {
            FP prevHealthShipDamaged = shipDamaged->Hull;
            FP damageToShields = FPMath.Min(shipDamaged->Shield, damageDealt);
            FP damageToHull = damageDealt - damageToShields;
            //Log.Error("damage "+ damageToShields + " damage to ship "+ damageToHull);
            damageToHull = FPMath.Min(damageToHull, shipDamaged->Hull);

            shipDamaged->Shield = FPMath.Max(shipDamaged->Shield - damageToShields, 0);
            shipDamaged->Hull = FPMath.Max(shipDamaged->Hull - damageToHull, 0);
            ModifyDamageTakenData(f, damageDealt, shipDamaged->VisibleEntity, shooterEntity);
            f.Events.ShipHit(shipDamaged->VisibleEntity, shooterEntity, WeaponType.DefaultCannon);
        }

        private static void ModifyDamageTakenData(Frame f, FP damage, EntityRef shipEntity, EntityRef enemyEntity)
        {
            if (!f.Unsafe.TryGetPointer(shipEntity, out Ship* ship))
            {
                return;
            }

            if (!f.Unsafe.TryGetPointer(enemyEntity, out NPCShip* npcShip))
            {
                return;
            }

            if (npcShip->damageTakenFromPlayers.Equals(default))
            {
                f.AllocateDictionary(out npcShip->damageTakenFromPlayers);
            }

            var damageTakenData = f.ResolveDictionary(npcShip->damageTakenFromPlayers);
            if (damageTakenData.TryGetValue(ship->PartyId, out var damageTaken))
            {
                damageTaken += damage;
                damageTakenData[ship->PartyId] = damageTaken;
                Log.Debug("npc ship taken damage from: " + ship->PartyId + " damage: " + damageTaken);
            }
            else
            {
                damageTakenData.Add(ship->PartyId, damage);
                Log.Debug("(new) npc ship taken damage from: " + ship->PartyId + " damage: " + damage);
            }

            npcShip->damageTakenFromPlayers = damageTakenData;
        }

        public static void ReportNpcKillEncounterByNpcShip(Frame f, EnemyTier tier, FPVector3 position, EntityRef enemyEntity)
        {
            var shipPartyId = 0;

            if (f.Unsafe.TryGetPointer<NPCShip>(enemyEntity, out var npcShip))
            {
                var newPartyId = EnemySystem.GetPartyIdWithMostDamageFromNPCShip(f, enemyEntity);
                if (newPartyId > 0)
                    shipPartyId = newPartyId;
            }

            // No player dealt damage — pick the leader with the smallest party ID as designated reporter.
            // All clients run this deterministically so they all agree on the same party.
            if (shipPartyId <= 0)
            {
                int smallestPartyId = int.MaxValue;
                for (int i = 0; i < f.PlayerCount; i++)
                {
                    var pd = f.GetPlayerData((PlayerRef)i);
                    if (pd != null && pd.IsLeader && pd.ShipPartyId > 0 && pd.ShipPartyId < smallestPartyId)
                        smallestPartyId = pd.ShipPartyId;
                }
                if (smallestPartyId != int.MaxValue)
                    shipPartyId = smallestPartyId;
            }

            Log.Debug("Reporting NPC kill for partyId: " + shipPartyId + " enemy tier: " + tier + " position: " + position);
            var enemyLevel = (int)(tier) + 1;
            f.Events.PluginShipReportEncounterNpcKill(f, shipPartyId, enemyLevel, position);
        }

        //      public static void DropLoot(Frame f, FP prevHealthShipDamaged, Ship* shipDamaged, int hullNuggiesToAddToChestForQuitter) {

        //	if (prevHealthShipDamaged > 0 && shipDamaged->Hull <= 0)
        //	{
        //		Transform3D* shiptransform = f.Unsafe.GetPointer<Transform3D>(shipDamaged->VisibleEntity);

        //		if (shipDamaged->NuggiesDamage + shipDamaged->NuggiesPlunder + hullNuggiesToAddToChestForQuitter > 0)
        //		{
        //			//EntityPrototype lootPrototype = f.FindAsset<EntityPrototype>("Resources/DB/LootEntityPrototype");
        //			//var lootEntity = f.Create(lootPrototype);

        //			int nuggiesKeptDamage = FPMath.FloorToInt(shipDamaged->NuggiesDamage * FP.FromRaw(ShipSystem.NuggiesKeepRatio));
        //			int nuggiesKeptPlunder = FPMath.FloorToInt(shipDamaged->NuggiesPlunder * FP.FromRaw(ShipSystem.NuggiesKeepRatio));
        //			int nuggiesDroppedDamage = shipDamaged->NuggiesDamage - nuggiesKeptDamage;
        //			int nuggiesDroppedPlunder = shipDamaged->NuggiesPlunder - nuggiesKeptPlunder;
        //			//f.Unsafe.GetPointer<Loot>(lootEntity)->Nuggies = nuggiesDroppedDamage + nuggiesDroppedPlunder + hullNuggiesToAddToChestForQuitter;
        //			shipDamaged->NuggiesDamage = nuggiesKeptDamage;
        //			shipDamaged->NuggiesPlunder = nuggiesKeptPlunder;
        //			//f.Unsafe.GetPointer<Transform3D>(lootEntity)->Position = new FPVector3(shiptransform->Position.X, 3, shiptransform->Position.Z);
        //			//Log.Info("madeloot");
        //		}
        //	}
        //}

        public static bool IsNPCShipOutOfBounds(Frame f, Transform3D* npcShipTransform)
        {
            var radius = ShipSystem.PillageBoundsRadius;
            FP distance = FPVector2.Distance(FPVector2.Zero,
                new FPVector2(npcShipTransform->Position.X, npcShipTransform->Position.Z));

            if (distance > radius)
            {
                return true;
            }

            return false;
        }

        public static void DropLoot(Frame f, FP prevHealthShipDamaged, NPCShip* shipDamaged, Ship* playerShip,
            PlayerRef playerDealing)
        {
            if (shipDamaged->hasReported)
            {
                return;
            }
            if (prevHealthShipDamaged > 0 && shipDamaged->Hull <= 0)
            {
                Transform3D* npcShipTransform = f.Unsafe.GetPointer<Transform3D>(shipDamaged->VisibleEntity);

                if (IsNPCShipOutOfBounds(f, npcShipTransform))
                {
                    // dont drop loot
                    return;
                }
                Log.Debug("DropLoot called");

                shipDamaged->hasReported = true;
                NPCShipData npcShipData = f.FindAsset<NPCShipData>(shipDamaged->npcShipData.Id);
                EnemySystem.ReportNpcKillEncounter(f, playerShip, npcShipData.enemyTier,
                    npcShipTransform->Position, playerDealing, shipDamaged->VisibleEntity);
            }
        }


        public void AttackTarget(Frame f, Filter filter, int cannonIndex)
        {
            ShootCannons(f, filter, cannonIndex);
            if (filter.npcShip->WeaponSlots.GetPointer(4) != null)
            {
                ShootCannons(f, filter, cannonIndex + 4);
                //ShootCannons(f, npcShip->WeaponSlots.GetPointer(index + 4), npcShip, physicsBody3D,offset);
            }
        }

        public static void ShootCannons(Frame f, Filter filter, int index)
        {
            PhysicsBody3D* worldRb = f.Unsafe.GetPointer<PhysicsBody3D>(filter.npcShip->WorldEntity);
            var occupiedWeapon = filter.npcShip->WeaponSlots.GetPointer(index);

            EntityPrototype cannonballPrototype =
                f.FindAsset<EntityPrototype>("Resources/DB/Cannonballs/CannonballEntityPrototype");
            EntityRef cannonballEntity = f.Create(cannonballPrototype);
            Transform3D* cannonballTransform = f.Unsafe.GetPointer<Transform3D>(cannonballEntity);

            Transform3D* shipVisibleTransform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->VisibleEntity);
            NPCShipData npcShipData = f.FindAsset<NPCShipData>(filter.npcShip->npcShipData.Id);

            // Predict aim position (XZ) for direction
            Transform3D* targetTransform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->targetRef);
            FPVector3 aimPos = GetLeadAimPosition(f, filter, shipVisibleTransform, worldRb, targetTransform);

            // final direction (still uses your WeaponPrecisionRotation)
            FPVector3 direction = WeaponPrecisionRotation(f, filter.npcShip, occupiedWeapon->Index, aimPos) *
                                  FPVector3.Forward;

            // spawn position (unchanged)
            cannonballTransform->Position =
                shipVisibleTransform->TransformPoint(npcShipData.WeaponSlots[occupiedWeapon->Index].Position);

            var weaponData = ConfigAssetsHelper.GetWeaponDataByWeaponType(f, WeaponType.DefaultCannon);
            Projectile* projectileComponent = f.Unsafe.GetPointer<Projectile>(cannonballEntity);

            projectileComponent->Velocity = direction * 250 + worldRb->Velocity; // keep your projectile speed
            projectileComponent->Falloff = weaponData.Falloff;
            projectileComponent->Range = weaponData.Range;
            projectileComponent->WeaponType = WeaponType.DefaultCannon;
            projectileComponent->OriginShipEntity = filter.npcShip->VisibleEntity;
            projectileComponent->OriginShipVisible = filter.npcShip->VisibleEntity;
            projectileComponent->OriginPlayer = PlayerRef.None;
            projectileComponent->ShooterEntity = filter.npcShip->VisibleEntity;
            projectileComponent->faction = filter.npcShip->spawnPoint.faction;

            // Damage scaling stays the same logic as before
            bool isShip = f.Unsafe.TryGetPointer<Ship>(filter.npcShip->targetRef, out var ship);
            if (isShip)
            {
                projectileComponent->Damage =
                    FPMath.CeilToInt(FPMath.Lerp(4, 15, 1 - ShipSystem.CompanyFullness(f, ship)));
            }
            else if (f.Unsafe.TryGetPointer<NPCShip>(filter.npcShip->targetRef, out var npcship))
            {
                if (npcship->spawnPoint.Agressor)
                {
                    projectileComponent->Damage = FPMath.CeilToInt(40);
                }
                else
                {
                    projectileComponent->Damage = FPMath.CeilToInt(15);
                }
            }
            else
            {
                projectileComponent->Damage = FPMath.CeilToInt(15);
            }
        }

        private static FPVector2 CalculateFacingEulerAngles(FPVector3 from, FPVector3 to)
        {
            FPVector3 direction = to - from;
            FP yaw = FPMath.Atan2(direction.X, direction.Z) * FP.Rad2Deg;
            FP horizontalDistance = new FPVector2(direction.X, direction.Z).Magnitude;
            FP pitch = -FPMath.Atan2(direction.Y, horizontalDistance) * FP.Rad2Deg;
            return new FPVector2(pitch, yaw);
        }

        public static FPQuaternion WeaponGlobalRotation(Frame f, NPCShip* ship, int slotIndex)
        {
            NPCShipData shipData = f.FindAsset<NPCShipData>(ship->npcShipData.Id);
            var shipVisibleTransform = f.Unsafe.GetPointer<Transform3D>(ship->VisibleEntity);

            FPVector3 weaponLocalRotationEuler = new FPVector3(ship->WeaponSlots[slotIndex].Rot.X,
                ship->WeaponSlots[slotIndex].Rot.Y + shipData.WeaponSlots[slotIndex].BaseRotation, 0);
            FPQuaternion weaponRotation = shipVisibleTransform->Rotation * FPQuaternion.Euler(weaponLocalRotationEuler);
            // Calculate the final global position of the offset
            return weaponRotation;
        }

        public static FPQuaternion WeaponPrecisionRotation(Frame f, NPCShip* ship, int slotIndex,
            FPVector3 targetPosition)
        {
            var shipVisibleTransform = f.Unsafe.GetPointer<Transform3D>(ship->VisibleEntity);

            var toPlayer = targetPosition - shipVisibleTransform->Position;
            FP horizontalAngle = FPVector3.SignedAngle(
                new FPVector3(shipVisibleTransform->Forward.X, 0, shipVisibleTransform->Forward.Z),
                new FPVector3(toPlayer.X, 0, toPlayer.Z), shipVisibleTransform->Up);
            horizontalAngle += horizontalAngle > 0 ? 4 : -4;
            FPVector3 weaponLocalRotationEuler = new FPVector3(ship->WeaponSlots[slotIndex].Rot.X, horizontalAngle, 0);
            FPQuaternion weaponRotation = shipVisibleTransform->Rotation * FPQuaternion.Euler(weaponLocalRotationEuler);
            // Calculate the final global position of the offset
            return weaponRotation;
        }

        public static void DestroyShip(Frame f, NPCShip* shipDamaged, bool ignoreHullValue = false)
        {
            if (shipDamaged->Hull <= 0 || ignoreHullValue)
            {
                if (!shipDamaged->damageTakenFromPlayers.Equals(default))
                {
                    f.FreeDictionary(shipDamaged->damageTakenFromPlayers);
                    shipDamaged->damageTakenFromPlayers = default;
                }
                Log.Info("Destroyed NPC Ship");
                f.Destroy(shipDamaged->WorldEntity);
                f.Destroy(shipDamaged->VisibleEntity);
                f.Destroy(shipDamaged->ImpactEntity);
            }
        }

        private void FindTarget(Frame f, Transform3D* npcTransform, Filter filter)
        {
            EntityRef nearestTarget = EntityRef.None;
            var minSqrDistance = filter.npcShip->navigator.targetRadius * filter.npcShip->navigator.targetRadius;

            var ships = f.Filter<Ship>();
            while (ships.Next(out EntityRef shipEntity, out Ship shipComponent))
            {
                if (filter.npcShip->spawnPoint.faction != PlayerFaction.None &&
                    shipComponent.faction == filter.npcShip->spawnPoint.faction)
                {
                    continue;
                }

                var playerTransform = f.Get<Transform3D>(shipEntity);
                var sqrDistance = (playerTransform.Position - npcTransform->Position).SqrMagnitude;
                if (sqrDistance < minSqrDistance)
                {
                    nearestTarget = shipEntity;
                    minSqrDistance = sqrDistance;
                }
            }

            if (nearestTarget == EntityRef.None)
            {
                var npcships = f.Filter<NPCShip>();
                while (npcships.Next(out EntityRef shipEntity, out NPCShip npcship))
                {
                    if (filter.npcShip->spawnPoint.faction != PlayerFaction.None &&
                        npcship.spawnPoint.faction == filter.npcShip->spawnPoint.faction)
                    {
                        continue;
                    }

                    var playerTransform = f.Get<Transform3D>(shipEntity);
                    var sqrDistance = (playerTransform.Position - npcTransform->Position).SqrMagnitude;
                    if (sqrDistance < minSqrDistance)
                    {
                        nearestTarget = shipEntity;
                        minSqrDistance = sqrDistance;
                    }
                }
            }

            filter.npcShip->targetRef = nearestTarget;
        }

        /*public EntityRef GetTarget(Frame f, Transform3D* npcTransform, NPCShip* ship)
        {
            ComponentFilter<Ship> playerShips = f.Filter<Ship>();
            EntityRef result = EntityRef.None;
            Transform3D* resultTransform = null;
            NPCShipData npcShipData = f.FindAsset<NPCShipData>(ship->npcShipData.Id);
            while (playerShips.Next(out EntityRef shipEntity, out Ship shipComponent))
            {
                if (shipComponent.faction == ship->spawnPoint.faction)
                {
                    continue;
                }

                Transform3D* playerTransform = f.Unsafe.GetPointer<Transform3D>(shipEntity);
                FPVector3 startPosition = npcTransform->Forward * -npcShipData.sightForward;
                FP dstToTarget = FPVector3.Distance(startPosition + npcTransform->Position, playerTransform->Position);
                if (dstToTarget <= npcShipData.sightRadius)
                {
                    FPVector3 dirToTarget = (playerTransform->Position - npcTransform->Position).Normalized;
                    if (FPVector3.Angle(npcTransform->Forward, dirToTarget) < npcShipData.sightAngle / 2)
                    {
                        FP dstToResultTarget = resultTransform == null ? 99999 : FPVector3.Distance(npcTransform->Position, resultTransform->Position);
                        if (dstToTarget <= dstToResultTarget)
                        {
                            resultTransform = playerTransform;
                            result = shipEntity;
                        }
                    }
                }
            }

            if (result == EntityRef.None)
            {
                ComponentFilter<NPCShip> NPCShips = f.Filter<NPCShip>();
                while (NPCShips.Next(out EntityRef npcshipEntity, out NPCShip shipComponent))
                {
                    if (shipComponent.spawnPoint.faction == ship->spawnPoint.faction)
                    {
                        continue;
                    }

                    Transform3D* npcsAttackTransform = f.Unsafe.GetPointer<Transform3D>(npcshipEntity);
                    FPVector3 startPosition = npcTransform->Forward * -npcShipData.sightForward;
                    FP dstToTarget = FPVector3.Distance(startPosition + npcTransform->Position,
                        npcsAttackTransform->Position);
                    if (dstToTarget <= npcShipData.sightRadius)
                    {
                        FPVector3 dirToTarget = (npcsAttackTransform->Position - npcTransform->Position).Normalized;
                        if (FPVector3.Angle(npcTransform->Forward, dirToTarget) < npcShipData.sightAngle / 2)
                        {
                            FP dstToResultTarget = resultTransform == null
                                ? 99999
                                : FPVector3.Distance(npcTransform->Position, resultTransform->Position);
                            if (dstToTarget <= dstToResultTarget)
                            {
                                resultTransform = npcsAttackTransform;
                                result = npcshipEntity;
                            }
                        }
                    }
                }
            }

            return result;
        }*/

        public override void Update(Frame f, ref Filter filter)
        {
            Transform3D* worldTransform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);
            SunkUpdate(f, filter);
            //AdaptiveDifficulty(f);

            if (filter.npcShip->Hull <= 0)
                return;
            var handledOOB = NPCBoundaryReturn(f, filter);
            if (!handledOOB)
            {
                NavigationUpdate(f, filter);
            }

            ApplyWindForce(f, filter);

            if (f.RuntimeConfig.Mode == ModeType.Docks)
            {
                DestroyShip(f, filter.npcShip);
                return;
            }

            filter.transform->Position = worldTransform->Position;
            filter.transform->Rotation = worldTransform->Rotation;
            Transform3D* impactTransform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->ImpactEntity);
            impactTransform->Position = filter.transform->Position;
            impactTransform->Rotation = filter.transform->Rotation;

            // Destroy NPC ship if it wanders too far off the map
            //DestroyWanderingNpcShip(f, filter);
            UpdateSlowSpeedStack(f, filter);
        }

        private void DestroyWanderingNpcShip(Frame f, Filter filter)
        {
            var shipWorldTransform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);

            var npcShipDistanceFromMapCenter = FPVector3.Distance(filter.npcShip->navigator.centerPoint,
                new FPVector3(shipWorldTransform->Position.X, 0, shipWorldTransform->Position.Z));

            var outOfBoundsRadius = FP._1 * 3500;
            if (npcShipDistanceFromMapCenter >= outOfBoundsRadius)
            {
                DestroyShip(f, filter.npcShip, true);
            }
        }

        private void NavigationUpdate(Frame f, Filter filter)
        {
            // Decide high-level mode
            filter.npcShip->navigator.currentStage = NPCStages.Fourth;
            Transform3D* worldTransform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);
            filter.npcShip->targetTimer -= f.DeltaTime;
            if (filter.npcShip->targetTimer < 0)
            {
                filter.npcShip->targetTimer = FP._0_50;
                bool findTarget = !(filter.npcShip->spawnPoint.Agressor && f.Exists(filter.npcShip->targetRef));

                if (findTarget)
                    FindTarget(f, worldTransform, filter);

                /*if (filter.npcShip->targetRef != EntityRef.None)
                {
                    filter.npcShip->lastTargetShip = filter.npcShip->targetRef;
                }
                else if (filter.npcShip->lastTimeAttackedByPlayerShip > 0)
                {
                    if (f.Exists(filter.npcShip->lastTargetShip))
                    {
                        filter.npcShip->targetRef = filter.npcShip->lastTargetShip;
                    }
                    else
                    {
                        filter.npcShip->targetRef = EntityRef.None;
                        filter.npcShip->lastTimeAttackedByPlayerShip = 0;
                    }
                }
                else
                {
                    filter.npcShip->navigator.combatStateTimer = 0;
                    filter.npcShip->navigator.navigationMode = NavigationMode.Patrol;
                }*/
            }


            // If in avoidance (manoeuvre) we keep a lightweight flag, but heading now comes from avoidance blend
            if (filter.npcShip->manoverAngle != 0)
            {
                filter.npcShip->navigator.navigationMode = NavigationMode.Manovering;
            }
            else if (filter.npcShip->lastTimeAttackedByPlayerShip > 0 && f.Exists(filter.npcShip->lastTargetShip))
            {
                filter.npcShip->targetRef = filter.npcShip->lastTargetShip;
                filter.npcShip->lastTimeAttackedByPlayerShip -= f.DeltaTime;
                CombatStateUpdate(f, filter);
            }
            else if (f.Exists(filter.npcShip->targetRef))
            {
                CombatStateUpdate(f, filter);
                filter.npcShip->lastTimeAttackedByPlayerShip = 0;
            }
            else
            {
                filter.npcShip->navigator.navigationMode = NavigationMode.Patrol;
                filter.npcShip->lastTimeAttackedByPlayerShip = 0;
            }

            ExecuteNavigationMode(f, filter);

            // Sails open based on mode, your original mapping preserved
            filter.npcShip->sail.numberOfSailsOpened = SailsFor(filter.npcShip->navigator.navigationMode);

            // Update avoidance/maneuver watchdog & stuck recovery
            ManoverUpdate(f, filter);
            StuckWatchdog(f, filter); // NEW
        }

// --------------------- Steering / Modes ----------------------

        private void ExecuteNavigationMode(Frame f, Filter filter)
        {
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");
            PhysicsBody3D* worldRb = f.Unsafe.GetPointer<PhysicsBody3D>(filter.npcShip->WorldEntity);
            Transform3D* worldTx = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);

            AvoidanceSupervisorUpdate(f, filter);
            filter.npcShip->navigator.speedRegulator = 1;

            Transform3D* targetTx = null;
            f.Unsafe.TryGetPointer<Transform3D>(filter.npcShip->targetRef, out targetTx);

            if (CombatLeashGuard(f, filter, worldTx, targetTx))
                return;

            // NEW: queued minions hold formation
            if (FormationHoldGuard(f, filter, worldTx, targetTx))
                return;

            FP ApplySteer(FP desiredDeg, FP lerpMul)
            {
                return SteeringLerp(filter.npcShip->sail.sailRotation, desiredDeg, lerpMul * GetSlowSpeedStack(filter));
            }

            switch (filter.npcShip->navigator.navigationMode)
            {
                case NavigationMode.Patrol:
                {
                    PatrolPath path = f.FindAsset<PatrolPath>(filter.npcShip->navigator.patrolPath.Id);
                    FP patrolAngle = GetPatrolPathAngle(filter.npcShip, path, worldTx);

                    if (GetWithinPatrolRange(filter, path))
                    {
                        var currentPosition = GetPatrolPoint(filter.npcShip, path);
                        filter.npcShip->navigator.currentIndex =
                            (filter.npcShip->navigator.currentIndex + 1) % path.path.Length;

                        if (filter.npcShip->navigator.currentIndex == 0 && !path.isClosed)
                        {
                            /*var factionsHandler = f.Unsafe.GetPointerSingleton<FactionsHandler>();
                            var factionT =
                                f.Unsafe.GetPointer<Transform3D>(
                                    factionsHandler->factions[factionsHandler->attackedFactionIndex]);*/
                            var assets =
                                f.FindAsset<PatrolPathAssets>("Resources/DB/NPCShipDataAssets/PatrolPathsData");
                            filter.npcShip->navigator.patrolPath = assets.PatrolPaths[1];
                            filter.npcShip->navigator.centerPoint = currentPosition;
                        }
                    }

                    // Light avoidance toward node
                    FP avoid = ObstacleAvoidanceAngle(f, filter, worldTx, patrolAngle, targetTx, false);
                    FP target = BlendDesiredWithAvoidance(worldTx, patrolAngle, avoid, NavTuning);

                    FP k = NavTuning.SteeringLerpBase * filter.npcShip->navigator.turnSpeed;
                    filter.npcShip->sail.sailRotation = ApplySteer(target, k);
                    break;
                }

                case NavigationMode.Manovering:
                {
                    FP fallback = filter.npcShip->sail.sailRotation + filter.npcShip->manoverAngle * 90;
                    FP avoid = ObstacleAvoidanceAngle(f, filter, worldTx, fallback, targetTx, false);
                    FP target = BlendDesiredWithAvoidance(worldTx, fallback, avoid, NavTuning);

                    FP k = NavTuning.SteeringLerpChase * filter.npcShip->navigator.chaseSpeed;
                    filter.npcShip->sail.sailRotation = ApplySteer(target, k);
                    break;
                }

                case NavigationMode.Pursuing:
                {
                    if (targetTx == null) break;

                    AttackingMode pid = (AttackingMode)PatternId(filter.npcShip->navigator.combatStateCounter);
                    int arg = PatternArg(filter.npcShip->navigator.combatStateCounter);

                    FPVector3 aimPoint;
                    if (pid == AttackingMode.SternRake)
                        aimPoint = targetTx->Position + targetTx->Back * 150 +
                                   (arg == 0 ? targetTx->Left : targetTx->Right) * 40;
                    else
                        aimPoint = targetTx->Position + targetTx->Forward * 55;

                    FP ang = CalculateAngleToTarget(worldTx->Position, aimPoint);
                    FP avoid = ObstacleAvoidanceAngle(f, filter, worldTx, ang, targetTx, true);
                    FP target = BlendDesiredWithAvoidance(worldTx, ang, avoid, NavTuning);

                    FP k = NavTuning.SteeringLerpChase * filter.npcShip->navigator.chaseSpeed;
                    filter.npcShip->sail.sailRotation = ApplySteer(target, k);

                    CannonsUpdate(f, filter);
                    break;
                }

                case NavigationMode.Attacking:
                {
                    if (targetTx == null) break;

                    AttackingMode pid = (AttackingMode)PatternId(filter.npcShip->navigator.combatStateCounter);
                    int arg = PatternArg(filter.npcShip->navigator.combatStateCounter);

                    // Aggressor vs NPC → tighter radius & steering boost
                    bool aggroClose = filter.npcShip->spawnPoint.Agressor &&
                                      f.Unsafe.TryGetPointer<NPCShip>(filter.npcShip->targetRef, out var _);

                    FP radiusBase = NavTuning.IdealBroadsideRadius;
                    FP radiusTight =
                        radiusBase * FP.FromFloat_UNSAFE(0.65f); // ~35% tighter when aggressive close fight
                    FP steerBoost = aggroClose ? FP.FromFloat_UNSAFE(1.35f) : FP._1;

                    FPVector3 anchor;
                    switch (pid)
                    {
                        case AttackingMode.Orbit: // sectors 0..3
                            anchor = (arg & 3) switch
                            {
                                1 => targetTx->Position + targetTx->Right * radiusBase,
                                2 => targetTx->Position + targetTx->Back * radiusBase,
                                3 => targetTx->Position + targetTx->Left * radiusBase,
                                _ => targetTx->Position + targetTx->Forward * radiusBase
                            };
                            break;

                        case AttackingMode.CrossT: // 0=Front cut, 1=Back cut
                            anchor = targetTx->Position + (arg == 0 ? targetTx->Forward : targetTx->Back) *
                                (radiusBase * FP._0_75);
                            break;

                        case AttackingMode.SternRake:
                            anchor = targetTx->Position + targetTx->Back * 150 +
                                     (arg == 0 ? targetTx->Left : targetTx->Right) * 60;
                            break;

                        case AttackingMode.BrakeTurn:
                            filter.npcShip->navigator.speedRegulator = FP.FromFloat_UNSAFE(0.65f);
                            anchor = targetTx->Position +
                                     (arg == 0 ? targetTx->Left : targetTx->Right) *
                                     (radiusBase * FP.FromFloat_UNSAFE(0.85f));
                            break;

                        // NEW: tight orbit that sticks close (sectors 0..3)
                        case AttackingMode.KnifeOrbit:
                            anchor = (arg & 3) switch
                            {
                                1 => targetTx->Position + targetTx->Right * radiusTight,
                                2 => targetTx->Position + targetTx->Back * radiusTight,
                                3 => targetTx->Position + targetTx->Left * radiusTight,
                                _ => targetTx->Position + targetTx->Forward * radiusTight
                            };
                            break;

                        // NEW: clamp a side with slight forward bias; keeps pressure & proximity
                        case AttackingMode.ClampSide:
                        {
                            FPVector3 side = (arg == 1
                                ? targetTx->Right
                                : (arg == 3
                                    ? targetTx->Left
                                    : (BoardSign(worldTx, targetTx) > 0 ? targetTx->Right : targetTx->Left)));
                            FPVector3 ahead = targetTx->Forward;
                            FP sideRadius = aggroClose ? radiusTight : radiusBase;
                            anchor = targetTx->Position + side * (sideRadius * FP.FromFloat_UNSAFE(0.85f))
                                                        + ahead * (sideRadius * FP.FromFloat_UNSAFE(0.35f));
                            break;
                        }

                        default:
                            anchor = targetTx->Position + targetTx->Right * radiusBase;
                            break;
                    }

                    // If aggressive close fight, clamp anchor to tight radius to avoid drifting apart
                    if (aggroClose)
                        anchor = ClampAroundTarget(targetTx->Position, anchor, radiusTight);

                    FP ang = CalculateAngleToTarget(worldTx->Position, anchor);
                    FP avoid = ObstacleAvoidanceAngle(f, filter, worldTx, ang, targetTx, true);
                    FP target = BlendDesiredWithAvoidance(worldTx, ang, avoid, NavTuning);

                    FP k = (NavTuning.SteeringLerpChase * filter.npcShip->navigator.chaseSpeed) * steerBoost;
                    filter.npcShip->sail.sailRotation = ApplySteer(target, k);

                    CannonsUpdate(f, filter);

                    // keep circling / evolving pattern naturally
                    if (FPVector3.DistanceSquared(worldTx->Position, anchor) < 1200)
                    {
                        if (pid == AttackingMode.Orbit)
                            filter.npcShip->navigator.combatStateCounter =
                                MakePattern((int)AttackingMode.Orbit, ((arg + 1) & 3));
                        if (pid == AttackingMode.BrakeTurn)
                            filter.npcShip->navigator.combatStateCounter =
                                MakePattern((int)AttackingMode.Orbit, (arg == 0 ? 1 : 3));
                        if (pid == AttackingMode.KnifeOrbit)
                            filter.npcShip->navigator.combatStateCounter =
                                MakePattern((int)AttackingMode.KnifeOrbit, ((arg + 1) & 3));
                    }

                    break;
                }

                case NavigationMode.Flanking:
                {
                    if (targetTx == null) break;

                    AttackingMode pid = (AttackingMode)PatternId(filter.npcShip->navigator.combatStateCounter);
                    int arg = PatternArg(filter.npcShip->navigator.combatStateCounter);

                    bool isPlayer = f.Unsafe.TryGetPointer<Ship>(filter.npcShip->targetRef, out var ship);
                    filter.npcShip->navigator.speedRegulator = isPlayer
                        ? ShipSystem.SailWindPower(f, ship)
                        : worldRb->Velocity.Magnitude / filter.npcShip->sail.maxForce;

                    bool aggroClose = filter.npcShip->spawnPoint.Agressor &&
                                      f.Unsafe.TryGetPointer<NPCShip>(filter.npcShip->targetRef, out var _);

                    FP radiusBase = NavTuning.IdealBroadsideRadius;
                    FP radiusTight = radiusBase * FP.FromFloat_UNSAFE(0.65f);
                    FP steerBoost = aggroClose ? FP.FromFloat_UNSAFE(1.25f) : FP._1;

                    FPVector3 anchor;
                    if (pid == AttackingMode.ShearDodge)
                    {
                        anchor = targetTx->Position + targetTx->Forward * 70 +
                                 (arg == 0 ? targetTx->Left : targetTx->Right) * 140;
                    }
                    else
                    {
                        FPVector3 lateral = (arg == 0 ? targetTx->Right : targetTx->Left) *
                                            (aggroClose ? radiusTight : radiusBase);
                        FPVector3 ahead = targetTx->Forward *
                                          ((aggroClose ? radiusTight : radiusBase) *
                                           FP.FromFloat_UNSAFE(0.40f)); // slightly smaller forward bias when close
                        anchor = targetTx->Position + ahead + lateral;
                    }

                    if (aggroClose)
                        anchor = ClampAroundTarget(targetTx->Position, anchor, radiusTight);

                    FP ang = CalculateAngleToTarget(worldTx->Position, anchor);
                    FP avoid = ObstacleAvoidanceAngle(f, filter, worldTx, ang, targetTx, true);
                    FP target = BlendDesiredWithAvoidance(worldTx, ang, avoid, NavTuning);

                    FP k = (NavTuning.SteeringLerpChase * filter.npcShip->navigator.chaseSpeed) * steerBoost;
                    filter.npcShip->sail.sailRotation = ApplySteer(target, k);

                    CannonsUpdate(f, filter);
                    break;
                }
                case NavigationMode.Ramming:
                {
                    if (targetTx == null) break;

                    FP d2 = FPVector3.DistanceSquared(worldTx->Position, targetTx->Position);
                    FP aim = (d2 <= NavTuning.RamWindow * NavTuning.RamWindow)
                        ? CalculateAngleToTarget(worldTx->Position, targetTx->Position)
                        : CalculateAngleToTarget(worldTx->Position, targetTx->Position + targetTx->Forward * 40);

                    FP avoid = ObstacleAvoidanceAngle(f, filter, worldTx, aim, targetTx, true);
                    FP target = BlendDesiredWithAvoidance(worldTx, aim, avoid, NavTuning);

                    FP k = NavTuning.SteeringLerpChase * filter.npcShip->navigator.chaseSpeed;
                    filter.npcShip->sail.sailRotation = ApplySteer(target, k);
                    break;
                }
            }

            // never stall throttle
            filter.npcShip->navigator.speedRegulator =
                FPMath.Max(filter.npcShip->navigator.speedRegulator, FP._0_25);
        }

// --------------------- Combat State Selection ----------------------

        private void CombatStateUpdate(Frame f, Filter filter)
        {
            if (filter.npcShip->targetRef == EntityRef.None) return;
            f.Unsafe.TryGetPointer<Transform3D>(filter.npcShip->targetRef, out var targetTx);
            if (targetTx == null) return;

            Transform3D* selfTx = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");

            bool enemyIsShip = f.Unsafe.TryGetPointer<Ship>(filter.npcShip->targetRef, out var enemyShip);
            bool enemyIsNPC = f.Unsafe.TryGetPointer<NPCShip>(filter.npcShip->targetRef, out var enemyNPC);

            // Tick pattern timer
            filter.npcShip->navigator.combatStateTimer -= f.DeltaTime;
            if (filter.npcShip->navigator.combatStateTimer >= 0) return;

            // >>> NEW: Aggressor vs NPC = MOBA one-on-one (pairs or queues). <<<
            if (filter.npcShip->spawnPoint.Agressor && enemyIsNPC)
            {
                // Prevent Pursuing/Ramming for aggressor-vs-NPC (keeps them close)
                if (filter.npcShip->navigator.navigationMode == NavigationMode.Pursuing ||
                    filter.npcShip->navigator.navigationMode == NavigationMode.Ramming)
                {
                    filter.npcShip->navigator.combatStateTimer = 0; // force reselection
                }
            }

            bool isDinghyOrRaft = false;
            if (enemyIsShip)
            {
                var shipData = f.FindAsset<ShipData>(enemyShip->ShipData.Id);
                isDinghyOrRaft = shipData.ShipType == ShipType.Raft || shipData.ShipType == ShipType.Dinghy;
            }

            // --- geometry ---
            FP d2 = FPVector3.DistanceSquared(selfTx->Position, targetTx->Position);
            FPVector3 toNPC = selfTx->Position - targetTx->Position;
            FP boards = FPVector3.Dot(targetTx->Right, toNPC.Normalized); // +right / -left
            bool close = d2 < NavTuning.EngageDistance * NavTuning.EngageDistance;
            bool ramViable = d2 < (NavTuning.RamWindow * NavTuning.RamWindow) * 2 && isDinghyOrRaft;

            // --- enemy meta ---
            NavigationMode enemyMode = enemyIsNPC ? enemyNPC->navigator.navigationMode : NavigationMode.Pursuing;
            int enemySector = enemyIsNPC ? enemyNPC->navigator.combatStateCounter : 0;

            FP rng = f.Global->RngSession.Next(FP._0, FP._1);
            bool isAggressor = filter.npcShip->spawnPoint.Agressor;

            // If an aggressor is stuck in Pursuing/Ramming vs NPC, force reselect now (to keep close)
            if (isAggressor && enemyIsNPC &&
                (filter.npcShip->navigator.navigationMode == NavigationMode.Pursuing ||
                 filter.npcShip->navigator.navigationMode == NavigationMode.Ramming))
            {
                filter.npcShip->navigator.combatStateTimer = 0;
            }

            // Tick pattern timer (after possible force-reset)
            filter.npcShip->navigator.combatStateTimer -= f.DeltaTime;
            if (filter.npcShip->navigator.combatStateTimer >= 0) return;

            // ----------- AGGRESSOR vs NPC: close-range, 2× aggressive -----------
            if (isAggressor && enemyIsNPC)
            {
                // Only close-holding patterns (no Pursuing, no Ramming)
                if (close)
                {
                    // Tight orbit or clamp a side; occasional cross-the-T
                    if (rng < FP.FromFloat_UNSAFE(0.55f))
                    {
                        filter.npcShip->navigator.navigationMode = NavigationMode.Attacking;
                        filter.npcShip->navigator.combatStateCounter =
                            MakePattern((int)AttackingMode.KnifeOrbit, (boards > 0 ? 1 : 3)); // stick to exposed side
                    }
                    else if (rng < FP.FromFloat_UNSAFE(0.85f))
                    {
                        filter.npcShip->navigator.navigationMode = NavigationMode.Attacking;
                        filter.npcShip->navigator.combatStateCounter =
                            MakePattern((int)AttackingMode.ClampSide, (boards > 0 ? 1 : 3)); // keep pressing
                    }
                    else
                    {
                        filter.npcShip->navigator.navigationMode = NavigationMode.Attacking;
                        filter.npcShip->navigator.combatStateCounter =
                            MakePattern((int)AttackingMode.CrossT, (boards > 0 ? 0 : 1)); // front/back cut
                    }
                }
                else
                {
                    // Not yet close: use a tight side clamp to close distance quickly
                    filter.npcShip->navigator.navigationMode = NavigationMode.Attacking;
                    filter.npcShip->navigator.combatStateCounter =
                        MakePattern((int)AttackingMode.ClampSide, (boards > 0 ? 1 : 3));
                }

                // 2× aggression: shorter timers (snappier pattern shifts)
                filter.npcShip->navigator.combatStateTimer = f.Global->RngSession.Next(3, 5);
                filter.npcShip->navigator.speedRegulator = 1;
                return;
            }

            // ----------- ORIGINAL adaptive selection (non-aggressor or vs player) -----------
            if (ramViable && rng < FP.FromFloat_UNSAFE(0.15f))
            {
                filter.npcShip->navigator.navigationMode = NavigationMode.Ramming;
                filter.npcShip->navigator.combatStateCounter = MakePattern((int)AttackingMode.Orbit, 0);
            }
            else if (enemyIsNPC)
            {
                switch (enemyMode)
                {
                    case NavigationMode.Ramming:
                        filter.npcShip->navigator.navigationMode = NavigationMode.Flanking;
                        filter.npcShip->navigator.combatStateCounter =
                            MakePattern((int)AttackingMode.ShearDodge, boards > 0 ? 1 : 0);
                        break;

                    case NavigationMode.Pursuing:
                        filter.npcShip->navigator.navigationMode = NavigationMode.Attacking;
                        filter.npcShip->navigator.combatStateCounter =
                            MakePattern((int)AttackingMode.BrakeTurn, boards > 0 ? 0 : 1);
                        break;

                    case NavigationMode.Attacking:
                        filter.npcShip->navigator.navigationMode = NavigationMode.Attacking;
                        if (close)
                        {
                            filter.npcShip->navigator.combatStateCounter =
                                MakePattern((int)AttackingMode.CrossT, (boards > 0) ? 0 : 1);
                        }
                        else
                        {
                            int theirSector = (enemySector % 10);
                            int mySector = (theirSector + 2) & 3;
                            filter.npcShip->navigator.combatStateCounter =
                                MakePattern((int)AttackingMode.Orbit, mySector);
                        }

                        break;

                    case NavigationMode.Flanking:
                        filter.npcShip->navigator.navigationMode =
                            close ? NavigationMode.Attacking : NavigationMode.Flanking;
                        if (close)
                            filter.npcShip->navigator.combatStateCounter =
                                MakePattern((int)AttackingMode.SternRake, boards > 0 ? 0 : 1);
                        else
                            filter.npcShip->navigator.combatStateCounter =
                                MakePattern((int)AttackingMode.Orbit, boards > 0 ? 3 : 1);
                        break;

                    default:
                        filter.npcShip->navigator.navigationMode =
                            close ? NavigationMode.Attacking : NavigationMode.Pursuing;
                        filter.npcShip->navigator.combatStateCounter =
                            MakePattern((int)AttackingMode.Orbit, boards > 0 ? 1 : 3);
                        break;
                }
            }
            else
            {
                // Vs players: your original simple aggressive mix
                if (close)
                {
                    if (rng < FP._0_50)
                    {
                        filter.npcShip->navigator.navigationMode = NavigationMode.Attacking;
                        filter.npcShip->navigator.combatStateCounter =
                            MakePattern((int)AttackingMode.Orbit, boards > 0 ? 1 : 3);
                    }
                    else
                    {
                        filter.npcShip->navigator.navigationMode = NavigationMode.Flanking;
                        filter.npcShip->navigator.combatStateCounter =
                            MakePattern((int)AttackingMode.SternRake, boards > 0 ? 0 : 1);
                    }
                }
                else
                {
                    filter.npcShip->navigator.navigationMode = NavigationMode.Pursuing;
                    filter.npcShip->navigator.combatStateCounter =
                        MakePattern((int)AttackingMode.SternRake, boards > 0 ? 0 : 1);
                }
            }

            // Timers (as before): NPC‑vs‑NPC short; vs player longer
            filter.npcShip->navigator.combatStateTimer = enemyIsNPC
                ? f.Global->RngSession.Next(4, 7)
                : f.Global->RngSession.Next(7, 11);

            filter.npcShip->navigator.speedRegulator = 1;
        }


// --------------------- Maneuver / Avoidance ----------------------

        private void AvoidanceSupervisorUpdate(Frame f, Filter filter)
        {
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");
            Transform3D* worldTx = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);
            PhysicsBody3D* rb = f.Unsafe.GetPointer<PhysicsBody3D>(filter.npcShip->WorldEntity);

            // Strategy timer countdown (if in fallback V1)
            if (filter.npcShip->NavTmp.avoidStrategyTimer > 0)
            {
                filter.npcShip->NavTmp.avoidStrategyTimer -= f.DeltaTime;
                if (filter.npcShip->NavTmp.avoidStrategyTimer <= 0)
                {
                    // Return to Variant-2
                    filter.npcShip->NavTmp.avoidStrategy = 0;
                    filter.npcShip->NavTmp.avoidProgressTimer = 0;
                    filter.npcShip->NavTmp.avoidLockTimer = 0;
                    filter.npcShip->NavTmp.avoidCooldownTimer = 0;
                    filter.npcShip->NavTmp.sampleCommitTimer = 0;
                }

                // While the fallback timer is running, we stay in V1
                return;
            }

            // We're in Variant-2: monitor progress
            // Goal = target if exists, else current patrol node
            FPVector3 goalPos = worldTx->Position + worldTx->Forward * 50; // default forward
            if (f.Unsafe.TryGetPointer<Transform3D>(filter.npcShip->targetRef, out var targetTx) && targetTx != null)
            {
                goalPos = targetTx->Position;
            }
            else
            {
                var path = f.FindAsset<PatrolPath>(filter.npcShip->navigator.patrolPath.Id);
                goalPos = path.path[filter.npcShip->navigator.currentIndex] + filter.npcShip->navigator.centerPoint;
            }

            // Planar distance to goal
            FPVector2 posXZ = new FPVector2(worldTx->Position.X, worldTx->Position.Z);
            FPVector2 goalXZ = new FPVector2(goalPos.X, goalPos.Z);
            FP distNow = FPVector2.Distance(posXZ, goalXZ);

            // Forward blocked?
            bool forwardBlocked = f.Physics3D.Raycast(
                worldTx->Position,
                worldTx->Forward.Normalized,
                (int)NavTuning.RayLengthForward.AsInt,
                NavTuning.RaycastMask,
                QueryOptions.HitStatics
            ).HasValue;

            // Speed ok?
            FP speed = rb->Velocity.Magnitude;
            bool speedOK = speed >= NavTuning.MinMoveSpeed * NavTuning.SPEED_OK_FACTOR;

            // Progress check: did we get closer by at least PROGRESS_EPS?
            FP last = filter.npcShip->NavTmp.avoidLastGoalDist;
            bool hasLast = last > FP._0;
            bool madeProgress = hasLast && (distNow + NavTuning.PROGRESS_EPS < last);

            // Update tracker
            if (!forwardBlocked || speedOK || madeProgress)
            {
                filter.npcShip->NavTmp.avoidProgressTimer = 0;
            }
            else
            {
                filter.npcShip->NavTmp.avoidProgressTimer += f.DeltaTime;
            }

            filter.npcShip->NavTmp.avoidLastGoalDist = distNow;

            // If we failed to make progress under V2 for 30s → fall back to V1 for 30s
            if (filter.npcShip->NavTmp.avoidStrategy == 0 &&
                filter.npcShip->NavTmp.avoidProgressTimer > NavTuning.V2_FAIL_WINDOW)
            {
                filter.npcShip->NavTmp.avoidStrategy = 1; // switch to V1
                filter.npcShip->NavTmp.avoidStrategyTimer = NavTuning.V1_RUN_WINDOW;
                // clear V2 commit so V1 takes control immediately
                filter.npcShip->NavTmp.sampleCommitTimer = 0;
                // reset tracker
                filter.npcShip->NavTmp.avoidProgressTimer = 0;
            }
        }

        private void ManoverUpdate(Frame f, Filter filter)
        {
            if (filter.npcShip->NavTmp.avoidStrategy == 0)
                ManoverUpdate_V1(f, filter);
            else
                ManoverUpdate_V2(f, filter);
        }

        private FP ObstacleAvoidanceAngle(Frame f, Filter filter, Transform3D* worldTx, FP desiredDeg,
            Transform3D* targetTx, bool isCombat)
        {
            if (filter.npcShip->NavTmp.avoidStrategy == 0)
                return ObstacleAvoidanceAngle_V1(f, filter, worldTx);
            else
                return ObstacleAvoidanceAngle_V2(f, filter, worldTx, desiredDeg, targetTx, isCombat);
        }

        private void ManoverUpdate_V1(Frame f, Filter filter)
        {
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");

            // timers
            filter.npcShip->NavTmp.avoidLockTimer -= f.DeltaTime;
            filter.npcShip->NavTmp.avoidCooldownTimer -= f.DeltaTime;

            // sample forward once
            FPVector3 pos = filter.transform->Position;
            FPVector3 fwd = filter.transform->Forward.Normalized;
            bool forwardBlocked = f.Physics3D.Raycast(
                pos, fwd,
                (int)NavTuning.RayLengthForward.AsInt,
                NavTuning.RaycastMask,
                QueryOptions.HitStatics
            ).HasValue;

            // If locked → keep side until forward has been clear for CLEAR_RELEASE_TIME
            if (filter.npcShip->NavTmp.avoidLockTimer > 0)
            {
                filter.npcShip->manoverAngle = filter.npcShip->NavTmp.avoidLockSide;

                if (!forwardBlocked)
                {
                    filter.npcShip->NavTmp.avoidClearTimer += f.DeltaTime;
                    if (filter.npcShip->NavTmp.avoidClearTimer >= FP.FromFloat_UNSAFE(0.35f))
                    {
                        filter.npcShip->NavTmp.avoidLockTimer = 0;
                        filter.npcShip->NavTmp.avoidCooldownTimer = NavTuning.AVOID_COOLDOWN_TIME;
                        filter.npcShip->NavTmp.avoidClearTimer = 0;
                    }
                }
                else
                {
                    filter.npcShip->NavTmp.avoidClearTimer = 0; // lost clarity, keep lock
                }

                return;
            }

            // During short cooldown we refuse to pick a new side (prevents ping‑pong)
            if (filter.npcShip->NavTmp.avoidCooldownTimer > 0)
            {
                filter.npcShip->manoverAngle = 0;
                return;
            }

            // Only lock if forward is actually blocked
            if (forwardBlocked)
            {
                int side = ChooseAvoidSideByClearance(f, filter); // -1 left, +1 right, no goal awareness
                filter.npcShip->NavTmp.avoidLockSide = side;
                filter.npcShip->NavTmp.avoidLockTimer = NavTuning.AVOID_LOCK_TIME;
                filter.npcShip->NavTmp.lastAvoidSign = side;
                filter.npcShip->NavTmp.avoidClearTimer = 0;
                filter.npcShip->manoverAngle = side;
            }
            else
            {
                filter.npcShip->manoverAngle = 0;
            }
        }

        private FP ObstacleAvoidanceAngle_V1(Frame f, Filter filter, Transform3D* worldTx)
        {
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");

            // If locked, produce a fixed angular sidestep from the current sail heading (no goal awareness)
            if (filter.npcShip->NavTmp.avoidLockTimer > 0)
            {
                int s = (filter.npcShip->NavTmp.avoidLockSide >= 0) ? +1 : -1;
                return ClampAngle(filter.npcShip->sail.sailRotation + s * NavTuning.AVOID_OFFSET_DEG);
            }

            // Not locked: if forward is clear → no avoidance
            FPVector3 pos = worldTx->Position;
            FPVector3 fwd = worldTx->Forward.Normalized;

            bool forwardBlocked = f.Physics3D.Raycast(
                pos, fwd,
                (int)NavTuning.RayLengthForward.AsInt,
                NavTuning.RaycastMask,
                QueryOptions.HitStatics
            ).HasValue;

            if (!forwardBlocked)
                return FP_NO_AVOID;

            // Forward blocked: pick the better side by local clearance & hysteresis (no goal)
            int side = ChooseAvoidSideByClearance(f, filter);
            return ClampAngle(filter.npcShip->sail.sailRotation +
                              (side >= 0 ? +NavTuning.AVOID_OFFSET_DEG : -NavTuning.AVOID_OFFSET_DEG));
        }

        private void ManoverUpdate_V2(Frame f, Filter filter)
        {
            filter.npcShip->NavTmp.sampleCommitTimer -= f.DeltaTime;
            if (filter.npcShip->NavTmp.sampleCommitTimer <= 0)
                filter.npcShip->manoverAngle = 0; // release hint
        }

        private FP ObstacleAvoidanceAngle_V2(Frame f, Filter filter, Transform3D* worldTx, FP desiredDeg,
            Transform3D* targetTx, bool isCombat)
        {
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");

            // If committed to a corridor, use it
            if (filter.npcShip->NavTmp.sampleCommitTimer > 0)
                return filter.npcShip->NavTmp.sampleChosenAngle;

            // Build goal direction (XZ)
            FPVector3 goalPos = worldTx->Position + worldTx->Forward * 50;
            if (targetTx != null) goalPos = targetTx->Position;
            else
            {
                var path = f.FindAsset<PatrolPath>(filter.npcShip->navigator.patrolPath.Id);
                goalPos = path.path[filter.npcShip->navigator.currentIndex] + filter.npcShip->navigator.centerPoint;
            }

            FPVector3 gdir = goalPos - worldTx->Position;
            gdir.Y = 0;
            gdir = gdir.SqrMagnitude > 0 ? gdir.Normalized : worldTx->Forward;

            // Planar forward/right
            FPVector3 fwd = worldTx->Forward;
            fwd.Y = 0;
            fwd = fwd.Normalized;
            FPVector3 right = worldTx->Right;
            right.Y = 0;
            right = right.Normalized;

            // Evaluate 5 corridors: forward, hard L/R, slight L/R
            int maxF = (int)NavTuning.RayLengthForward.AsInt;

            FP bestScore = FP.MinValue;
            int bestIdx = 0;
            FPVector3 bestDir = fwd;

            // helper to evaluate a single direction
            void EvalDir(int idx, FPVector3 dir)
            {
                var hit = f.Physics3D.Raycast(worldTx->Position, 
                    dir, maxF, NavTuning.RaycastMask, QueryOptions.HitStatics);
                FP clearance = hit.HasValue
                    ? FPVector3.Distance(hit.Value.Point, worldTx->Position)
                    : NavTuning.RayLengthForward;
                FP align = FPMath.Max(FP._0, FPVector3.Dot(dir, gdir));
                FP score = clearance + NavTuning.ALIGN_WEIGHT * NavTuning.RayLengthForward * align;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = idx;
                    bestDir = dir;
                }
            }

            EvalDir(0, fwd);
            EvalDir(1, (fwd - right).Normalized); // hard left
            EvalDir(2, (fwd - right * FP._0_50).Normalized); // slight left (~30°)
            EvalDir(3, (fwd + right * FP._0_50).Normalized); // slight right
            EvalDir(4, (fwd + right).Normalized); // hard right

            // If forward was best and actually clear, no avoidance needed
            bool forwardClear = !f.Physics3D
                .Raycast(worldTx->Position, fwd, maxF, NavTuning.RaycastMask, QueryOptions.HitAll).HasValue;
            if (bestIdx == 0 && forwardClear)
                return FP_NO_AVOID;

            // Commit to best corridor
            FPVector3 aimPoint = worldTx->Position + bestDir * NavTuning.AVOID_RETURN;
            FP chosen = CalculateAngleToTarget(worldTx->Position, aimPoint);

            filter.npcShip->NavTmp.sampleChosenAngle = chosen;
            filter.npcShip->NavTmp.sampleCommitTimer = NavTuning.SAMPLE_COMMIT_TIME;
            filter.npcShip->manoverAngle = (FPVector3.Dot(bestDir, right) >= 0) ? 1 : -1;

            // Cap away-from-target deflection in combat
            if (isCombat && targetTx != null)
            {
                FP targetDeg = CalculateAngleToTarget(worldTx->Position, targetTx->Position);
                FP delta = ClampAngle(chosen - targetDeg);
                if (FPMath.Abs(delta) > NavTuning.AVOID_CAP_COMBAT)
                {
                    chosen = ClampAngle(targetDeg + FPMath.Sign(delta) * NavTuning.AVOID_CAP_COMBAT);
                    filter.npcShip->NavTmp.sampleChosenAngle = chosen;
                }
            }

            return chosen;
        }
        
        
// --------------------- Helpers ----------------------

        private static int MakePattern(int id, int arg) => id * 10 + FPMath.Clamp(arg, 0, 9);
        private static int PatternId(int packed) => packed / 10;

        private static int PatternArg(int packed) => packed % 10;

        private static int BoardSign(Transform3D* self, Transform3D* target)
        {
            FPVector3 toSelf = self->Position - target->Position;
            FP s = FPVector3.Dot(target->Right, toSelf.Normalized);
            return (s >= 0) ? +1 : -1;
        }

        private FPVector3 ClampAroundTarget(FPVector3 targetPos, FPVector3 anchor, FP desiredRadius)
        {
            FPVector3 off = anchor - targetPos;
            off.Y = 0;
            if (off.SqrMagnitude <= FP._0) return targetPos + FPVector3.Forward * desiredRadius;
            return targetPos + off.Normalized * desiredRadius;
        }

        private FP SteeringLerp(FP currentDeg, FP targetDeg, FP k)
        {
            FP curRad = currentDeg * FP.Deg2Rad;
            FP tarRad = targetDeg * FP.Deg2Rad;
            return FPMath.LerpRadians(curRad, tarRad, k) * FP.Rad2Deg;
        }


        /*private bool IsEnemyNPC(NPCShip* self, NPCShip* other)
        {
            if (other == null) return false;
            if (self->spawnPoint.faction == PlayerFaction.None) return false; 
            return other->spawnPoint.faction != self->spawnPoint.faction;
        }

        private bool IsAllyNPC(NPCShip* self, NPCShip* other)
        {
            if (other == null) return false;
            if (self->spawnPoint.faction == PlayerFaction.None) return false;
            return other->spawnPoint.faction == self->spawnPoint.faction;
        }

        private bool InLocalFightArea(FPVector3 pos, FPVector2 centerXZ, FP radius)
        {
            FPVector2 p = new FPVector2(pos.X, pos.Z);
            return FPVector2.Distance(p, centerXZ) <= radius;
        }*/

        // Returns true if we handled an out-of-bounds NPC this tick
        private bool NPCBoundaryReturn(Frame f, Filter filter)
        {
            Transform3D* worldTx = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);
            PhysicsBody3D* worldRb = f.Unsafe.GetPointer<PhysicsBody3D>(filter.npcShip->WorldEntity);

            // --- Match the player's boundary logic ---
            int radius = ShipSystem.PillageBoundsRadius - 500;
            if (f.RuntimeConfig.Mode == ModeType.Match && f.RuntimeConfig.GameType == GameType.Deathmatch)
                radius = ShipSystem.DeathmatchBoundsRadius;

            FP distanceSqr = FPVector2.DistanceSquared(FPVector2.Zero,
                new FPVector2(worldTx->Position.X, worldTx->Position.Z));

            if (distanceSqr <= radius * radius)
                return PushBackFromSpawnAreas(f, filter.npcShip,
                    filter.transform); // inside bounds → no special handling

            // Same wall behavior as player (assumes this is accessible; if it's in another class, call that one)
            ShipSystem.EnableInnerMovementOnly(worldTx, worldRb);

            // --- Drive back toward center (0, 0) explicitly this tick ---
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");

            FP returnAngle = CalculateAngleToTarget(worldTx->Position, FPVector3.Zero);
            FP lerpK = NavTuning.SteeringLerpChase * filter.npcShip->navigator.chaseSpeed * GetSlowSpeedStack(filter);

            // Steer via the normal sail-heading pathway (keeps your physics loop intact)
            filter.npcShip->sail.sailRotation = SteeringLerp(filter.npcShip->sail.sailRotation, returnAngle, lerpK);

            // Give them power to actually re-enter
            filter.npcShip->navigator.speedRegulator = 1;
            filter.npcShip->sail.numberOfSailsOpened = 3;

            // Temporarily suspend combat/patterns so the return isn't overridden this frame
            filter.npcShip->targetRef = EntityRef.None;
            filter.npcShip->navigator.navigationMode = NavigationMode.Manovering; // lightweight "I’m correcting course"
            filter.npcShip->navigator.combatStateTimer = FP._0_50; // short cooldown before patterns resume

            return true;
        }

        private bool FormationHoldGuard(Frame f, Filter filter, Transform3D* worldTx, Transform3D* targetTx)
        {
            if (filter.npcShip->NavTmp.minionHoldActive == 0) return false;

            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");
            FP ang = CalculateAngleToTarget(worldTx->Position, filter.npcShip->NavTmp.minionHoldPos);
            FP avoid = ObstacleAvoidanceAngle(f, filter, worldTx, ang, targetTx, false);
            FP goal = BlendDesiredWithAvoidance(worldTx, ang, avoid, NavTuning);

            FP k = NavTuning.SteeringLerpBase * filter.npcShip->navigator.turnSpeed;
            filter.npcShip->sail.sailRotation = SteeringLerp(filter.npcShip->sail.sailRotation, goal, k);

            // gentle power while holding
            filter.npcShip->navigator.navigationMode = NavigationMode.Manovering;
            filter.npcShip->navigator.speedRegulator = FP.FromFloat_UNSAFE(0.85f);
            filter.npcShip->sail.numberOfSailsOpened = 2;

            // Arrived near hold pos? small radius
            FPVector3 d = filter.npcShip->NavTmp.minionHoldPos - worldTx->Position;
            d.Y = 0;
            if (d.SqrMagnitude < FP.FromFloat_UNSAFE(35) * FP.FromFloat_UNSAFE(35))
                filter.npcShip->NavTmp.minionHoldActive = 0; // let normal nav take over next tick

            return true;
        }

        // Keep the fight near where it started. Returns true if this tick is fully handled (skip normal nav).
        private bool CombatLeashGuard(Frame f, Filter filter, Transform3D* worldTx, Transform3D* targetTx)
        {
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");
            // If no valid target, clear and do nothing
            if (!f.Exists(filter.npcShip->targetRef))
            {
                filter.npcShip->NavTmp.combatAnchorActive = 0;
                filter.npcShip->NavTmp.combatLeashEngaged = 0;
                return false;
            }

            // Initialize the anchor the first time we engage this target (midpoint at fight start)
            if (filter.npcShip->NavTmp.combatAnchorActive == 0)
            {
                FPVector3 mid = new FPVector3(
                    (worldTx->Position.X + targetTx->Position.X) * FP._0_50,
                    worldTx->Position.Y,
                    (worldTx->Position.Z + targetTx->Position.Z) * FP._0_50
                );
                filter.npcShip->NavTmp.combatAnchor = mid;
                filter.npcShip->NavTmp.combatAnchorActive = 1;
                filter.npcShip->NavTmp.combatLeashEngaged = 0;
            }

            // Planar distance from the arena center
            FPVector2 posXZ = new FPVector2(worldTx->Position.X, worldTx->Position.Z);
            FPVector2 ancXZ = new FPVector2(filter.npcShip->NavTmp.combatAnchor.X,
                filter.npcShip->NavTmp.combatAnchor.Z);
            FP dist = FPVector2.Distance(posXZ, ancXZ);

            FP outer = NavTuning.LEASH_RADIUS + NavTuning.LEASH_MARGIN;
            FP inner = NavTuning.LEASH_RADIUS * NavTuning.LEASH_INNER_FACTOR;

            // If we’re outside the outer boundary → engage the leash
            if (dist > outer)
                filter.npcShip->NavTmp.combatLeashEngaged = 1;

            // While engaged, steer to a recall point at half radius and skip normal combat nav
            if (filter.npcShip->NavTmp.combatLeashEngaged == 1)
            {
                FPVector2 dirXZ = posXZ - ancXZ;
                if (dirXZ.SqrMagnitude > 0) dirXZ = dirXZ.Normalized;

                // Recall point = anchor + direction * (radius * 0.5)
                FPVector2 recallXZ = ancXZ + dirXZ * (NavTuning.LEASH_RADIUS * NavTuning.LEASH_RETURN_FRACTION);
                FPVector3 recallPos = new FPVector3(recallXZ.X, worldTx->Position.Y, recallXZ.Y);

                // Blend with your obstacle avoidance to prevent getting stuck
                FP ang = CalculateAngleToTarget(worldTx->Position, recallPos);
                FP avoid = ObstacleAvoidanceAngle(f, filter, worldTx, ang, targetTx, true);
                FP goal = BlendDesiredWithAvoidance(worldTx, ang, avoid, NavTuning);

                // Strong steering & full power while returning
                FP k = (NavTuning.SteeringLerpChase * filter.npcShip->navigator.chaseSpeed) *
                       NavTuning.LEASH_STEER_BOOST;
                filter.npcShip->sail.sailRotation = SteeringLerp(filter.npcShip->sail.sailRotation, goal, k);
                filter.npcShip->navigator.navigationMode = NavigationMode.Manovering;
                filter.npcShip->navigator.speedRegulator = 1;
                filter.npcShip->sail.numberOfSailsOpened = 3;

                // Once well inside the arena, release and let normal combat resume
                if (dist <= inner)
                    filter.npcShip->NavTmp.combatLeashEngaged = 0;

                return true; // handled this tick
            }

            return false; // not engaged → continue with normal nav
        }

        private bool PushBackFromSpawnAreas(Frame f, NPCShip* ship, Transform3D* shipTransform)
        {
            var gameplay = f.Unsafe.GetPointerSingleton<Gameplay>();

            if (ShipSystem.IsInSpawnBounds(f, shipTransform, gameplay->blueMinSpawnBounds,
                    gameplay->blueMaxSpawnBounds)
                || ShipSystem.IsInSpawnBounds(f, shipTransform, gameplay->redMinSpawnBounds,
                    gameplay->redMaxSpawnBounds))
            {
                Transform3D* shipWorldTransform = f.Unsafe.GetPointer<Transform3D>(ship->WorldEntity);
                PhysicsBody3D* shipWorldRigidbody = f.Unsafe.GetPointer<PhysicsBody3D>(ship->WorldEntity);
                ShipSystem.EnableInnerMovementOnly(shipWorldTransform, shipWorldRigidbody);
                return true;
            }

            return false;
        }

// Blend desired & avoidance by distance-to-threat (inside ObstacleAvoidanceAngle we encode threat factor via ray hit)
        private FP BlendDesiredWithAvoidance(Transform3D* worldTx, FP desiredDeg, FP avoidDeg,
            NavigationTuning NavTuning)
        {
            if (avoidDeg == FP_NO_AVOID) return desiredDeg; // no avoidance
            // Weight by proximity proxy baked in avoidDeg via caller's logic; here just use fixed blend
            FP wAvoid = NavTuning.AvoidWeightFar;
            FP wGoal = NavTuning.DesiredWeight;
            FP blended = ClampAngle(desiredDeg + (avoidDeg - desiredDeg) * wAvoid);
            return blended * wGoal + desiredDeg * (FP._1 - wGoal); // still keep goal as anchor
        }

        private FP ClampAngle(FP deg)
        {
            while (deg > 180) deg -= 360;
            while (deg < -180) deg += 360;
            return deg;
        }

        private int ChooseAvoidSideByClearance(Frame f, Filter filter)
        {
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");
            FPVector3 pos = filter.transform->Position;
            FPVector3 fwd = filter.transform->Forward;
            fwd.Y = 0;
            fwd = fwd.Normalized;
            FPVector3 right = filter.transform->Right;
            right.Y = 0;
            right = right.Normalized;

            // Two feelers per side: a diagonal and a pure lateral
            FPVector3 lDiag = (fwd - right * FP.FromFloat_UNSAFE(0.35f)).Normalized; // ~35° left
            FPVector3 rDiag = (fwd + right * FP.FromFloat_UNSAFE(0.35f)).Normalized; // ~35° right
            FPVector3 lSide = (-right); // lateral
            FPVector3 rSide = (right); // lateral

            // Cast distances (if no hit → full ray length)
            FP lenF = NavTuning.RayLengthForward;
            FP lenS = NavTuning.RayLengthSide;

            FP dL1 = RayClearance(f, pos, lDiag, (int)lenS.AsInt, NavTuning.RaycastMask);
            FP dL2 = RayClearance(f, pos, lSide, (int)lenS.AsInt, NavTuning.RaycastMask);
            FP dR1 = RayClearance(f, pos, rDiag, (int)lenS.AsInt, NavTuning.RaycastMask);
            FP dR2 = RayClearance(f, pos, rSide, (int)lenS.AsInt, NavTuning.RaycastMask);

            // Clearance per side = min(diagonal, lateral). Smaller means closer obstacle.
            FP clearL = FPMath.Min(dL1, dL2);
            FP clearR = FPMath.Min(dR1, dR2);

            // Hysteresis: require the other side to be better by a margin before flipping
            FP flipMargin = NavTuning.RayLengthSide * FP.FromFloat_UNSAFE(0.20f); // 20% better to flip

            if (filter.npcShip->NavTmp.lastAvoidSign == -1)
            {
                // Currently biased LEFT; only flip to right if it's clearly better by margin
                if (clearR > clearL + flipMargin) return +1;
                return -1;
            }
            else if (filter.npcShip->NavTmp.lastAvoidSign == +1)
            {
                // Currently biased RIGHT; only flip to left if it's clearly better by margin
                if (clearL > clearR + flipMargin) return -1;
                return +1;
            }

            // No prior bias: pick the more open side; tie-break with forward surface normal sign
            if (clearR > clearL) return +1;
            if (clearL > clearR) return -1;

            // tie‑break by forward hit normal orientation (no goal)
            var hitF = f.Physics3D.Raycast(pos, fwd,
                (int)lenF.AsInt, NavTuning.RaycastMask, QueryOptions.HitStatics);
            if (hitF.HasValue)
            {
                FP surfTurn = FPVector3.SignedAngle(fwd, hitF.Value.Normal, FPVector3.Up);
                return (surfTurn >= 0) ? +1 : -1; // turn away from the normal
            }

            // truly equal & no forward normal available → keep right by default
            return +1;
        }

        private FP RayClearance(Frame f, FPVector3 origin, FPVector3 dir, int dist, int mask)
        {
            var h = f.Physics3D.Raycast(origin, dir, dist, mask, QueryOptions.HitStatics);
            return h.HasValue ? FPVector3.Distance(origin, h.Value.Point) : (FP)dist;
        }

        private static FPVector3 GetLeadAimPosition(Frame f, Filter filter,
            Transform3D* shooterTx, PhysicsBody3D* shooterRb,
            Transform3D* targetTx)
        {
            // Try to get target velocity regardless of type
            FPVector3 targetVel = FPVector3.Zero;
            if (f.Unsafe.TryGetPointer<Ship>(filter.npcShip->targetRef, out var ship))
            {
                var rb = f.Unsafe.GetPointer<PhysicsBody3D>(ship->WorldEntity);
                targetVel = rb->Velocity;
            }
            else if (f.Unsafe.TryGetPointer<NPCShip>(filter.npcShip->targetRef, out var npc))
            {
                var rb = f.Unsafe.GetPointer<PhysicsBody3D>(npc->WorldEntity);
                targetVel = rb->Velocity;
            }

            return PredictInterceptXZ(shooterTx->Position, shooterRb->Velocity,
                targetTx->Position, targetVel,
                250); // projectile speed in ShootCannons
        }

        private static FPVector3 PredictInterceptXZ(FPVector3 shooterPos, FPVector3 shooterVel,
            FPVector3 targetPos, FPVector3 targetVel,
            FP projSpeed)
        {
            // 2D (XZ) intercept solve: (v_rel^2 - s^2)t^2 + 2 r·v_rel t + r^2 = 0
            FP rx = targetPos.X - shooterPos.X;
            FP rz = targetPos.Z - shooterPos.Z;
            FP vx = targetVel.X - shooterVel.X;
            FP vz = targetVel.Z - shooterVel.Z;

            FP a = vx * vx + vz * vz - projSpeed * projSpeed;
            FP b = 2 * (rx * vx + rz * vz);
            FP c = rx * rx + rz * rz;

            FP t;
            FP eps = FP._0_01; // small
            if (FPMath.Abs(a) < eps)
            {
                // almost linear; pick a small positive time
                t = (FPMath.Abs(b) < eps) ? FP._0 : FPMath.Max(-c / b, FP._0);
            }
            else
            {
                FP disc = b * b - 4 * a * c;
                if (disc <= 0) t = FP._0;
                else
                {
                    FP sqrt = FPMath.Sqrt(disc);
                    FP t1 = (-b - sqrt) / (2 * a);
                    FP t2 = (-b + sqrt) / (2 * a);
                    // choose the smallest positive time
                    t = (t1 > 0 && t2 > 0)
                        ? FPMath.Min(t1, t2)
                        : (t1 > 0 ? t1 : (t2 > 0 ? t2 : FP._0));
                }
            }

            return new FPVector3(
                targetPos.X + targetVel.X * t,
                targetPos.Y, // keep current elevation (your cannons are mostly planar)
                targetPos.Z + targetVel.Z * t
            );
        }

// --------------------- Stuck Watchdog ----------------------

        private void StuckWatchdog(Frame f, Filter filter)
        {
            PhysicsBody3D* rb = f.Unsafe.GetPointer<PhysicsBody3D>(filter.npcShip->WorldEntity);
            var NavTuning = f.FindAsset<NavigationTuning>("Resources/DB/NPCShipDataAssets/NavigationTuning");
            // If moving slowly, accumulate stuck time
            if (rb->Velocity.Magnitude < NavTuning.MinMoveSpeed)
                filter.npcShip->NavTmp.stuckTimer += f.DeltaTime;
            else
                filter.npcShip->NavTmp.stuckTimer = 0;

            // If we triggered, force a brief unstick behavior: a quick jink + reset avoidance side
            if (filter.npcShip->NavTmp.stuckTimer > NavTuning.StuckTime)
            {
                filter.npcShip->NavTmp.unstickTimer = NavTuning.UnstickBurstTime;
                filter.npcShip->NavTmp.stuckTimer = 0;

                // Flip last avoid sign to try the other way
                filter.npcShip->NavTmp.lastAvoidSign = (filter.npcShip->NavTmp.lastAvoidSign >= 0) ? -1 : +1;

                // Nudge sail rotation immediately
                FP jink = NavTuning.UnstickJinkAngleDeg * (filter.npcShip->NavTmp.lastAvoidSign >= 0 ? 1 : -1);
                filter.npcShip->sail.sailRotation = ClampAngle(filter.npcShip->sail.sailRotation + jink);

                // Slightly reduce speed regulator to help tight turn
                filter.npcShip->navigator.speedRegulator = FP.FromFloat_UNSAFE(0.85f);
            }

            // Countdown the unstick "burst"
            if (filter.npcShip->NavTmp.unstickTimer > 0)
            {
                filter.npcShip->NavTmp.unstickTimer -= f.DeltaTime;
                // While in burst, bias the mode to Manovering so avoidance dominates
                filter.npcShip->navigator.navigationMode = NavigationMode.Manovering;
            }
        }

        private void CannonsUpdate(Frame f, Filter filter)
        {
            if (!f.Exists(filter.npcShip->targetRef)) return;

            Transform3D* selfTx = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);
            PhysicsBody3D* selfRb = f.Unsafe.GetPointer<PhysicsBody3D>(filter.npcShip->WorldEntity);
            Transform3D* tgtTx = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->targetRef);

            filter.npcShip->cannonCooldown -= f.DeltaTime;

            // distance gate
            FP distanceSqr = (tgtTx->Position - selfTx->Position).SqrMagnitude;
            if (distanceSqr < 0) return;

            bool inRange = distanceSqr <
                           filter.npcShip->navigator.attackDistance * filter.npcShip->navigator.attackDistance;
            if (!inRange) return;

            // Predict where to shoot (XZ lead)
            FPVector3 predictedPos = GetLeadAimPosition(f, filter, selfTx, selfRb, tgtTx);

            // Decide which side is presented (broadside arcs)
            FPVector3 toPred = (predictedPos - selfTx->Position).Normalized;
            FP angle = FPVector3.SignedAngle(selfTx->Forward, toPred, selfTx->Up);

            // Optional: narrower shoot window when farther (improves hit quality)
            FP dist = FPMath.Sqrt(distanceSqr);
            FP baseShoot = 30;
            var isShip = f.Unsafe.TryGetPointer<Ship>(filter.npcShip->targetRef, out var ship);
            if (isShip) baseShoot = f.FindAsset<ShipData>(ship->ShipData.Id).ShootAngle;
            FP shootAngle = FPMath.Clamp(baseShoot - FPMath.Min(dist / 20, 10), 15, 45); // 15..45°

            bool shoot = true;
            int cannonIndex = 0;
            if (angle >= 90 - shootAngle && angle <= 90) cannonIndex = 0; // Front Left
            else if (angle > 90 && angle <= 90 + shootAngle) cannonIndex = 1; // Rear Left
            else if (angle <= -(90 - shootAngle) && angle >= -90) cannonIndex = 2; // Front Right
            else if (angle < -90 && angle >= -(90 + shootAngle)) cannonIndex = 3; // Rear Right
            else shoot = false;

            if (shoot && filter.npcShip->cannonCooldown <= 0)
            {
                AttackTarget(f, filter, cannonIndex); // ShootCannons now also uses lead prediction internally
                NPCShipData npcShipData = f.FindAsset<NPCShipData>(filter.npcShip->npcShipData.Id);
                filter.npcShip->cannonCooldown =
                    f.Global->RngSession.Next(npcShipData.MinWeaponCooldown, npcShipData.MaxWeaponCooldown);
            }
        }

        private void SunkUpdate(Frame f, Filter filter)
        {
            if (filter.npcShip->Hull > 0)
            {
                return;
            }

            Transform3D* visibleTransform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->VisibleEntity);
            PhysicsBody3D* worldRigidbody = f.Unsafe.GetPointer<PhysicsBody3D>(filter.npcShip->WorldEntity);
            //Transform3D* worldTransform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);

            worldRigidbody->Velocity = FPVector3.Lerp(worldRigidbody->Velocity, FPVector3.Zero, f.DeltaTime * 1);
            worldRigidbody->Velocity = FPVector3.Zero;
            //worldTransform->Position.X = FPMath.Lerp(worldTransform->Position.X, f.Global->ExtractionLocation.X, f.DeltaTime * 1);
            //worldTransform->Position.Z = FPMath.Lerp(worldTransform->Position.Z, f.Global->ExtractionLocation.Y, f.DeltaTime * 1);
            // worldTransform->Rotate(FPVector3.Up * f.DeltaTime * 15);

            if (visibleTransform->EulerAngles.X > -30)
            {
                visibleTransform->Rotate(FPVector3.Right * f.DeltaTime * -15);
            }

            if (visibleTransform->EulerAngles.X <= -15)
            {
                if (visibleTransform->Position.Y > -100)
                {
                    filter.npcShip->DeathSink -= f.DeltaTime + f.DeltaTime * filter.npcShip->DeathSink;
                    FP rate = filter.npcShip->DeathSink / 40;
                    visibleTransform->Position.Y -= 10 * f.DeltaTime;
                    // visibleTransform->Rotation -= FPQuaternion.Euler(60*f.DeltaTime,0,0);
                }
            }

            if (visibleTransform->Position.Y < -50)
            {
                DestroyShip(f, filter.npcShip);
            }
        }

        public void OnCollisionEnter3D(Frame f, CollisionInfo3D info)
        {
            if (f.Unsafe.TryGetPointer<ShipWorld>(info.Entity, out var NpcshipWorld))
            {
                if (!f.Has<ShipImpact>(info.Other))
                {
                    // Log.Error("Ramming no ship Impact Found " + info.Other);
                    return;
                }

                var NPCsshipTransform = f.Unsafe.GetPointer<Transform3D>(NpcshipWorld->ShipVisible);
                var NPCsship = f.Unsafe.GetPointer<NPCShip>(NpcshipWorld->ShipVisible);
                var shipImpact = f.Unsafe.GetPointer<ShipImpact>(info.Other);
                var shipComponent = f.Unsafe.GetPointer<Ship>(shipImpact->ShipVisible);
                var shipBody = f.Unsafe.GetPointer<PhysicsBody3D>(shipComponent->WorldEntity);

                if (NPCsship->spawnPoint.faction == shipComponent->faction || shipComponent->recieveRamDamageTimer > 0)
                {
                    return;
                }

                //FPVector3 WorldVeloctyDiff = shipBody->Velocity;
                //FPVector3 LocalCollisionImpulse = NPCsshipTransform->InverseTransformDirection(WorldVeloctyDiff).Normalized;

                //var damage = FPMath.CeilToInt(FPMath.Lerp(4, 15, 1 - ShipSystem.CompanyFullness(f, shipComponent)));
                //FP rearDamage = 0;
                //if (LocalCollisionImpulse.Z < 1)
                //{
                //    rearDamage += FPMath.Abs(LocalCollisionImpulse.Z);
                //}

                //FP sideDamage = FPMath.Abs(LocalCollisionImpulse.X);
                //FP damageDealt = (rearDamage + sideDamage) *  60;
                FP damageDealt = 30;

                //damageDealt = shipBody->Velocity.Magnitude * FP._0_50;


                shipComponent->recieveRamDamageTimer = 1;
                DamageShip(f, NPCsship, shipComponent, damageDealt, PlayerRef.None, WeaponType.DefaultCannon, false);
                //Log.Error("Rammed" + info.Other);
                if (NPCsship->navigator.navigationMode == NavigationMode.Ramming)
                {
                    //Log.Error("Ramming Done");
                    NPCsship->navigator.combatStateTimer = 0;
                }
            }
        }

        private void ApplyWindForce(Frame f, Filter filter)
        {
            Transform3D* transform = f.Unsafe.GetPointer<Transform3D>(filter.npcShip->WorldEntity);
            PhysicsBody3D* rb = f.Unsafe.GetPointer<PhysicsBody3D>(filter.npcShip->WorldEntity);
            FPVector3 worldForce = transform->TransformDirection(GetSailForce(filter.npcShip));
            rb->Velocity += worldForce;
            // rb->Velocity = FPVector3.Lerp(rb->Velocity, rb->Velocity + worldForce, f.DeltaTime);

            rb->Velocity = FPVector3.ClampMagnitude(rb->Velocity,
                filter.npcShip->sail.maxForce * filter.npcShip->navigator.speedRegulator);

            FPVector3 eulerAngle = transform->Rotation.AsEuler;
            eulerAngle.Y = FPMath.LerpRadians(
                eulerAngle.Y * FP.Deg2Rad,
                (eulerAngle.Y + GetSailRotationDifference(filter.npcShip, transform)) * FP.Deg2Rad,
                filter.npcShip->sail.rateOfRotation
            ) * FP.Rad2Deg;

            transform->Rotation = FPQuaternion.Euler(eulerAngle);
            transform->Position.Y = 2;
        }
/*
        private bool GetIsWithinSight(Transform3D* transform, Transform3D* target, NPCShipNavigator navigator)
        {
            if (target == null)
                return false;
            bool result = FPVector3.Distance(transform->Position, GetPointOnCircle(transform, target, navigator.targetSightRadius)) < navigator.targetSightRadius;
            if (!result) navigator.hasReachedTarget = false;
            return result;
        }

        private bool GetIsWithinRange(Transform3D* transform, Transform3D* target, NPCShipNavigator navigator)
        {
            if (target == null)
                return false;
            bool result = FPVector3.Distance(transform->Position, GetPointOnCircle(transform, target, navigator.targetRadius)) < navigator.targetRangeRadius;
            if (result) navigator.hasReachedTarget = true;
            return result;
        }



        private FP CalculateAnglePerpendicularToTarget(Transform3D* transform, Transform3D* targetTransform)
        {
            FPVector3 direction = transform->Position - targetTransform->Position;
            FPQuaternion rotation = FPQuaternion.Euler(0, 90, 0);
            FPVector3 rotatedDirection = rotation * direction;
            FP angle = FPMath.Atan2(rotatedDirection.Z, -rotatedDirection.X) * FP.Rad2Deg;
            return angle;
        }

        public FPVector3 GetPointOnCircle(Transform3D* transform, Transform3D* targetTransform, FP radius)
        {
            FPVector3 directionToTarget = targetTransform->Position - transform->Position;
            if (directionToTarget.Magnitude > radius) directionToTarget = directionToTarget.Normalized * radius;
            FPVector3 pointOnCircle = targetTransform->Position - directionToTarget;
            return pointOnCircle;
        }
*/

        private bool GetWithinPatrolRange(Filter filter, PatrolPath patrolPath)
        {
            filter.npcShip->distanceToPatrolTarget = FPVector3.Distance(filter.transform->Position, GetPatrolPoint(filter.npcShip, patrolPath));
            //Log.Error("	petrol distance "+ distance);
            return FPVector3.Distance(filter.transform->Position, GetPatrolPoint(filter.npcShip, patrolPath)) <
                   filter.npcShip->navigator.patrolDistance;
        }

        private FP CalculateAngleToTarget(FPVector3 position, FPVector3 targetPosition)
        {
            FPVector3 direction = position - targetPosition;
            FPQuaternion rotation = FPQuaternion.Euler(0, 90, 0);
            FPVector3 rotatedDirection = rotation * direction;
            FP angle = FPMath.Atan2(rotatedDirection.Z, -rotatedDirection.X) * FP.Rad2Deg;
            return angle;
        }

        public override void OnInit(Frame f)
        {
            base.OnInit(f);
            f.PhysicsSceneSettings->CCDSettings.AllowCCD = true;
        }

        private void UpdateSlowSpeedStack(Frame f, Filter filter)
        {
            filter.npcShip->slowSpeedTimer -= f.DeltaTime;
            if (filter.npcShip->slowSpeedTimer < 0)
            {
                filter.npcShip->slowSpeedStack -= FPMath.Max(filter.npcShip->slowSpeedStack, 100);
                filter.npcShip->slowSpeedTimer += 1;
            }
        }

        private FP GetSlowSpeedStack(Filter filter)
        {
            return 1 - (filter.npcShip->slowSpeedStack / 10000);
        }

        private static readonly FP FP_NO_AVOID = 9999; // impossible heading value
    }
}