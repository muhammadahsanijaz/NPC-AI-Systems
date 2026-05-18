using System.Collections.Generic;
using Photon.Deterministic;
using Quantum.Task;

namespace Quantum
{
    public unsafe class EnemySystem : SystemMainThreadFilter<EnemySystem.Filter>, ISignalOnCollisionEnter3D
    {
        public struct Filter
        {
            public EntityRef Entity;
            public EnemyAI* ai;
            public GauntletEnemyAnimation* anim;
            public Transform3D* transform;
            public PhysicsCollider3D* collider;
        }

        private void Start(Frame f, Filter filter)
        {
            if (f.Exists(filter.ai->spawnPoint))
            {
                PatrolPath patrolPath = f.FindAsset<PatrolPath>(filter.ai->patrolPath.Id);
                Transform3D* pointTransform = f.Unsafe.GetPointer<Transform3D>(filter.ai->spawnPoint);
                filter.ai->normalTargetPosition =
                    patrolPath.GetCurrentPoint(filter.ai->patrolPathIndex, pointTransform->Position);
                filter.ai->enemyDirectionFollowingPatrolPath =
                    (EnemyDirectionFollowingPatrolPath)f.Global->RngSession.Next(0, 2);
            }
        }

        public override void Update(Frame f, ref Filter filter)
        {
            if (f.RuntimeConfig.Mode != ModeType.Match)
            {
                return;
            }

            if (filter.ai->enemyRank == EnemyRank.Normal && !filter.ai->IsStarted)
            {
                Start(f, filter);
                filter.ai->IsStarted = true;
            }

            // Death Animation
            DeathSink(f, filter);
            if (f.RuntimeConfig.GameType == GameType.Gauntlet)
            {
                var gauntlet = f.Unsafe.GetPointerSingleton<Gauntlet>();
                if (gauntlet->State != GauntletGameplayState.WaveInProgress)
                {
                    return;
                }
            }

            if (filter.ai->isDead)
            {
                return;
            }

            //Sometimes spawn Y position is below 0, not sure why
            FixYSpawn(f, filter);

            //Process damage over time
            if (filter.ai->overTimeDamageTimer > 0)
            {
                TakeDamageOverTime(f, filter);
            }

            //Update timers for attack cooldowns
            UpdateTimers(f, filter);


            //Stop movement and attack cooldowns after attacking to give a better feel after they attack (so they don't suddenly move or attack after recently attacking
            if (filter.ai->stats.currentPauseMovementAfterAttacking > 0)
            {
                filter.ai->stats.currentPauseMovementAfterAttacking -= f.DeltaTime;
                return;
            }

            int currentShips = f.ComponentCount<Ship>();

            if (currentShips == 0)
            {
                return;
            }

            if (filter.ai->shipSearchTime > 1 && filter.ai->lastTimeAttackedByPlayerShip <= 0)
            {
                filter.ai->shipSearchTime = 0;
                FindTarget(f, filter);
            }
            else
            {
                filter.ai->shipSearchTime += f.DeltaTime;
            }

            if (filter.ai->targetShip == default)
            {
                if (filter.ai->stats.baseMovementSpeed != 0 && !filter.ai->forceFollowEnemy)
                {
                    NormalBehaviour(f, filter, filter.ai->isAttacking);
                }
                return;
            }

            if (!f.Unsafe.TryGetPointer<Ship>(filter.ai->targetShip, out var ship))
            {
                return;
            }

            //AnimationTimers
            AnimationTimers(f, filter, filter.ai->targetShip);

            //This is used for the actual attacks indicator since we're not sure how to use Tasks
            if (filter.ai->stats.currentAttackDelayBeforeHit <= 0 && !filter.ai->delayedAttackDone)
            {
                DelayedAttack(f, filter, filter.ai->targetShip);
            }

            Transform3D* playerTargetT = f.Unsafe.GetPointer<Transform3D>(filter.ai->targetShip);
            Transform3D* enemyTransform = filter.transform;
            FP targetAngle = CalculateAngleToTarget(enemyTransform, playerTargetT->Position);

            FP rangeAttackRange = filter.ai->stats.baseAttackRange;
            FP meleeAttackRange = filter.ai->stats.baseMeleeRange;

            FP distanceToTarget = FPVector3.Distance(playerTargetT->Position, enemyTransform->Position);


            bool isWithinTargetRangeAttackRange = distanceToTarget <= rangeAttackRange;
            bool isWithinTargetMeleeAttackRange = distanceToTarget <= meleeAttackRange;

            bool isStationary = filter.ai->stats.baseMovementSpeed == 0;

            if ((distanceToTarget > filter.ai->stats.baseSightRange && filter.ai->lastTimeAttackedByPlayerShip <= 0
               && !filter.ai->forceFollowEnemy) || (filter.ai->lastTimeAttackedByPlayerShip <= 0 && filter.ai->attackOnProvokedOnly))
            {
                if (f.RuntimeConfig.GameType == GameType.Gauntlet)
                {
                    filter.ai->stats.currentMovementSpeed = 30;
                    filter.ai->isAttacking = true;
                }
                else
                {
                    if (!isStationary)
                    {
                        NormalBehaviour(f, filter, filter.ai->isAttacking);
                    }

                    filter.ai->isAttacking = false;
                }
            }
            else
            {
                if (filter.ai->enemyRank == EnemyRank.Boss)
                {
                    filter.ai->isAttacking = true;
                    FP distanceToAnchor = FPVector3.Distance(enemyTransform->Position, filter.ai->normalTargetPosition);
                    bool outOfAnchorRange = distanceToAnchor > filter.ai->stats.baseFollowRange;
                    if (outOfAnchorRange && !filter.ai->forceFollowEnemy)
                    {
                        if (!isStationary)
                        {
                            NormalBehaviour(f, filter, filter.ai->isAttacking);
                        }

                        filter.ai->isAttacking = false;
                    }
                }
                else
                {
                    filter.ai->isAttacking = true;

                    
                    if (filter.ai->attackOnProvokedOnly)
                    {
                        filter.ai->isAttacking = false;
                    }
                    if (filter.ai->lastTimeAttackedByPlayerShip > 0)
                    {
                        filter.ai->lastTimeAttackedByPlayerShip -= f.DeltaTime;
                        filter.ai->isAttacking = true;
                    }

                    if (filter.ai->isAttacking)
                    {
                        filter.ai->stats.currentMovementSpeed = filter.ai->stats.baseMovementSpeed;
                        FaceTarget(f,filter, enemyTransform, targetAngle, filter.ai->stats.FaceTargetSpeed);
                    }
                }
            }


            //if (GauntletSystem.debug)
            //    Log.Error("This working " + filter.ai->attackType + " enemy  " + filter.ai->enemyType);

            if (!filter.ai->isAttackCooldown)
            {
                if (filter.ai->enemyType == EnemyType.Siren)
                {
                    HealEnemies(f, filter);
                }
                else if (filter.ai->isAttacking)
                {
                    //Default delay after enemy has recently rammed player before being able to move again
                    if (filter.ai->hasRecentlyRammed && !isStationary)
                    {
                        if (filter.ai->stats.currentPauseMovementAfterAttacking <= 0)
                        {
                            filter.ai->hasRecentlyRammed = false;
                        }
                    }

                    switch (filter.ai->attackType)
                    {
                        default:
                        case AttackType.None:
                            break;
                        case AttackType.Ram:
                            if (!isWithinTargetMeleeAttackRange && !isStationary)
                            {
                                Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                            }

                            if (isWithinTargetMeleeAttackRange && !filter.ai->isAttackCooldown)
                            {
                                DelayedAttackBehaviour(f, filter, playerTargetT, filter.ai->targetShip);
                            }

                            break;
                        case AttackType.MeleeOnly:
                            if (!isWithinTargetMeleeAttackRange && !isStationary)
                            {
                                Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                            }

                            //Log.Error(filter.ai->enemyType +  " - " + isStationary  + " - " +isWithinTargetShortRange + " - " + filter.ai->isAttackCooldown);
                            if (isWithinTargetMeleeAttackRange && !filter.ai->isAttackCooldown)
                            {
                                bool attack = true;
                                if (filter.ai->enemyType == EnemyType.KrakenTentacle)
                                {
                                    var tentacle = f.Unsafe.GetPointer<Tentacle>(filter.Entity);
                                    if (tentacle->currentState == TentacleState.Hidden)
                                    {
                                        attack = false;
                                    }
                                }

                                if (attack)
                                {
                                    DelayedAttackBehaviour(f, filter, playerTargetT, filter.ai->targetShip);
                                }
                            }

                            break;
                        case AttackType.Range:
                            switch (filter.ai->projectileStats.projectileDirectionType)
                            {
                                default:
                                case ProjectileDirectionType.CanonShot:
                                case ProjectileDirectionType.Straight:
                                    if (isWithinTargetMeleeAttackRange || isWithinTargetRangeAttackRange)
                                    {
                                        RangedAttack(f, filter, playerTargetT, targetAngle, filter.ai->targetShip);
                                    }
                                    else if (!isStationary)
                                    {
                                        Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                                    }

                                    break;
                                case ProjectileDirectionType.Mortar:
                                    if (distanceToTarget < rangeAttackRange)
                                    {
                                        RangedAttack(f, filter, playerTargetT, targetAngle, filter.ai->targetShip);
                                    }
                                    else if (!isStationary)
                                    {
                                        Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                                    }

                                    break;
                            }

                            break;
                        case AttackType.MeleeAndRange:
                            switch (filter.ai->projectileStats.projectileDirectionType)
                            {
                                default:
                                case ProjectileDirectionType.CanonShot:
                                case ProjectileDirectionType.Straight:
                                    if (isWithinTargetMeleeAttackRange)
                                    {
                                        DelayedAttackBehaviour(f, filter, playerTargetT, filter.ai->targetShip);
                                        //Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                                    }
                                    else if (isWithinTargetRangeAttackRange)
                                    {
                                        RangedAttack(f, filter, playerTargetT, targetAngle, filter.ai->targetShip);
                                    }
                                    else if (!isStationary)
                                    {
                                        Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                                    }

                                    break;
                                case ProjectileDirectionType.Mortar:
                                    if (isWithinTargetMeleeAttackRange)
                                    {
                                        DelayedAttackBehaviour(f, filter, playerTargetT, filter.ai->targetShip);
                                        //Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                                    }
                                    else if (distanceToTarget < rangeAttackRange && !isWithinTargetMeleeAttackRange)
                                    {
                                        RangedAttack(f, filter, playerTargetT, targetAngle, filter.ai->targetShip);
                                    }
                                    /*else if (isWithinTargetMeleeAttackRange && !isStationary)
                                    {
                                        Move(f, filter, enemyTransform, distanceToTarget, targetAngle,
                                            filter.ai->FaceTargetSpeed, true);
                                    }*/
                                    else if (!isStationary)
                                    {
                                        Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                                    }

                                    break;
                            }

                            break;
                    }

                    UpdateslowSpeedStack(f,filter);
                    /*
                     if (isWithinTargetShortRange)
                            {
                                DelayedAttackBehaviour(f, filter, playerTargetT, filter.ai->targetShip);
                                //Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                            }
                            else if (isWithinTargetLongRange)
                            {
                                RangedAttack(f, filter, playerTargetT, targetAngle, filter.ai->targetShip);
                            }
                            else if (!isStationary)
                            {
                                Move(f, filter, enemyTransform, distanceToTarget, targetAngle, filter.ai->FaceTargetSpeed);
                            }

                     if ((filter.ai->attackType == AttackType.MeleeOnly && isWithinTargetShortRange) ||
                        (filter.ai->attackType == AttackType.MeleeAndRange && isWithinTargetLongRange &&
                         !isWithinTargetShortRange)
                        || (filter.ai->attackType == AttackType.Range && isWithinTargetLongRange ||
                            isWithinTargetShortRange))
                    {
                        //Face enemy before attacking
                        //FaceTarget(enemyTransform, targetAngle);
                        RangedAttack(f, filter, playerTargetT, targetAngle, filter.ai->targetShip);
                    }
                    else if (!isWithinTargetLongRange &&
                             (filter.ai->attackType == AttackType.MeleeOnly && filter.ai->attackType != AttackType.Ram)
                             && !isStationary)
                    {
                        //Move towards player within minimum range for ranged units
                        Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                    }
                    else if (!filter.ai->hasRecentlyRammed && !isStationary)
                    {
                        // if (filter.ai->attackType == AttackType.Ram)
                        // {
                        //     //Movement for ramming units
                        //     Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                        // }
                        else if (filter.ai->attackType == AttackType.Melee &&
                                 (!isWithinTargetShortRange || !isWithinTargetLongRange))
                        {
                            Move(f, filter, enemyTransform, distanceToTarget, targetAngle);
                        }
                    }*/
                }
            }


            if (filter.ai->enemyType == EnemyType.Kraken)
            {
                var krakenTentacle = f.Unsafe.GetPointer<KrakenTentacles>(filter.Entity);
                krakenTentacle->Update(f, filter, filter.ai->targetShip);
            }
        }


        private void NormalBehaviour(Frame f, Filter filter, bool previouslyAttacking)
        {
            if (previouslyAttacking && filter.ai->patrolPath != null)
            {
                EnemySpawnPoint* point = f.Unsafe.GetPointer<EnemySpawnPoint>(filter.ai->spawnPoint);
                Transform3D* pointTransform = f.Unsafe.GetPointer<Transform3D>(filter.ai->spawnPoint);
                var enemySpawn = f.Unsafe.GetPointerSingleton<EnemySpawn>();
                PatrolPath patrolPath = f.FindAsset<PatrolPath>(filter.ai->patrolPath.Id);
                filter.ai->patrolPathIndex = patrolPath.FindNearestIndex(filter.transform->Position);
                var position = patrolPath.GetCurrentPoint(filter.ai->patrolPathIndex, pointTransform->Position);
                filter.ai->normalTargetPosition = enemySpawn->GetRandomPosition(f, position,
                    point->SpawnInnerRadius, point->SpawnOuterRadius);
                filter.ai->enemyDirectionFollowingPatrolPath =
                    (EnemyDirectionFollowingPatrolPath)f.Global->RngSession.Next(0, 2);
            }

            if (filter.ai->enemyRank == EnemyRank.Normal && f.Exists(filter.ai->spawnPoint))
            {
                if (filter.ai->patrolPath != null)
                {
                    var distance = FPVector3.Distance(filter.ai->normalTargetPosition, filter.transform->Position);
                    if (distance < 10)
                    {
                        /*if (filter.ai->waitForNextPatrolPath > 0)
                        {
                            filter.ai->waitForNextPatrolPath -= f.DeltaTime;
                            filter.ai->stats.currentMovementSpeed = 0;
                        }
                        else
                        {*/
                        // Implementing walk toward random position
                        EnemySpawnPoint* point = f.Unsafe.GetPointer<EnemySpawnPoint>(filter.ai->spawnPoint);
                        Transform3D* pointTransform = f.Unsafe.GetPointer<Transform3D>(filter.ai->spawnPoint);
                        var enemySpawn = f.Unsafe.GetPointerSingleton<EnemySpawn>();
                        PatrolPath patrolPath = f.FindAsset<PatrolPath>(filter.ai->patrolPath.Id);
                        if (filter.ai->enemyDirectionFollowingPatrolPath == EnemyDirectionFollowingPatrolPath.Straight)
                        {
                            filter.ai->patrolPathIndex = (filter.ai->patrolPathIndex + 1) % patrolPath.path.Length;
                        }
                        else
                        {
                            filter.ai->patrolPathIndex = (filter.ai->patrolPathIndex - 1 + patrolPath.path.Length) %
                                                         patrolPath.path.Length;
                        }

                        var position = patrolPath.GetCurrentPoint(filter.ai->patrolPathIndex, pointTransform->Position);
                        filter.ai->normalTargetPosition = enemySpawn->GetRandomPosition(f, position,
                            point->SpawnInnerRadius, point->SpawnOuterRadius);
                        filter.ai->waitForNextPatrolPath = f.Global->RngSession.Next(FP._2, FP._10);
                        if (filter.ai->waitForNextPatrolPath > 7)
                        {
                            filter.ai->enemyDirectionFollowingPatrolPath =
                                (EnemyDirectionFollowingPatrolPath)f.Global->RngSession.Next(0, 2);
                        }
                        //}
                    }
                    else
                    {
                        FP targetAngle = CalculateAngleToTarget(filter.transform, filter.ai->normalTargetPosition);
                        Move(f, filter, filter.transform, distance, targetAngle);
                    }
                }
            }
            else if (filter.ai->enemyRank == EnemyRank.Boss) // behaviour for boss
            {
                // Implementing walk back to center
                var distance = FPVector3.Distance(filter.transform->Position, filter.ai->normalTargetPosition);
                if (distance > 10)
                {
                    FP targetAngle = CalculateAngleToTarget(filter.transform, filter.ai->normalTargetPosition);
                    Move(f, filter, filter.transform, distance, targetAngle);
                }
                else
                {
                    filter.anim->moveSpeed = 0;
                }
            }
        }

        private void DeathSink(Frame f, Filter filter)
        {
            var deathSinkRate = 5;
            if (filter.ai->isDead)
            {
                if (filter.collider->Enabled)
                {
                    filter.collider->Enabled = false;
                }

                filter.transform->Position.Y -= deathSinkRate * f.DeltaTime;
                if (filter.transform->Position.Y <= -50)
                {
                    Log.Debug("sunk " + filter.Entity + filter.transform->Position.Y);
                    f.Destroy(filter.Entity);
                }
            }
        }

        private void AnimationTimers(Frame f, Filter filter, EntityRef shipToHit)
        {
            if (filter.ai->currentAttack1on)
            {
                filter.ai->currentAttack1Timer -= f.DeltaTime;
                if (filter.ai->currentAttack1Timer <= 0)
                {
                    //DelayedAttackCallback(f, filter, f.Unsafe.GetPointer<Transform3D>(filter.ai->targetEntity), filter.ai->targetEntity);
                    DelayedAttack(f, filter, shipToHit);
                    filter.ai->currentAttack1on = false;
                }
            }

            if (filter.ai->currentAttack2on)
            {
                filter.ai->currentAttack2Timer -= f.DeltaTime;
                if (filter.ai->currentAttack2Timer <= 0)
                {
                    ProjectileAttackCallback(f, filter, shipToHit);
                    if (filter.ai->projectileStats.IsConsistantProjectileDelay &&
                        filter.ai->projectileStats.currentConsistantProjectileCount <
                        filter.ai->projectileStats.projectileCount - 1)
                    {
                        filter.ai->currentAttack2Timer += filter.ai->projectileStats.ConsistantProjectileDelay;
                        filter.ai->projectileStats.currentConsistantProjectileCount += 1;
                    }
                    else
                    {
                        filter.ai->projectileStats.currentConsistantProjectileCount = 0;
                        filter.ai->currentAttack2on = false;
                    }
                }
            }
        }

        private void FixYSpawn(Frame f, Filter filter)
        {
            Transform3D* transform = f.Unsafe.GetPointer<Transform3D>(filter.Entity);
            if (transform->Position.Y < 0 || transform->Position.Y > 3)
            {
                transform->Position = new FPVector3(transform->Position.X, 0, transform->Position.Y);
            }
        }

        //Heal enemies
        private void HealEnemies(Frame f, Filter filter)
        {
            if (filter.ai->isAttackCooldown)
            {
                return;
            }

            ComponentFilter<EnemyAI> iterator = f.Filter<EnemyAI>();
            //foreach (var enemyAi in f.Unsafe.GetComponentBlockIterator<EnemyAI>())
            while (iterator.Next(out EntityRef entity, out EnemyAI enemy))
            {
                if (entity != filter.Entity)
                {
                    var enemyTransform = f.Unsafe.GetPointer<Transform3D>(entity);
                    if (FPVector3.Distance(filter.transform->Position, enemyTransform->Position) <
                        filter.ai->stats.baseAttackRange
                        && !enemy.isDead)
                    {
                        HealPercent(f, filter.Entity, entity, filter.ai->stats.baseDamage);
                    }
                }
            }

            f.Events.SirenHeal(filter.Entity);
            filter.ai->isAttackCooldown = true;
            filter.ai->stats.currentAttackCooldown = filter.ai->stats.baseAttackCooldown +
                                                     filter.anim->spawnProjectileDelay +
                                                     filter.anim->spawnProjectileDelayAfter;
        }

        //Movement behaviour
        private void Move(Frame f, Filter filter, Transform3D* enemyTransform, FP distanceToTarget, FP targetAngle,
            bool reverse = false)
        {
            FP movementSpeed = GetModifiedMovementSpeed(f, filter, distanceToTarget);
            filter.anim->moveSpeed = movementSpeed;
            //Move towards target
            enemyTransform->Rotation = FPQuaternion.RotateTowards(enemyTransform->Rotation,
                FPQuaternion.Euler(new FPVector3(enemyTransform->EulerAngles.X, targetAngle,
                    enemyTransform->EulerAngles.Z)), f.DeltaTime * filter.ai->stats.FaceTargetSpeed);
            if (reverse)
            {
                enemyTransform->Position -= enemyTransform->Forward * movementSpeed * f.DeltaTime;
            }
            else
            {
                enemyTransform->Position += enemyTransform->Forward * movementSpeed * f.DeltaTime;
            }
        }

        private FP GetModifiedMovementSpeed(Frame f, Filter filter, FP distanceToTarget)
        {
            var gameplay = f.Unsafe.GetPointerSingleton<Gameplay>();
            FP movementSpeed = filter.ai->stats.baseMovementSpeed * GetSlowSpeedStack(filter);
            //Enemy Portal Modifiers
            if (gameplay->ModifierValues[(int)EnemyModifiers.MoveSpeed] > 0)
            {
                movementSpeed =
                    (movementSpeed * FP._1_50); //todo: set this to 1.5 for now since its not implemented yet
            }

            //If target is too far, increase speed by a certain threshold
            /*if (distanceToTarget > 30)
            {
                movementSpeed = (movementSpeed * FP._1_25);
            }*/

            //Debuffs
            /*if (filter.ai->stats.Debuffs[(int)Perks.Poison] > 0)
            {
                FP quantity = filter.ai->stats.Debuffs[(int)Perks.Poison];
                FP poisonSlow = (GauntletModifiers.poisonDamage / 100) * quantity; //2x of damage
                movementSpeed = movementSpeed - (movementSpeed * poisonSlow);
            }

            if (filter.ai->stats.Debuffs[(int)Perks.Ice] > 0)
            {
                FP quantity = filter.ai->stats.Debuffs[(int)Perks.Ice];
                FP iceSlow = (GauntletModifiers.ice / 100) * 2 * quantity; //2x of damage
                movementSpeed = movementSpeed - (movementSpeed * iceSlow);
            }*/
            return movementSpeed;
        }

        //Updates enemy attacks, timers, etc
        private void UpdateTimers(Frame f, Filter filter)
        {
            if (filter.ai->stats.currentAttackDelayBeforeHit > 0)
            {
                filter.ai->stats.currentAttackDelayBeforeHit -= f.DeltaTime;
            }

            if (filter.ai->isAttackCooldown)
            {
                filter.ai->stats.currentAttackCooldown -= f.DeltaTime;
                if (filter.ai->stats.currentAttackCooldown <= 0)
                {
                    filter.ai->isAttackCooldown = false;
                }
            }

            if (filter.ai->stats.currentPauseMovementAfterAttacking > 0)
            {
                filter.ai->stats.currentPauseMovementAfterAttacking -= f.DeltaTime;
            }
        }

        private void FaceTarget(Frame f, Filter filter,Transform3D* enemyTransform, FP targetAngle, FP faceTargetSpeed)
        {
            enemyTransform->Rotation = FPQuaternion.RotateTowards(enemyTransform->Rotation, FPQuaternion.Euler(
                new FPVector3(enemyTransform->EulerAngles.X, targetAngle,
                    enemyTransform->EulerAngles.Z)), f.DeltaTime * faceTargetSpeed * GetSlowSpeedStack(filter));
        }

        //Ranged attack controller
        private void RangedAttack(Frame f, Filter filter, Transform3D* playerTargetT, FP angleToShip,
            EntityRef playerTargetEntity)
        {
            if (filter.ai->isAttackCooldown)
            {
                return;
            }

            if (filter.ai->attackType == AttackType.Range || filter.ai->attackType == AttackType.MeleeAndRange)
            {
                //Attack for shooting the ship directly'
                ProjectileAttackBehaviour(f, filter, playerTargetT, angleToShip, playerTargetEntity);
            }
        }

        //Projectile attacks
        private void ProjectileAttackBehaviour(Frame f, Filter filter, Transform3D* targetTransform, FP angleToShip,
            EntityRef targetEntity)
        {
            filter.ai->isAttackCooldown = true;
            filter.ai->stats.currentAttackCooldown =
                filter.ai->stats.baseAttackCooldown + filter.anim->spawnProjectileDelay +
                filter.anim->spawnProjectileDelayAfter;
            filter.ai->currentAttack2Timer = filter.anim->spawnProjectileDelay;
            filter.ai->attackAngleToShip = angleToShip;
            filter.ai->currentAttack2on = true;
            f.Events.EnemyAttack2(filter.Entity);
        }

        private void ProjectileAttackCallback(Frame f, Filter filter, EntityRef target)
        {
            FP projectileCount = filter.ai->projectileStats.projectileCount;

            FP currentRot = 0;// = (projectileCount % 2 == 1) ? 0 : 0;
            FP rotationPerShot = 15;
            if (filter.ai->projectileStats.IsConsistantProjectileDelay)
            {
                projectileCount = 1;
            }
            else if (filter.ai->projectileStats.projectileDirectionType == ProjectileDirectionType.Around)
            {
                rotationPerShot = 360 / projectileCount;
            }

            FP bulletDamage = filter.ai->stats.baseDamage;
            Transform3D* playerTargetT = f.Unsafe.GetPointer<Transform3D>(target);
            Transform3D* enemyTransform = filter.transform;
            FP targetAngle = CalculateAngleToTarget(enemyTransform, playerTargetT->Position);

            int halfCount = (int)projectileCount / 2;
            currentRot = -rotationPerShot * halfCount;

            //Spawn 3 projectiles in a cone pattern
            for (int i = 0; i < projectileCount; i++)
            {
                var prototypeEntity = GetEnemyProjectileEntity(f, filter);
                var projectile = f.Create(prototypeEntity);
                Transform3D* projectileT = f.Unsafe.GetPointer<Transform3D>(projectile);
                GauntletProjectile* proj = f.Unsafe.GetPointer<GauntletProjectile>(projectile);

                //var playerEntity = GetPlayer(f);
                //Transform3D* shipTransform = f.Unsafe.GetPointer<Transform3D>(playerEntity);
                proj->ShooterEntity = filter.Entity;
                FPVector3 adjustedRotation = new FPVector3(0, targetAngle + currentRot, 0);
                projectileT->Rotation = FPQuaternion.Euler(adjustedRotation);
                proj->directionType = filter.ai->projectileStats.projectileDirectionType;
                if (filter.ai->projectileStats.projectileDirectionType == ProjectileDirectionType.Mortar)
                {
                    var pos = playerTargetT->Position + playerTargetT->Forward * 50 * i;
                    projectileT->Position = filter.transform->Position +
                                            filter.transform->TransformDirection(filter.ai->projectileStats
                                                .projectileSpawnOffset);
                    proj->mortarInitialPosition = projectileT->Position;
                    EnemyProjectileSystem.InitalizeMortar(f, pos, projectile);
                }
                else
                {
                    if (filter.ai->projectileStats.projectileSpawnOffset == FPVector3.Zero)
                    {
                        projectileT->Position = filter.transform->Position + new FPVector3(0, 5);
                    }
                    else
                    {
                        projectileT->Position = filter.transform->Position +
                                                filter.transform->TransformDirection(filter.ai->projectileStats
                                                    .projectileSpawnOffset);
                    }
                }


                currentRot += rotationPerShot;

                InitializeProjectileProperties(f, projectile, bulletDamage, projectileT->Forward, filter, target);


                if (filter.ai->projectileStats.isProjectileFollowAnimation)
                {
                    proj->followingAnimation = true;
                }
            }

            filter.ai->targetPosition = playerTargetT->Position;
            filter.ai->stats.currentPauseMovementAfterAttacking = filter.ai->stats.basePauseMovementAfterAttacking;
        }

        private void InitializeProjectileProperties(Frame f, EntityRef projectile, FP damage, FPVector3 forward,
            Filter filter, EntityRef target)
        {
            var projectileComponent = f.Unsafe.GetPointer<GauntletProjectile>(projectile);
            projectileComponent->Velocity = forward * filter.ai->projectileStats.projectileSpeed;
            if (filter.ai->enemyType == EnemyType.TreeHouse)
            {
                projectileComponent->Velocity = forward * filter.ai->projectileStats.projectileSpeed;
            }
            
            if (filter.ai->projectileStats.applyCalculatedFalloff && f.Unsafe.TryGetPointer<Ship>(target, out var ship))
            {
                var shipData = f.FindAsset<ShipData>(ship->ShipData.Id);
                if (shipData.ShipType == ShipType.Raft || shipData.ShipType == ShipType.Dinghy)
                {
                    var shipTransform = f.Unsafe.GetPointer<Transform3D>(target);
                    var distance = FPVector3.DistanceSquared(shipTransform->Position , filter.transform->Position);
                    projectileComponent->Falloff = FP._1/distance * shipData.NPCShootOffSet;
                    filter.ai->projectileStats.projectileDirectionType = ProjectileDirectionType.CanonShot;
                }

                if (filter.ai->enemyType == EnemyType.SeaOgre)
                {
                    var shipTransform = f.Unsafe.GetPointer<Transform3D>(target);
                    var distance = FPVector3.DistanceSquared(shipTransform->Position , filter.transform->Position);
                    projectileComponent->Falloff = (FP._100 * shipData.NPCShootOffSet)/distance;
                    filter.ai->projectileStats.projectileDirectionType = ProjectileDirectionType.CanonShot;
                }
            }
            else if (filter.ai->projectileStats.projectileDirectionType == ProjectileDirectionType.CanonShot)
            {
                projectileComponent->Falloff = filter.ai->projectileStats.projectileCanonShotFalloff;
            }
            else
            {
                projectileComponent->Falloff = 0;
            }

            

            projectileComponent->Range = (int)filter.ai->projectileStats.projectileSpeed * 3;
            projectileComponent->WeaponType = WeaponType.DefaultCannon;
            projectileComponent->Damage = (int)damage;
            projectileComponent->IsCaptainsCannons = false;
        }

        private EntityPrototype GetEnemyProjectileEntity(Frame f, Filter filter)
        {
            var projectilePath = "Resources/DB/Gauntlet/Attacks/RockProjectileEntityPrototype";
            switch (filter.ai->enemyType)
            {
                case EnemyType.SkeletonArcher:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/ArrowProjectileEntityPrototype";
                    break;
                case EnemyType.Kraken:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/KrakenBossProjectileEntityPrototype";
                    break;
                case EnemyType.BossGolem:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/SeaweedBossProjectileEntityPrototype";
                    break;
                case EnemyType.RockGolem:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/RockProjectileEntityPrototype";
                    break;
                case EnemyType.SeaweedGolem:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/SeaweedProjectileEntityPrototype";
                    break;
                case EnemyType.SeaOgre:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/SeaOgreBossProjectileEntityPrototype";
                    break;
                case EnemyType.CoralGolem:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/CrystalProjectileEntityPrototype";
                    break;
                case EnemyType.Jellyfish:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/SquidProjectileEntityPrototype";
                    break;
                case EnemyType.SeaSerpent:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/SeaSerpentProjectileEntityPrototype";
                    break;
                case EnemyType.Crab:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/CrabProjectileEntityPrototype";
                    break;
                default:
                    projectilePath = "Resources/DB/Gauntlet/Attacks/RockProjectileEntityPrototype";
                    break;
            }

            return f.FindAsset<EntityPrototype>(projectilePath);
        }

        //Mortar attacks create a red indicator where the attack will land
        private void DelayedAttackBehaviour(Frame f, Filter filter, Transform3D* targetTransform,
            EntityRef playerTargetEntity)
        {
            filter.ai->isAttackCooldown = true;
            filter.ai->stats.currentAttackCooldown =
                filter.ai->stats.baseAttackCooldown + filter.anim->meleeAttackDelay;
            filter.ai->currentAttack1Timer = filter.anim->meleeAttackDelay;
            filter.ai->currentAttack1on = true;

            filter.ai->targetPosition = targetTransform->Position;
            //filter.ai->stats.currentAttackDelayBeforeHit = filter.ai->stats.baseAttackDelayBeforeHit;
            filter.ai->stats.currentAttackDelayBeforeHit = filter.anim->meleeAttackDelay;
            //filter.ai->stats.currentAttackCooldown = filter.ai->stats.baseAttackCooldown;
            //filter.ai->stats.currentAttackCooldown = 3;
            filter.ai->stats.currentPauseMovementAfterAttacking = filter.ai->stats.basePauseMovementAfterAttacking;
            filter.ai->delayedAttackDone = false;

            f.Events.EnemyAttack1(filter.Entity);
        }

        //The red indicator attacks from enemy
        private void DelayedAttack(Frame f, Filter filter, EntityRef shipToHit)
        {
            //Physics3D.HitCollection3D hit = f.Physics3D.OverlapShape(filter.ai->targetPosition, FPQuaternion.Identity,
            //    Shape3D.CreateSphere(2));
            var gameplay = f.Unsafe.GetPointerSingleton<Gameplay>();
            //Log.Error("Delayed Attack worked" + hit.Count + " Hits");
            //if (hit.Count > 0)
            {
                FP damage = filter.ai->stats.baseMeleeDamage;
                if (gameplay->ModifierValues[(int)EnemyModifiers.Damage] >= 1)
                {
                    FP totalValue = gameplay->ModifierValues[(int)EnemyModifiers.Damage];
                    damage = (damage + ((damage * FP._0_25) * totalValue));
                }

                //Log.Debug("attacked by " + filter.Entity + ", enemy type: " + filter.ai->enemyType);
                HitTargetWithAttack(f, damage, shipToHit, filter.Entity);
            }

            filter.ai->delayedAttackDone = true;
        }

        //Test Function - delete later if Task is not possible to do
        protected override TaskHandle Schedule(Frame f, TaskHandle taskHandle)
        {
            return base.Schedule(f, taskHandle);
        }

        private void HitTargetWithAttack(Frame f, FP damage, EntityRef shipToHit, EntityRef attackerEntity)
        {
            ComponentFilter<Ship> playerShips = f.Filter<Ship>();
            while (playerShips.Next(out EntityRef shipEntity, out Ship shipComponent))
            {
                if (shipToHit == shipEntity)
                {
                    Ship* shipPtr = f.Unsafe.GetPointer<Ship>(shipEntity);
                    ShipSystem.DamageShip(f, shipPtr, attackerEntity, damage);
                    f.Events.EnemyMeleeHit(shipPtr->Index,
                        f.Unsafe.GetPointer<Transform3D>(shipPtr->WorldEntity)->Position,
                        shipEntity);

                    break;
                }
            }
        }

        //Get modified damage of attack
        /*public static FP GetModifiedShipDamage(Frame f, FP damage, EntityRef leaderEntity)
        {
            FP totalDamage = 0;
            FP shellShockPerkQuantity = PerkSystem.GetShipPerk(f, leaderEntity, Perks.Steel_Hull);
            if (shellShockPerkQuantity > 10)
            {
                shellShockPerkQuantity = 10;
            }

            totalDamage = damage - (damage * (GauntletModifiers.steelHull * shellShockPerkQuantity / 100));
            var reinforcedHull =  totalDamage - totalDamage * TalentTreeSystem.GetShipTalentTreeBuff(f, leaderEntity, TalentType.ReinforcedHull);
            totalDamage -= reinforcedHull;
            if (totalDamage <= 0)
            {
                //Can't let player be invulnerable on defenses alone
                totalDamage = 1;
            }

            //Temporary request change, lower damage by 50%
            totalDamage = totalDamage * FP._0_50;
            return totalDamage;
        }*/


        private void FindTarget(Frame f, Filter filter)
        {
            if (f.RuntimeConfig.GameType == GameType.Adventure)
            {
                var enemyTransform = f.Get<Transform3D>(filter.Entity);
                var enemyPosition = enemyTransform.Position;

                EntityRef nearestPlayer = default;
                var minSqrDistance = FP.MaxValue;

                var ships = f.Filter<Ship>();
                while (ships.Next(out EntityRef shipEntity, out Ship shipComponent))
                {
                    if (filter.ai->faction != PlayerFaction.None)
                    {
                        if (shipComponent.faction == filter.ai->faction)
                        {
                            continue;
                        }
                    }

                    var playerTransform = f.Get<Transform3D>(shipEntity);
                    var sqrDistance = (playerTransform.Position - enemyPosition).SqrMagnitude;

                    if (sqrDistance < minSqrDistance)
                    {
                        minSqrDistance = sqrDistance;
                        nearestPlayer = shipEntity;
                    }
                }

                if (nearestPlayer != default)
                {
                    // Update the target for the enemy

                    filter.ai->targetShip = nearestPlayer;
                }
                else
                {
                    //Log.Debug("critical : No player ship found");
                }
            }
            else
            {
                ComponentFilter<Ship> playerShips = f.Filter<Ship>();
                EntityRef playerRef = EntityRef.None;
                playerShips.Next(out playerRef, out Ship ship);
                filter.ai->targetShip = playerRef;
            }
        }
        //Gets player entity
        /*private EntityRef GetPlayer(Frame f, Filter filter)
        {
            if (f.RuntimeConfig.GameType == GameType.Adventure)
            {
                ComponentFilter<Ship> playerShips = f.Filter<Ship>();
                Transform3D* transform = f.Unsafe.GetPointer<Transform3D>(filter.Entity);
                SortedList<FP, EntityRef> playerRefs = new SortedList<FP, EntityRef>();
                while (playerShips.Next(out EntityRef shipEntity, out Ship shipComponent))
                {
                    Transform3D* shipTransform = f.Unsafe.GetPointer<Transform3D>(shipEntity);
                    var distance = FPVector3.Distance(shipTransform->Position, transform->Position);
                    if (!playerRefs.ContainsKey(distance))
                    {
                        playerRefs.Add(distance, shipEntity);
                    }
                }

                return playerRefs[playerRefs.Keys[0]];
            }
            else
            {
                ComponentFilter<Ship> playerShips = f.Filter<Ship>();
                EntityRef playerRef = EntityRef.None;
                playerShips.Next(out playerRef, out Ship ship);
                return playerRef;
            }
        }*/

        //Checks if a ship has rammed to a player
        public void OnCollisionEnter3D(Frame f, CollisionInfo3D info)
        {
            if (f.Has<EnemyAI>(info.Entity) || f.Has<GauntletProjectile>(info.Entity))
            {
                return;
            }

            if (!f.Has<PhysicsBody3D>(info.Other))
            {
                if (GauntletSystem.debug) Log.Error("Hit other without physics body");
                return;
            }

            var enemyBody = f.Unsafe.GetPointer<Transform3D>(info.Entity);
            var shipBody = f.Unsafe.GetPointer<PhysicsBody3D>(info.Other);

            FPVector3 WorldVeloctyDiff = shipBody->Velocity;
            FPVector3 LocalCollisionImpulse = enemyBody->InverseTransformDirection(WorldVeloctyDiff).Normalized;

            FP rearDamage = 0;
            if (LocalCollisionImpulse.Z < 0)
            {
                rearDamage += FPMath.Abs(LocalCollisionImpulse.Z);
            }

            FP sideDamage = FPMath.Abs(LocalCollisionImpulse.X);
            FP damageDealt = (rearDamage + sideDamage) * 60;

            damageDealt = shipBody->Velocity.Magnitude * FP._0_50;
            if (f.RuntimeConfig.GameType == GameType.Gauntlet)
            {
                var gauntlet = f.Unsafe.GetPointerSingleton<Gauntlet>();
                damageDealt = damageDealt * gauntlet->RamDamageModifier;
            }

            damageDealt = 60;
            if (f.Unsafe.TryGetPointer<Ship>(info.Other, out var ship))
            {
                var shipData = f.FindAsset<ShipData>(ship->ShipData.Id);
                damageDealt *= shipData.RamDamageMultiper;
            }

            var enemies = f.Filter<EnemyAI>();
            while (enemies.Next(out var entity, out EnemyAI enemy))
            {
                if (entity == info.Entity)
                {
                    if (f.Unsafe.TryGetPointer<EnemyAI>(entity, out EnemyAI* enemyPtr))
                    {
                        TakeRamDamage(f, entity, damageDealt);

                        //Attack behaviour for melee / ramming enemies
                        if (enemyPtr->attackType == AttackType.Ram)
                        {
                            return;
                        }
                        else
                        {
                            enemyPtr->hasRecentlyRammed = true;
                            enemyPtr->stats.currentPauseMovementAfterAttacking =
                                enemyPtr->stats.basePauseMovementAfterAttacking;
                        }
                    }
                }
            }

            // TODO: removed ramming code for now as it unintentionally hits players even if enemy gets hit by something else

            //Right now it collides with the only ship in-game
            //Checking the world entity does not work which is why we implemented it this way for now
            /*var ships = f.Filter<Ship>();
            while (ships.Next(out var shipVisibleEntity, out var ship))
            {
                //Log.Error("ship collided");
                f.Events.ShipCollision(ship.WorldEntity, LocalCollisionImpulse.Magnitude);

                if (f.Unsafe.TryGetPointer<Ship>(shipVisibleEntity, out Ship* shipPtr))
                {
                    shipPtr->RotationImpactTarget += new FPVector2(LocalCollisionImpulse.X, -LocalCollisionImpulse.Z) * 2;
                    Log.Debug("player ship was hit by: " + info.Other);
                    ShipSystem.DamageShip(f, shipPtr, damageDealt);
                }
            }*/
        }

        public void HealPercent(Frame f, EntityRef sirenEntity, EntityRef entity, FP amount)
        {
            var enemy = f.Unsafe.GetPointer<EnemyAI>(entity);
            enemy->stats.currentHealth += amount;
            if (enemy->stats.currentHealth >= enemy->stats.baseHealth)
            {
                enemy->stats.currentHealth = enemy->stats.baseHealth;
            }

            f.Events.EnemyHealed(sirenEntity, entity);
        }


        //Enemy take damage from weapons behaviour
        public static void TakeDamageFromPlayer(Frame f, EntityRef projectileRef, EntityRef enemyRef)
        {
            Projectile* projectile = f.Unsafe.GetPointer<Projectile>(projectileRef);
            EnemyAI* ai = f.Unsafe.GetPointer<EnemyAI>(enemyRef);
            Transform3D* enemyT = f.Unsafe.GetPointer<Transform3D>(enemyRef);
            PlayerPerks* shooterPerks = f.Unsafe.GetPointer<PlayerPerks>(projectile->ShooterEntity);
            FP projectileDamage = projectile->Damage;
            var attackedBySameEnemy = false;
            if (f.Exists(projectile->OriginShipVisible))
            {
                if (f.Unsafe.TryGetPointer<Ship>(projectile->OriginShipVisible, out var shipComponent))
                {
                    if (ai->faction != PlayerFaction.None)
                    {
                        if (ai->faction == shipComponent->faction)
                        {
                            return;
                        }
                    }

                    if (projectile->OriginShipVisible == ai->targetShip)
                    {
                        attackedBySameEnemy = true;
                    }
                }
            }
            
            if (ai->lastTimeAttackedByPlayerShip <= 0 || attackedBySameEnemy)
            {
                ai->lastTimeAttackedByPlayerShip = 15;
                ai->targetShip = projectile->OriginShipVisible; 
            }

            if (ai->enemyType == EnemyType.KrakenTentacle)
            {
                Tentacle* tentacle = f.Unsafe.GetPointer<Tentacle>(enemyRef);
                if (tentacle->currentState == TentacleState.Hidden)
                {
                    return;
                }
            }

            if (ai->enemyRank == EnemyRank.Boss)
            {
                f.Events.BossTakeDamage(projectile->Damage);
            }

            f.Events.ShipHit(enemyRef, projectile->OriginShipVisible, WeaponType.DefaultCannon);
            var attackDetails = GetModifiedAttackDamage(f, projectileDamage, projectile->ShooterEntity);
            var modifiedDamage = attackDetails.Key;
            ai->slowSpeedStack += attackDetails.Value;
            ai->overTimeDamageTimer += 5;
            bool isDestroyProjectile = true;

            if (projectile->isMine)
            {
                var MinesAway = GauntletModifiers.minesAway * shooterPerks->GetPerk(Rune.Mine);

                modifiedDamage *= MinesAway * FP._0_05 * TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity,
                    TalentType.Detonate)* TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity,
                    TalentType.Payload) * TalentTreeSystem.GetShipTalentTreeBuff(f, projectile->ShooterEntity,
                    TalentType.Clinker);


                var MineMasteryTalentBuff =
                    TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Barrage);
                if (MineMasteryTalentBuff > 1)
                {
                    if (TalentTreeSystem.debug)
                        Log.Debug("TalentTree: MineMastery MineMastery Buff: Before " + projectileDamage + " After " +
                                  projectileDamage * 2);
                    modifiedDamage *= 2;
                }
            }

