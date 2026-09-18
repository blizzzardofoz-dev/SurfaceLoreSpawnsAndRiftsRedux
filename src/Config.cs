using System.Collections.Generic;

namespace SurfaceLoreSpawnsAndRiftsRedux
{
    /// <summary>
    /// Serialized to/from ModConfig/surfacelorespawnsandriftsredux.json. Edit
    /// that file directly and restart the server to change any value - no
    /// rebuild needed.
    /// </summary>
    public class SurfaceLoreSpawnsAndRiftsReduxConfig
    {
        // ---- Ambient spawn gate (SurfaceSpawnGate) ----

        // Master switch for the whole ambient spawn gate. When false, this
        // mod never touches vanilla's own ambient spawning at all - no
        // creature is blocked or restricted from spawning the way it
        // always has, and RiftSurfaceSpawner (below) simply layers on top
        // as an ADDITIONAL source of spawns near rifts, rather than
        // replacing anything. Turn this off if you just want "extra
        // rift-driven danger" without touching how surface spawns normally
        // work.
        public bool PreventSurfaceSpawns = true;

        // Reject a candidate position that's inside a fully enclosed Room
        // (the same Room concept the game uses for temperature/cellars -
        // RoomRegistry, ExitCount == 0). Shared by both spawn paths in this
        // mod: here in SurfaceSpawnGate it's a backstop for the ambient
        // path, denying a restricted creature inside a small enclosed room
        // regardless of light level (for someone who forgets a torch in a
        // cellar); RiftSurfaceSpawner also checks it so a rift next to your
        // base can't put something inside your walls. Note RoomRegistry
        // only recognizes enclosed spaces up to roughly 14x14x14 (its own
        // MAXROOMSIZE) - a large open structure won't register as a "Room"
        // and isn't covered by this, light level is still what protects
        // those.
        public bool PreventSpawnsInsideRooms = true;

        // Which creatures the ambient spawn gate restricts. Matched
        // against the full resolved entity code (e.g. "game:drifter-
        // tainted"). Three ways to write an entry:
        //   "game:drifter*"      - wildcard, catches every tier of it
        //                          (normal, deep, tainted, etc.) - this is
        //                          what the shipped default below uses
        //   "drifter"            - plain substring, same effect as the
        //                          wildcard above, just without the "*"
        //   "game:drifter-normal" - exact code, matches only that one tier
        // Matching every tier (not just the surface one) by default is
        // deliberate here - see SurfaceSpawnGate's class comment for why
        // sky exposure, not tier name, is what actually decides "surface"
        // in this mod, so narrowing to one tier would leave gaps (a cave
        // above sea level can still produce a "-normal"/"-surface" tier
        // creature that should be caught here). Left null here - see
        // SurfaceLoreSpawnsAndRiftsReduxModSystem.ApplyDefaults for the
        // actual defaults ("game:drifter*", "game:shiver*",
        // "game:bowtorn*") and why they're set there rather than as an
        // inline initializer. The default deliberately uses wildcard
        // syntax (rather than the shorter plain-substring form) so opening
        // the live JSON shows a player exactly how to add or remove a
        // creature family themselves.
        public List<string> SurfaceCreatureSpawnsToPrevent = null;

        // When false (the default), a restricted creature may still spawn
        // via the ordinary ambient spawner while a temporal storm is
        // active - storms are one of the two intended sources of surface
        // lore-creature spawns, the other being RiftSurfaceSpawner below.
        // Set true to deny the ambient spawner for these creatures
        // unconditionally, storm or not, with no exception at all - every
        // surface lore-creature spawn then comes exclusively from
        // RiftSurfaceSpawner.
        public bool AlsoPreventSpawnsDuringTemporalStorms = false;

        // ---- Dedicated rift spawner (RiftSurfaceSpawner) ----

        // Not a real setting - purely a label so this is obvious from the
        // JSON file itself, without needing to open the code. Everything
        // from here to the end of the file is the optional rift-spawning
        // system: turn the whole thing off with EnableCustomRiftMechanics
        // just below, and the surface spawn prevention above
        // (PreventSurfaceSpawns and friends) keeps working completely on
        // its own either way.
        public string RiftSpawnerSectionNote =
            "Everything below this line is the optional custom rift spawning system - disable it with EnableCustomRiftMechanics without affecting the surface spawn prevention above.";

