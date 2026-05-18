
using Photon.Deterministic;
using UnityEngine;

namespace Quantum
{
    public partial class NavigationTuning : Quantum.AssetObject
    {
        // Steering
        public FP SteeringLerpBase = FP._0_10; // baseline turn smoothing
        public FP SteeringLerpChase = FP._0_20; // chase/attack sharper turns
        
        public FP AvoidWeightNear = FP._0_75; // avoidance dominance when near obstacle

         public  FP AvoidWeightFar   = FP._0_25;   // avoidance blend when far
        public FP DesiredWeight = FP._0_10 * 8; // goal heading influence

        // Raycasts
        public int RaycastMask = -1;
        public FP RayLengthForward = 120; // forward probe
        public FP RayLengthSide = 90; // side feelers

        // Distances (squared when used against DistanceSquared)
        public FP EngageDistance = 220; // how close to begin non-pursuit patterns
        public FP IdealBroadsideRadius = 160;
        public FP RamWindow = 110;

        // Stuck detection
        public FP MinMoveSpeed = FP._0_25; // world units/sec below which we consider "stuck"
        public FP StuckTime = FP.FromFloat_UNSAFE(1.75f); // seconds below threshold to trigger recovery
        public FP UnstickJinkAngleDeg = 35; // quick jink rotation
        public FP UnstickBurstTime = FP._0_10 * 7;
        
        [Header("Fixed Values")]
        public FP V2_FAIL_WINDOW     = FP.FromFloat_UNSAFE(30);  // 30 sec to prove progress
        public FP V1_RUN_WINDOW      = FP.FromFloat_UNSAFE(30);  // fall back to V1 for 30 sec
        public FP PROGRESS_EPS       = FP.FromFloat_UNSAFE(0.75f); // need to get this much closer
        public FP SPEED_OK_FACTOR    = FP.FromFloat_UNSAFE(0.90f); // 0.9 * MinMoveSpeed considered OK
        
        public FP AVOID_LOCK_TIME     = FP.FromFloat_UNSAFE(0.9f);
        public FP AVOID_COOLDOWN_TIME = FP.FromFloat_UNSAFE(0.35f);
        public int AVOID_OFFSET_DEG   = 60;
        public int AVOID_CAP_COMBAT   = 110;
        
        public FP SAMPLE_COMMIT_TIME = FP.FromFloat_UNSAFE(0.6f);
        public FP ALIGN_WEIGHT       = FP.FromFloat_UNSAFE(0.45f);
        public int AVOID_RETURN      = 100;  // how far to form a target point along chosen corridor
        
        // ---- Leash tuning (local constants; no asset change required) ----
        public FP LEASH_RADIUS           = FP.FromFloat_UNSAFE(300);        // fight arena radius
        public FP LEASH_MARGIN           = FP.FromFloat_UNSAFE(40);         // outer cushion before we pull back
        public FP LEASH_INNER_FACTOR     = FP.FromFloat_UNSAFE(0.60f); // release inside 60% of radius
        public FP LEASH_RETURN_FRACTION  = FP._0_50;                   // pull to half radius (150)
        public FP LEASH_STEER_BOOST      = FP.FromFloat_UNSAFE(1.25f); // quicker steering while returning
        
        public FP ONE_ON_ONE_SCAN_RADIUS = FP.FromFloat_UNSAFE(420); // local fight scan (~leash + margin)
        public FP FORMATION_SPACING      = FP.FromFloat_UNSAFE(60);  // lateral spacing between queued minions
        public FP FORMATION_BACK_OFFSET  = FP.FromFloat_UNSAFE(140); // how far behind the front to hold


    }
}