using Photon.Deterministic;

namespace Quantum
{
    public unsafe partial struct KrakenTentacles
    {
        public void Update(Frame f, EnemySystem.Filter kraken, EntityRef player)
        {
            SpawnWait -= f.DeltaTime;
            if (SpawnWait < 0)
            {
                int index = 0;
                //while (tentacles.Next(out var checktentacle, out var playerLink))
                //foreach (var checktentacle in tentacles)
                for (; index < Constants.KRAKEN_TENTACLES; index++)
                {
                    if (!f.Exists(tentacles[index]))
                    {
                        break;
                    }
                }

                if (index < Constants.KRAKEN_TENTACLES)
                {
                    tentacles[index] = SpawnTentacle(f, kraken.transform->Position, spawnRadius);
                    Tentacle* tentacle = f.Unsafe.GetPointer<Tentacle>(tentacles[index]);
                    tentacle->currentState = TentacleState.Idle;
                    tentacle->Kraken = kraken.Entity;
                    tentacle->stateTime = f.Global->RngSession.Next(minTimeToHide, maxTimeToHide);
                    SpawnWait = f.Global->RngSession.Next(5, 10);
                }
            }

            Transform3D* playerT = f.Unsafe.GetPointer<Transform3D>(player);
            FP distanceToTarget = FPVector3.Distance(playerT->Position, kraken.transform->Position);
            for (int i = 0; i < Constants.KRAKEN_TENTACLES; i++)
            {
                if (f.Exists(tentacles[i]))
                {
                    Tentacle* tentacle = f.Unsafe.GetPointer<Tentacle>(tentacles[i]);
                    tentacle->Update(f, tentacles[i], this, player);
                }
            }
            //Log.Error("current far distance " + distanceToTarget);
            if (distanceToTarget > spawnRadius)
            {
                for (int i = 0; i < Constants.KRAKEN_TENTACLES; i++)
                {
                    if (!f.Exists(tentacles[i]))
                    {
                        continue;
                    }

                    Tentacle* tentacle = f.Unsafe.GetPointer<Tentacle>(tentacles[i]);
                    Transform3D* tentacleT = f.Unsafe.GetPointer<Transform3D>(tentacles[i]);
                    tentacle->attackPlayer = false;
                    if (tentacle->currentState == TentacleState.Idle)
                    {
                        tentacle->stateTime -= f.DeltaTime;
                        if (tentacle->stateTime < 0)
                        {
                            tentacle->stateTime = f.Global->RngSession.Next(minTimeToHide, maxTimeToHide);
                            tentacle->currentState = TentacleState.Hidden;
                            f.Events.TentacleHide(tentacles[i]);
                        }
                    }
                    else if (tentacle->currentState == TentacleState.Hidden)
                    {
                        tentacle->stateTime -= f.DeltaTime;
                        if (tentacle->stateTime < 0)
                        {
                            tentacle->stateTime = f.Global->RngSession.Next(minTimeToStay, maxTimeToStay);
                            var enemySpawn = f.Unsafe.GetOrAddSingletonPointer<EnemySpawn>();
                            tentacleT->Position = kraken.transform->Position +
                                                  enemySpawn->GetRandomPosition(f, spawnRadius);
                            tentacle->currentState = TentacleState.Idle;
                            f.Events.TentacleSpawn(tentacles[i]);
                        }
                    }
                }

                EmemyInRange = false;
            }
            else
            {
                if (!EmemyInRange)
                {
                    for (int i = 0; i < Constants.KRAKEN_TENTACLES; i++)
                    {
                        if (!f.Exists(tentacles[i]))
                        {
                            continue;
                        }
                        Tentacle* tentacle = f.Unsafe.GetPointer<Tentacle>(tentacles[i]);
                        if (tentacle->currentState == TentacleState.Idle)
                        {
                            tentacle->stateTime = tauntAnimationTime;
                            tentacle->currentState = TentacleState.Taunting;
                            f.Events.EnemyTaunt(tentacles[i]);
                        }
                    }

                    f.Events.EnemyTaunt(kraken.Entity);
                    EmemyInRange = true;
                }


                checkDistanceAfter -= f.DeltaTime;
                if (checkDistanceAfter < 0)
                {
                    int neartentacles = 0;
                    EntityRef nearestTentacle = default;
                    var tentacleDistanceToTarget = kraken.ai->stats.baseAttackRange * kraken.ai->stats.baseAttackRange;
                    for (int j = 0; j < Constants.KRAKEN_TENTACLES; j++)
                    {
                        if (!f.Exists(tentacles[j]))
                        {
                            continue;
                        }
                        Tentacle* thistentacle = f.Unsafe.GetPointer<Tentacle>(tentacles[j]);
                        if (thistentacle->currentState == TentacleState.Idle
                            || thistentacle->currentState == TentacleState.Hidden)
                        {
                            if (thistentacle->attackPlayer)
                            {
                                neartentacles++;
                                continue;
                            }

                            Transform3D* thistentacleT = f.Unsafe.GetPointer<Transform3D>(tentacles[j]);
                            FP distanceSqr = FPVector3.DistanceSquared(playerT->Position, thistentacleT->Position);

                            if (distanceSqr < tentacleDistanceToTarget)
                            {
                                //Log.Error("tentacle found with less distance ");
                                if (tentacleDistanceToTarget < distanceFromTentacles)
                                {
                                    neartentacles++;
                                    //Log.Error("tentacle found skipping");
                                }
                                else
                                {
                                    nearestTentacle = tentacles[j];
                                    tentacleDistanceToTarget = distanceSqr;
                                }
                            }
                        }
                    }

                    //Log.Error("tentacle found " + neartentacles + " -- " + tentacleDistanceToTarget);
                    checkDistanceAfter = FP._0_50;
                    if (neartentacles < 2 && tentacleDistanceToTarget > distanceFromTentacles &&
                        nearestTentacle != default)
                    {
                        // Spawn Tentacle Near
                        Tentacle* nearesttentacle = f.Unsafe.GetPointer<Tentacle>(nearestTentacle);
                        //Log.Error("Attacking " + nearesttentacle->currentState);
                        nearesttentacle->attackPlayer = true;
                        nearesttentacle->stateTime = 5;
                        if (nearesttentacle->currentState == TentacleState.Idle)
                        {
                            nearesttentacle->currentState = TentacleState.Hidden;
                            f.Events.TentacleHide(nearestTentacle);
                            //Log.Error("Hiding it");
                        }
                        /*else if (tentacle->currentState == TentacleState.Hidden)
                        {
                                tentacle->hidingOrSpawningTime = 0;
                                tentacle->currentState = TentacleState.Spawning;
                                f.Events.TentacleSpawn(tentacles[i]);
                                Log.Error("spawning it");
                        }*/
                    }
                }
            }
        }


