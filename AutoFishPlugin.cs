using System;
using System.IO;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using Vector2 = Microsoft.Xna.Framework.Vector2;

namespace AutoFishPlugin
{
    [ApiVersion(2, 1)]
    public class AutoFishPlugin : TerrariaPlugin
    {
        public override string Name => "AutoFish";
        public override string Author => "Enigfrank";
        public override string Description => "挂机钓鱼插件";
        public override Version Version => new(1, 4, 0);

        public const string UsePermission = "autofish.use";
        public const string AdminPermission = "autofish.admin";

        public static ConfigFile Config = new();

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
            {
                PlayerStates[i] = new PlayerState();
            }

            ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
            ServerApi.Hooks.ServerLeave.Register(this, OnServerLeave);
            GeneralHooks.ReloadEvent += OnReload;

            GetDataHandlers.NewProjectile += OnNewProjectile;

            Commands.ChatCommands.Add(new Command(UsePermission, FishCommands, "autofish", "af")
            {
                HelpText = "自动钓鱼指令输入 /af 开关功能，输入 /af reload 重载配置"
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

            if (args.Type >= 0 && args.Type < ContentSamples.ProjectilesByType.Count)
            {
                if (ContentSamples.ProjectilesByType[args.Type].bobber)
                {
                    if (args.Velocity.Length() > 0.5f)
                    {
                        state.LastVelocity = args.Velocity;
                        state.LastBobberType = args.Type;
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
            {
                PlayerStates[args.Who] = new PlayerState();
            }
        }

        private void FishCommands(CommandArgs args)
        {
            if (args.Parameters.Count > 0 && args.Parameters[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                if (!args.Player.HasPermission(AdminPermission))
                {
                    args.Player.SendErrorMessage("你没有重载自动钓鱼配置的权限！");
                    return;
                }

                Config = ConfigFile.Read();
                args.Player.SendSuccessMessage("[自动钓鱼] 配置文件已重载！咬钩延迟: {0} 帧 | 鱼饵消耗倍率: {1:F2}x",
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
                args.Player.SendSuccessMessage("[挂机钓鱼] 已开启！咬钩延迟: {0:F2}秒 | 鱼饵消耗倍率: {1:F2}x. 请手动抛竿一次以启动",
                    Config.BiteDelayTicks / 60f, Config.BaitConsumeMultiplier);
            }
            else
            {
                args.Player.SendInfoMessage("[挂机钓鱼] 已关闭");
            }
        }

        private void OnGameUpdate(EventArgs args)
        {
            // 1. 处理水面浮标
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
                            proj.ai[0] = 660f;
                            proj.FishingCheck();

                            if (proj.ai[1] < 0f)
                            {
                                NetMessage.SendData(27, -1, -1, null, proj.whoAmI);
                            }
                        }
                    }
                }
                else
                {
                    state.WaterTicks = 0;
                }

                // 咬钩阶段
                if (proj.ai[1] < 0f)
                {
                    int caughtItemType = (int)-proj.ai[1];

                    // 1. 生成战利品掉落
                    if (caughtItemType > 0)
                    {
                        int itemIndex = Item.NewItem(null, (int)player.position.X, (int)player.position.Y, player.width, player.height, caughtItemType);
                        if (itemIndex >= 0 && itemIndex < Main.maxItems)
                        {
                            NetMessage.SendData(21, -1, -1, null, itemIndex, 1f);
                        }
                    }

                    // 2. 执行鱼饵消耗判定并同步背包
                    TryConsumeBait(player, Config.BaitConsumeMultiplier);

                    // 3. 销毁旧浮标
                    proj.ai[1] = 0f;
                    proj.active = false;
                    proj.Kill();
                    NetMessage.SendData(29, -1, -1, null, proj.whoAmI, owner);

                    state.WaterTicks = 0;
                    state.WaitingRecast = true;
                    state.RecastDelayTimer = Config.RecastDelayTicks;
                }
            }

            // 2. 自动补抛
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
                    tsPlayer.SendWarningMessage("[挂机钓鱼] 未手持钓竿，挂机已暂停手持钓竿抛竿即可继续");
                    continue;
                }

                // 检查背包内是否仍有鱼饵
                if (!HasBait(player))
                {
                    tsPlayer.SendWarningMessage("[挂机钓鱼] 鱼饵已耗尽，挂机已自动停止");
                    state.Enabled = false;
                    continue;
                }

                Vector2 shootVelocity = state.LastVelocity;
                if (shootVelocity.Length() < 0.5f)
                {
                    shootVelocity = new Vector2(player.direction * 7.5f, -3.5f);
                }

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
                    NetMessage.SendData(27, -1, -1, null, newProjIndex);
                }
            }
        }

        /// <summary>
        /// 按照原版公式与自定义倍率扣除鱼饵，并向客户端同步背包状态
        /// </summary>
        private static void TryConsumeBait(Player player, float multiplier)
        {
            if (multiplier <= 0f)
                return;

            for (int i = 0; i < 58; i++)
            {
                Item item = player.inventory[i];
                if (item != null && item.stack > 0 && item.bait > 0)
                {
                    // 原版基础消耗概率公式：1 / (1 + 鱼饵力 / 6)
                    float baseChance = 1f / (1f + item.bait / 6f);
                    float finalChance = Math.Clamp(baseChance * multiplier, 0f, 1f);

                    if (Main.rand.NextFloat() < finalChance)
                    {
                        item.stack--;
                        if (item.stack <= 0)
                        {
                            item.TurnToAir();
                        }

                        NetMessage.SendData(5, -1, -1, null, player.whoAmI, i, (float)item.prefix);
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
                {
                    return true;
                }
            }
            return false;
        }
    }
}