        // Master toggle for this mod's entire custom rift subsystem: the
        // dedicated spawner below, and (since they depend on there being
        // spawns to kill) weakening and closing along with it. Deliberately
        // NOT called "spawning enabled" - that phrasing is ambiguous
        // between "do Temporal Rifts themselves spawn" (vanilla, untouched
        // by this mod either way), "does anything spawn near a rift at
        // all" (also controlled by PreventSurfaceSpawns above, which
        // already blocks vanilla's own rift-proximity ambient exception
        // regardless of this setting), and what this field actually does -
        // turn this mod's own dedicated spawner on or off. "Custom
        // mechanics" names the whole bundle honestly instead of picking a
        // narrower word that undersells its scope.
        //
        // Has NO light-level condition of any kind (not even the
        // daylight-based check used elsewhere) - light level shouldn't be a
        // lever a player can pull with a torch. The only thing that stops
        // an individual rift's spawning is the rift itself: this spawner
        // skips any rift with Size <= 0, which is exactly what a Rift Ward
        // sets on every rift that spawns in its radius (BlockEntityRiftWard
        // hooks ModSystemRifts.OnRiftSpawned and does `rift.Size = 0`) - so
        // a Rift Ward is the intended, per-rift way to shut this off for a
        // given area, while this field is the global on/off switch.
        public bool EnableCustomRiftMechanics = true;

        // Maps each vanilla rift activity level (the pattern codes in
        // config/riftweather.json - calm, low, medium, high, veryhigh,
        // apocalyptic; see ModSystemRiftWeather.CurrentPattern, and the
        // wiki's Temporal Rift page for what each looks like in practice)
        // to the full, resolvable entity codes RiftSurfaceSpawner is
        // allowed to pick from while that activity level is current. One
        // code is chosen at random per spawn from whichever list matches
        // right now. Each entry can be an exact code (e.g.
        // "game:drifter-normal") or end in "*" as a wildcard (e.g.
        // "game:drifter*"), which at startup expands to every currently
        // loaded entity whose code starts with that prefix - a quick way
        // to pull in every tier of a creature, including ones this mod
        // doesn't curate by default (see the tier-4/oddball note in
        // SurfaceLoreSpawnsAndRiftsReduxModSystem.ApplyDefaults). Left
        // null here - see ApplyDefaults for the actual defaults (calm/low
        // = tier 0 only, medium = tier 0-1, high/veryhigh = tier 0-2,
        // apocalyptic = tier 0-3 exclusively) and why they're set there.
        // If ModSystemRiftWeather isn't available (e.g. riftweather.json
        // missing) or the current activity code isn't a key here, falls
        // back to the combined list of every code across every level, so
        // this never silently spawns nothing just because the activity
        // system is unreachable.
        public Dictionary<string, List<string>> RiftCreaturesByActivityLevel = null;

        // How far from the rift's exact center a spawn can land, at most.
        // The search always starts AT the rift's own position (which
        // typically floats a bit above the ground - a spawn landing there
        // just falls to the ground on its own, like popping out of the
        // rift) and expands outward through the surrounding blocks only as
        // far as needed to find a valid spot, up to this distance - so
        // spawns cluster tightly around the rift rather than anywhere in a
        // wide radius. This is a RADIUS, not a total width - 2 means "up to
        // 2 blocks out from center in every direction" (33 candidate
        // cells), a tight cluster right at the rift rather than a wide
        // bubble around it. A rift boxed in badly enough that nothing valid
        // exists within this distance just doesn't spawn that cycle -
        // that's fine, not every rift needs to produce spawns every check.
        public double CreatureSpawnMaxDistanceFromRift = 2.0;

        // How often (real-world seconds) this spawner re-rolls for every
        // active rift, day or night alike. Deliberately short: this is also
        // how fast the territory modifiers below can react to a player
        // actually walking closer to a rift - at a long interval you could
        // close the distance and wait several real seconds before it
        // "notices." The cheap part of a check (loop the rifts, compare a
        // distance, roll a die) is trivial even at this cadence; only an
        // actual chance success triggers the expensive part (population-cap
        // entity walk, placement search), so checking more often costs
        // little as long as the chances below are scaled down to
        // compensate (which they are - see their comment).
        public double RiftCreatureSpawnCheckIntervalSeconds = 2.0;

