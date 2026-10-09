using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Fingerprints
{
    public static class MagnifierUtil
    {
        public static bool HoldsMagnifier(IPlayer player)
        {
            return player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible is ItemMagnifyingGlass;
        }
    }

    /// <summary>
    /// Added in front of all other behaviors of tracked containers (chests, trunks, vessels...).
    /// While the player holds a magnifying glass the container refuses to open,
    /// so the right click goes to the magnifying glass instead.
    /// </summary>
    public class BlockBehaviorMagnifierGuard : BlockBehavior
    {
        public BlockBehaviorMagnifierGuard(Block block) : base(block)
        {
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            if (MagnifierUtil.HoldsMagnifier(byPlayer))
            {
                handling = EnumHandling.PreventSubsequent;
                return false;
            }
            return false;
        }
    }

    /// <summary>
    /// Crates handle right clicks without asking block behaviors, so they get their own subclass.
    /// </summary>
    public class BlockCrateFingerprints : BlockCrate
    {
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (MagnifierUtil.HoldsMagnifier(byPlayer)) return false;
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
    }
}
