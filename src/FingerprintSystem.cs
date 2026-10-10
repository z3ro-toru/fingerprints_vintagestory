using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Fingerprints
{
    /// <summary>One fingerprint left by a player on a block.</summary>
    public class FingerprintRecord
    {
        public int X;
        public int Y;
        public int Z;
        public int Dim;
        public string PlayerUid = "";
        public string PlayerName = "";
        /// <summary>In-game day (Calendar.TotalDays) of the last touch.</summary>
        public double LastTouchDays;
        public int Touches;
        /// <summary>True when the print fell onto this block from a broken container above.</summary>
        public bool Transferred;
        /// <summary>Block code of the container the print originally came from (for transferred prints).</summary>
        public string? SourceBlockCode;
    }

    public class FingerprintsConfig
    {
        /// <summary>Block entity class names (C# type names, base classes count) that receive prints when opened.</summary>
        public string[] TrackedBlockEntityClasses = new[]
        {
            "BlockEntityGenericTypedContainer",
            "BlockEntityGenericContainer",
            "BlockEntityCrate"
        };
        /// <summary>After this many in-game days a print becomes smudged (only the first letter is readable).</summary>
        public double SmudgeAfterDays = 7;
        /// <summary>After this many in-game days a print disappears. 0 = never.</summary>
        public double LifetimeDays = 30;
        /// <summary>How many different prints a single block can hold (oldest are wiped first).</summary>
        public int MaxPrintsPerBlock = 10;
        /// <summary>How long (seconds) the player has to hold the magnifying glass to study a block.</summary>
        public float InspectSeconds = 1.5f;
        /// <summary>When a container is broken, look this many blocks down for a surface to drop the prints onto.</summary>
        public int MaxDropDistance = 4;
        /// <summary>Real-time seconds during which repeated uses by the same player count as one touch.</summary>
        public double TouchCooldownSeconds = 5;
        /// <summary>When a block holding fallen prints is destroyed, they move to a random block within this radius.</summary>
        public int SpreadRadius = 2;
    }

    public class FingerprintSystem : ModSystem
    {
        public const string SaveKey = "fingerprints-data";
        public const string ConfigFile = "fingerprints.json";

        public static FingerprintsConfig Config { get; private set; } = new FingerprintsConfig();

        ICoreServerAPI sapi = null!;
        readonly Dictionary<string, long> lastTouchMs = new();
        readonly Dictionary<string, List<FingerprintRecord>> prints = new();
        HashSet<string> trackedClasses = new();

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterItemClass("fingerprints.ItemMagnifyingGlass", typeof(ItemMagnifyingGlass));
            api.RegisterBlockBehaviorClass("fingerprints.MagnifierGuard", typeof(BlockBehaviorMagnifierGuard));
            api.RegisterBlockClass("fingerprints.BlockCrate", typeof(BlockCrateFingerprints));

            try
            {
                var loaded = api.LoadModConfig<FingerprintsConfig>(ConfigFile);
                if (loaded != null) Config = loaded;
                api.StoreModConfig(Config, ConfigFile);
            }
            catch (Exception e)
            {
                api.Logger.Error("[fingerprints] Could not read config, using defaults: {0}", e.Message);
                Config = new FingerprintsConfig();
            }
            trackedClasses = new HashSet<string>(Config.TrackedBlockEntityClasses ?? Array.Empty<string>());
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            // Done on the server only: block behaviors are sent to the clients together with the block types
            if (api.Side != EnumAppSide.Server) return;

            int count = 0;
            foreach (Block block in api.World.Blocks)
            {
                if (block == null || block.Code == null || block.Id == 0) continue;
                if (string.IsNullOrEmpty(block.EntityClass)) continue;
                Type? t = api.ClassRegistry.GetBlockEntity(block.EntityClass);
                if (t == null || !IsTracked(t)) continue;

                bool has = false;
                foreach (var bh in block.BlockBehaviors) if (bh is BlockBehaviorMagnifierGuard) { has = true; break; }
                if (has) continue;

                // Must be first, so it runs before the "Container" behavior that opens the chest
                block.BlockBehaviors = block.BlockBehaviors.InsertAt(new BlockBehaviorMagnifierGuard(block), 0);
                block.CollectibleBehaviors = block.CollectibleBehaviors.Append(new BlockBehaviorMagnifierGuard(block));
                count++;
            }
            api.Logger.Notification("[fingerprints] Magnifier guard added to {0} container blocks", count);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            api.Event.SaveGameLoaded += OnLoad;
            api.Event.GameWorldSave += OnSave;
            api.Event.DidUseBlock += OnDidUseBlock;
            api.Event.DidBreakBlock += OnDidBreakBlock;

            api.ChatCommands.Create("fingerprints")
                .WithDescription("Fingerprints mod: admin tools")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("stats")
                    .WithDescription("Show how many fingerprints are stored and how much space they take")
                    .HandleWith(OnCmdStats)
                .EndSubCommand();
        }

        // ---------------------------------------------------------------- persistence

        void OnLoad()
        {
            prints.Clear();
            try
            {
                byte[]? data = sapi.WorldManager.SaveGame.GetData(SaveKey);
                if (data == null) return;
                var list = JsonUtil.FromBytes<List<FingerprintRecord>>(data);
                if (list == null) return;
                foreach (var rec in list) GetOrCreate(rec.X, rec.Y, rec.Z, rec.Dim).Add(rec);
                sapi.Logger.Notification("[fingerprints] Loaded {0} fingerprints", list.Count);
            }
            catch (Exception e)
            {
                sapi.Logger.Error("[fingerprints] Failed to load fingerprint data: {0}", e);
            }
        }

        void OnSave()
        {
            PurgeExpired();
            var all = new List<FingerprintRecord>();
            foreach (var l in prints.Values) all.AddRange(l);
            sapi.WorldManager.SaveGame.StoreData(SaveKey, JsonUtil.ToBytes(all));
        }

        // ---------------------------------------------------------------- events

        void OnDidUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
        {
            if (byPlayer == null || blockSel?.Position == null) return;

            BlockPos pos = blockSel.Position;
            BlockEntity? be = sapi.World.BlockAccessor.GetBlockEntity(pos);
            if (be == null || !IsTracked(be.GetType())) return;

            AddPrint(pos, byPlayer, false, null);
        }

        void OnDidBreakBlock(IServerPlayer byPlayer, int oldblockId, BlockSelection blockSel)
        {
            if (blockSel?.Position == null) return;
            BlockPos pos = blockSel.Position;
            string key = Key(pos.X, pos.Y, pos.Z, pos.dimension);
            if (!prints.TryGetValue(key, out var list)) return;

            Block? oldBlock = sapi.World.GetBlock(oldblockId);

            if (oldBlock != null && BlockHasTrackedEntity(oldBlock))
            {
                // A container was broken: all its prints fall onto the block it stood on.
                // Do NOT remove the entry until the move actually succeeds, otherwise prints are lost.
                BlockPos? below = FindSurfaceBelow(pos);
                if (below != null)
                {
                    prints.Remove(key);
                    MovePrints(list, below, oldBlock.Code?.ToString());
                }
                return;
            }

            // Another block was broken. Prints that once fell from a container survive and
            // move to one random nearby block, preferably of the same type.
            var survivors = new List<FingerprintRecord>();
            foreach (var rec in list) if (rec.Transferred) survivors.Add(rec);
            if (survivors.Count == 0) return;

            if (oldBlock == null) return;
            BlockPos? target = FindNearbyBlock(pos, oldBlock);
            if (target == null) return;

            prints.Remove(key);
            MovePrints(survivors, target, null);
        }

        void MovePrints(List<FingerprintRecord> moving, BlockPos target, string? sourceCodeIfNew)
        {
            var targetList = GetOrCreate(target.X, target.Y, target.Z, target.dimension);
            foreach (var rec in moving)
            {
                rec.X = target.X; rec.Y = target.Y; rec.Z = target.Z; rec.Dim = target.dimension;
                if (!rec.Transferred)
                {
                    rec.Transferred = true;
                    rec.SourceBlockCode = sourceCodeIfNew;
                }
                var existing = FindByPlayer(targetList, rec.PlayerUid);
                if (existing != null)
                {
                    existing.Touches += rec.Touches;
                    if (rec.LastTouchDays > existing.LastTouchDays)
                    {
                        existing.LastTouchDays = rec.LastTouchDays;
                        existing.Transferred = rec.Transferred;
                        // Не обнуляем уже известный источник
                        existing.SourceBlockCode = rec.SourceBlockCode ?? existing.SourceBlockCode;
                    }
                }
                else targetList.Add(rec);
            }
            TrimList(targetList);
        }

        /// <summary>
        /// Picks one random solid block around pos (within Config.SpreadRadius).
        /// Preference: exactly the same block, then the same kind of block (e.g. any soil), then anything solid.
        /// Closer blocks are preferred within each group.
        /// </summary>
        BlockPos? FindNearbyBlock(BlockPos pos, Block oldBlock)
        {
            int radius = Math.Max(1, Config.SpreadRadius);
            string? oldKind = oldBlock.FirstCodePart();
            var sameBlock = new List<BlockPos>();
            var sameKind = new List<BlockPos>();
            var any = new List<BlockPos>();
            int bestSame = int.MaxValue, bestKind = int.MaxValue, bestAny = int.MaxValue;

            IBlockAccessor ba = sapi.World.BlockAccessor;
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        var p = new BlockPos(pos.X + dx, pos.Y + dy, pos.Z + dz, pos.dimension);
                        if (p.Y < 0) continue;
                        Block b = ba.GetBlock(p);
                        if (b == null || b.Id == 0 || b.Replaceable >= 6000 || b.IsLiquid()) continue;

                        int dist = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                        if (b.Id == oldBlock.Id) AddClosest(sameBlock, p, dist, ref bestSame);
                        else if (oldKind != null && b.FirstCodePart() == oldKind) AddClosest(sameKind, p, dist, ref bestKind);
                        else AddClosest(any, p, dist, ref bestAny);
                    }

            List<BlockPos> pick = sameBlock.Count > 0 ? sameBlock : sameKind.Count > 0 ? sameKind : any;
            if (pick.Count == 0) return null;
            return pick[sapi.World.Rand.Next(pick.Count)];
        }

        static void AddClosest(List<BlockPos> list, BlockPos p, int dist, ref int best)
        {
            if (dist < best) { list.Clear(); best = dist; }
            if (dist == best) list.Add(p);
        }

        // ---------------------------------------------------------------- admin command

        TextCommandResult OnCmdStats(TextCommandCallingArgs args)
        {
            PurgeExpired();
            string lang = args.LanguageCode ?? "en";

            var all = new List<FingerprintRecord>();
            foreach (var l in prints.Values) all.AddRange(l);

            int bytes = JsonUtil.ToBytes(all).Length;
            var players = new HashSet<string>();
            int transferred = 0;
            int touches = 0;
            double oldest = 0;
            double now = NowDays;
            foreach (var r in all)
            {
                players.Add(r.PlayerUid);
                if (r.Transferred) transferred++;
                touches += r.Touches;
                oldest = Math.Max(oldest, now - r.LastTouchDays);
            }

            string size = bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.0") + " KB" : (bytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
            double perRecord = all.Count > 0 ? (double)bytes / all.Count : 0;

            string text = Lang.GetL(lang, "fingerprints:stats",
                all.Count, prints.Count, players.Count, transferred, touches, size, perRecord.ToString("0"), oldest.ToString("0.0"),
                Config.LifetimeDays > 0 ? Config.LifetimeDays.ToString("0") : "∞", Config.MaxPrintsPerBlock);
            return TextCommandResult.Success(text);
        }

        // ---------------------------------------------------------------- API used by the magnifying glass

        public List<FingerprintRecord> GetPrints(BlockPos pos)
        {
            PurgeExpired();
            if (prints.TryGetValue(Key(pos.X, pos.Y, pos.Z, pos.dimension), out var list))
            {
                var copy = new List<FingerprintRecord>(list);
                copy.Sort((a, b) => b.LastTouchDays.CompareTo(a.LastTouchDays));
                return copy;
            }
            return new List<FingerprintRecord>();
        }

        public double NowDays => sapi.World.Calendar.TotalDays;

        // ---------------------------------------------------------------- helpers

        void AddPrint(BlockPos pos, IServerPlayer player, bool transferred, string? sourceCode)
        {
            var list = GetOrCreate(pos.X, pos.Y, pos.Z, pos.dimension);
            var rec = FindByPlayer(list, player.PlayerUID);
            if (rec == null)
            {
                rec = new FingerprintRecord
                {
                    X = pos.X,
                    Y = pos.Y,
                    Z = pos.Z,
                    Dim = pos.dimension,
                    PlayerUid = player.PlayerUID,
                };
                list.Add(rec);
            }
            rec.PlayerName = player.PlayerName;
            rec.LastTouchDays = NowDays;

            // The game reports several "uses" for a single opening, so count at most one touch per cooldown
            string touchKey = player.PlayerUID + "|" + Key(pos.X, pos.Y, pos.Z, pos.dimension);
            long nowMs = sapi.World.ElapsedMilliseconds;
            long lastMs;
            bool counted = lastTouchMs.TryGetValue(touchKey, out lastMs) && nowMs - lastMs < Config.TouchCooldownSeconds * 1000;
            if (!counted || rec.Touches == 0) rec.Touches++;
            lastTouchMs[touchKey] = nowMs;
            if (lastTouchMs.Count > 5000) lastTouchMs.Clear();
            rec.Transferred = transferred;
            rec.SourceBlockCode = sourceCode;
            TrimList(list);
        }

        void TrimList(List<FingerprintRecord> list)
        {
            int max = Math.Max(1, Config.MaxPrintsPerBlock);
            if (list.Count <= max) return;
            list.Sort((a, b) => b.LastTouchDays.CompareTo(a.LastTouchDays));
            list.RemoveRange(max, list.Count - max);
        }

        void PurgeExpired()
        {
            if (Config.LifetimeDays <= 0) return;
            double now = NowDays;
            List<string>? empty = null;
            foreach (var kv in prints)
            {
                kv.Value.RemoveAll(r => now - r.LastTouchDays > Config.LifetimeDays);
                if (kv.Value.Count == 0)
                {
                    empty ??= new List<string>();
                    empty.Add(kv.Key);
                }
            }
            if (empty != null) foreach (var k in empty) prints.Remove(k);
        }

        BlockPos? FindSurfaceBelow(BlockPos pos)
        {
            BlockPos cur = pos.Copy();
            int maxDist = Math.Max(1, Config.MaxDropDistance);
            for (int i = 0; i < maxDist; i++)
            {
                cur.Y--;
                if (cur.Y < 0) return null;
                Block b = sapi.World.BlockAccessor.GetBlock(cur);
                // Не требуем Replaceable < 6000: иначе трава, снег и листва не считаются "полом".
                if (b != null && b.Id != 0 && !b.IsLiquid()) return cur.Copy();
            }
            return null;
        }

        bool BlockHasTrackedEntity(Block block)
        {
            if (string.IsNullOrEmpty(block.EntityClass)) return false;
            Type? t = sapi.ClassRegistry.GetBlockEntity(block.EntityClass);
            return t != null && IsTracked(t);
        }

        bool IsTracked(Type? t)
        {
            for (; t != null && t != typeof(object); t = t.BaseType)
            {
                if (trackedClasses.Contains(t.Name)) return true;
            }
            return false;
        }

        List<FingerprintRecord> GetOrCreate(int x, int y, int z, int dim)
        {
            string key = Key(x, y, z, dim);
            if (!prints.TryGetValue(key, out var list))
            {
                list = new List<FingerprintRecord>();
                prints[key] = list;
            }
            return list;
        }

        static FingerprintRecord? FindByPlayer(List<FingerprintRecord> list, string uid)
        {
            foreach (var r in list) if (r.PlayerUid == uid) return r;
            return null;
        }

        static string Key(int x, int y, int z, int dim) => x + "," + y + "," + z + "," + dim;
    }
}