        // The two ENDPOINTS of a continuous day/night gradient, not a hard
        // switch - see GetDaylightFraction in RiftSurfaceSpawner. Baseline
        // chance per check, per rift = Nighttime + (Daytime - Nighttime) *
        // daylightFraction, where daylightFraction comes from
        // IGameCalendar.GetDayLightStrength (roughly 0 at full dark to 1 at
        // full daylight - accounts for latitude and season, not just clock
        // time, and fades smoothly through dawn/dusk rather than flipping
        // at a fixed sun angle). Deliberately NOT based on the sun's
        // position (which doesn't reflect actual local daylight in
        // "always day"/"always night" latitudes) and NOT based on light
        // level at the spawn position (that would let a player suppress it
        // with a torch - see EnableCustomRiftMechanics's comment for why
        // that's ruled out on principle).
        //
        // These values started out scaled to
        // RiftCreatureSpawnCheckIntervalSeconds (1/7.5th of an earlier
        // 15-second-interval tuning, so checking 7.5x more often wouldn't
        // mean 7.5x more spawns) - both have since been hand-tuned well
        // past that starting point after playtesting kept showing rift
        // activity too rare to notice, especially with the kill/close
        // mechanic needing repeated encounters to ever reach its ceiling.
        // Not "scientific" (not derived from the interval math), just a
        // direct response to how it played. Nighttime is deliberately 2x
        // daytime - these are meant to be active portals things are
        // actively coming through, and night should feel noticeably more
        // dangerous, not just marginally so. At a rift's full health
        // (RiftCreatureSpawnCheckIntervalSeconds=2s), 0.05 daytime averages
        // one spawn roughly every 40s; 0.10 nighttime roughly every 20s -
        // both roughly double once RiftWeaknessMaxReduction kicks in near a
        // rift's death. If you change RiftCreatureSpawnCheckIntervalSeconds
        // again, rescale both of these the same way to keep the baseline
        // (outside a rift's territory, see below) rate where you want it.
        public double RiftCreatureDaytimeSpawnChancePerCheck = 0.05;
        public double RiftCreatureNighttimeSpawnChancePerCheck = 0.10;

        // -- A rift's territory: it senses and reacts to a nearby player --
        //
        // Every rift defends two concentric rings around itself. Beyond the
        // outer ring, the plain day/night baseline above applies with no
        // change. Inside the outer ring but outside the inner one,
        // RiftOuterTerritoryCreatureSpawnChanceModifier applies. Inside the
        // inner ring, RiftInnerTerritoryCreatureSpawnChanceModifier applies
        // instead. This is a flat swap between three fixed values based on
        // which ring a player is standing in - not a gradient/ramp - so the
        // rule is simply "closest ring wins": check the inner radius first,
        // then the outer, then fall back to baseline. Set
        // RiftOuterTerritoryRadius to 0 (or negative) to disable the whole
        // territory system.
        //
        // Each modifier MULTIPLIES the day/night baseline chance for
        // whichever ring applies: 1.0 leaves it unaffected, above 1.0
        // boosts it (the rift senses you and gets more dangerous the closer
        // you press in - the intended, defended-portal feel this was
        // originally built for), and below 1.0 suppresses it. That last
        // direction is deliberately supported too: a player might want the
        // OPPOSITE effect - reducing spawn chance right next to a rift - to
        // discourage "rift camping" (parking next to one rift and farming
        // whatever it produces, which trivializes an Apocalyptic-level rift
        // cluster by turning "many dangerous rifts" into "sit at one and
        // let it feed you"). Both directions are the same mechanism, just a
        // modifier on either side of 1.0.
        //
        // How a weakened rift changes this: as a rift weakens, each modifier
        // is linearly interpolated from its configured value (at full
        // health) toward an amplified TARGET (at the weakness floor set by
        // RiftWeaknessMaxReduction) - a straight line, no exponents. The
        // target is computed once, from the floor, the same way you'd
        // naturally expect: a boost (> 1.0) divides by the floor, a
        // suppression (< 1.0) multiplies by it. E.g. the default inner
        // modifier of 10.0, with the default 0.5 weakness floor, has a
        // target of 10.0 / 0.5 = 20.0 - so as a rift goes from full health
        // to fully weakened, the inner modifier climbs in a straight line
        // from 10.0 up to 20.0. A suppression modifier of 0.2 would have a
        // target of 0.2 * 0.5 = 0.1, sliding DOWN in a straight line from
        // 0.2 to 0.1 instead - same rule, just running in whichever
        // direction the modifier already points.
        //
        // While a player is inside either ring, this interpolated modifier
        // REPLACES the ordinary weakness reduction entirely rather than
        // composing with it - the territory system fully owns how weakness
        // affects danger up close, so the two effects can't fight each
        // other or cancel out. Outside both rings, the ordinary weakness
        // reduction (see RiftWeaknessMaxReduction) still applies as normal.
        // Worked example at the defaults (5% daytime baseline, 10.0 inner
        // modifier, 0.5 weakness floor), standing in the inner ring: full
        // health gives 5% x 10.0 = 50%; halfway to fully weakened gives 5%
        // x 15.0 = 75%; fully weakened gives 5% x 20.0 = 100% (clamped) - a
        // straight line the whole way, and "halfway" here means the exact
        // same fraction of progress GetWeaknessFactor's own kill count
        // uses, so the two stay in lockstep.
        public double RiftOuterTerritoryRadius = 15.0;
        public double RiftOuterTerritoryCreatureSpawnChanceModifier = 4.0;

