using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SurfaceLoreSpawnsAndRiftsRedux
{
    public class SurfaceLoreSpawnsAndRiftsReduxModSystem : ModSystem
    {
        private const string HarmonyId = "surfacelorespawnsandriftsredux";

        private Harmony harmony;
        private RiftSurfaceSpawner riftSpawner;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            base.StartServerSide(sapi);

            var config = sapi.LoadModConfig<SurfaceLoreSpawnsAndRiftsReduxConfig>("surfacelorespawnsandriftsredux.json");
            if (config == null) config = new SurfaceLoreSpawnsAndRiftsReduxConfig();
            ApplyDefaults(config);
            sapi.StoreModConfig(config, "surfacelorespawnsandriftsredux.json");

            harmony = new Harmony(HarmonyId);
            SurfaceSpawnGate.Apply(sapi, harmony, config);
            riftSpawner = new RiftSurfaceSpawner(sapi, config);
        }

        /// <summary>
        /// Fills in the default restricted-creature list if it's still null or
        /// empty after loading. Deliberately left without an inline field
        /// initializer in the config class: giving a List field a non-empty
        /// default causes LoadModConfig's deserialization to merge the saved
        /// JSON's items into that existing default rather than replace it,
        /// which (per the same lesson learned in TemporalEmissionsConfig)
        /// would make the list grow every restart. Leaving the field null
        /// sidesteps this since there's nothing to merge into.
        /// </summary>
        private static void ApplyDefaults(SurfaceLoreSpawnsAndRiftsReduxConfig config)
        {
            if (config.SurfaceCreatureSpawnsToPrevent == null || config.SurfaceCreatureSpawnsToPrevent.Count == 0)
            {
                // Wildcards, not bare substrings, so the shipped default
                // itself teaches the syntax - a player opening the JSON
                // sees exactly how to add/remove a creature family rather
                // than having to find the comment in Config.cs first.
                config.SurfaceCreatureSpawnsToPrevent = new List<string>
                {
                    "game:drifter*",
                    "game:shiver*",
                    "game:bowtorn*",
                };
            }

            if (config.RiftCreaturesByActivityLevel == null || config.RiftCreaturesByActivityLevel.Count == 0)
            {
                // Tier 0 ("surface"/"normal") through tier 3 ("corrupt"),
                // stepped up one tier per activity level and topping out at
                // tier 3 exclusively for apocalyptic - so apocalyptic reads
                // as a distinct peak (a creature type nothing below it ever
                // produces), not just "veryhigh with more rifts nearby"
                // (which it also is, via vanilla's own mobSpawnMul scaling
                // rift density - see the class comment on RiftSurfaceSpawner
                // for how that stacks with this). Note the tier-0 variant is
                // spelled differently per creature in the actual vanilla
                // assets - drifter uses "-normal", shiver and bowtorn both
                // use "-surface". Tier 4+ ("nightmare" and the rare oddball
                // variants like drifter's "double-headed") are deliberately
                // left out of every level here - add them yourself if you
                // want the very top end in the mix, e.g. by adding
                // "game:drifter*" to a level's list to wildcard-include
                // every loaded drifter variant instead of listing tiers by
                // hand (see RiftCreaturesByActivityLevel's comment in
                // Config.cs).
                var tier0 = new List<string> { "game:drifter-normal", "game:shiver-surface", "game:bowtorn-surface" };
                var tier1 = new List<string> { "game:drifter-deep", "game:shiver-deep", "game:bowtorn-deep" };
                var tier2 = new List<string> { "game:drifter-tainted", "game:shiver-tainted", "game:bowtorn-tainted" };
                var tier3 = new List<string> { "game:drifter-corrupt", "game:shiver-corrupt", "game:bowtorn-corrupt" };

                config.RiftCreaturesByActivityLevel = new Dictionary<string, List<string>>
                {
                    { "calm", Combine(tier0) },
                    { "low", Combine(tier0) },
                    { "medium", Combine(tier0, tier1) },
                    { "high", Combine(tier0, tier1, tier2) },
                    { "veryhigh", Combine(tier0, tier1, tier2) },
                    { "apocalyptic", Combine(tier0, tier1, tier2, tier3) },
                };
            }
        }

        private static List<string> Combine(params List<string>[] tiers)
        {
            var combined = new List<string>();
            foreach (var tier in tiers) combined.AddRange(tier);
            return combined;
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            base.Dispose();
        }
    }
}
