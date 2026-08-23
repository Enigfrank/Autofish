using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
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
        public override Version Version => new(1, 7, 0);

        public const string UsePermission = "autofish.use";
        public const string AdminPermission = "autofish.admin";

        public static ConfigFile Config = new();
        private static MethodInfo? MiRollDropLevels;   // (int, out int,out int,out int,out int,out int,out int)
        private static MethodInfo? MiProbeQuestFish;   // (ref FishingAttempt)
        private static MethodInfo? MiRollEnemySpawns;  // (ref FishingAttempt)
        private static MethodInfo? MiRollItemDrop;     // (ref FishingAttempt)

        private static bool ReflectionReady;

        // 运行期统计：走了几次原版真实掉落链路、几次自定义兜底表、
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
                HelpText = "自动钓鱼指令。/af 开关功能，/af reload 重载配置，/af status 查看原版/兜底掉落路径命中统计（需管理权限）。"
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

                ReflectionReady = MiRollDropLevels != null && MiProbeQuestFish != null &&
                                   MiRollEnemySpawns != null && MiRollItemDrop != null;

                TShock.Log.ConsoleInfo(
                    $"[AutoFish] 原版钓鱼方法反射结果: " +
                    $"RollDropLevels={(MiRollDropLevels != null)} " +
                    $"ProbeForQuestFish={(MiProbeQuestFish != null)} " +
                    $"RollEnemySpawns={(MiRollEnemySpawns != null)} " +
                    $"RollItemDrop={(MiRollItemDrop != null)}");

                if (!ReflectionReady)
                {
                    TShock.Log.ConsoleError(
                        "[AutoFish] 未能完整获取原版钓鱼掉落链路方法，缺失的部分将始终使用" +
                        "自定义兜底掉落表（RollRandomBiomeFish），无法完全还原原版随机机制。" +
                        "请用反编译工具核实当前服务端程序集里的真实方法名/签名。");
                }
            }
            catch (Exception ex)
            {
                ReflectionReady = false;
                TShock.Log.ConsoleError($"[AutoFish] 钓鱼反射初始化异常，将回退到自定义掉落池: {ex.Message}");
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
                    "[自动钓鱼] 反射方法就绪: {0} | 原版链路命中: {1} 次 | 兜底表命中: {2} 次 | " +
                    "原版占比: {3} | 原版链路调用失败: {4} 次",
                    ReflectionReady, VanillaUseCount, FallbackUseCount, ratio, FallbackFailureCount);

                if (!ReflectionReady)
                {
                    args.Player.SendWarningMessage("[自动钓鱼] 原版反射未就绪，请反馈至许工!");
                }
                else if (FallbackUseCount > 0 && VanillaUseCount == 0)
                {
                    args.Player.SendWarningMessage("[自动钓鱼] 原版反射方法调用失败，请反馈至许工!");
                }
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
                args.Player.SendSuccessMessage("[自动钓鱼] 配置文件已重载！当前咬钩延迟: {0} 帧 | 鱼饵消耗倍率: {1:F2}x",
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
                args.Player.SendSuccessMessage("[挂机钓鱼] 已开启！咬钩延迟: {0:F2}秒 | 鱼饵消耗倍率: {1:F2}x 请手动抛竿一次以启动",
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
                            ExecuteFishingReward(player, proj);
                            TryConsumeBait(player, Config.BaitConsumeMultiplier);

                            proj.active = false;
                            proj.Kill();
                            NetMessage.SendData(29, -1, -1, null, proj.identity, owner);

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

                if (player.HeldItem == null || player.HeldItem.fishingPole <= 0)
                {
                    tsPlayer.SendWarningMessage("[挂机钓鱼] 未手持钓竿，挂机已暂停 手持钓竿抛竿即可继续");
                    continue;
                }

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
                    NetMessage.SendData(27, -1, -1, null, newProjIndex);
            }
        }

        private static void ExecuteFishingReward(Player player, Projectile proj)
        {
            PlayerFishingConditions conditions = player.GetFishingConditions();

            FishingAttempt attempt = default;
            attempt.X = (int)(proj.Center.X / 16f);
            attempt.Y = (int)(proj.Center.Y / 16f);
            attempt.bobberType = proj.type;
            attempt.inLava = proj.lavaWet;
            attempt.inHoney = proj.honeyWet;
            attempt.waterTilesCount = 300;
            attempt.waterNeededToFish = proj.honeyWet ? 50 : 75;
            attempt.fishingLevel = conditions.FinalFishingLevel;

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

            attempt.playerFishingConditions = conditions;

            int dropItem = 0;
            bool usedVanillaPath = false;

            if (ReflectionReady)
            {
                try
                {
                    object[] dropLevelArgs = { attempt.fishingLevel, false, false, false, false, false, false };
                    object? dropLevelTarget = MiRollDropLevels!.IsStatic ? null : proj;
                    MiRollDropLevels!.Invoke(dropLevelTarget, dropLevelArgs);
                    attempt.common = (bool)dropLevelArgs[1];
                    attempt.uncommon = (bool)dropLevelArgs[2];
                    attempt.rare = (bool)dropLevelArgs[3];
                    attempt.veryrare = (bool)dropLevelArgs[4];
                    attempt.legendary = (bool)dropLevelArgs[5];
                    attempt.crate = (bool)dropLevelArgs[6];

                    // 步骤 2：渔夫任务鱼判定
                    object[] questArgs = { attempt };
                    object? questTarget = MiProbeQuestFish!.IsStatic ? null : proj;
                    MiProbeQuestFish!.Invoke(questTarget, questArgs);
                    attempt = (FishingAttempt)questArgs[0];

                    // 步骤 3：怪物入侵（宝箱怪等）判定
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

                    // 步骤 4：真正的物品掉落判定
                    object[] itemArgs = { attempt };
                    object? itemTarget = MiRollItemDrop!.IsStatic ? null : proj;
                    MiRollItemDrop!.Invoke(itemTarget, itemArgs);
                    attempt = (FishingAttempt)itemArgs[0];

                    dropItem = attempt.rolledItemDrop;
                    usedVanillaPath = dropItem > 0;
                }
                catch (Exception ex)
                {
                    FallbackFailureCount++;
                    if (FallbackFailureCount == 1 || FallbackFailureCount % 50 == 0)
                    {
                        TShock.Log.ConsoleError(
                            $"[AutoFish] 原版掉落链路调用异常（累计 {FallbackFailureCount} 次），" +
                            $"本次及此前调用均回退到自定义掉落池，掉落分布仅为近似值: {ex}");
                    }
                }
            }

            if (!usedVanillaPath)
            {
                dropItem = RollRandomBiomeFish(player, attempt);
                FallbackUseCount++;
            }
            else
            {
                VanillaUseCount++;
            }

            if (dropItem <= 0)
                return; // 相当于原版的“摸到垃圾/空军”，不产出物品

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
                if (item != null && item.stack > 0 && item.bait > 0)
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
                if (item != null && item.stack > 0 && item.bait > 0)
                    return true;
            }
            return false;
        }
    }
}