        // The inner ring - checked before the outer one, so it should
        // normally be the SMALLER of the two radii (the defended core
        // inside the wider territory). If it's set larger than
        // RiftOuterTerritoryRadius instead, the outer ring simply never
        // triggers, since anything within it is already within the inner
        // one.
        public double RiftInnerTerritoryRadius = 5.0;
        public double RiftInnerTerritoryCreatureSpawnChanceModifier = 10.0;

        // Skip a rift's roll entirely once this many of ITS OWN spawns (see
        // RiftSurfaceSpawner.OwningRiftIdAttribute) already exist within
        // RiftSpawnCountRadius of it - a population cap so camping one rift
        // doesn't pile up an unbounded crowd from that rift alone. Counted
        // per-rift on purpose, not as one shared pool: rift activity level
        // (peaceful through apocalyptic) already controls how many rifts
        // can cluster together, so a cluster of rifts should produce
        // proportionally more spawns, one quota per rift, rather than all
        // sharing (and quickly exhausting) a single area-wide cap. Same
        // day/night gradient as the chances above (the effective cap is
        // rounded to the nearest whole creature). Vanilla's own despawn
        // behavior on these creatures (despawn after being >32 blocks from
        // every player for 30s, or >64 blocks for 6s) still cleans them up
        // once you actually leave, same as any other creature - this cap is
        // just for while you're standing right there.
        public int MaxSpawnsPerRiftDaytime = 3;
        public int MaxSpawnsPerRiftNighttime = 6;

        // Radius (from the rift's center) used ONLY for the population-cap
        // count above - it has nothing to do with kill tracking. A spawn
        // that wanders outside this radius and gets killed still counts
        // toward its owning rift's weakening/closing progress: kills are
        // matched purely by the tag on the entity itself (see
        // RiftSurfaceSpawner.OwningRiftIdAttribute), checked via a global
        // death event with no distance limit at all, so there's no way for
        // a spawn to "escape" its rift's kill count by wandering off.
        // Deliberately wider than CreatureSpawnMaxDistanceFromRift
        // (placement stays tight to the rift itself) so one of this rift's
        // own spawns that wanders off a little still counts against its
        // population cap instead of immediately "freeing up a slot" a few
        // blocks away. Raised from an original 32 to 64 (matching
        // RiftCreatureSpawnMaxDistanceFromPlayer's default) after confirming
        // this - a wandering spawn no longer risks slipping outside the
        // count just because it walked toward whatever player is nearby.
        public double RiftSpawnCountRadius = 64.0;

        // -- Where a rift's spawns can appear relative to a player --

