using HarmonyLib;

namespace GungeonOnline.Game.NPlayers
{
    /// <summary>
    /// Stats and credit for players 3 and 4. The game names a bullet's owner "primaryplayer" or "secondaryplayer",
    /// credits kills and damage to those two only, and builds synergies and curse/coolness totals from those two.
    /// </summary>
    internal static class StatsPatches
    {
        static bool inSynergy;
        static readonly System.Reflection.FieldInfo isPlayerField = AccessTools.Field(typeof(HealthHaver), "isPlayerCharacter");

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.PropertySetter(typeof(Projectile), nameof(Projectile.Owner)), postfix: new HarmonyMethod(typeof(StatsPatches), nameof(OwnerPostfix)));
            h.Patch(AccessTools.Method(typeof(HealthHaver), "ApplyDamageDirectional"),
                prefix: new HarmonyMethod(typeof(StatsPatches), nameof(DamagePrefix)), postfix: new HarmonyMethod(typeof(StatsPatches), nameof(DamagePostfix)));
            h.Patch(AccessTools.Method(typeof(AdvancedSynergyEntry), nameof(AdvancedSynergyEntry.SynergyIsAvailable)), postfix: new HarmonyMethod(typeof(StatsPatches), nameof(AvailablePostfix)));
            h.Patch(AccessTools.Method(typeof(AdvancedSynergyEntry), nameof(AdvancedSynergyEntry.SynergyIsActive)), postfix: new HarmonyMethod(typeof(StatsPatches), nameof(ActivePostfix)));
            h.Patch(AccessTools.Method(typeof(PlayerStats), nameof(PlayerStats.GetTotalCurse)), postfix: new HarmonyMethod(typeof(StatsPatches), nameof(CursePostfix)));
            h.Patch(AccessTools.Method(typeof(PlayerStats), nameof(PlayerStats.GetTotalCoolness)), postfix: new HarmonyMethod(typeof(StatsPatches), nameof(CoolnessPostfix)));
            h.Patch(AccessTools.Method(typeof(PlayerStats), nameof(PlayerStats.GetTotalEnemyProjectileSpeedMultiplier)), postfix: new HarmonyMethod(typeof(StatsPatches), nameof(SpeedPostfix)));
            h.Patch(AccessTools.Method(typeof(PlayerStats), nameof(PlayerStats.RecalculateStats)), postfix: new HarmonyMethod(typeof(StatsPatches), nameof(RecalcPostfix)));
        }

        /// <summary>Bullets from players 3 and 4 are named by id; the game already understands "Player ID n" names.</summary>
        static void OwnerPostfix(Projectile __instance)
        {
            if (!Players.NPlus) return;
            var pc = __instance.Owner as PlayerController;
            if (pc && pc.PlayerIDX >= 2) __instance.OwnerName = "Player ID " + pc.PlayerIDX;
        }

        static readonly System.Reflection.FieldInfo statValues = AccessTools.Field(typeof(PlayerStats), "StatValues");
        static bool HasStats(PlayerController p) => p.stats != null && statValues != null && statValues.GetValue(p.stats) != null;

        static PlayerController ExtraPlayerFrom(string source)
        {
            if (source == null || !source.StartsWith("Player ID ")) return null;
            if (!int.TryParse(source.Substring(10), out var id) || id < 2) return null;
            foreach (var p in Players.All) if (p.PlayerIDX == id) return p;
            return null;
        }

        /// <summary>The game applies a player's boss-damage stat for players 0 and 1 only.</summary>
        static void DamagePrefix(HealthHaver __instance, ref float damage, string damageSource)
        {
            if (!Players.NPlus || !__instance.IsBoss) return;
            var p = ExtraPlayerFrom(damageSource);
            if (p && p.stats != null) damage *= p.stats.GetStatValue(PlayerStats.StatType.DamageToBosses);
        }

        /// <summary>Kill and damage credit (items that trigger on damage dealt) for players 3 and 4.</summary>
        static void DamagePostfix(HealthHaver __instance, float damage, string damageSource)
        {
            if (!Players.NPlus || (isPlayerField != null && (bool)isPlayerField.GetValue(__instance))) return;
            var p = ExtraPlayerFrom(damageSource);
            if (p) p.OnDidDamage(damage, __instance.GetCurrentHealth() <= 0f && __instance.Armor <= 0f, __instance);
        }

        /// <summary>A synergy that players 3 or 4 hold the items for counts like one held by players 0 or 1.</summary>
        static void AvailablePostfix(AdvancedSynergyEntry __instance, ref bool __result, int additionalID)
        {
            if (__result || inSynergy || !Players.NPlus) return;
            inSynergy = true;
            try
            {
                foreach (var p in Players.All)
                    if (p.PlayerIDX >= 2 && __instance.SynergyIsAvailable(p, null, additionalID)) { __result = true; return; }
            }
            finally { inSynergy = false; }
        }

        static void ActivePostfix(AdvancedSynergyEntry __instance, ref bool __result)
        {
            if (__result || inSynergy || !Players.NPlus) return;
            inSynergy = true;
            try
            {
                foreach (var p in Players.All)
                    if (p.PlayerIDX >= 2 && __instance.SynergyIsActive(p, null)) { __result = true; return; }
            }
            finally { inSynergy = false; }
        }

        static void CursePostfix(ref int __result)
        {
            if (!Players.NPlus) return;
            foreach (var p in Players.All)
                if (p.PlayerIDX >= 2 && HasStats(p)) __result += UnityEngine.Mathf.FloorToInt(p.stats.GetStatValue(PlayerStats.StatType.Curse));
        }

        static void CoolnessPostfix(ref int __result)
        {
            if (!Players.NPlus) return;
            foreach (var p in Players.All)
                if (p.PlayerIDX >= 2 && HasStats(p)) __result += UnityEngine.Mathf.FloorToInt(p.stats.GetStatValue(PlayerStats.StatType.Coolness));
        }

        static void SpeedPostfix(ref float __result)
        {
            if (!Players.NPlus) return;
            foreach (var p in Players.All)
                if (p.PlayerIDX >= 2 && HasStats(p)) __result *= p.stats.GetStatValue(PlayerStats.StatType.EnemyProjectileSpeedMultiplier);
        }

        /// <summary>A stat change recalculates "the other player"; with more players, recalculate the rest too.</summary>
        static void RecalcPostfix(PlayerController owner, bool force, bool recursive)
        {
            if (recursive || !Players.NPlus) return;
            var partner = GameManager.Instance.GetOtherPlayer(owner);
            foreach (var p in Players.Others(owner))
                if (p != partner && p.stats != null) p.stats.RecalculateStats(p, force, true);
        }
    }
}
