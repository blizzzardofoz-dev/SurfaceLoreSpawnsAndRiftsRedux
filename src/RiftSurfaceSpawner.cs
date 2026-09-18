using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SurfaceLoreSpawnsAndRiftsRedux
{
    /// <summary>
    /// Periodically tries to spawn a creature right at (or as close as
    /// possible to) each active rift, picking and validating the position
    /// itself rather than asking the vanilla ambient spawner to happen to
    /// roll one nearby. This is the deliberate, sole source of "spawns from
    /// rifts" in this mod - see SurfaceSpawnGate for why the ambient
    /// spawner's own rift exception is too rare and hard to reason about to
    /// build on directly.
    ///
    /// Search strategy: candidate offsets around the rift's exact position
    /// are precomputed once (at CreatureSpawnMaxDistanceFromRift) and sorted
    /// closest-to-center first, so the search always tries the rift's own
    /// position before anywhere else, and only spreads outward as far as
    /// needed to find a spot that isn't too close to a player and isn't
    /// inside an enclosed Room. No ground-snapping is done deliberately -
    /// rifts float a bit above the ground, so a spawn landing there just
    /// falls into place under its own physics, like popping out of the
    /// rift, which is the look being asked for here.
    ///
    /// Deliberately has no light-level check of any kind (unlike the
    /// vanilla mechanic this replaces) - see EnableCustomRiftMechanics' doc
    /// comment in Config.cs for why: only a dead/warded rift (Size &lt;= 0)
    /// turns this off, not a nearby torch.
    ///
    /// A rift is skipped entirely unless an online player is within
    /// RiftCreatureSpawnMaxDistanceFromPlayer of it - rifts can otherwise sit loaded up to
    /// ~190 blocks from a player (ModSystemRifts.despawnDistance), and
    /// without this check the server would be doing placement work and
    /// creating entities for rifts nobody is anywhere near.
    ///
    /// The per-rift population cap (MaxSpawnsPerRiftDaytime/Nighttime)
    /// counts only creatures THIS rift spawned - each one is tagged with
    /// its owning rift's RiftId at spawn time (see OwningRiftIdAttribute),
    /// and CountRiftOwnedSpawns only counts matching tags. That's
    /// deliberate: rift activity level (peaceful through apocalyptic)
    /// already controls how many rifts can cluster together
    /// (ModSystemRiftWeather/ModSystemRifts.GetRiftCap), so a cluster of
    /// rifts a few blocks apart during an apocalyptic pattern should
    /// produce proportionally more spawns, not get squeezed down to one
    /// shared quota because they all "see" each other's spawns. Tagging by
    /// owner keeps each rift's cap independent without this mod having to
    /// read or duplicate that difficulty scaling itself.
    ///
    /// Which creature TIERS a rift can produce, though, IS read from that
    /// same activity level (RiftCreaturesByActivityLevel, keyed by
    /// ModSystemRiftWeather.CurrentPattern.Code) - that's a different
    /// question (how tough, not how many) that the base game doesn't
    /// already answer, so this fills it in rather than duplicating
    /// anything vanilla does.
    ///
    /// The spawn chance itself isn't flat: GetTerritoryModifier swaps in a
    /// flat multiplier - RiftOuterTerritoryCreatureSpawnChanceModifier or
    /// RiftInnerTerritoryCreatureSpawnChanceModifier - once the nearest
    /// player crosses into one of a rift's two territory rings, so a rift
    /// feels like it's actively reacting to you rather than being a static
    /// spawn point (and, configured the other way, can just as easily
    /// discourage camping instead). That's also why the check interval is
    /// short (2s by default, not the original 15s) - a long interval means
    /// the territory swap can't react to you actually closing the distance
    /// until the next check happens to land. The baseline chances were
    /// divided down to compensate for the faster interval, so the rate far
    /// from any rift is unchanged; only the close-up behavior is new.
    ///
    /// The other direction a rift's danger can move: RiftKillTracker
    /// weakens (and eventually closes) a specific rift as its own tagged
    /// spawns are killed by a player. Outside both territory rings,
    /// GetWeaknessFactor's result directly reduces the baseline chance as
    /// you'd expect; inside either ring, weakness instead linearly
    /// amplifies that ring's own modifier (see GetTerritoryModifier) and
    /// the plain baseline reduction is skipped entirely - the two effects
    /// never compose, so one can never accidentally cancel the other out.
    /// See RiftKillTracker for the kill-tracking mechanics - this class
    /// only asks it "how weak is this rift right now" and otherwise
    /// doesn't know it exists.
    /// </summary>
    public class RiftSurfaceSpawner
    {
        internal const string OwningRiftIdAttribute = "surfacelorespawnsandriftsredux:owningRiftId";

        private readonly ICoreServerAPI sapi;
        private readonly SurfaceLoreSpawnsAndRiftsReduxConfig config;
        private readonly ModSystemRifts riftSystem;
        private readonly ModSystemRiftWeather riftWeatherSystem;
        private readonly EntityPartitioning entityPartitioning;
        private readonly RoomRegistry roomRegistry;
        private readonly RiftKillTracker killTracker;
        private readonly CollisionTester collisionTester = new CollisionTester();
        private readonly Random rand = new Random();

        private readonly Dictionary<string, EntityProperties[]> resolvedTypesByActivity = new Dictionary<string, EntityProperties[]>();
        private EntityProperties[] fallbackEntityTypes = Array.Empty<EntityProperties>();
        private HashSet<AssetLocation> resolvedCodeSet = new HashSet<AssetLocation>();
        private Vec3i[] offsetsByDistance = Array.Empty<Vec3i>();

        public RiftSurfaceSpawner(ICoreServerAPI sapi, SurfaceLoreSpawnsAndRiftsReduxConfig config)
        {
            this.sapi = sapi;
            this.config = config;

            if (!config.EnableCustomRiftMechanics) return;

            riftSystem = sapi.ModLoader.GetModSystem<ModSystemRifts>();
            riftWeatherSystem = sapi.ModLoader.GetModSystem<ModSystemRiftWeather>();
            entityPartitioning = sapi.ModLoader.GetModSystem<EntityPartitioning>();
            roomRegistry = sapi.ModLoader.GetModSystem<RoomRegistry>();

            if (riftSystem == null || entityPartitioning == null)
            {
                sapi.Logger.Error("[SurfaceLoreSpawnsAndRiftsRedux] Could not find ModSystemRifts and/or " +
                    "EntityPartitioning - the rift surface spawner is disabled.");
                return;
            }

            ResolveEntityTypes();
            if (fallbackEntityTypes.Length == 0)
            {
                sapi.Logger.Error("[SurfaceLoreSpawnsAndRiftsRedux] None of the configured " +
                    "RiftCreaturesByActivityLevel entries resolved to a real entity - the rift surface " +
                    "spawner is disabled.");
                return;
            }

            BuildOffsetsByDistance();

            if (config.EnableRiftWeakening || config.EnableRiftClosing)
            {
                killTracker = new RiftKillTracker(sapi, config, riftSystem);
            }

            int intervalMs = Math.Max(1000, (int)(config.RiftCreatureSpawnCheckIntervalSeconds * 1000));
            sapi.Event.RegisterGameTickListener(OnCheck, intervalMs);

            string killTrackingMsg;
            if (config.EnableRiftWeakening && config.EnableRiftClosing)
            {
                killTrackingMsg = $"weakening + closing enabled - {config.KillsToFullyWeakenRift}±{config.KillsToFullyWeakenRiftVariance} kills fully " +
                    $"weakens, {config.KillsToCloseRift}±{config.KillsToCloseRiftVariance} kills closes a rift (rolled per rift).";
            }
            else if (config.EnableRiftWeakening)
            {
                killTrackingMsg = $"weakening enabled ({config.KillsToFullyWeakenRift}±{config.KillsToFullyWeakenRiftVariance} kills fully weakens, rolled per rift), " +
                    "closing disabled - rifts never close from kills.";
            }
            else if (config.EnableRiftClosing)
            {
                killTrackingMsg = $"closing enabled ({config.KillsToCloseRift}±{config.KillsToCloseRiftVariance} kills closes a rift, rolled per rift), weakening " +
                    "disabled - full chance right up until closed.";
            }
            else
            {
                killTrackingMsg = "weakening and closing both disabled - killing a rift's spawns has no effect on it.";
            }

            sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] Rift surface spawner active: checking every " +
                $"{config.RiftCreatureSpawnCheckIntervalSeconds}s for rifts within {config.RiftCreatureSpawnMaxDistanceFromPlayer} blocks of " +
                $"a player, spawning within {config.CreatureSpawnMaxDistanceFromRift} blocks of the rift " +
                $"(baseline day {config.RiftCreatureDaytimeSpawnChancePerCheck:P1}/max {config.MaxSpawnsPerRiftDaytime}, " +
                $"night {config.RiftCreatureNighttimeSpawnChancePerCheck:P1}/max {config.MaxSpawnsPerRiftNighttime} " +
                $"within {config.RiftSpawnCountRadius} blocks), territory modifiers " +
                $"{config.RiftOuterTerritoryCreatureSpawnChanceModifier:0.##}x within {config.RiftOuterTerritoryRadius} blocks / " +
                $"{config.RiftInnerTerritoryCreatureSpawnChanceModifier:0.##}x within {config.RiftInnerTerritoryRadius} blocks of a rift, " +
                $"tiers by activity level " +
                $"({(riftWeatherSystem != null ? "ModSystemRiftWeather found" : "NOT found, using the fallback pool")}), " +
                killTrackingMsg);
        }

        /// <summary>
        /// Resolves every code in RiftCreaturesByActivityLevel once at
        /// startup, per activity level, plus a distinct-by-code fallback
        /// pool (the union of every level) used when the activity system
        /// is unavailable or the current code isn't a configured key. An
        /// entry ending in "*" is a wildcard: every currently loaded entity
        /// (sapi.World.EntityTypes) whose code starts with that prefix is
        /// added, instead of resolving one exact code.
        /// </summary>
        private void ResolveEntityTypes()
        {
            var fallback = new Dictionary<AssetLocation, EntityProperties>();

            foreach (var kv in config.RiftCreaturesByActivityLevel)
            {
                var list = new List<EntityProperties>();

                foreach (var codeStr in kv.Value)
                {
                    if (codeStr.Length > 0 && codeStr[codeStr.Length - 1] == '*')
                    {
                        string prefix = codeStr.Substring(0, codeStr.Length - 1);
                        int matchCount = 0;

                        foreach (var candidate in sapi.World.EntityTypes)
                        {
                            if (!candidate.Code.ToString().StartsWith(prefix)) continue;
                            list.Add(candidate);
                            resolvedCodeSet.Add(candidate.Code);
                            fallback[candidate.Code] = candidate;
                            matchCount++;
                        }

                        if (matchCount == 0)
                        {
                            sapi.Logger.Warning($"[SurfaceLoreSpawnsAndRiftsRedux] RiftCreaturesByActivityLevel[\"{kv.Key}\"] " +
                                $"wildcard entry \"{codeStr}\" matched no loaded entity - skipped.");
                        }
                        continue;
                    }

                    var code = new AssetLocation(codeStr);
                    var resolved = sapi.World.GetEntityType(code);
                    if (resolved == null)
                    {
                        sapi.Logger.Warning($"[SurfaceLoreSpawnsAndRiftsRedux] RiftCreaturesByActivityLevel[\"{kv.Key}\"] " +
                            $"entry \"{codeStr}\" did not resolve to a known entity - skipped.");
                        continue;
                    }
                    list.Add(resolved);
                    resolvedCodeSet.Add(resolved.Code);
                    fallback[resolved.Code] = resolved;
                }

                resolvedTypesByActivity[kv.Key] = list.ToArray();
            }

            fallbackEntityTypes = fallback.Values.ToArray();
        }

        /// <summary>
        /// The entity pool for whatever rift activity level is current, per
        /// ModSystemRiftWeather.CurrentPattern.Code - falling back to the
        /// combined pool of every configured level if that system isn't
        /// available, isn't enabled (its config asset can be missing), the
        /// current pattern isn't set yet, or its code isn't a configured
        /// key. Wrapped defensively since CurrentPattern can throw if asked
        /// for before the weather system has chosen its first pattern.
        /// </summary>
        private EntityProperties[] GetEntityPoolForCurrentActivity()
        {
            if (riftWeatherSystem != null && riftWeatherSystem.Enabled)
            {
                try
                {
                    string code = riftWeatherSystem.CurrentPattern?.Code;
                    if (code != null && resolvedTypesByActivity.TryGetValue(code, out var pool) && pool.Length > 0)
                    {
                        return pool;
                    }
                }
                catch
                {
                    // CurrentPattern not chosen yet or riftweather.json missing entries - use the fallback below
                }
            }

            return fallbackEntityTypes;
        }

        /// <summary>
        /// All integer offsets within a sphere of CreatureSpawnMaxDistanceFromRift,
        /// sorted ascending by distance from center (so index 0 is always
        /// (0,0,0) - the rift's exact position), capped at
        /// MaxSpawnPositionCandidatesToCheck as a safety valve against an
        /// unreasonably large configured radius.
        /// </summary>
        private void BuildOffsetsByDistance()
        {
            int radius = Math.Max(0, (int)Math.Ceiling(config.CreatureSpawnMaxDistanceFromRift));
            double maxDistSq = config.CreatureSpawnMaxDistanceFromRift * config.CreatureSpawnMaxDistanceFromRift;

            var offsets = new List<Vec3i>();
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (dx * dx + dy * dy + dz * dz <= maxDistSq)
                        {
                            offsets.Add(new Vec3i(dx, dy, dz));
                        }
                    }
                }
            }

            offsets = offsets.OrderBy(o => o.X * o.X + o.Y * o.Y + o.Z * o.Z).ToList();

            if (offsets.Count > config.MaxSpawnPositionCandidatesToCheck)
            {
                sapi.Logger.Warning($"[SurfaceLoreSpawnsAndRiftsRedux] CreatureSpawnMaxDistanceFromRift={config.CreatureSpawnMaxDistanceFromRift} " +
                    $"produces {offsets.Count} candidate cells, above MaxSpawnPositionCandidatesToCheck=" +
                    $"{config.MaxSpawnPositionCandidatesToCheck} - only the closest {config.MaxSpawnPositionCandidatesToCheck} " +
                    "will ever be tried. Raise MaxSpawnPositionCandidatesToCheck if that's not enough.");
                offsets = offsets.Take(config.MaxSpawnPositionCandidatesToCheck).ToList();
            }

            offsetsByDistance = offsets.ToArray();
        }

        private void OnCheck(float dt)
        {
            bool trackPerformance = config.LogAllRiftCheckPerformance || config.LogSlowRiftCheckPerformance;
            Stopwatch stopwatch = trackPerformance ? Stopwatch.StartNew() : null;
            int inRangeCount = 0;
            int spawnAttemptCount = 0;

            var rifts = riftSystem.ServerRifts;
            for (int i = 0; i < rifts.Count; i++)
            {
                var rift = rifts[i];
                // Size <= 0 covers both a not-yet-manifested rift and one
                // zeroed out by a Rift Ward (BlockEntityRiftWard sets
                // Size = 0 on every rift that spawns in its radius) - either
                // way, nothing should come from it.
                if (rift.Size <= 0) continue;

                // Cheapest check first: skip rifts nobody is actually near
                // before doing any of the day/night rolls or placement work.
                // Reused below for the territory modifier, so this is a
                // distance, not just a bool. Bounded to the max range up
                // front (see NearestPlayerDistance) so this scales with how
                // many players are actually near THIS rift, not with the
                // server's total online player count.
                double nearestPlayerDist = NearestPlayerDistance(rift.Position, config.RiftCreatureSpawnMaxDistanceFromPlayer);
                if (nearestPlayerDist > config.RiftCreatureSpawnMaxDistanceFromPlayer) continue;
                inRangeCount++;

                double daylightFraction = GetDaylightFraction(rift.Position);
                double baseChance = Lerp(config.RiftCreatureNighttimeSpawnChancePerCheck, config.RiftCreatureDaytimeSpawnChancePerCheck, daylightFraction);
                double weakness = killTracker?.GetWeaknessFactor(rift.RiftId) ?? 1.0;
                double territoryModifier = GetTerritoryModifier(nearestPlayerDist, weakness, out string zoneName, out bool inZone);
                // Inside a territory ring, the ring's own (weakness-
                // amplified) modifier is the full multiplier - weakness
                // already went into computing it. Outside both rings,
                // weakness applies directly to the baseline as normal. The
                // two never compose - see the class comment for why.
                double appliedMultiplier = inZone ? territoryModifier : weakness;
                double chance = GameMath.Clamp((float)(baseChance * appliedMultiplier), 0f, 1f);

                double roll = rand.NextDouble();
                bool passed = roll <= chance;

                if (config.LogSpawnChecks)
                {
                    sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [rift-check] rift={rift.RiftId} " +
                        $"playerDist={nearestPlayerDist:0.#} daylight={daylightFraction:0.00} " +
                        $"base={baseChance:P2} weakness={weakness:P0} zone={zoneName} " +
                        $"appliedMultiplier={appliedMultiplier:0.00}x " +
                        $"final={chance:P2} roll={roll:P2} -> {(passed ? "PASS" : "FAIL")}");
                }

                if (!passed) continue;
                spawnAttemptCount++;

                int max = (int)Math.Round(Lerp(config.MaxSpawnsPerRiftNighttime,
                    config.MaxSpawnsPerRiftDaytime, daylightFraction));
                TrySpawnNear(rift, max);
            }

            if (trackPerformance)
            {
                stopwatch.Stop();
                double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                bool isSlow = elapsedMs >= config.SlowRiftCheckThresholdMilliseconds;

                if (config.LogAllRiftCheckPerformance || (config.LogSlowRiftCheckPerformance && isSlow))
                {
                    sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [rift-check-performance] " +
                        $"{rifts.Count} total rift(s), {inRangeCount} within range, {spawnAttemptCount} spawn attempt(s), " +
                        $"{elapsedMs:0.00}ms" +
                        (isSlow ? $" - SLOW (>= {config.SlowRiftCheckThresholdMilliseconds}ms)" : ""));
                }
            }
        }

        /// <summary>
        /// Roughly 0 (full dark) to 1 (full daylight) - see
        /// IGameCalendar.GetDayLightStrength, which factors in latitude and
        /// season (so it stays correct in "always day"/"always night"
        /// regions) and fades smoothly through dawn/dusk rather than
        /// flipping at a fixed sun angle. Clamped since the underlying
        /// value can run slightly above 1 near some latitudes/seasons and
        /// this is used directly as an interpolation factor.
        /// </summary>
        private double GetDaylightFraction(Vec3d pos)
        {
            float daylight = sapi.World.Calendar.GetDayLightStrength(pos.X, pos.Z);
            return GameMath.Clamp(daylight, 0f, 1f);
        }

        /// <summary>
        /// Picks which of a rift's two territory rings the nearest player is
        /// standing in (inner checked first, since it should be the smaller
        /// of the two - see RiftInnerTerritoryRadius's comment), and returns
        /// that ring's modifier linearly interpolated toward its weakness-
        /// amplified target - see the long comment on
        /// RiftOuterTerritoryRadius in Config.cs for the full reasoning and
        /// worked numbers. Reports which ring (or "none"/"disabled") via
        /// <paramref name="zoneName"/> for logging, and whether a ring
        /// applied at all via <paramref name="inZone"/> - when true, the
        /// caller should use the returned value AS the full chance
        /// multiplier instead of separately applying weakness, since the
        /// interpolation already accounts for it.
        /// </summary>
        private double GetTerritoryModifier(double nearestPlayerDist, double weakness, out string zoneName, out bool inZone)
        {
            if (config.RiftOuterTerritoryRadius <= 0)
            {
                zoneName = "disabled";
                inZone = false;
                return 1.0;
            }

            double rawModifier;
            if (nearestPlayerDist <= config.RiftInnerTerritoryRadius)
            {
                zoneName = "inner";
                rawModifier = config.RiftInnerTerritoryCreatureSpawnChanceModifier;
            }
            else if (nearestPlayerDist <= config.RiftOuterTerritoryRadius)
            {
                zoneName = "outer";
                rawModifier = config.RiftOuterTerritoryCreatureSpawnChanceModifier;
            }
            else
            {
                zoneName = "none";
                inZone = false;
                return 1.0;
            }

            inZone = true;

            // How far toward the weakness floor this rift currently is - 0
            // at full health, 1 fully weakened. The same fraction
            // GetWeaknessFactor itself is built from, so "halfway to fully
            // weakened by kill count" and "halfway through this
            // interpolation" always agree.
            double floorWeakness = Math.Max(0.0001, 1.0 - config.RiftWeaknessMaxReduction);
            double progress = floorWeakness >= 1.0
                ? 0.0
                : GameMath.Clamp((float)((1.0 - weakness) / (1.0 - floorWeakness)), 0f, 1f);

            double targetModifier;
            if (rawModifier > 1.0) targetModifier = rawModifier / floorWeakness;
            else if (rawModifier < 1.0) targetModifier = rawModifier * floorWeakness;
            else targetModifier = 1.0;

            return Lerp(rawModifier, targetModifier, progress);
        }

        private static double Lerp(double atZero, double atOne, double t) => atZero + (atOne - atZero) * t;

        private void TrySpawnNear(Rift rift, int maxNearby)
        {
            int nearbyCount = CountRiftOwnedSpawns(rift);
            if (nearbyCount >= maxNearby)
            {
                if (config.LogSpawnChecks)
                {
                    sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [rift-check] rift={rift.RiftId} " +
                        $"rolled a spawn but population cap already reached ({nearbyCount}/{maxNearby}).");
                }
                return;
            }

            var pool = GetEntityPoolForCurrentActivity();
            if (pool.Length == 0) return; // already logged as an error at startup if this is reachable

            var entityType = pool[rand.Next(pool.Length)];

            for (int i = 0; i < offsetsByDistance.Length; i++)
            {
                var offset = offsetsByDistance[i];
                Vec3d candidate = new Vec3d(
                    rift.Position.X + offset.X,
                    rift.Position.Y + offset.Y,
                    rift.Position.Z + offset.Z);

                if (IsValidPosition(candidate, entityType))
                {
                    SpawnOneAt(entityType, candidate, rift.RiftId);
                    return;
                }
            }

            if (config.LogSpawnChecks)
            {
                sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [rift-check] rift={rift.RiftId} rolled a " +
                    $"spawn ({entityType.Code}) but found no valid position among {offsetsByDistance.Length} " +
                    "candidates (too close to a player, inside a room, or colliding everywhere tried).");
            }
        }

        /// <summary>
        /// Counts only creatures tagged with THIS rift's RiftId (see
        /// OwningRiftIdAttribute), within RiftSpawnCountRadius - wide
        /// enough to still count one that's wandered off a bit (per-rift
        /// now, so a wide radius no longer risks counting a neighboring
        /// rift's spawns too), but bounded rather than an unbounded world
        /// scan. A creature this mod didn't spawn (ambient, storm, another
        /// mod) never carries the tag and is never counted here.
        /// </summary>
        private int CountRiftOwnedSpawns(Rift rift)
        {
            int count = 0;
            entityPartitioning.WalkEntities(rift.Position, config.RiftSpawnCountRadius, (e) =>
            {
                if (!resolvedCodeSet.Contains(e.Code)) return true;
                if (e.Attributes.GetInt(OwningRiftIdAttribute, int.MinValue) != rift.RiftId) return true;
                count++;
                return true;
            }, EnumEntitySearchType.Creatures);
            return count;
        }

        /// <summary>
        /// Distance to the closest online player within maxRadius, or
        /// double.MaxValue if none are that close - matches
        /// NearestRiftDistance's own "nothing found" convention in
        /// vanilla's TemporalStability.cs. Uses EntityPartitioning's
        /// spatial grid (the same index CountRiftOwnedSpawns already
        /// relies on) rather than a raw loop over every online player, so
        /// the cost scales with how many players happen to be near THIS
        /// position, not with the server's total player count - matters
        /// once a server has many players spread across a large map, since
        /// this runs for every active rift on every check.
        /// </summary>
        private double NearestPlayerDistance(Vec3d pos, double maxRadius)
        {
            Entity nearest = entityPartitioning.GetNearestEntity(pos, maxRadius, e => e is EntityPlayer, EnumEntitySearchType.Creatures);
            return nearest?.Pos.DistanceTo(pos) ?? double.MaxValue;
        }

        private bool IsValidPosition(Vec3d candidate, EntityProperties entityType)
        {
            if (IsTooCloseToAnyPlayer(candidate)) return false;

            if (config.PreventSpawnsInsideRooms && roomRegistry != null)
            {
                var room = roomRegistry.GetRoomForPosition(candidate.AsBlockPos);
                if (room != null && room.ExitCount == 0) return false; // fully enclosed - e.g. inside a player's base
            }

            Cuboidf collisionBox = entityType.SpawnCollisionBox.OmniNotDownGrowBy(0.1f);
            if (collisionTester.IsColliding(sapi.World.BlockAccessor, collisionBox, candidate, false)) return false;

            return true;
        }

        private bool IsTooCloseToAnyPlayer(Vec3d pos)
        {
            // <= 0 means "no exclusion" (same convention as
            // RiftOuterTerritoryRadius) - skip the search entirely rather
            // than asking EntityPartitioning to do a zero/negative-radius
            // lookup, and (at the default of 0.0) avoid a spatial query at
            // all for every single placement candidate.
            if (config.RiftCreatureSpawnMinDistanceFromPlayer <= 0) return false;

            return entityPartitioning.GetNearestEntity(pos, config.RiftCreatureSpawnMinDistanceFromPlayer,
                e => e is EntityPlayer, EnumEntitySearchType.Creatures) != null;
        }

        private void SpawnOneAt(EntityProperties entityType, Vec3d spawnPos, int owningRiftId)
        {
            Entity entity = sapi.ClassRegistry.CreateEntity(entityType);

            entity.Pos.SetPosWithDimension(spawnPos);
            entity.Pos.SetYaw((float)rand.NextDouble() * GameMath.TWOPI);
            entity.PositionBeforeFalling.Set(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
            entity.Attributes.SetString("origin", "riftvicinity");
            entity.Attributes.SetInt(OwningRiftIdAttribute, owningRiftId);

            sapi.World.SpawnEntity(entity);

            if (config.LogSpawnChecks)
            {
                sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [rift-spawn] spawned {entityType.Code} at " +
                    $"({spawnPos.X:0.#},{spawnPos.Y:0.#},{spawnPos.Z:0.#}).");
            }
        }
    }
}
