using System;
using System.Diagnostics;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SurfaceLoreSpawnsAndRiftsRedux
{
    /// <summary>
    /// Postfix-patches EntityPlayer.CanSpawnNearby - the same public
    /// extension point vanilla's own SystemTemporalStability uses (via
    /// EntityPlayer.OnCanSpawnNearby) to gate surface spawning of Drifters,
    /// Shivers and Bowtorns to "near a rift" at night / "near a rift plus a
    /// 7% roll" during the day.
    ///
    /// That vanilla gate only fires when the candidate spawn position is
    /// directly under open sky (sunlight level >= 16). Anywhere shaded -
    /// under a tree, a roof overhang, inside a small alcove, on the lee side
    /// of a hill - falls through to a plain "far enough from the player"
    /// check with no rift requirement at all, which is why these creatures
    /// keep turning up on the surface at night in ordinary terrain. Reducing
    /// maxLightLevel (as e.g. the LowLightSpawns mod does) doesn't close
    /// that gap either, since outdoor block light is already ~0 at night
    /// regardless of the configured threshold - these creatures use
    /// LightLevelType=OnlyBlockLight, not total ambient light.
    ///
    /// This patch denies the restricted creatures outright via the ambient
    /// spawner unless a storm is active - deliberately with NO rift-distance
    /// exception here, even though that means it's also removing vanilla's
    /// own (rare, awkward) rift exception for these creatures. See
    /// RiftSurfaceSpawner for why: it's a dedicated, directly-controlled
    /// spawner for "near a rift", so this gate doesn't need to (and
    /// shouldn't try to) approximate that case too - one predictable rule
    /// each.
    ///
    /// Deliberately does NOT gate by variant tier (e.g. matching only
    /// "-normal"/"-surface" codes) - vanilla's own Y-range per tier is an
    /// imperfect proxy for "is this actually outdoors," since a cave that
    /// happens to sit above sea level (a hillside tunnel, a mountain cavern)
    /// still gets classified into the "surface" tier purely by height, and
    /// gating on tier alone would block it too. Instead this checks whether
    /// the exact candidate position has a solid, rain-blocking block
    /// anywhere above it (IBlockAccessor.GetRainMapHeightAt - the same
    /// heightmap the game itself uses to decide where rain lands), which
    /// correctly recognizes a cave as sheltered regardless of its absolute
    /// Y, and correctly still counts foliage-shaded outdoor terrain as
    /// "surface" (leaves don't block rain in this engine, so they don't
    /// block this check either) - closing the original gap rather than
    /// reopening it. SurfaceCreatureSpawnsToPrevent's substring/wildcard
    /// matching (see its comment in Config.cs) catches every tier of these
    /// three creatures (not just the surface one) precisely because this
    /// sky-exposure check, not the tier name, is now what decides whether a
    /// spawn counts as "surface."
    ///
    /// This still only ever narrows the ambient result (ANDs an extra
    /// condition onto whatever vanilla or another mod already decided), so
    /// it can't accidentally re-allow a spawn something else already
    /// blocked, and it never touches storm-proximity spawning
    /// (SystemTemporalStability.trySpawnMobs/DoSpawn), which creates entities
    /// directly and never calls CanSpawnNearby at all.
    ///
    /// A sheltered position is normally left entirely to vanilla, whose own
    /// light-level condition (maxLightLevel: 7, OnlyBlockLight) is what
    /// keeps a lit base monster-free - same as it's always been, unrelated
    /// to this mod. As a backstop for a room someone forgot to light,
    /// PreventSpawnsInsideRooms (shared with RiftSurfaceSpawner's own use of
    /// it) additionally denies a sheltered spawn outright if it's inside a
    /// small fully-enclosed Room (RoomRegistry, ExitCount == 0), regardless
    /// of light level. Only covers spaces small enough for RoomRegistry to
    /// recognize as a Room (roughly 14x14x14) - a large open structure
    /// isn't covered by this and still relies on light level alone.
    /// </summary>
    public static class SurfaceSpawnGate
    {
        private static ICoreServerAPI sapi;
        private static SurfaceLoreSpawnsAndRiftsReduxConfig config;
        private static SystemTemporalStability stormSystem;
        private static RoomRegistry roomRegistry;

        // Accumulated between ReportAmbientPerformance calls - see that
        // method and LogAllAmbientCheckPerformance's comment in Config.cs
        // for why this exists (we can't read vanilla's own call schedule
        // from source, so this measures it directly instead).
        private static long ambientWindowCallCount = 0;
        private static long ambientWindowElapsedTicks = 0;

        // Fixed real-world reporting window for ambient performance - see the
        // comment where this is passed to RegisterGameTickListener in Apply().
        private const int AmbientPerformanceReportIntervalMs = 1000;

        public static void Apply(ICoreServerAPI sapi, Harmony harmony, SurfaceLoreSpawnsAndRiftsReduxConfig config)
        {
            SurfaceSpawnGate.sapi = sapi;
            SurfaceSpawnGate.config = config;

            if (!config.PreventSurfaceSpawns)
            {
                sapi.Logger.Notification("[SurfaceLoreSpawnsAndRiftsRedux] Ambient spawn gate disabled " +
                    "(PreventSurfaceSpawns=false) - vanilla ambient spawning is left completely untouched; " +
                    "only RiftSurfaceSpawner adds anything on top of it.");
                return;
            }

            stormSystem = sapi.ModLoader.GetModSystem<SystemTemporalStability>();
            roomRegistry = sapi.ModLoader.GetModSystem<RoomRegistry>();

            if (stormSystem == null)
            {
                sapi.Logger.Error("[SurfaceLoreSpawnsAndRiftsRedux] Could not find SystemTemporalStability - " +
                    "surface spawn gating is disabled.");
                return;
            }

            var target = AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.CanSpawnNearby),
                new System.Type[] { typeof(EntityProperties), typeof(Vec3d), typeof(RuntimeSpawnConditions) });

            if (target == null)
            {
                sapi.Logger.Error("[SurfaceLoreSpawnsAndRiftsRedux] EntityPlayer.CanSpawnNearby not found - " +
                    "surface spawn gating is disabled. This method may have been renamed or restructured " +
                    "since this was last verified against the real vsapi source.");
                return;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SurfaceSpawnGate), nameof(Postfix)));

                sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] Ambient spawn gate active for " +
                    $"{config.SurfaceCreatureSpawnsToPrevent.Count} restricted creature code(s), sky-exposed positions only " +
                    (!config.AlsoPreventSpawnsDuringTemporalStorms
                        ? "- allowed during an active storm, denied otherwise"
                        : "- always denied, even during a storm") +
                    (config.PreventSpawnsInsideRooms
                        ? "; sheltered spawns inside a small enclosed room are also denied regardless of light."
                        : "."));

                if (config.LogAllAmbientCheckPerformance || config.LogSlowAmbientCheckPerformance)
                {
                    // Fixed at exactly 1 real-world second, deliberately NOT a config
                    // field and NOT reusing RiftCreatureSpawnCheckIntervalSeconds (that
                    // was tried and reverted 2026-09-18) - a real second is the natural
                    // unit for "how much of this tick's/second's frame budget did the
                    // ambient gate use," not an arbitrary tunable that needs keeping in
                    // sync with anything else. See SlowAmbientCheckThresholdMilliseconds
                    // in Config.cs for the reasoning this feeds into.
                    sapi.Event.RegisterGameTickListener(ReportAmbientPerformance, AmbientPerformanceReportIntervalMs);
                }
            }
            catch (System.Exception ex)
            {
                sapi.Logger.Error($"[SurfaceLoreSpawnsAndRiftsRedux] Failed to patch CanSpawnNearby - {ex.Message}");
            }
        }

        private static void Postfix(EntityProperties type, Vec3d spawnPosition, ref bool __result)
        {
            bool trackPerformance = config.LogAllAmbientCheckPerformance || config.LogSlowAmbientCheckPerformance;
            long startTicks = trackPerformance ? Stopwatch.GetTimestamp() : 0;

            PostfixInner(type, spawnPosition, ref __result);

            if (trackPerformance)
            {
                ambientWindowCallCount++;
                ambientWindowElapsedTicks += Stopwatch.GetTimestamp() - startTicks;
            }
        }

        /// <summary>
        /// The actual gate logic - split out from Postfix purely so the
        /// performance-timing wrapper above can time the whole thing
        /// (including every early return below) without duplicating it.
        /// </summary>
        private static void PostfixInner(EntityProperties type, Vec3d spawnPosition, ref bool __result)
        {
            if (!__result) return; // already denied by vanilla/another mod - nothing to add
            if (type?.Code == null) return;

            string code = type.Code.ToString();
            if (!IsRestricted(code)) return;

            if (!IsExposedToSky(spawnPosition))
            {
                if (config.PreventSpawnsInsideRooms && IsInsideEnclosedRoom(spawnPosition))
                {
                    Log(code, spawnPosition, false, "sheltered AND inside an enclosed room - denied regardless of light");
                    __result = false;
                    return;
                }

                Log(code, spawnPosition, true, "sheltered (cave/roof) - left to vanilla");
                return;
            }

            bool stormActive = stormSystem.StormStrength > 0f;
            if (stormActive && !config.AlsoPreventSpawnsDuringTemporalStorms)
            {
                Log(code, spawnPosition, true, "storm active");
                return;
            }

            Log(code, spawnPosition, false, stormActive
                ? "storm active but AlsoPreventSpawnsDuringTemporalStorms=true"
                : "exposed to sky, no storm active");
            __result = false;
        }

        /// <summary>
        /// Flushes the call-count/elapsed-time totals accumulated since
        /// the last report and (maybe) logs them - see
        /// LogAllAmbientCheckPerformance's comment in Config.cs for why
        /// this exists: we can't read vanilla's own ambient-spawn-attempt
        /// schedule from source, so this measures the real, observed call
        /// rate into our own postfix directly instead of guessing at it.
        /// </summary>
        private static void ReportAmbientPerformance(float dt)
        {
            long callCount = ambientWindowCallCount;
            double elapsedMs = ambientWindowElapsedTicks * 1000.0 / Stopwatch.Frequency;
            ambientWindowCallCount = 0;
            ambientWindowElapsedTicks = 0;

            bool isSlow = elapsedMs >= config.SlowAmbientCheckThresholdMilliseconds;
            if (!config.LogAllAmbientCheckPerformance && !(config.LogSlowAmbientCheckPerformance && isSlow)) return;

            double avgMsPerCall = callCount > 0 ? elapsedMs / callCount : 0;
            sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [ambient-check-performance] " +
                $"{callCount} call(s) in the last 1s, " +
                $"{elapsedMs:0.00}ms total ({avgMsPerCall:0.###}ms/call)" +
                (isSlow ? $" - SLOW (>= {config.SlowAmbientCheckThresholdMilliseconds}ms/second)" : ""));
        }

        private static bool IsRestricted(string code)
        {
            var parts = config.SurfaceCreatureSpawnsToPrevent;
            for (int i = 0; i < parts.Count; i++)
            {
                string part = parts[i];
                if (part.Length > 0 && part[part.Length - 1] == '*')
                {
                    if (code.StartsWith(part.Substring(0, part.Length - 1))) return true;
                }
                else if (code.Contains(part))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsExposedToSky(Vec3d pos)
        {
            BlockPos bpos = pos.AsBlockPos;
            int rainHeight = sapi.World.BlockAccessor.GetRainMapHeightAt(bpos);
            return bpos.Y >= rainHeight;
        }

        private static bool IsInsideEnclosedRoom(Vec3d pos)
        {
            if (roomRegistry == null) return false;
            var room = roomRegistry.GetRoomForPosition(pos.AsBlockPos);
            return room != null && room.ExitCount == 0;
        }

        private static void Log(string code, Vec3d pos, bool allow, string reason)
        {
            if (!config.LogSpawnChecks) return;

            sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [ambient-spawn-check] code=\"{code}\" " +
                $"pos=({pos.X:0},{pos.Y:0},{pos.Z:0}) -> {(allow ? "ALLOW" : "DENY")} ({reason})");
        }
    }
}
