using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Fingerprints
{
    /// <summary>Small progress bar under the crosshair while studying a block.</summary>
    public class HudInspectProgress : HudElement
    {
        GuiElementStatbar bar;

        public HudInspectProgress(ICoreClientAPI capi) : base(capi)
        {
            Compose();
        }

        void Compose()
        {
            double width = 220;
            ElementBounds dialogBounds = new ElementBounds()
            {
                Alignment = EnumDialogArea.CenterMiddle,
                BothSizing = ElementSizing.Fixed,
                fixedWidth = width,
                fixedHeight = 44
            }.WithFixedAlignmentOffset(0, 90);

            ElementBounds textBounds = ElementBounds.Fixed(0, 0, width, 20);
            ElementBounds barBounds = ElementBounds.Fixed(0, 24, width, 12);

            Composers["fingerprintsinspect"] = capi.Gui
                .CreateCompo("fingerprintsinspect", dialogBounds)
                .AddStaticText(Lang.Get("fingerprints:hud-inspecting"), CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center), textBounds)
                .AddStatbar(barBounds, new double[] { 0.85, 0.66, 0.28, 1 }, "progress")
                .Compose();

            bar = Composers["fingerprintsinspect"].GetStatbar("progress");
            bar.ShowValueOnHover = false;
            bar.SetLineInterval(100);
            bar.SetValues(0, 0, 100);
        }

        public void SetProgress(float progress01)
        {
            bar?.SetValues(GameMath.Clamp(progress01, 0, 1) * 100f, 0, 100);
        }

        public override string ToggleKeyCombinationCode => null;
        public override bool Focusable => false;
        public override bool ShouldReceiveKeyboardEvents() => false;
        public override void OnMouseDown(MouseEvent args) { }
    }

    /// <summary>Client-side visuals of the magnifying glass: progress bar, block highlight and dust particles.</summary>
    public class FingerprintsClientSystem : ModSystem
    {
        public const int HighlightSlot = 7311;

        ICoreClientAPI capi;
        HudInspectProgress hud;
        BlockPos activePos;
        long lastParticleMs;

        static readonly SimpleParticleProperties dust = new SimpleParticleProperties(
            1, 2, ColorUtil.ToRgba(180, 235, 205, 120),
            new Vec3d(), new Vec3d(),
            new Vec3f(-0.08f, 0.02f, -0.08f), new Vec3f(0.08f, 0.12f, 0.08f),
            0.9f, -0.02f, 0.12f, 0.3f, EnumParticleModel.Quad);

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            dust.SelfPropelled = true;
            dust.WithTerrainCollision = false;
            dust.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -150);
        }

        public void Begin(BlockPos pos)
        {
            activePos = pos.Copy();
            if (hud == null) hud = new HudInspectProgress(capi);
            hud.SetProgress(0);
            hud.TryOpen();

            SetHighlight(new List<BlockPos> { activePos }, new List<int> { ColorUtil.ToRgba(70, 60, 190, 230) });
        }

        public bool IsTarget(BlockPos pos) => activePos == null || activePos.Equals(pos);

        public void Progress(float progress01, BlockSelection blockSel)
        {
            if (activePos == null) return;
            hud?.SetProgress(progress01);

            long now = capi.World.ElapsedMilliseconds;
            if (blockSel != null && now - lastParticleMs > 120)
            {
                lastParticleMs = now;
                Vec3d hit = blockSel.HitPosition != null
                    ? blockSel.FullPosition
                    : new Vec3d(blockSel.Position.X + 0.5, blockSel.Position.InternalY + 0.5, blockSel.Position.Z + 0.5);
                dust.MinPos.Set(hit.X - 0.15, hit.Y - 0.15, hit.Z - 0.15);
                dust.AddPos.Set(0.3, 0.3, 0.3);
                capi.World.SpawnParticles(dust);
            }
        }

        public void End(bool completed)
        {
            if (activePos == null) return;
            activePos = null;
            hud?.TryClose();
            SetHighlight(new List<BlockPos>(), new List<int>());
            if (completed)
            {
                capi.World.PlaySoundAt(new AssetLocation("game:sounds/effect/latch"), capi.World.Player.Entity, null, true, 8, 0.5f);
            }
        }

        // The in-game C# compiler does not reference System.Collections, so API methods that take
        // a List<> parameter can't be called directly from source mods. Call it via reflection instead.
        static MethodInfo highlightMethod;
        static bool highlightLookupDone;

        void SetHighlight(List<BlockPos> blocks, List<int> colors)
        {
            try
            {
                if (!highlightLookupDone)
                {
                    highlightLookupDone = true;
                    foreach (MethodInfo m in typeof(IWorldAccessor).GetMethods())
                    {
                        if (m.Name == "HighlightBlocks" && m.GetParameters().Length == 7) { highlightMethod = m; break; }
                    }
                }
                if (highlightMethod == null) return;
                highlightMethod.Invoke(capi.World, new object[] {
                    capi.World.Player, HighlightSlot, blocks, colors,
                    EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary, 1f
                });
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[fingerprints] Block highlight failed: {0}", e.Message);
                highlightMethod = null;
            }
        }

        public override void Dispose()
        {
            hud?.Dispose();
        }
    }
}
