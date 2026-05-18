namespace Quantum
{
    public unsafe partial struct Tentacle
    {

        public void Update(Frame f,EntityRef entity,KrakenTentacles tentacles, EntityRef player)
        {
            stateTime -= f.DeltaTime;

            switch (currentState)
            {
                default:
                case TentacleState.Idle:
                    if (attackPlayer && stateTime < 0)
                    {
                        currentState = TentacleState.Attacking;
                        stateTime = 5;
                    }
                    break;
                case TentacleState.Hidden:
                    if (attackPlayer && stateTime < 0)
                    {
                        currentState = TentacleState.Idle;
                        var entityT = f.Unsafe.GetPointer<Transform3D>(entity);
                        var enemySpawn = f.Unsafe.GetOrAddSingletonPointer<EnemySpawn>();
                        var ship = f.Unsafe.GetPointer<Ship>(player);
                        PhysicsBody3D* shipWorldRigidbody = f.Unsafe.GetPointer<PhysicsBody3D>(ship->WorldEntity);
                        Transform3D* playerT = f.Unsafe.GetPointer<Transform3D>(player);
                        entityT->Position = playerT->Position +playerT->Forward * tentacles.ForwardMultiplier * shipWorldRigidbody->Velocity.Magnitude +
                                            enemySpawn->GetRandomPosition(f, tentacles.RadiusAroundPlayer);
                        f.Events.TentacleSpawn(entity);
                    }
                    break;
                case TentacleState.Attacking:
                    if (attackPlayer && stateTime < 0)
                    {
                        stateTime = f.Global->RngSession.Next(tentacles.minTimeToStay, tentacles.maxTimeToStay);
                        currentState = TentacleState.Idle;
                        attackPlayer = false;
                    }
                    break;
                case TentacleState.Taunting:
                    if (stateTime < 0)
                    {
                        currentState = TentacleState.Idle;
                    }
                    break;
            }
        }

        
    }
}