        private EntityRef SpawnTentacle(Frame f, FPVector3 position, FP radius)
        {
            var prototypeEntity =
                f.FindAsset<EntityPrototype>("Resources/DB/Gauntlet/Enemies/KrakenTentacleEntityPrototype");

            EntityRef enemyInstance = f.Create(prototypeEntity);
            f.Events.EnemySpawn();
            var gameplay = f.Unsafe.GetPointerSingleton<Gameplay>();
            Transform3D* enemyTransform = f.Unsafe.GetPointer<Transform3D>(enemyInstance);
            ComponentFilter<Ship> ships = f.Filter<Ship>();
            EnemyAI* enemyAi = f.Unsafe.GetPointer<EnemyAI>(enemyInstance);
            f.AllocateDictionary(out enemyAi->damageTakenFromPlayers);
            enemyAi->enemyType = EnemyType.KrakenTentacle;
            enemyAi->stats.currentHealth = enemyAi->stats.baseHealth;
            enemyAi->bossRewardPer10PercentHealth = 9;
            enemyAi->delayedAttackDone = true;
            enemyAi->stats.currentDamage = enemyAi->stats.baseDamage;

            if (gameplay->ModifierValues[(int)EnemyModifiers.Health] >= 1)
            {
                FP totalValue = gameplay->ModifierValues[(int)EnemyModifiers.Health];
                enemyAi->stats.currentHealth = (enemyAi->stats.currentHealth +
                                                ((enemyAi->stats.currentHealth *
                                                  GauntletModifiers.challengeHealthModifier) * totalValue));
            }

            if (gameplay->ModifierValues[(int)EnemyModifiers.Damage] >= 1)
            {
                FP totalValue = gameplay->ModifierValues[(int)EnemyModifiers.Damage];
                enemyAi->stats.currentDamage = (enemyAi->stats.currentDamage +
                                                ((enemyAi->stats.currentDamage *
                                                  GauntletModifiers.challengeDamageModifier) * totalValue));
            }

            var enemySpawn = f.Unsafe.GetOrAddSingletonPointer<EnemySpawn>();
            enemyTransform->Position = position + enemySpawn->GetRandomPosition(f, radius);
            return enemyInstance;
        }
    }
}