        // A rift's check is skipped entirely unless at least one online
        // player is within this many blocks of it. Rifts themselves can
        // exist up to ~190 blocks from a player before vanilla despawns
        // them (ModSystemRifts.despawnDistance) - far past anywhere a
        // player would actually notice a spawn - so without this, the
        // server would be doing placement checks and creating/tracking
        // entities for rifts nobody is anywhere near. Matches vanilla's own
        // general ballpark for "close enough to matter" (its ambient
        // spawner's default MinDistanceToPlayer is 18 blocks; this is a bit
        // more generous since rift encounters are meant to be found, not
        // stumbled into at point-blank range).
        public double RiftCreatureSpawnMaxDistanceFromPlayer = 64.0;

        // A candidate spawn position must be at least this many blocks from
        // every online player. Defaults to 0 (no exclusion, other than the
        // entity's own collision box not being allowed to overlap yours) -
        // this used to default to 4 as the only thing standing between a
        // spawn and your face, but now that PreventSpawnsInsideRooms
        // handles "don't spawn inside my base," there's no reason left to
        // hold spawns back from a player standing right at a rift. That's
        // actually the point now, especially combined with the territory
        // modifiers above - the rift should feel like it's defending itself
        // as you close in. Raise this if you'd rather have some breathing
        // room again.
        public double RiftCreatureSpawnMinDistanceFromPlayer = 0.0;

        // Safety cap on how many candidate positions (out of the sphere of
        // blocks within CreatureSpawnMaxDistanceFromRift, checked
        // closest-to-center first) get examined before giving up on a rift
        // for this check. Only matters if you set
        // CreatureSpawnMaxDistanceFromRift large enough that the full
        // sphere would be expensive to search fully; the default
        // comfortably covers the default 2-block radius (33 cells) with a
        // lot of headroom for raising CreatureSpawnMaxDistanceFromRift back
        // up.
        public int MaxSpawnPositionCandidatesToCheck = 600;

        // -- Closing a rift --

        // Master toggle for whether a rift can be CLOSED outright by
        // killing its spawns (rift.Size = 0 - exactly, and only, what a
        // Rift Ward does, plus a fresh despawn timer - see
        // RiftCloseDespawnDelayInCalendarHours below). Independent
        // of EnableRiftWeakening: you can have closing without weakening
        // (a rift stays at full aggression right up until the kill that
        // closes it), weakening without closing (a rift winds down but can
        // never actually be shut off by kills alone), both, or neither.
        public bool EnableRiftClosing = true;

        // Sends a plain chat notification (no new sound/asset, just
        // EnumChatType.Notification text) to every online player within
        // RiftCreatureSpawnMaxDistanceFromPlayer of a rift the moment it closes.
        public bool NotifyNearbyPlayersOnRiftClose = true;

        // How many of a rift's own spawns need to die by a player's hand
        // before that rift closes - only checked while EnableRiftClosing is
        // true. Combined with KillsToCloseRiftVariance below, each rift
        // rolls its own personal threshold once (the moment it's first
        // damaged) rather than every rift needing exactly the same count -
        // some rifts close a little easier, some put up more of a fight.
        public int KillsToCloseRift = 8;

        // The +/- range each rift's own close threshold is randomized
        // within, around KillsToCloseRift - e.g. the default 8 +/- 2 means
        // a given rift's actual threshold is a whole number uniformly
        // chosen from 6 to 10, rolled once and then fixed for that rift's
        // whole lifetime (not re-rolled every check). Set to 0 for every
        // rift to use exactly KillsToCloseRift with no variation.
        public int KillsToCloseRiftVariance = 2;

        // When EnableRiftClosing closes a rift, its despawn timer
        // (DieAtTotalHours) is set to now plus a fresh duration averaging
        // this many calendar hours (randomized - see
        // RiftCloseDespawnDelayVarianceInCalendarHours below) -
        // completely decoupled from however much natural lifespan the rift
        // had left when it closed. An earlier version instead scaled down
        // the rift's REMAINING natural lifespan by a fraction, which meant
        // closing a rift that was already about to expire barely did
        // anything (a small fraction of a small remainder is still small) -
        // exactly backwards, since closing a rift that was moments from
        // despawning anyway should still buy real breathing room, not
        // almost none. These two settings give you that directly: how many
        // calendar hours (on average) an area stays rift-free after a
        // close, full stop, regardless of what the closed rift's own clock
        // said. 3.0 is a reasonable starting point - raise it for a longer
        // guaranteed respite, lower it (or set to something small like 0.1)
        // to make closing feel closer to "immediately eligible for a
        // replacement" again.
        public double RiftCloseDespawnDelayInCalendarHours = 3.0;

