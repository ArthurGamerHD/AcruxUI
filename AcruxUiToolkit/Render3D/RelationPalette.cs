using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>
    /// Colors for what the local player sees by whom owns it: red for enemies, yellow for neutral or unowned, green
    /// for friendly factions, and the player's own faction color (the game's blue without one) for their own and
    /// their faction's.
    /// </summary>
    public static class RelationPalette
    {
        static readonly Color Enemy = new Color(255, 60, 60);
        static readonly Color Neutral = new Color(255, 210, 60);
        static readonly Color Allied = new Color(80, 220, 90);
        // The game's own blue for a player outside any faction.
        static readonly Color Unaffiliated = new Color(0, 88, 151);

        enum Relation
        {
            Neutral,
            Enemy,
            Allied,
            Own
        }

        /// <summary>The color for something owned by this identity (zero: unowned).</summary>
        public static Color ForOwner(long ownerIdentityId)
        {
            IMyFaction mine;
            switch (RelationTo(ownerIdentityId, out mine))
            {
                case Relation.Enemy:
                    return Enemy;
                case Relation.Allied:
                    return Allied;
                case Relation.Own:
                    return mine == null ? Unaffiliated : MyColorPickerConstants.HSVOffsetToHSV(mine.CustomColor).HSVtoColor();
                default:
                    return Neutral;
            }
        }

        /// <summary>
        /// The color for a missile, by its launcher's owner: one friendly green for the player's own, their faction's
        /// and allied factions' alike, so a friendly missile never takes a faction color.
        /// </summary>
        public static Color ForMissile(long launcherOwnerIdentityId)
        {
            IMyFaction mine;
            switch (RelationTo(launcherOwnerIdentityId, out mine))
            {
                case Relation.Enemy:
                    return Enemy;
                case Relation.Allied:
                case Relation.Own:
                    return Allied;
                default:
                    return Neutral;
            }
        }

        /// <summary>The color for a grid, by its main owner.</summary>
        public static Color ForGrid(IMyCubeGrid grid)
        {
            return ForOwner(MainOwner(grid));
        }

        /// <summary>Whether the grid's main owner is an enemy of the local player (shown red).</summary>
        public static bool IsEnemy(IMyCubeGrid grid)
        {
            IMyFaction mine;
            return RelationTo(MainOwner(grid), out mine) == Relation.Enemy;
        }

        static long MainOwner(IMyCubeGrid grid)
        {
            return grid == null || grid.BigOwners == null || grid.BigOwners.Count == 0 ? 0 : grid.BigOwners[0];
        }

        static Relation RelationTo(long owner, out IMyFaction mine)
        {
            mine = null;
            IMyPlayer player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            if (owner == 0 || player == null || MyAPIGateway.Session.Factions == null)
                return Relation.Neutral;
            long me = player.IdentityId;
            mine = MyAPIGateway.Session.Factions.TryGetPlayerFaction(me);
            if (owner == me)
                return Relation.Own;
            IMyFaction theirs = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);
            // Players outside factions are each other's enemies, as the game treats them.
            if (mine == null || theirs == null)
                return Relation.Enemy;
            if (mine.FactionId == theirs.FactionId)
                return Relation.Own;
            switch (MyAPIGateway.Session.Factions.GetRelationBetweenFactions(mine.FactionId, theirs.FactionId))
            {
                case MyRelationsBetweenFactions.Friends:
                    return Relation.Allied;
                case MyRelationsBetweenFactions.Neutral:
                    return Relation.Neutral;
                default:
                    return Relation.Enemy;
            }
        }
    }
}
