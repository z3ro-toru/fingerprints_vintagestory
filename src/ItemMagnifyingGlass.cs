using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Fingerprints
{
    /// <summary>
    /// Magnifying glass: hold right mouse button on a block to study it.
    /// While it is held, chests and crates can't be opened. After a short while
    /// the fingerprints on the block are listed in chat.
    /// </summary>
    public class ItemMagnifyingGlass : Item
    {
        WorldInteraction[] interactions;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            // With sneak held, our interaction goes before any block's own
            HeldPriorityInteract = true;

            interactions = new[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "fingerprints:heldhelp-inspect",
                    MouseButton = EnumMouseButton.Right,
                }
            };
        }

        FingerprintsClientSystem ClientFx(EntityAgent byEntity)
        {
            if (api.Side != EnumAppSide.Client) return null;
            var capi = api as ICoreClientAPI;
            if (capi?.World?.Player?.Entity == null || byEntity != capi.World.Player.Entity) return null;
            return api.ModLoader.GetModSystem<FingerprintsClientSystem>();
        }

        /// <summary>
        /// In creative mode the game re-triggers held interactions very quickly (vanilla tools such as the
        /// crowbar finish their action after 0.1 s there), so the study is nearly instant in creative.
        /// </summary>
        static float Duration(EntityAgent byEntity)
        {
            var plr = (byEntity as EntityPlayer)?.Player;
            if (plr?.WorldData?.CurrentGameMode == EnumGameMode.Creative) return 0.1f;
            return Math.Max(0.1f, FingerprintSystem.Config.InspectSeconds);
        }

        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
        {
            if (blockSel == null)
            {
                base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
                return;
            }

            // Always claim the interaction (also for repeated, non-first events), otherwise the game aborts it
            handling = EnumHandHandling.PreventDefault;
            if (firstEvent)
            {
                ClientFx(byEntity)?.Begin(blockSel.Position);
                api.Logger.VerboseDebug("[fingerprints] Magnifier study started at {0} ({1})", blockSel.Position, api.Side);
            }
        }

        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            var fx = ClientFx(byEntity);
            if (blockSel == null || (fx != null && !fx.IsTarget(blockSel.Position)))
            {
                fx?.End(false);
                return false;
            }

            float total = Duration(byEntity);
            fx?.Progress(secondsUsed / total, blockSel);
            return secondsUsed < total;
        }

        public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, EnumItemUseCancelReason cancelReason)
        {
            ClientFx(byEntity)?.End(false);
            return base.OnHeldInteractCancel(secondsUsed, slot, byEntity, blockSel, entitySel, cancelReason);
        }

        public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            base.OnHeldInteractStop(secondsUsed, slot, byEntity, blockSel, entitySel);
            float total = Duration(byEntity);
            bool completed = blockSel != null && secondsUsed >= total - Math.Min(0.15f, total * 0.5f);
            api.Logger.VerboseDebug("[fingerprints] Magnifier study stopped after {0:0.00}s, completed: {1} ({2})", secondsUsed, completed, api.Side);

            ClientFx(byEntity)?.End(completed);

            if (!completed || api.Side != EnumAppSide.Server) return;

            var player = (byEntity as EntityPlayer)?.Player as IServerPlayer;
            if (player == null) return;

            var system = api.ModLoader.GetModSystem<FingerprintSystem>();
            if (system == null) return;

            Report(player, system, blockSel.Position);
        }

        void Report(IServerPlayer player, FingerprintSystem system, BlockPos pos)
        {
            string lang = player.LanguageCode ?? "en";
            Block block = api.World.BlockAccessor.GetBlock(pos);
            string blockName = block?.GetPlacedBlockName(api.World, pos) ?? "?";

            List<FingerprintRecord> list = system.GetPrints(pos);
            var sb = new StringBuilder();

            if (list.Count == 0)
            {
                sb.Append(Lang.GetL(lang, "fingerprints:inspect-none", blockName));
            }
            else
            {
                sb.Append(Lang.GetL(lang, "fingerprints:inspect-header", blockName, list.Count));
                double now = system.NowDays;
                foreach (var rec in list)
                {
                    double ageDays = Math.Max(0, now - rec.LastTouchDays);
                    bool smudged = FingerprintSystem.Config.SmudgeAfterDays > 0 && ageDays > FingerprintSystem.Config.SmudgeAfterDays;

                    string who = smudged
                        ? Lang.GetL(lang, "fingerprints:smudged", Obscure(rec.PlayerName))
                        : rec.PlayerName;

                    string line = Lang.GetL(lang, "fingerprints:print-line", who, FormatAge(lang, ageDays), FreshnessWord(lang, ageDays));
                    if (rec.Touches > 1) line += " " + Lang.GetL(lang, "fingerprints:print-touches", rec.Touches);
                    if (rec.Transferred)
                    {
                        line += " " + Lang.GetL(lang, "fingerprints:print-fallen", SourceName(lang, rec.SourceBlockCode));
                    }
                    sb.Append("\n").Append(line);
                }
            }

            player.SendMessage(GlobalConstants.GeneralChatGroup, sb.ToString(), EnumChatType.Notification);
        }

        string SourceName(string lang, string code)
        {
            if (string.IsNullOrEmpty(code)) return Lang.GetL(lang, "fingerprints:unknown-container");
            Block b = api.World.GetBlock(new AssetLocation(code));
            if (b == null || b.Code == null) return code;
            return Lang.GetMatchingL(lang, b.Code.Domain + AssetLocation.LocationSeparator + "block-" + b.Code.Path);
        }

        static string Obscure(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            return name.Substring(0, 1) + new string('*', Math.Max(2, name.Length - 1));
        }

        static string FormatAge(string lang, double days)
        {
            double hours = days * 24;
            if (hours < 1) return Lang.GetL(lang, "fingerprints:age-justnow");
            if (days < 1) return Lang.GetL(lang, "fingerprints:age-hours", (int)Math.Round(hours));
            return Lang.GetL(lang, "fingerprints:age-days", (int)Math.Round(days));
        }

        static string FreshnessWord(string lang, double days)
        {
            if (days < 1) return Lang.GetL(lang, "fingerprints:fresh-1");
            if (days < 3) return Lang.GetL(lang, "fingerprints:fresh-2");
            if (FingerprintSystem.Config.SmudgeAfterDays <= 0 || days <= FingerprintSystem.Config.SmudgeAfterDays) return Lang.GetL(lang, "fingerprints:fresh-3");
            return Lang.GetL(lang, "fingerprints:fresh-4");
        }

        public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
        {
            return interactions;
        }
    }
}