        // The +/- range CloseRift randomizes around
        // RiftCloseDespawnDelayInCalendarHours - e.g. an average of
        // 2.0 with a variance of 1.0 produces a despawn timer uniformly
        // random between 1.0 and 3.0 calendar hours from the moment of
        // closing. Set to 0 for a fixed duration every time (no variance).
        // The result is floored just above zero so a close never
        // accidentally frees the rift's cap slot instantly, no matter how
        // the average/variance are configured.
        public double RiftCloseDespawnDelayVarianceInCalendarHours = 1.0;

        // -- Weakening a rift --

        // Master toggle for whether a rift's spawn chance fades down as its
        // own spawns are killed (see RiftKillTracker.GetWeaknessFactor,
        // which multiplies straight into the chance RiftSurfaceSpawner
        // computes each check). Independent of EnableRiftClosing - see that
        // field's comment for why.
        public bool EnableRiftWeakening = true;

        // How many of a rift's own spawns need to die by a player's hand
        // for its weakness to bottom out at RiftWeaknessMaxReduction - only
        // used while EnableRiftWeakening is true, and completely
        // independent of KillsToCloseRift (a rift can, for example, be
        // fully weakened after 4 kills but not close until 10, or close
        // after 8 kills while never fading below 80% chance along the way).
        // Combined with KillsToFullyWeakenRiftVariance below, each rift
        // rolls its own personal threshold once, the same way
        // KillsToCloseRift does.
        public int KillsToFullyWeakenRift = 4;

        // The +/- range each rift's own weaken-to-full threshold is
        // randomized within, around KillsToFullyWeakenRift - same mechanic
        // as KillsToCloseRiftVariance, just for weakening instead of
        // closing. Default 4 +/- 1 means a whole number uniformly chosen
        // from 3 to 5, rolled once per rift.
        public int KillsToFullyWeakenRiftVariance = 1;

        // How much (as a fraction, 0 to 1) a rift's spawn chance is
        // reduced by, at most, as its kills approach KillsToFullyWeakenRift
        // - only used while EnableRiftWeakening is true. 0.5 (the default)
        // means a fully-weakened rift's chance is cut in half; 1.0 would
        // silence it completely once fully weakened; 0 means weakening has
        // no effect on chance at all. Kept below 1.0 by default on
        // purpose: a rift reduced all the way to 0% chance almost never
        // produces another spawn, which (if EnableRiftClosing is also on)
        // could leave it stuck one kill away from closing indefinitely -
        // 0.5 guarantees there's always something left to fight, even at
        // maximum weakening. Raised from an original 0.25 after
        // playtesting - these are meant to be rare spawns overall, and a
        // heavily-weakened rift still needs to feel alive enough to finish
        // off. This is also the floor that sets how aggressively the
        // territory modifiers above get amplified as a rift weakens - see
        // their comment.
        public double RiftWeaknessMaxReduction = 0.5;

        // ---- Diagnostics ----

        // Diagnostic only - the single toggle for every log line this mod
        // writes beyond its one-time startup summary: every intercepted
        // ambient spawn decision (SurfaceSpawnGate), every rift check with
        // its full chance breakdown and pass/fail roll, every population-
        // cap or no-valid-position miss, and every tracked kill/weakening/
        // close (RiftSurfaceSpawner, RiftKillTracker). This is genuinely
        // noisy (a line every couple seconds per rift someone's near, on
        // top of the ambient decisions, and once per vanilla ambient spawn
        // attempt) - was defaulted to true for a 2026-09-18 performance-
        // data-gathering pass, now back to false with real numbers in
        // hand; turn it back on any time you need to dig through logs
        // again.
        public bool LogSpawnChecks = false;

