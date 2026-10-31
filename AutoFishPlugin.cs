using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.FishDropRules;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace AutoFishPlugin
{
    [ApiVersion(2, 1)]
    public class AutoFishPlugin : TerrariaPlugin
    {
        public override string Name => "AutoFish";
        public override string Author => "Enigfrank";
        public override string Description => "挂机钓鱼插件";
        public override Version Version => new(1, 7, 5);

        public const string UsePermission = "autofish.use";
        public const string AdminPermission = "autofish.admin";

        public static ConfigFile Config = new();
        private static MethodInfo? MiRollDropLevels;   // (int, out bool, out bool, out bool, out bool, out bool, out bool)
        private static MethodInfo? MiProbeQuestFish;   // (ref FishingAttempt)
        private static MethodInfo? MiRollEnemySpawns;  // (ref FishingAttempt)
        private static MethodInfo? MiRollItemDrop;     // (ref FishingAttempt)
        private static FieldInfo? FiProjectileContext; // Projectile._context

        private static bool ReflectionReady;

        private static long VanillaUseCount;
        private static long FallbackUseCount;
        private static long FallbackFailureCount;

        private class PlayerState
        {
            public bool Enabled { get; set; } = false;
            public Vector2 LastVelocity { get; set; } = Vector2.Zero;
            public int LastBobberType { get; set; } = 0;
            public bool WaitingRecast { get; set; } = false;
            public int RecastDelayTimer { get; set; } = 0;
            public int WaterTicks { get; set; } = 0;
        }

        private static readonly PlayerState[] PlayerStates = new PlayerState[Main.maxPlayers];

        public AutoFishPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            Config = ConfigFile.Read();

            for (int i = 0; i < Main.maxPlayers; i++)
                PlayerStates[i] = new PlayerState();

            InitializeFishingReflection();

            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            GeneralHooks.ReloadEvent += OnReload;
            GetDataHandlers.NewProjectile += OnNewProjectile;

            Commands.ChatCommands.Add(new Command(UsePermission, FishCommands, "autofish", "af")
            {
                HelpText = "自动钓鱼指令。/af 开关功能，/af reload 重载配置，/af status 查看掉落统计。"
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnServerLeave);
                GeneralHooks.ReloadEvent -= OnReload;
                GetDataHandlers.NewProjectile -= OnNewProjectile;
            }
            base.Dispose(disposing);
        }

        private static MethodInfo? FindMethod(Type type, string name)
        {
            return type.GetMethod(name,
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static void InitializeFishingReflection()
        {
            try
            {
                MiRollDropLevels = FindMethod(typeof(Projectile), "FishingCheck_RollDropLevels");
                MiProbeQuestFish = FindMethod(typeof(Projectile), "FishingCheck_ProbeForQuestFish");
                MiRollEnemySpawns = FindMethod(typeof(Projectile), "FishingCheck_RollEnemySpawns");
                MiRollItemDrop = FindMethod(typeof(Projectile), "FishingCheck_RollItemDrop");
                FiProjectileContext = typeof(Projectile).GetField("_context",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

                ReflectionReady = MiRollDropLevels != null && MiProbeQuestFish != null &&
                                  MiRollEnemySpawns != null && MiRollItemDrop != null &&
                                  FiProjectileContext != null;

                TShock.Log.ConsoleInfo(
                    $"[AutoFish] 原版钓鱼方法反射就绪状态: " +
                    $"RollDropLevels={(MiRollDropLevels != null)} " +
                    $"ProbeForQuestFish={(MiProbeQuestFish != null)} " +
                    $"RollEnemySpawns={(MiRollEnemySpawns != null)} " +
                    $"RollItemDrop={(MiRollItemDrop != null)} " +
                    $"ProjectileContext={(FiProjectileContext != null)}");
            }
            catch (Exception ex)
            {
                ReflectionReady = false;
                TShock.Log.ConsoleError($"[AutoFish] 钓鱼反射初始化异常: {ex.Message}");
            }
        }

        private void OnNewProjectile(object? sender, GetDataHandlers.NewProjectileEventArgs args)
        {
            if (args.Player == null || !args.Player.RealPlayer)
                return;

            int owner = args.Owner;
            if (owner < 0 || owner >= Main.maxPlayers)
                return;

            var state = PlayerStates[owner];
            if (!state.Enabled)
                return;

            int type = args.Type;
            if (type >= 0 && type < ContentSamples.ProjectilesByType.Count)
            {
                if (ContentSamples.ProjectilesByType[type].bobber)
                {
                    Vector2 velocity = args.Velocity;
                    if (velocity.Length() > 0.5f)
                    {
                        state.LastVelocity = velocity;
                        state.LastBobberType = type;
                    }
                }
            }
        }

        private void OnReload(ReloadEventArgs args)
        {
            Config = ConfigFile.Read();
            args.Player?.SendSuccessMessage("[自动钓鱼] 配置文件已重新加载");
        }

        private void OnServerLeave(LeaveEventArgs args)
        {
            if (args.Who >= 0 && args.Who < Main.maxPlayers)
                PlayerStates[args.Who] = new PlayerState();
        }

        private void FishCommands(CommandArgs args)
        {
            if (args.Parameters.Count > 0 && args.Parameters[0].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                if (!args.Player.HasPermission(AdminPermission))
                {
                    args.Player.SendErrorMessage("你没有查看自动钓鱼状态的权限！");
                    return;
                }

                long total = VanillaUseCount + FallbackUseCount;
                string ratio = total > 0 ? $"{(double)VanillaUseCount / total:P1}" : "暂无数据";

                args.Player.SendInfoMessage(
                    "[自动钓鱼] 反射就绪: {0} | 原版链路命中: {1} 次 | 兜底表命中: {2} 次 | " +
                    "原版占比: {3} | 原版链路异常失败: {4} 次",
                    ReflectionReady, VanillaUseCount, FallbackUseCount, ratio, FallbackFailureCount);
                return;
            }

            if (args.Parameters.Count > 0 && args.Parameters[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                if (!args.Player.HasPermission(AdminPermission))
                {
                    args.Player.SendErrorMessage("你没有重载自动钓鱼配置的权限！");
                    return;
                }

                Config = ConfigFile.Read();
                args.Player.SendSuccessMessage("[自动钓鱼] 配置已重载！咬钩延迟: {0} 帧 | 鱼饵消耗倍率: {1:F2}x",
                    Config.BiteDelayTicks, Config.BaitConsumeMultiplier);
                return;
            }

            if (args.Player == null || !args.Player.RealPlayer)
            {
                args.Player?.SendErrorMessage("该指令只能由游戏内玩家执行！");
                return;
            }

            var state = PlayerStates[args.Player.Index];
            state.Enabled = !state.Enabled;
            state.WaitingRecast = false;
            state.RecastDelayTimer = 0;
            state.WaterTicks = 0;

            if (state.Enabled)
            {
                args.Player.SendSuccessMessage("[挂机钓鱼] 已开启！咬钩延迟: {0:F2}秒 | 鱼饵消耗倍率: {1:F2}x 请手动抛竿一次启动",
                    Config.BiteDelayTicks / 60f, Config.BaitConsumeMultiplier);
            }
            else
            {
                args.Player.SendInfoMessage("[挂机钓鱼] 已关闭");
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            // 1. 处理水面活动浮标
            for (int i = 0; i < Main.maxProjectiles; i++)
            {
                Projectile proj = Main.projectile[i];
                if (!proj.active || !proj.bobber)
                    continue;

                int owner = proj.owner;
                if (owner < 0 || owner >= Main.maxPlayers)
                    continue;

                var state = PlayerStates[owner];
                if (!state.Enabled)
                    continue;

                TSPlayer tsPlayer = TShock.Players[owner];
                Player player = Main.player[owner];

                if (tsPlayer == null || !tsPlayer.Active || player.dead)
                    continue;

                if (proj.wet || proj.lavaWet || proj.honeyWet)
                {
                    if (proj.ai[1] == 0f)
                    {
                        state.WaterTicks++;
                        if (state.WaterTicks >= Config.BiteDelayTicks)
                        {
                            if (!HasBait(player))
                            {
                                tsPlayer.SendWarningMessage("[挂机钓鱼] 鱼饵已耗尽，挂机已暂停");
                                state.Enabled = false;
                                proj.active = false;
                                proj.Kill();
                                NetMessage.SendData(29, -1, -1, null, proj.whoAmI, owner);
                                continue;
                            }

                            ExecuteFishingReward(player, proj);
                            TryConsumeBait(player, Config.BaitConsumeMultiplier);

                            proj.active = false;
                            proj.Kill();
                            NetMessage.SendData(29, -1, -1, null, proj.whoAmI, owner);

                            state.WaterTicks = 0;
                            state.WaitingRecast = true;
                            state.RecastDelayTimer = Config.RecastDelayTicks;
                        }
                    }
                }
                else
                {
                    state.WaterTicks = 0;
                }
            }

            // 2. 自动补抛逻辑
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                var state = PlayerStates[i];
                if (!state.Enabled || !state.WaitingRecast)
                    continue;

                TSPlayer tsPlayer = TShock.Players[i];
                Player player = Main.player[i];

                if (tsPlayer == null || !tsPlayer.Active || player.dead)
                {
                    state.WaitingRecast = false;
                    continue;
                }

                if (--state.RecastDelayTimer > 0)
                    continue;

                state.WaitingRecast = false;

                if (!HasBait(player))
                {
                    tsPlayer.SendWarningMessage("[挂机钓鱼] 鱼饵已耗尽，挂机已停止");
                    state.Enabled = false;
                    continue;
                }

                Vector2 shootVelocity = state.LastVelocity;
                if (shootVelocity.Length() < 0.5f)
                    shootVelocity = new Vector2(player.direction * 7.5f, -3.5f);

                int bobberType = state.LastBobberType > 0 ? state.LastBobberType : ProjectileID.BobberWooden;

                int newProjIndex = Projectile.NewProjectile(
                    null,
                    player.MountedCenter.X,
                    player.MountedCenter.Y,
                    shootVelocity.X,
                    shootVelocity.Y,
                    bobberType,
                    0,
                    0f,
                    player.whoAmI
                );

                if (newProjIndex >= 0 && newProjIndex < Main.maxProjectiles)
                {
                    Main.projectile[newProjIndex].owner = player.whoAmI;
                    NetMessage.SendData(27, -1, -1, null, newProjIndex);
                }
            }
        }

        private static void ExecuteFishingReward(Player player, Projectile proj)
        {
            proj.owner = player.whoAmI;

            PlayerFishingConditions conditions = player.GetFishingConditions();

            // 补全钓竿
            if (conditions.PoleItemType <= 0)
            {
                for (int i = 0; i < 58; i++)
                {
                    if (player.inventory[i] != null && player.inventory[i].fishingPole > 0)
                    {
                        conditions.PoleItemType = player.inventory[i].type;
                        conditions.PolePower = player.inventory[i].fishingPole;
                        break;
                    }
                }
            }

            // 补全鱼饵（过滤松露虫，防止挂机误消耗或误召唤 Boss）
            if (conditions.BaitItemType <= 0 || conditions.BaitItemType == ItemID.TruffleWorm)
            {
                for (int i = 0; i < 58; i++)
                {
                    Item invItem = player.inventory[i];
                    if (invItem != null && invItem.stack > 0 && invItem.bait > 0 && invItem.type != ItemID.TruffleWorm)
                    {
                        conditions.BaitItemType = invItem.type;
                        conditions.BaitPower = invItem.bait;
                        break;
                    }
                }
            }

            if (conditions.FinalFishingLevel <= 0)
            {
                conditions.FinalFishingLevel = conditions.PolePower + conditions.BaitPower + player.fishingSkill;
            }

            FishingAttempt attempt = default;
            attempt.X = (int)(proj.Center.X / 16f);
            attempt.Y = (int)(proj.Center.Y / 16f);
            attempt.bobberType = proj.type;
            attempt.inLava = proj.lavaWet;
            attempt.inHoney = proj.honeyWet;

            // 海洋大水体判定（满足 IsOriginalOcean 与 Water1000 规则）
            bool isAtOcean = player.ZoneBeach || attempt.X < 380 || attempt.X > Main.maxTilesX - 380;
            attempt.waterTilesCount = isAtOcean ? 1001 : 300;
            attempt.waterNeededToFish = proj.honeyWet ? 50 : 75;
            attempt.waterQuality = 1f;

            // 熔岩垂钓资格计算
            bool hasLavaBait = conditions.BaitItemType > 0 &&
                               conditions.BaitItemType < ItemID.Sets.IsLavaBait.Length &&
                               ItemID.Sets.IsLavaBait[conditions.BaitItemType];

            attempt.CanFishInLava = player.accLavaFishing ||
                                   conditions.PoleItemType == ItemID.HotlineFishingHook ||
                                   hasLavaBait;

            attempt.fishingLevel = conditions.FinalFishingLevel;
            attempt.playerFishingConditions = conditions;

            if (attempt.Y < Main.worldSurface * 0.45)
                attempt.heightLevel = 0;
            else if (attempt.Y < Main.worldSurface)
                attempt.heightLevel = 1;
            else if (attempt.Y < Main.rockLayer)
                attempt.heightLevel = 2;
            else if (attempt.Y < Main.maxTilesY - 300)
                attempt.heightLevel = 3;
            else
                attempt.heightLevel = 4;

            int dropItem = 0;
            bool vanillaSuccess = false;

            if (ReflectionReady)
            {
                try
                {
                    // 步骤 1：稀有度掷骰
                    object[] dropLevelArgs = { attempt.fishingLevel, false, false, false, false, false, false };
                    object? dropLevelTarget = MiRollDropLevels!.IsStatic ? null : proj;
                    MiRollDropLevels!.Invoke(dropLevelTarget, dropLevelArgs);
                    attempt.common = (bool)dropLevelArgs[1];
                    attempt.uncommon = (bool)dropLevelArgs[2];
                    attempt.rare = (bool)dropLevelArgs[3];
                    attempt.veryrare = (bool)dropLevelArgs[4];
                    attempt.legendary = (bool)dropLevelArgs[5];
                    attempt.crate = (bool)dropLevelArgs[6];

                    // 步骤 2：任务鱼判定
                    object[] questArgs = { attempt };
                    object? questTarget = MiProbeQuestFish!.IsStatic ? null : proj;
                    MiProbeQuestFish!.Invoke(questTarget, questArgs);
                    attempt = (FishingAttempt)questArgs[0];

                    // 步骤 3：敌怪生成判定
                    object[] enemyArgs = { attempt };
                    object? enemyTarget = MiRollEnemySpawns!.IsStatic ? null : proj;
                    MiRollEnemySpawns!.Invoke(enemyTarget, enemyArgs);
                    attempt = (FishingAttempt)enemyArgs[0];

                    if (attempt.rolledEnemySpawn > 0)
                    {
                        int npcIndex = NPC.NewNPC(null, (int)proj.Center.X, (int)proj.Center.Y, attempt.rolledEnemySpawn);
                        if (npcIndex >= 0 && npcIndex < Main.maxNPCs)
                            NetMessage.SendData(23, -1, -1, null, npcIndex);
                        return;
                    }

                    // 核心修复点：完整初始化并灌入静态 Projectile._context
                    FishingContext context = new FishingContext();
                    context.Fisher = attempt;
                    context.Player = player;

                    // 计算互斥生态与复合沙漠判定
                    bool flagCorrupt = player.ZoneCorrupt;
                    bool flagCrimson = player.ZoneCrimson;
                    bool flagJungle = player.ZoneJungle;
                    bool flagSnow = player.ZoneSnow;
                    bool flagDesert = player.ZoneDesert;

                    if (flagCorrupt && flagCrimson)
                    {
                        if (Main.rand.Next(2) == 0) flagCrimson = false;
                        else flagCorrupt = false;
                    }
                    if (flagSnow && flagJungle && Main.rand.Next(2) == 0)
                    {
                        flagSnow = false;
                    }

                    context.RolledCorruption = flagCorrupt;
                    context.RolledCrimson = flagCrimson;
                    context.RolledJungle = flagJungle;
                    context.RolledSnow = flagSnow;
                    context.RolledDesert = flagDesert;
                    context.RolledInfectedDesert = flagDesert && (flagCorrupt || flagCrimson || player.ZoneHallow);
                    context.RolledRemixOcean = Main.remixWorld && attempt.heightLevel == 1 && (double)attempt.Y >= Main.rockLayer && Main.rand.Next(2) == 0;

                    FiProjectileContext!.SetValue(null, context);

                    // 步骤 4：原版掉落规则引擎结算
                    object[] itemArgs = { attempt };
                    object? itemTarget = MiRollItemDrop!.IsStatic ? null : proj;
                    MiRollItemDrop!.Invoke(itemTarget, itemArgs);
                    attempt = (FishingAttempt)itemArgs[0];

                    dropItem = attempt.rolledItemDrop;
                    vanillaSuccess = true;
                }
                catch (Exception ex)
                {
                    vanillaSuccess = false;
                    FallbackFailureCount++;
                    if (FallbackFailureCount == 1 || FallbackFailureCount % 50 == 0)
                    {
                        TShock.Log.ConsoleError(
                            $"[AutoFish] 原版掉落链路调用异常（累计 {FallbackFailureCount} 次），" +
                            $"已回退至自定义掉落池: {ex}");
                    }
                }
            }

            if (vanillaSuccess)
            {
                VanillaUseCount++;
            }
            else
            {
                // 仅在反射异常时回退到兜底表
                dropItem = RollRandomBiomeFish(player, attempt);
                FallbackUseCount++;
            }

            // 原版合法空钩或无掉落产物时直接返回
            if (dropItem <= 0)
                return;

            int itemIndex = Item.NewItem(null, (int)player.position.X, (int)player.position.Y, player.width, player.height, dropItem);
            if (itemIndex >= 0 && itemIndex < Main.maxItems)
            {
                Main.item[itemIndex].noGrabDelay = 0;
                NetMessage.SendData(21, -1, -1, null, itemIndex, 1f);
            }
        }

        private static int RollRandomBiomeFish(Player player, FishingAttempt attempt)
        {
            if (attempt.inHoney)
                return ItemID.Honeyfin;

            if (attempt.inLava)
            {
                if (!attempt.CanFishInLava)
                    return 0;

                int[] lavaDrops = Main.hardMode
                    ? new int[] { ItemID.ObsidianSwordfish, ItemID.FlarefinKoi, ItemID.Obsidifish, ItemID.LavaCrateHard }
                    : new int[] { ItemID.FlarefinKoi, ItemID.Obsidifish, ItemID.LavaCrate };
                return lavaDrops[Main.rand.Next(lavaDrops.Length)];
            }

            if (Main.rand.Next(100) < 15)
            {
                if (player.ZoneBeach)
                    return Main.hardMode ? ItemID.OceanCrateHard : ItemID.OceanCrate;
                if (player.ZoneJungle)
                    return Main.hardMode ? ItemID.JungleFishingCrateHard : ItemID.JungleFishingCrate;
                if (player.ZoneCorrupt)
                    return Main.hardMode ? ItemID.CorruptFishingCrateHard : ItemID.CorruptFishingCrate;
                if (player.ZoneCrimson)
                    return Main.hardMode ? ItemID.CrimsonFishingCrateHard : ItemID.CrimsonFishingCrate;
                if (player.ZoneHallow)
                    return ItemID.HallowedFishingCrateHard;
                if (player.ZoneSnow)
                    return Main.hardMode ? ItemID.FrozenCrateHard : ItemID.FrozenCrate;
                if (attempt.heightLevel == 0)
                    return Main.hardMode ? ItemID.FloatingIslandFishingCrateHard : ItemID.FloatingIslandFishingCrate;

                int crateRoll = Main.rand.Next(100);
                if (crateRoll < 5)
                    return Main.hardMode ? ItemID.GoldenCrateHard : ItemID.GoldenCrate;
                if (crateRoll < 30)
                    return Main.hardMode ? ItemID.IronCrateHard : ItemID.IronCrate;
                return Main.hardMode ? ItemID.WoodenCrateHard : ItemID.WoodenCrate;
            }

            if (player.ZoneBeach)
            {
                int rareRoll = Main.rand.Next(100);
                if (rareRoll < 3) return ItemID.ReaverShark;
                if (rareRoll < 6) return ItemID.SawtoothShark;
                if (rareRoll < 9) return ItemID.Swordfish;
                if (rareRoll < 12) return ItemID.FrogLeg;
                if (rareRoll < 15) return ItemID.BalloonPufferfish;

                int[] oceanFish = new int[] { ItemID.Tuna, ItemID.Trout, ItemID.RedSnapper, ItemID.PinkJellyfish };
                return oceanFish[Main.rand.Next(oceanFish.Length)];
            }

            if (player.ZoneJungle)
            {
                int[] jungleFish = new int[] { ItemID.NeonTetra, ItemID.VariegatedLardfish, ItemID.DoubleCod };
                return jungleFish[Main.rand.Next(jungleFish.Length)];
            }

            if (player.ZoneCorrupt)
            {
                if (Main.hardMode && Main.rand.Next(100) < 5) return ItemID.Toxikarp;
                return ItemID.Ebonkoi;
            }

            if (player.ZoneCrimson)
            {
                if (Main.hardMode && Main.rand.Next(100) < 5) return ItemID.Bladetongue;
                int[] crimsonFish = new int[] { ItemID.CrimsonTigerfish, ItemID.Hemopiranha };
                return crimsonFish[Main.rand.Next(crimsonFish.Length)];
            }

            if (player.ZoneHallow)
            {
                if (Main.rand.Next(100) < 5) return ItemID.CrystalSerpent;
                if (Main.rand.Next(100) < 10) return ItemID.ChaosFish;
                int[] hallowFish = new int[] { ItemID.Prismite, ItemID.PrincessFish };
                return hallowFish[Main.rand.Next(hallowFish.Length)];
            }

            if (player.ZoneSnow)
            {
                int[] snowFish = new int[] { ItemID.FrostMinnow, ItemID.AtlanticCod };
                return snowFish[Main.rand.Next(snowFish.Length)];
            }

            if (attempt.heightLevel == 0)
                return ItemID.Damselfish;

            if (attempt.heightLevel >= 2)
            {
                int[] caveFish = new int[] { ItemID.ArmoredCavefish, ItemID.SpecularFish, ItemID.Stinkfish };
                return caveFish[Main.rand.Next(caveFish.Length)];
            }

            int[] forestFish = new int[] { ItemID.Bass, ItemID.Trout };
            return forestFish[Main.rand.Next(forestFish.Length)];
        }

        private static void TryConsumeBait(Player player, float multiplier)
        {
            if (multiplier <= 0f)
                return;

            for (int i = 0; i < 58; i++)
            {
                Item item = player.inventory[i];
                // 忽略松露虫，防止挂机自动消耗
                if (item != null && item.stack > 0 && item.bait > 0 && item.type != ItemID.TruffleWorm)
                {
                    float baseChance = 1f / (1f + item.bait / 6f);
                    float finalChance = Math.Clamp(baseChance * multiplier, 0f, 1f);

                    if (Main.rand.NextFloat() < finalChance)
                    {
                        item.stack--;
                        if (item.stack <= 0)
                            item.TurnToAir();

                        NetMessage.SendData(5, -1, -1, null, player.whoAmI, i, item.stack, item.prefix, item.type);
                    }
                    break;
                }
            }
        }

        private static bool HasBait(Player player)
        {
            for (int i = 0; i < 58; i++)
            {
                Item item = player.inventory[i];
                if (item != null && item.stack > 0 && item.bait > 0 && item.type != ItemID.TruffleWorm)
                    return true;
            }
            return false;
        }
    }
}