            var BruteForceTalentBuff =
                TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Harmony);
            if (TalentTreeSystem.debug)
                Log.Debug("TalentTree: BruteForce Damage Buff: Before " + projectileDamage + " After " +
                          projectileDamage * BruteForceTalentBuff);
            modifiedDamage *= BruteForceTalentBuff;

            var FocusedFireTalentBuff =
                TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Brutalize);
            if (TalentTreeSystem.debug)
                Log.Debug("TalentTree: FocusedFire Damage Buff: Before " + projectileDamage + " After " +
                          projectileDamage * FocusedFireTalentBuff);
            modifiedDamage *= FocusedFireTalentBuff;
            
            var BiteTalentBuff =
                TalentTreeSystem.GetShipTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Bite);
            if (TalentTreeSystem.debug)
                Log.Debug("TalentTree: FocusedFire Damage Buff: Before " + projectileDamage + " After " +
                          projectileDamage * BiteTalentBuff);
            modifiedDamage *= BiteTalentBuff;

            var HeavyHitterTalentBuff =
                TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Slaughter);
            if (TalentTreeSystem.debug)
                Log.Debug("TalentTree: Heavy Hitter Damage Buff: Before " + projectileDamage + " After " +
                          projectileDamage * HeavyHitterTalentBuff);
            modifiedDamage *= HeavyHitterTalentBuff;

            ai->stats.currentHealth -= modifiedDamage;
            if (projectile->WeaponType == WeaponType.MortarCannon)
            {
                f.Events.MortarDamageRecieved(projectile->OriginPlayer, enemyT->Position, modifiedDamage.AsInt);
            }
            ModifyDamageTakenData(f, modifiedDamage, projectile->OriginShipVisible, enemyRef);

            foreach (var playerLink in f.Unsafe.GetComponentBlockIterator<PlayerLink>())
            {
                if (projectile->OriginPlayer != default && playerLink.Component->Player == projectile->OriginPlayer)
                {
                    var roundedDamage = FPMath.RoundToInt(modifiedDamage);
                    playerLink.Component->ScoreboardNPCDamage += roundedDamage;
                    playerLink.Component->NPCEnemyKillsDamage[(int)ai->enemyType] += roundedDamage;

                    PlayerSystem.DoXP(f, Skill.Gun, 10, projectile->OriginPlayer);
                    break;
                }
            }

            ai->LastPlayerDamagedFrom = projectile->OriginPlayer;
            if (ai->stats.currentHealth <= 0)
            {
                if (!ai->isDead)
                {
                    KillEnemy(f, ai->LastPlayerDamagedFrom, ai, enemyRef);
                    //CheckAndSpawnPerkLoot(f,enemyT->Position,ai->enemyType);
                }
            }
            else
            {
                //Apply debuffs if player has perks and enemy lives

                var FireAfterShockTalentBuff =
                    TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Flamequake);
                FP fireQuantity = shooterPerks->GetPerk(Rune.Ember);
                if (TalentTreeSystem.debug)
                    Log.Debug("TalentTree: FireAftershock FireDebuffs Buff: " + FireAfterShockTalentBuff);

                ai->stats.Debuffs[(int)Rune.Ember] = (FP)projectile->Damage *
                    (GauntletModifiers.fireDamage * fireQuantity / 100 +
                     GauntletModifiers.fireDamage * FireAfterShockTalentBuff) / 100;


                var IceAfterShockTalentBuff =
                    TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Frostquake);
                FP iceQuantity = shooterPerks->GetPerk(Rune.Frost);
                if (TalentTreeSystem.debug)
                    Log.Debug("TalentTree: IceAftershock IceDebuffs Buff: " + IceAfterShockTalentBuff);

                ai->stats.Debuffs[(int)Rune.Frost] = (FP)projectile->Damage *
                    (GauntletModifiers.ice * iceQuantity / 100 + GauntletModifiers.ice * IceAfterShockTalentBuff) / 100;

                FP poisonQuantity = shooterPerks->GetPerk(Rune.Venom);
                var PoisonAfterShockTalentBuff =
                    TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Toxquake);
                if (TalentTreeSystem.debug)
                    Log.Debug("TalentTree: PoisonAftershock PoisonDebuffs Buff: " + PoisonAfterShockTalentBuff);

                ai->stats.Debuffs[(int)Rune.Venom] = (FP)projectile->Damage *
                    (GauntletModifiers.poisonDamage * poisonQuantity / 100 +
                     GauntletModifiers.poisonDamage * PoisonAfterShockTalentBuff) / 100; //Also adds 50% slow

                

                // Implemented loot spawn for bosses on 10% health
                if (ai->enemyTier == EnemyTier.Tier4) //(RewardsUtility.GetEnemyLevel(ai->enemyTier) == 4)
                {
                    if (ai->stats.currentHealth <= ai->stats.baseHealth / 10 * ai->bossRewardPer10PercentHealth)
                    {
                        foreach (var playerLink in f.Unsafe.GetComponentBlockIterator<PlayerLink>())
                        {
                            if (ai->LastPlayerDamagedFrom != default &&
                                playerLink.Component->Player == ai->LastPlayerDamagedFrom)
                            {

                                var ship = f.Unsafe.GetPointer<Ship>(playerLink.Component->ShipVisibleEntity);
                                var enemyTransform = f.Unsafe.GetPointer<Transform3D>(enemyRef);
                                        
                                ReportNpcKillEncounter(f, ship, ai->enemyTier, enemyTransform->Position, 
                                    playerLink.Component->Player, enemyRef);
                                ai->bossRewardPer10PercentHealth--;
                                break;
                            }
                        }
                    }
                }
            }

            //Pierce projectile check
            var impaleCount = shooterPerks->GetPerk(Rune.Pierce);
            var drilltipTalentTreeBuff =
                TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Drilltip);
           
            if (impaleCount > 0)
            {
                if (projectile->EnemiesHit < impaleCount)
                {
                    projectile->EnemiesHit += 1;
                    isDestroyProjectile = false;
                }
                else if (drilltipTalentTreeBuff > 1 && projectile->EnemiesHit < impaleCount + 1)
                {
                    var dripTripPercent = (1 - drilltipTalentTreeBuff) * 100;
                    var possiblity = f.Global->RngSession.Next(0 ,100) < dripTripPercent;
                    if (possiblity)
                    {
                        projectile->EnemiesHit += 1;
                        isDestroyProjectile = false;
                    }
                }
            }

            //Explode projectile check


            if (isDestroyProjectile)
            {
                projectile->MarkedForDestruction = true;
                bool echocharge = false;
                var echochargeTalentTreeBuff =
                    TalentTreeSystem.GetPlayerTalentTreeBuff(f, projectile->ShooterEntity, TalentType.Echocharge);
                if (echochargeTalentTreeBuff > 1)
                {
                    var echochargePercent = (1 - echochargeTalentTreeBuff) * 100;
                    var possiblity = f.Global->RngSession.Next(0 ,100) < echochargePercent;
                    if (possiblity)
                    {
                        echocharge = true;
                    }
                }
                
                var POW = shooterPerks->GetPerk(Rune.Echo) + (echocharge? 1 : 0);
                
                if (POW > 0 && !projectile->alreadyAfterShocked)
                {
                    //Explode
                    projectile->afterShocks = POW;
                    projectile->alreadyAfterShocked = true;
                    projectile->afterShockTimer = FP._0_10;
                }
            }

            if (ai->isDead)
            {
                var Magical_CaltropsPerk = shooterPerks->GetPerk(Rune.Spore);
                if (Magical_CaltropsPerk > 0)
                {
                    for (int i = 0; i < Magical_CaltropsPerk; i++)
                    {
                        var randX = f.Global->RngSession.Next(FP._0, FP._2);
                        var randZ = f.Global->RngSession.Next(FP._0, FP._2);
                        EnemyProjectileSystem.CreateMineOnDeath(f, enemyT->Position + new FPVector3(randX, 1, randZ),
                            modifiedDamage, projectile->ShooterEntity);
                    }
                }

                ProcessEnemyDead(f, enemyRef, ai);
            }
        }

        public static void ProcessEnemyDead(Frame f, EntityRef enemyRef, EnemyAI* ai)
        {
            if (f.RuntimeConfig.GameType == GameType.Gauntlet)
            {
                var gauntlet = f.Unsafe.GetPointerSingleton<Gauntlet>();
                gauntlet->WaveEnemiesKilled += 1;
                if (ai->enemyType == EnemyType.TreeHouse)
                {
                    gauntlet->WaveEliteKilled += 1;
                }
            }
            else if (f.Unsafe.TryGetPointerSingleton<EnemySpawnPointHandler>(out var enemySpawnPointHandler))
            {
                if (ai->enemyRank == EnemyRank.Boss)
                {
                    enemySpawnPointHandler->BossKilled = true;
                }

                if (ai->enemyType == EnemyType.Kraken)
                {
                    var krakenTentacle = f.Unsafe.GetPointer<KrakenTentacles>(enemyRef);
                    for (int i = 0; i < Constants.KRAKEN_TENTACLES; i++)
                    {
                        if (f.Exists(krakenTentacle->tentacles[i]))
                        {
                            var EnemyAI = f.Unsafe.GetPointer<EnemyAI>(krakenTentacle->tentacles[i]);
                            EnemyAI->isDead = true;
                            if (!ai->damageTakenFromPlayers.Equals(default))
                            {
                                f.FreeDictionary(ai->damageTakenFromPlayers);
                                ai->damageTakenFromPlayers = default;
                            }
                        }
                    }
                }
            }

            ai->isDead = true;
            
            
            if (!ai->damageTakenFromPlayers.Equals(default))
            {
                f.FreeDictionary(ai->damageTakenFromPlayers);
                ai->damageTakenFromPlayers = default;
            }
            f.Events.EnemyDead(enemyRef);
        }

        private static void ModifyDamageTakenData(Frame f, FP damage, EntityRef shipEntity, EntityRef enemyEntity)
        {
            if (!f.Unsafe.TryGetPointer(shipEntity, out Ship* ship))
            {
                return;
            }

            if (!f.Unsafe.TryGetPointer(enemyEntity, out EnemyAI* enemyAI))
            {
                return;
            }

            if(enemyAI->damageTakenFromPlayers.Equals(default))
            {
                Log.Debug("damage taken is not allocated: " + enemyAI->enemyType.ToString());
                f.AllocateDictionary(out enemyAI->damageTakenFromPlayers);
            }

            var damageTakenData = f.ResolveDictionary(enemyAI->damageTakenFromPlayers);
            if(damageTakenData.TryGetValue(ship->PartyId, out var damageTaken))
            {
                damageTaken += damage;
                damageTakenData[ship->PartyId] = damageTaken;
                Log.Debug("taken damage from: " + ship->PartyId + " damage: " + damageTaken);
            }
            else
            {
                damageTakenData.Add(ship->PartyId, damage);
                Log.Debug("(new) taken damage from: " + ship->PartyId + " damage: " + damage);
            }
            enemyAI->damageTakenFromPlayers = damageTakenData;
        }

        private static void KillEnemy(Frame f, PlayerRef player, EnemyAI* ai, EntityRef enemyRef)
        {
            if(f.IsPredicted)
            {return;}
            foreach (var playerLink in f.Unsafe.GetComponentBlockIterator<PlayerLink>())
            {
                if (player != default && playerLink.Component->Player == player)
                {
                    playerLink.Component->ScoreboardNPCKills++;
                    playerLink.Component->NPCEnemyKills[(int)ai->enemyType]++;

                    // report npc kill for rewards
                    if (f.RuntimeConfig.GameType == GameType.Adventure)
                    {
                        var ship = f.Unsafe.GetPointer<Ship>(playerLink.Component->ShipVisibleEntity);
                        var enemyTransform = f.Unsafe.GetPointer<Transform3D>(enemyRef);
                        ReportNpcKillEncounter(f, ship, ai->enemyTier, enemyTransform->Position, playerLink.Component->Player,
                            enemyRef);
                        /* foreach (var ship in f.Unsafe.GetComponentBlockIterator<Ship>())
                        {
                            if (ship.Component->Index == playerLink.Component->ShipIndex)
                            {
                                var enemyTransform = f.Unsafe.GetPointer<Transform3D>(enemyRef);
                                ReportNpcKillEncounter(f, ship.Component, ai->enemyTier, enemyTransform->Position,playerLink.Component->Player);
                                break;
                            }
                        }*/
                    }

                    break;
                }
            }

            ProcessEnemyDead(f, enemyRef, ai);
        }

        public static void ReportNpcKillEncounter(Frame f, Ship* playerShip, EnemyTier tier, FPVector3 position,
            PlayerRef playerRef, EntityRef enemyEntity)
        {
            var shipPartyId = playerShip->PartyId;

            if (f.Unsafe.TryGetPointer<EnemyAI>(enemyEntity, out var enemyAI))
            {
                var newPartyId = GetPartyIdWithMostDamageFromEnemyAI(f, enemyEntity);
                if (newPartyId > 0)
                { 
                    shipPartyId = newPartyId;
                }
            }
            else if (f.Unsafe.TryGetPointer<NPCShip>(enemyEntity, out var npcShip))
            {
                var newPartyId = GetPartyIdWithMostDamageFromNPCShip(f, enemyEntity);
                if (newPartyId > 0)
                {
                    shipPartyId = newPartyId;
                }
            }
            Log.Debug("Reporting NPC kill for partyId: " + shipPartyId + " enemy tier: " + tier + " position: " + position);
            var enemyLevel = (int)(tier) + 1;
            f.Events.PluginShipReportEncounterNpcKill(f, shipPartyId, enemyLevel, position);
        }
        
        private static int GetPartyIdWithMostDamageFromEnemyAI(Frame f, EntityRef enemyEntity)
        {
            if (!f.Unsafe.TryGetPointer<EnemyAI>(enemyEntity, out var enemyAI))
            {
                return -1;
            }
            if(enemyAI->damageTakenFromPlayers.Equals(default))
            {
                f.AllocateDictionary(out enemyAI->damageTakenFromPlayers);
                Log.Debug("damage taken is not allocated: " + enemyAI->enemyType.ToString());
            }
            var damageTakenData = f.ResolveDictionary(enemyAI->damageTakenFromPlayers);
            if (damageTakenData.Count == 0)
            {
                return -1;
            }
            var maxPartyId = -1;
            var maxDamage = FP._0;
            foreach (var pair in damageTakenData)
            {
                if (pair.Value > maxDamage)
                {
                    maxDamage = pair.Value;
                    maxPartyId = pair.Key;
                }
            }
            return maxPartyId;
        }

        public static int GetPartyIdWithMostDamageFromNPCShip(Frame f, EntityRef enemyEntity)
        {
            if (!f.Unsafe.TryGetPointer<NPCShip>(enemyEntity, out var enemyAI))
            {
                return -1;
            }

            var damageTakenData = f.ResolveDictionary(enemyAI->damageTakenFromPlayers);

            if (damageTakenData.Count == 0)
            {
                return -1;
            }
            var maxPartyId = -1;
            var maxDamage = FP._0;

            foreach (var pair in damageTakenData)
            {
                if (pair.Value > maxDamage)
                {
                    maxDamage = pair.Value;
                    maxPartyId = pair.Key;
                }
            }
            return maxPartyId;
        }

        //Get modified damage of attack
        public static KeyValuePair<FP,FP> GetModifiedAttackDamage(Frame f, FP damage, EntityRef shooterEntity)
        {
            FP slowStack = 0;

            if (!f.Unsafe.TryGetPointer<PlayerPerks>(shooterEntity, out var shooterPerks))
            {
                if (TalentTreeSystem.debug)
                    Log.Debug("TalentTree: shooterEntity does not have PlayerPerks component. Returning unmodified damage.");

                return new KeyValuePair<FP, FP>(damage, slowStack);
            }

            
            var shellShockedTalentBuff =
                TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Powderpack);
            if (TalentTreeSystem.debug)
                Log.Debug("TalentTree: shellShocked ShellShockDamage Buff: " + shellShockedTalentBuff);

            FP fireQty = shooterPerks->GetPerk(Rune.Ember);
            var fireTalentBuff = TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Ashbolt);
            fireTalentBuff *= TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Firestorm);
            fireTalentBuff *= TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Sparkshot);
            if (TalentTreeSystem.debug)
                Log.Debug("TalentTree: FireSpecialist+FireEnhancement FireDamage Buff: " + fireTalentBuff);

            FP iceQty = shooterPerks->GetPerk(Rune.Frost);
            var iceTalentBuff = TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Coldshot);
            iceTalentBuff *= TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Icestorm);
            iceTalentBuff *= TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Sparkshot);
            if (TalentTreeSystem.debug)
                Log.Debug("TalentTree: IceSpecialist+IceEnhancement IceDamage Buff: " + iceTalentBuff);
            slowStack += (iceQty + TalentTreeSystem.GetPlayerCurrentPoint(f, shooterEntity, TalentType.Coldshot)
                    + TalentTreeSystem.GetPlayerCurrentPoint(f, shooterEntity, TalentType.Icestorm)) * 2;
            FP poisonQty = shooterPerks->GetPerk(Rune.Venom);
            var PoisonTalentBuff =
                TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Toxcharge);
            PoisonTalentBuff *= TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Toxstorm);
                TalentTreeSystem.GetPlayerTalentTreeBuff(f, shooterEntity, TalentType.Sparkshot);
            if (TalentTreeSystem.debug)
                Log.Debug("TalentTree: PoisonSpecialist+PoisonEnhancement PoisonDamage Buff: " + PoisonTalentBuff);
            slowStack += poisonQty + TalentTreeSystem.GetPlayerCurrentPoint(f, shooterEntity, TalentType.Toxcharge)
                                   + TalentTreeSystem.GetPlayerCurrentPoint(f, shooterEntity, TalentType.Toxstorm);
            
            var totalDamage = damage;
            totalDamage += totalDamage * ((fireQty * GauntletModifiers.fireDamage) / 100);
            totalDamage += totalDamage * ((iceQty * GauntletModifiers.ice) / 100);
            totalDamage += totalDamage * ((poisonQty * GauntletModifiers.poisonDamage) / 100);
            totalDamage *= shellShockedTalentBuff;
            totalDamage *= fireTalentBuff;
            totalDamage *= iceTalentBuff;
            totalDamage *= PoisonTalentBuff;
            return new KeyValuePair<FP,FP>(totalDamage,slowStack);
        }

        //Enemy take damage from DOTs
        public static void TakeDamageOverTime(Frame f, Filter filter)
        {
            EnemyAI* ai = filter.ai;
            ai->damageOverTime += f.DeltaTime;
            ai->overTimeDamageTimer -= f.DeltaTime;
            FP totalDamage = 0;
            totalDamage += ai->stats.Debuffs[(int)Rune.Ember];
            totalDamage += ai->stats.Debuffs[(int)Rune.Frost];
            totalDamage += ai->stats.Debuffs[(int)Rune.Venom];

            //Log.Error("Taking damage over time " + totalDamage + " fire damage " + ai->stats.Debuffs[(int)Perks.Fire]
            //+ " Ice damage " + ai->stats.Debuffs[(int)Perks.Ice] + " posion damage " + ai->stats.Debuffs[(int)Perks.Poison]);

            //No need to call if damage is 0
            if (totalDamage > 0 && ai->damageOverTime >= 1)
            {
                //Log.Error("DOT taken : " + totalDamage);
                ai->damageOverTime = 0;
                ai->stats.currentHealth -= totalDamage;
                if (ai->stats.currentHealth <= 0 && !ai->isDead)
                {
                    filter.ai->overTimeDamageTimer = 0;
                    ProcessEnemyDead(f, filter.Entity, ai);
                    KillEnemy(f, ai->LastPlayerDamagedFrom, ai, filter.Entity);
                }
            }
        }

        //Enemy take damage from ramming
        public static void TakeRamDamage(Frame f, EntityRef enemyRef, FP ramDamage)
        {
            EnemyAI* ai = f.Unsafe.GetPointer<EnemyAI>(enemyRef);

            ai->stats.currentHealth -= ramDamage;
            if (ai->stats.currentHealth <= 0)
            {
                //EnemyAI* currentAttack = f.Unsafe.GetPointer<EnemyAI>(enemyRef);

                if (!ai->isDead)
                {
                    KillEnemy(f, ai->LastPlayerDamagedFrom, ai, enemyRef);
                    ProcessEnemyDead(f, enemyRef, ai);
                }
            }
        }

        //Gets the angle when Vector3.forward would face the target position
        public static FP CalculateAngleToTarget(Transform3D* transform, FPVector3 targetPosition)
        {
            FPVector3 direction = transform->Position - targetPosition;
            FPQuaternion rotation = FPQuaternion.Euler(0, 90, 0);
            FPVector3 rotatedDirection = rotation * direction;
            FP angle = FPMath.Atan2(rotatedDirection.Z, -rotatedDirection.X) * FP.Rad2Deg;
            return angle;
        }
        
        private void UpdateslowSpeedStack(Frame f, Filter filter)
        {
            filter.ai->slowSpeedTimer -= f.DeltaTime;
            if (filter.ai->slowSpeedTimer < 0)
            {
                filter.ai->slowSpeedStack -= FPMath.Max(filter.ai->slowSpeedStack,100);
                filter.ai->slowSpeedTimer += 1;
            }
        }
        		
        private FP GetSlowSpeedStack(Filter filter)
        {
            return 1 - (filter.ai->slowSpeedStack / 10000);
        }
    }
}