        // Logs how long every single RiftSurfaceSpawner.OnCheck tick took
        // (total rifts examined, how many were within range, how many
        // attempted a spawn, elapsed milliseconds) - unconditionally,
        // every RiftCreatureSpawnCheckIntervalSeconds, regardless of
        // whether anything was actually slow. Noisy for the same reason
        // LogSpawnChecks is (a line every couple seconds for as long as
        // the server runs) - off by default; turn it on temporarily if
        // you want the real numbers behind a specific tick, not just
        // whether it crossed the "slow" threshold below.
        public bool LogAllRiftCheckPerformance = false;

        // Logs the same performance line as LogAllRiftCheckPerformance,
        // but ONLY when a tick took at least
        // SlowRiftCheckThresholdMilliseconds - silent the rest of the
        // time, so there's no meaningful cost to leaving this on
        // permanently (unlike the field above). Defaults to true for
        // exactly that reason - it's a smoke detector, not a running
        // log. If both this and LogAllRiftCheckPerformance are true,
        // every tick still only gets logged once, not twice.
        public bool LogSlowRiftCheckPerformance = true;

        // The cutoff (in milliseconds) LogSlowRiftCheckPerformance uses
        // to decide a tick was "slow" enough to log. Originally derived
        // from a real solo playtest log's p95 tick time (~2.6ms) scaled
        // to ~20 concurrent players, landing on 50 - then deliberately
        // doubled to 100 (2026-09-18) once the reasoning shifted to
        // "how much of a ~1000ms/sec frame budget did this one tick eat
        // by itself," which is what actually matters for a smoke
        // detector: this whole 100ms lands on the single server tick
        // that ran RiftSurfaceSpawner.OnCheck, so it's a meaningful bite
        // out of that tick's budget on its own, not just a busy-rift
        // reading. See SlowAmbientCheckThresholdMilliseconds - both are
        // now set to the same number under this same "is this a
        // significant chunk of a second's worth of frame budget"
        // reasoning, even though what they each measure is different
        // (see that field's comment).
        public double SlowRiftCheckThresholdMilliseconds = 100.0;

        // Reports how many times VANILLA itself called into this mod's
        // ambient spawn gate (SurfaceSpawnGate.Postfix) and how much
        // cumulative time was spent inside it, totaled over a fixed
        // real-world 1-second window (hardcoded in SurfaceSpawnGate, not
        // a config field - see the comment on the tick listener
        // registration in SurfaceSpawnGate.Apply for why) -
        // unconditionally, every second, regardless of whether it was
        // slow. Unlike the rift side, this mod doesn't control how often
        // vanilla's own ambient spawner calls CanSpawnNearby - that
        // scheduling lives in the core game, not in any source available
        // to read - so this measures the real, observed call rate
        // directly instead of guessing at it. Noisy for the same reason
        // the other "LogAll" toggles are - was defaulted to true for the
        // 2026-09-18 data-gathering pass, now back to false with real
        // numbers in hand.
        public bool LogAllAmbientCheckPerformance = false;

        // Logs the same summary as LogAllAmbientCheckPerformance, but
        // ONLY when a window's cumulative time crossed
        // SlowAmbientCheckThresholdMilliseconds - silent otherwise, so
        // (like its rift-side counterpart) there's no real cost to
        // leaving this on permanently. Defaults to true for the same
        // reason.
        public bool LogSlowAmbientCheckPerformance = true;

        // The cutoff (in milliseconds, summed across every
        // SurfaceSpawnGate.Postfix call within one real-world second)
        // that decides a window was "slow" enough for
        // LogSlowAmbientCheckPerformance to log it. Set to 100.0
        // (2026-09-18), the same number as SlowRiftCheckThresholdMilliseconds,
        // under the same reasoning: a server has roughly 1000ms of frame
        // budget per real second, so 100ms of cumulative time in this
        // mod's ambient gate during that same second is a meaningful
        // slice of that budget, regardless of how many individual calls
        // it was spread across. Important nuance: this is a SUM across
        // many small calls landing on many different ticks within that
        // second, whereas the rift threshold above is ONE call's time
        // landing entirely on ONE tick - the same 100ms is therefore
        // less likely to visibly stall any single tick on this side than
        // on the rift side, even though the number matches. Matching the
        // numbers makes the two readings comparable at a glance; it does
        // not claim they carry equal risk.
        public double SlowAmbientCheckThresholdMilliseconds = 100.0;
    }
}
