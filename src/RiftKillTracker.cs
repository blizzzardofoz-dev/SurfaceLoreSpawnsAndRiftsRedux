using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SurfaceLoreSpawnsAndRiftsRedux
{
    /// <summary>
    /// Tracks, per rift, how many of its own RiftSurfaceSpawner-tagged
    /// spawns (RiftSurfaceSpawner.OwningRiftIdAttribute) a player has
    /// killed, via the vanilla ICoreAPI.Event.OnEntityDeath hook (the same
    /// event vanilla's own SystemTemporalStability uses for its stability-
    /// recovery-on-kill mechanic). The same kill count feeds two
    /// independently-toggleable effects: EnableRiftWeakening fades the
    /// rift's spawn chance down as kills approach its own weaken threshold
    /// (GetWeaknessFactor), and EnableRiftClosing closes the rift outright
    /// once kills reach its own close threshold - either, both, or neither
    /// can be on, and the two kill thresholds are entirely independent of
    /// each other.
    ///
    /// Neither threshold is one shared constant: the first time a rift's
    /// kill count is needed for each purpose, a value is rolled once
    /// (KillsToCloseRift +/- KillsToCloseRiftVariance for closing,
    /// KillsToFullyWeakenRift +/- KillsToFullyWeakenRiftVariance for
    /// weakening) and cached for that rift's lifetime - see
    /// GetCloseThreshold/GetWeakenThreshold - so identical-looking rifts
    /// don't all take exactly the same number of kills to weaken or close.
    ///
    /// "Closed" sets Size = 0 - precisely what BlockEntityRiftWard does
    /// (stops it rendering, stops it being a valid target for anything,
    /// including RiftSurfaceSpawner's own Size &lt;= 0 check) - plus gives
    /// it a fresh despawn timer (see CloseRift/
    /// RiftCloseDespawnDelayInCalendarHours) so the area it was
    /// defending eventually opens back up to a new rift rather than staying
    /// permanently suppressed. A rift still counts toward a player's rift
    /// cap for as long as it exists in ModSystemRifts' registry regardless
    /// of Size (KillOldRifts doesn't check it), so closing one still isn't
    /// instant - the slot frees up once the new DieAtTotalHours actually
    /// arrives, not on the next housekeeping tick.
    ///
    /// Kill counts and rolled thresholds are deliberately NOT persisted
    /// across a server restart (an earlier draft persisted kill counts, and
    /// that was a real bug): ModSystemRifts' own RiftId counter isn't
    /// restored from the reloaded rift list on startup, so it can hand out
    /// an already-used ID to a brand new rift at a completely different
    /// location, silently overwriting the old dictionary entries. Rifts
    /// themselves are already exposed to this, and there's no reliable way
    /// from out here to tell "the same rift continuing" apart from "an
    /// unrelated rift that inherited its number" - so rather than risk a
    /// fresh rift inheriting a dead one's kill progress (or rolled
    /// threshold), every rift simply starts unweakened, with fresh
    /// thresholds, each time the server starts. In-memory state still gets
    /// pruned periodically (see PruneDeadRifts) so these dictionaries don't
    /// grow for the life of a long-running server session.
    /// </summary>
    public class RiftKillTracker
    {
        private readonly ICoreServerAPI sapi;
        private readonly SurfaceLoreSpawnsAndRiftsReduxConfig config;
        private readonly ModSystemRifts riftSystem;
        private readonly Random rand = new Random();

        private readonly Dictionary<int, int> killCountByRiftId = new Dictionary<int, int>();
        private readonly Dictionary<int, int> closeThresholdByRiftId = new Dictionary<int, int>();
        private readonly Dictionary<int, int> weakenThresholdByRiftId = new Dictionary<int, int>();

        public RiftKillTracker(ICoreServerAPI sapi, SurfaceLoreSpawnsAndRiftsReduxConfig config, ModSystemRifts riftSystem)
        {
            this.sapi = sapi;
            this.config = config;
            this.riftSystem = riftSystem;

            sapi.Event.OnEntityDeath += OnEntityDeath;
            // Not persisted (see class comment for why) - GameWorldSave is
            // only used as a convenient recurring tick to prune the
            // in-memory dictionaries, not to write them to disk.
            sapi.Event.GameWorldSave += PruneDeadRifts;
        }

        // Drops entries for rifts no longer in ModSystemRifts' registry
        // (expired naturally, or closed by us) - keeps these dictionaries
        // from growing by one entry per rift for the life of a long-running
        // server session. Only bothers on the save tick, not every kill -
        // this is just tidiness, not correctness (a stale entry costs a
        // few bytes, nothing more).
        private void PruneDeadRifts()
        {
            var liveIds = new HashSet<int>(riftSystem.ServerRifts.Select(r => r.RiftId));
            var staleIds = killCountByRiftId.Keys
                .Concat(closeThresholdByRiftId.Keys)
                .Concat(weakenThresholdByRiftId.Keys)
                .Where(id => !liveIds.Contains(id))
                .Distinct()
                .ToList();
            foreach (var id in staleIds)
            {
                killCountByRiftId.Remove(id);
                closeThresholdByRiftId.Remove(id);
                weakenThresholdByRiftId.Remove(id);
            }
        }

        private void OnEntityDeath(Entity entity, DamageSource damageSource)
        {
            if (!(damageSource?.GetCauseEntity() is EntityPlayer)) return; // only kills by a player's own hand count

            int riftId = entity.Attributes.GetInt(RiftSurfaceSpawner.OwningRiftIdAttribute, int.MinValue);
            if (riftId == int.MinValue) return; // not one of ours

            killCountByRiftId.TryGetValue(riftId, out int count);
            count++;

            int closeThreshold = GetCloseThreshold(riftId);
            if (config.EnableRiftClosing && count >= closeThreshold)
            {
                CloseRift(riftId, entity, closeThreshold);
                killCountByRiftId.Remove(riftId);
                closeThresholdByRiftId.Remove(riftId);
                weakenThresholdByRiftId.Remove(riftId);
                return;
            }

            killCountByRiftId[riftId] = count;

            if (config.LogSpawnChecks)
            {
                string closeNote = config.EnableRiftClosing ? $"{count}/{closeThreshold} to close" : $"{count} tracked (closing disabled)";
                string weaknessNote = config.EnableRiftWeakening ? $", weakness factor now {GetWeaknessFactor(riftId):P0}" : "";
                sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [rift-kill] rift={riftId} killed " +
                    $"{entity.Code} - {closeNote}{weaknessNote}.");
            }
        }

        private void CloseRift(int riftId, Entity lastKill, int closeThreshold)
        {
            var rift = riftSystem.ServerRifts.FirstOrDefault(r => r.RiftId == riftId);
            if (rift == null) return; // already gone (expired naturally in the same window) - nothing to close

            rift.Size = 0;

            // A fresh despawn timer, entirely decoupled from however much
            // natural lifespan the rift had left - see
            // RiftCloseDespawnDelayInCalendarHours's comment in
            // Config.cs for why (closing a rift that was about to expire
            // anyway should still buy real breathing room). Floored just
            // above zero so a close can never free the rift's cap slot
            // instantly regardless of how average/variance are configured.
            double variance = config.RiftCloseDespawnDelayVarianceInCalendarHours;
            double offset = variance > 0 ? (rand.NextDouble() * 2.0 - 1.0) * variance : 0.0;
            double hoursUntilDespawn = Math.Max(0.01, config.RiftCloseDespawnDelayInCalendarHours + offset);
            rift.DieAtTotalHours = sapi.World.Calendar.TotalHours + hoursUntilDespawn;

            if (config.NotifyNearbyPlayersOnRiftClose)
            {
                NotifyNearbyPlayers(rift.Position, "The rift shudders and collapses in on itself.");
            }

            if (config.LogSpawnChecks)
            {
                sapi.Logger.Notification($"[SurfaceLoreSpawnsAndRiftsRedux] [rift-closed] rift {riftId} closed after " +
                    $"{closeThreshold} of its spawns were killed (last: {lastKill.Code}) - despawns in " +
                    $"{hoursUntilDespawn:0.00} calendar hours.");
            }
        }

        private void NotifyNearbyPlayers(Vec3d pos, string message)
        {
            double radiusSq = config.RiftCreatureSpawnMaxDistanceFromPlayer * config.RiftCreatureSpawnMaxDistanceFromPlayer;
            foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
            {
                var entity = player?.Entity;
                if (entity == null) continue;
                if (entity.Pos.SquareDistanceTo(pos) <= radiusSq)
                {
                    player.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
                }
            }
        }

        /// <summary>
        /// 1.0 (unweakened) fading down toward (1.0 - RiftWeaknessMaxReduction)
        /// as kills approach this rift's own weaken threshold (see
        /// GetWeakenThreshold) - e.g. the default RiftWeaknessMaxReduction=0.5
        /// means a fully-weakened rift's spawn chance bottoms out at half
        /// its normal value. Completely independent of the close threshold/
        /// EnableRiftClosing (see the class comment). Always 1.0 while
        /// EnableRiftWeakening is false, or for a rift with no tracked
        /// kills. RiftWeaknessMaxReduction can be pushed all the way to 1.0
        /// if you want a fully-weakened rift to stop spawning entirely - the
        /// default of 0.5 deliberately stops short of that so there's
        /// always something left to fight.
        /// </summary>
        public double GetWeaknessFactor(int riftId)
        {
            if (!config.EnableRiftWeakening) return 1.0;
            if (!killCountByRiftId.TryGetValue(riftId, out int count) || count <= 0) return 1.0;
            int weakenThreshold = GetWeakenThreshold(riftId);
            double progress = Math.Min(1.0, count / (double)weakenThreshold);
            double reduction = progress * config.RiftWeaknessMaxReduction;
            return Math.Max(0.0, 1.0 - reduction);
        }

        // Rolls (once per rift, then caches) how many kills THIS rift
        // personally needs before EnableRiftClosing closes it -
        // KillsToCloseRift +/- KillsToCloseRiftVariance.
        private int GetCloseThreshold(int riftId)
        {
            if (!closeThresholdByRiftId.TryGetValue(riftId, out int threshold))
            {
                threshold = RollThreshold(config.KillsToCloseRift, config.KillsToCloseRiftVariance);
                closeThresholdByRiftId[riftId] = threshold;
            }
            return threshold;
        }

        // Same idea as GetCloseThreshold, but for how many kills THIS rift
        // personally needs before it's fully weakened -
        // KillsToFullyWeakenRift +/- KillsToFullyWeakenRiftVariance.
        private int GetWeakenThreshold(int riftId)
        {
            if (!weakenThresholdByRiftId.TryGetValue(riftId, out int threshold))
            {
                threshold = RollThreshold(config.KillsToFullyWeakenRift, config.KillsToFullyWeakenRiftVariance);
                weakenThresholdByRiftId[riftId] = threshold;
            }
            return threshold;
        }

        // A whole number uniformly chosen from [average - variance, average
        // + variance], floored at 1 so a threshold can never roll to 0 or
        // below (which would close/weaken a rift on a kill count that
        // hasn't happened yet).
        private int RollThreshold(int average, int variance)
        {
            if (variance <= 0) return Math.Max(1, average);
            int offset = rand.Next(-variance, variance + 1);
            return Math.Max(1, average + offset);
        }
    }
}
