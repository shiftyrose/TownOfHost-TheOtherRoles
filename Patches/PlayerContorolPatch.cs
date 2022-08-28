using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using HarmonyLib;
using Hazel;
using UnityEngine;
using static TownOfHost.Translator;

namespace TownOfHost
{
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckProtect))]
    class CheckProtectPatch
    {
        public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            if (!AmongUsClient.Instance.AmHost) return false;
            Logger.Info("CheckProtect発生: " + __instance.GetNameWithRole() + "=>" + target.GetNameWithRole(), "CheckProtect");
            if (__instance.Is(CustomRoles.Sheriff))
            {
                if (__instance.Data.IsDead)
                {
                    Logger.Info("守護をブロックしました。", "CheckProtect");
                    return false;
                }
            }
            return true;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
    class CheckMurderPatch
    {
        public static Dictionary<byte, float> TimeSinceLastKill = new();
        public static void Update()
        {
            for (byte i = 0; i < 15; i++)
            {
                if (TimeSinceLastKill.ContainsKey(i))
                {
                    TimeSinceLastKill[i] += Time.deltaTime;
                    if (15f < TimeSinceLastKill[i]) TimeSinceLastKill.Remove(i);
                }
            }
        }
        public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            if (!AmongUsClient.Instance.AmHost) return false;

            var killer = __instance; //読み替え変数

            Logger.Info($"{killer.GetNameWithRole()} => {target.GetNameWithRole()}", "CheckMurder");

            //死人はキルできない
            if (killer.Data.IsDead)
            {
                Logger.Info($"{killer.GetNameWithRole()}は死亡しているためキャンセルされました。", "CheckMurder");
                return false;
            }

            //不正キル防止処理
            if (target.Data == null || //PlayerDataがnullじゃないか確認
                target.inVent || target.inMovingPlat //targetの状態をチェック
            )
            {
                Logger.Info("targetは現在キルできない状態です。", "CheckMurder");
                return false;
            }
            if (MeetingHud.Instance != null) //会議中でないかの判定
            {
                Logger.Info("会議が始まっていたため、キルをキャンセルしました。", "CheckMurder");
                return false;
            }

            float minTime = Mathf.Max(0.02f, AmongUsClient.Instance.Ping / 1000f * 6f); //※AmongUsClient.Instance.Pingの値はミリ秒(ms)なので÷1000
            //TimeSinceLastKillに値が保存されていない || 保存されている時間がminTime以上 => キルを許可
            //↓許可されない場合
            if (TimeSinceLastKill.TryGetValue(killer.PlayerId, out var time) && time < minTime)
            {
                Logger.Info("前回のキルからの時間が早すぎるため、キルをブロックしました。", "CheckMurder");
                return false;
            }
            TimeSinceLastKill[killer.PlayerId] = 0f;

            killer.ResetKillCooldown();

            //キルボタンを使えない場合の判定
            if ((Options.CurrentGameMode == CustomGameMode.HideAndSeek || Options.IsStandardHAS) && Options.HideAndSeekKillDelayTimer > 0)
            {
                Logger.Info("HideAndSeekの待機時間中だったため、キルをキャンセルしました。", "CheckMurder");
                return false;
            }

            //キル可能判定
            if (killer.PlayerId != target.PlayerId)
            {
                //自殺でない場合のみ役職チェック
                switch (killer.GetCustomRole())
                {
                    //==========インポスター役職==========//
                    case CustomRoles.Mafia:
                        if (!killer.CanUseKillButton())
                        {
                            Logger.Info(killer?.Data?.PlayerName + "はMafiaだったので、キルはキャンセルされました。", "CheckMurder");
                            return false;
                        }
                        else
                        {
                            Logger.Info(killer?.Data?.PlayerName + "はMafiaですが、他のインポスターがいないのでキルが許可されました。", "CheckMurder");
                        }
                        break;
                    case CustomRoles.FireWorks:
                        if (!killer.CanUseKillButton())
                        {
                            return false;
                        }
                        break;
                    case CustomRoles.Sniper:
                        if (!killer.CanUseKillButton())
                        {
                            return false;
                        }
                        break;
                    case CustomRoles.Mare:
                        if (!killer.CanUseKillButton())
                            return false;
                        break;

                    //==========マッドメイト系役職==========//
                    case CustomRoles.SKMadmate:
                        //キル可能職がサイドキックされた場合
                        return false;

                    //==========第三陣営役職==========//

                    //==========クルー役職==========//
                    case CustomRoles.Sheriff:
                        if (!Sheriff.CanUseKillButton(killer))
                            return false;
                        break;
                    case CustomRoles.PlagueBearer:
                    case CustomRoles.Pestilence:
                        break;
                }
            }


            //キルされた時の特殊判定
            switch (target.GetCustomRole())
            {
                case CustomRoles.SchrodingerCat:
                    //シュレディンガーの猫が切られた場合の役職変化スタート
                    //直接キル出来る役職チェック
                    // Sniperなど自殺扱いのものもあるので追加するときは注意
                    var canDirectKill = !killer.Is(CustomRoles.Arsonist);
                    if (canDirectKill && !killer.Is(CustomRoles.PlagueBearer))
                    {
                        killer.RpcGuardAndKill(target);
                        if (PlayerState.GetDeathReason(target.PlayerId) == PlayerState.DeathReason.Sniped)
                        {
                            //スナイプされた時
                            target.RpcSetCustomRole(CustomRoles.MSchrodingerCat);
                            var sniperId = Sniper.GetSniper(target.PlayerId);
                            NameColorManager.Instance.RpcAdd(sniperId, target.PlayerId, $"{Utils.GetRoleColorCode(CustomRoles.SchrodingerCat)}");
                        }
                        else if (BountyHunter.GetTarget(killer) == target)
                            BountyHunter.ResetTarget(killer);//ターゲットの選びなおし
                        else
                        {
                            SerialKiller.OnCheckMurder(killer, isKilledSchrodingerCat: true);
                            if (killer.GetCustomRole().IsImpostor())
                                target.RpcSetCustomRole(CustomRoles.MSchrodingerCat);
                            if (killer.Is(CustomRoles.Sheriff))
                                target.RpcSetCustomRole(CustomRoles.CSchrodingerCat);
                            if (killer.Is(CustomRoles.Egoist))
                                target.RpcSetCustomRole(CustomRoles.EgoSchrodingerCat);
                            if (killer.Is(CustomRoles.Jackal))
                                target.RpcSetCustomRole(CustomRoles.JSchrodingerCat);
                            if (killer.Is(CustomRoles.Pestilence))
                            {
                                //pesti cat.
                            }

                            NameColorManager.Instance.RpcAdd(killer.PlayerId, target.PlayerId, $"{Utils.GetRoleColorCode(CustomRoles.SchrodingerCat)}");
                        }
                        Utils.NotifyRoles();
                        Utils.CustomSyncAllSettings();
                        return false;
                        //シュレディンガーの猫の役職変化処理終了
                        //第三陣営キル能力持ちが追加されたら、その陣営を味方するシュレディンガーの猫の役職を作って上と同じ書き方で書いてください
                    }
                    break;

                //==========マッドメイト系役職==========//
                case CustomRoles.MadGuardian:
                    //killerがキルできないインポスター判定役職の場合はスキップ
                    if (killer.Is(CustomRoles.Arsonist) //アーソニスト
                    ) break;
                    if (killer.Is(CustomRoles.PlagueBearer) //アーソニスト
                    ) break;

                    //MadGuardianを切れるかの判定処理
                    var taskState = target.GetPlayerTaskState();
                    if (taskState.IsTaskFinished)
                    {
                        int dataCountBefore = NameColorManager.Instance.NameColors.Count;
                        NameColorManager.Instance.RpcAdd(killer.PlayerId, target.PlayerId, "#ff0000");
                        if (Options.MadGuardianCanSeeWhoTriedToKill.GetBool())
                            NameColorManager.Instance.RpcAdd(target.PlayerId, killer.PlayerId, "#ff0000");

                        if (dataCountBefore != NameColorManager.Instance.NameColors.Count)
                            Utils.NotifyRoles();
                        return false;
                    }
                    break;
            }

            //キル時の特殊判定
            if (killer.PlayerId != target.PlayerId)
            {
                //自殺でない場合のみ役職チェック
                if (CustomRoles.TheGlitch.IsEnable())
                {
                    List<PlayerControl> hackedPlayers = new();
                    PlayerControl glitch;
                    foreach (var cp in Main.CursedPlayers)
                    {
                        if (Utils.GetPlayerById(cp.Key).Is(CustomRoles.TheGlitch))
                        {
                            hackedPlayers.Add(cp.Value);
                            glitch = Utils.GetPlayerById(cp.Key);
                        }
                    }
                    if (hackedPlayers.Contains(killer))
                    {
                        return false;
                    }
                }
                if (killer.GetRoleType() == target.GetRoleType() && killer.GetRoleType() == RoleType.Coven)
                {
                    //they are both coven
                    return false;
                }
                if (killer.GetCustomRole().IsCoven() && !Main.HasNecronomicon && !killer.Is(CustomRoles.PotionMaster) && !killer.Is(CustomRoles.HexMaster) && !killer.Is(CustomRoles.CovenWitch))
                    return false;
                foreach (var protect in Main.GuardianAngelTarget)
                {
                    if (target.PlayerId == protect.Value && Main.IsProtected)
                    {
                        killer.RpcGuardAndKill(target);
                        return false;
                    }
                }
                if (target.Is(CustomRoles.Pestilence) && !killer.Is(CustomRoles.Vampire) && !killer.Is(CustomRoles.Werewolf) && !killer.Is(CustomRoles.TheGlitch))
                {
                    target.RpcMurderPlayer(killer);
                    return false;
                }
                if (target.Is(CustomRoles.CovenWitch) && !Main.WitchProtected && !killer.Is(CustomRoles.Arsonist) && !killer.Is(CustomRoles.PlagueBearer))
                {
                    // killer.RpcGuardAndKill(target);
                    killer.ResetKillCooldown();
                    target.RpcGuardAndKill(target);
                    Main.WitchProtected = true;
                    return false;
                }
                switch (killer.GetCustomRole())
                {
                    //==========インポスター役職==========//
                    case CustomRoles.Medusa:
                        if (Main.HasNecronomicon)
                        {
                            if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                            {
                                target.RpcMurderPlayer(killer);
                                return false;
                            }
                            break;
                        }
                        else
                        {
                            return false;
                        }
                    case CustomRoles.CovenWitch:
                        if (Main.HasNecronomicon)
                        {
                            Main.WitchedList[target.PlayerId] = 0;
                            if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                            {
                                target.RpcMurderPlayer(killer);
                                return false;
                            }
                            break;
                        }
                        else
                        {
                            if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                            {
                                target.RpcMurderPlayer(killer);
                                return false;
                            }
                            Main.WitchedList[target.PlayerId] = killer.PlayerId;
                            Main.AllPlayerKillCooldown[killer.PlayerId] = Options.CovenKillCooldown.GetFloat() * 2;
                            killer.CustomSyncSettings();
                            killer.RpcGuardAndKill(target);
                            return false;
                        }
                        break;
                    case CustomRoles.Jackal:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        break;
                    case CustomRoles.TheGlitch:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted && !Main.IsHackMode)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        if (Main.IsHackMode && Main.CursedPlayers[killer.PlayerId] == null)
                        { //Warlockが変身時以外にキルしたら、呪われる処理
                            Utils.CustomSyncAllSettings();
                            Main.CursedPlayers[killer.PlayerId] = target;
                            Main.WarlockTimer.Add(killer.PlayerId, 0f);
                            Main.isCurseAndKill[killer.PlayerId] = true;
                            killer.RpcGuardAndKill(target);
                            new LateTask(() =>
                            {
                                Main.CursedPlayers[killer.PlayerId] = null;
                            }, Options.GlobalRoleBlockDuration.GetFloat(), "Glitch Hacking");
                            return false;
                        }
                        if (!Main.IsHackMode)
                        {
                            if (target.Is(CustomRoles.Pestilence))
                            {
                                target.RpcMurderPlayer(killer);
                                return false;
                            }
                            killer.RpcMurderPlayer(target);
                            //killer.RpcGuardAndKill(target);
                            return false;
                        }
                        if (Main.isCurseAndKill[killer.PlayerId]) killer.RpcGuardAndKill(target);
                        return false;
                    //break;
                    case CustomRoles.Werewolf:
                        if (Main.IsRampaged)
                        {
                            if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                            {
                                target.RpcMurderPlayer(killer);
                                return false;
                            }
                            if (target.Is(CustomRoles.Pestilence))
                            {
                                target.RpcMurderPlayer(killer);
                                return false;
                            }
                            if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                            {
                                target.RpcMurderPlayer(killer);
                                new LateTask(() =>
                                {
                                    Main.unreportableBodies.Add(killer.PlayerId);
                                }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                                return false;
                            }
                            killer.RpcMurderPlayer(target);
                            return false;
                        }
                        else
                        {
                            return false;
                        }
                        break;
                    case CustomRoles.Amnesiac:
                        return false;
                    case CustomRoles.Juggernaut:
                        //calculating next kill cooldown
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        Main.JugKillAmounts++;
                        float DecreasedAmount = Main.JugKillAmounts * Options.JuggerDecrease.GetFloat();
                        Main.AllPlayerKillCooldown[killer.PlayerId] = Options.JuggerKillCooldown.GetFloat() - DecreasedAmount;
                        if (Main.AllPlayerKillCooldown[killer.PlayerId] < 1)
                            Main.AllPlayerKillCooldown[killer.PlayerId] = 1;
                        //after calculating make the kill happen ?
                        killer.CustomSyncSettings();
                        killer.RpcMurderPlayer(target);
                        return false;
                        break;
                    case CustomRoles.BountyHunter: //キルが発生する前にここの処理をしないとバグる
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        BountyHunter.OnCheckMurder(killer, target);
                        break;
                    case CustomRoles.SerialKiller:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        SerialKiller.OnCheckMurder(killer);
                        break;
                    case CustomRoles.Poisoner:
                    case CustomRoles.Vampire:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        if (!target.Is(CustomRoles.Bait))
                        { //キルキャンセル&自爆処理
                            if (!target.Is(CustomRoles.Bewilder))
                            {
                                Utils.CustomSyncAllSettings();
                                Main.AllPlayerKillCooldown[killer.PlayerId] = Options.DefaultKillCooldown * 2;
                                killer.CustomSyncSettings(); //負荷軽減のため、killerだけがCustomSyncSettingsを実行
                                killer.RpcGuardAndKill(target);
                                Main.BitPlayers.Add(target.PlayerId, (killer.PlayerId, 0f));
                                return false;
                            }
                        }
                        else
                        {
                            if (Options.VampireBuff.GetBool()) //Vampire Buff will still make Vampire report but later.
                            {
                                Utils.CustomSyncAllSettings();
                                Main.AllPlayerKillCooldown[killer.PlayerId] = Options.DefaultKillCooldown * 2;
                                killer.CustomSyncSettings(); //負荷軽減のため、killerだけがCustomSyncSettingsを実行
                                killer.RpcGuardAndKill(target);
                                Main.BitPlayers.Add(target.PlayerId, (killer.PlayerId, 0f));
                                return false;
                            }
                        }
                        break;
                    case CustomRoles.Warlock:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        if (!Main.CheckShapeshift[killer.PlayerId] && !Main.isCurseAndKill[killer.PlayerId])
                        { //Warlockが変身時以外にキルしたら、呪われる処理
                            Main.isCursed = true;
                            Utils.CustomSyncAllSettings();
                            killer.RpcGuardAndKill(target);
                            Main.CursedPlayers[killer.PlayerId] = target;
                            Main.WarlockTimer.Add(killer.PlayerId, 0f);
                            Main.isCurseAndKill[killer.PlayerId] = true;
                            return false;
                        }
                        if (Main.CheckShapeshift[killer.PlayerId])
                        {//呪われてる人がいないくて変身してるときに通常キルになる
                            killer.RpcMurderPlayer(target);
                            killer.RpcGuardAndKill(target);
                            return false;
                        }
                        if (Main.isCurseAndKill[killer.PlayerId]) killer.RpcGuardAndKill(target);
                        return false;
                    case CustomRoles.Silencer:
                        //Silenced Player
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        if (Main.SilencedPlayer.Count > 0)
                        {
                            killer.RpcMurderPlayer(target);
                            return false;
                            break;
                        }
                        else if (Main.SilencedPlayer.Count <= 0)
                        {
                            Main.firstKill.Add(killer);
                            killer.RpcGuardAndKill(target);
                            Main.SilencedPlayer.Add(target);
                            RPC.RpcDoSilence(target.PlayerId);
                            break;
                        }
                        if (!Main.firstKill.Contains(killer) && !Main.SilencedPlayer.Contains(target)) return false;
                        break;
                    case CustomRoles.Witch:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        if (killer.IsSpellMode() && !Main.SpelledPlayer.Contains(target))
                        {
                            killer.RpcGuardAndKill(target);
                            Main.SpelledPlayer.Add(target);
                            RPC.RpcDoSpell(target.PlayerId);
                        }
                        Main.KillOrSpell[killer.PlayerId] = !killer.IsSpellMode();
                        Utils.NotifyRoles();
                        killer.SyncKillOrSpell();
                        if (!killer.IsSpellMode()) return false;
                        break;
                    case CustomRoles.HexMaster:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted && Main.HexesThisRound != Options.MaxHexesPerRound.GetFloat())
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        Main.AllPlayerKillCooldown[killer.PlayerId] = 10f;
                        Utils.CustomSyncAllSettings();
                        if (!Main.isHexed[(killer.PlayerId, target.PlayerId)] && killer.IsHexMode() && Main.HexesThisRound != Options.MaxHexesPerRound.GetFloat())
                        {
                            killer.RpcGuardAndKill(target);
                            Main.HexesThisRound++;
                            Utils.NotifyRoles(SpecifySeer: __instance);
                            Main.isHexed[(killer.PlayerId, target.PlayerId)] = true;//塗り完了
                        }
                        if (Main.HexesThisRound != Options.MaxHexesPerRound.GetFloat())
                            Main.KillOrSpell[killer.PlayerId] = !killer.IsHexMode();
                        Utils.NotifyRoles();
                        killer.SyncKillOrHex();
                        if (!killer.IsHexMode()) return false;
                        //return false;
                        if (!Main.HasNecronomicon && Main.HexesThisRound == Options.MaxHexesPerRound.GetFloat()) return false;
                        break;
                    case CustomRoles.Puppeteer:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        Main.PuppeteerList[target.PlayerId] = killer.PlayerId;
                        Main.AllPlayerKillCooldown[killer.PlayerId] = Options.DefaultKillCooldown * 2;
                        killer.CustomSyncSettings(); //負荷軽減のため、killerだけがCustomSyncSettingsを実行
                        killer.RpcGuardAndKill(target);
                        return false;
                    case CustomRoles.TimeThief:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        TimeThief.OnCheckMurder(killer);
                        break;

                    //==========マッドメイト系役職==========//

                    //==========第三陣営役職==========//
                    case CustomRoles.Arsonist:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        Main.AllPlayerKillCooldown[killer.PlayerId] = 10f;
                        Utils.CustomSyncAllSettings();
                        if (!Main.isDoused[(killer.PlayerId, target.PlayerId)] && !Main.ArsonistTimer.ContainsKey(killer.PlayerId))
                        {
                            Main.ArsonistTimer.Add(killer.PlayerId, (target, 0f));
                            Utils.NotifyRoles(SpecifySeer: __instance);
                            RPC.SetCurrentDousingTarget(killer.PlayerId, target.PlayerId);
                        }
                        return false;

                    //==========クルー役職==========//
                    case CustomRoles.PlagueBearer:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        Main.AllPlayerKillCooldown[killer.PlayerId] = 10f;
                        Utils.CustomSyncAllSettings();
                        if (!Main.isInfected[(killer.PlayerId, target.PlayerId)] && !Main.PlagueBearerTimer.ContainsKey(killer.PlayerId))
                        {
                            Main.PlagueBearerTimer.Add(killer.PlayerId, (target, 0f));
                            Utils.NotifyRoles(SpecifySeer: __instance);
                            RPC.SetCurrentInfectingTarget(killer.PlayerId, target.PlayerId);
                            //Main.isInfected[(target.PlayerId, target.PlayerId)] = true;
                            //killer.RpcGuardAndKill(target);
                        }
                        return false;
                    case CustomRoles.Sheriff:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted && Options.CrewRolesVetted.GetBool())
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        Sheriff.OnCheckMurder(killer, target, Process: "RemoveShotLimit");

                        if (!Sheriff.OnCheckMurder(killer, target, Process: "Suicide"))
                            return false;
                        break;
                    default:
                        if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                        {
                            target.RpcMurderPlayer(killer);
                            return false;
                        }
                        if (target.Is(CustomRoles.Medusa) && Main.IsGazing)
                        {
                            target.RpcMurderPlayer(killer);
                            new LateTask(() =>
                            {
                                Main.unreportableBodies.Add(killer.PlayerId);
                            }, Options.StoneReport.GetFloat(), "Medusa Stone Gazing");
                            return false;
                        }
                        break;
                }
            }

            //==キル処理==D
            if (!killer.Is(CustomRoles.Silencer))
            {
                if (!target.Is(CustomRoles.Pestilence))
                    killer.RpcMurderPlayer(target);
                else if (killer.Is(CustomRoles.Arsonist))
                {
                    killer.RpcMurderPlayer(target);
                }
                else if (killer.Is(CustomRoles.Pestilence))
                {
                    //so ARSONIST, CHILD, TERRORIST
                    killer.RpcMurderPlayer(target);
                    //but IDC WHO IT IS PESTI DYING
                }
                else if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
                {
                    if (killer.Is(CustomRoles.Pestilence))
                    {
                        switch (Options.PestiAttacksVet.GetString())
                        {
                            case "Trade":
                                killer.RpcMurderPlayer(killer);
                                target.RpcMurderPlayer(target);
                                break;
                            case "VetKillsPesti":
                                target.RpcMurderPlayer(killer);
                                break;
                            case "PestiKillsVet":
                                killer.RpcMurderPlayer(target);
                                break;
                        }
                    }
                    else
                    {
                        target.RpcMurderPlayer(killer);
                    }
                }
                else
                    target.RpcMurderPlayer(killer);
            }
            //============

            return false;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
    class MurderPlayerPatch
    {
        public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            Logger.Info($"{__instance.GetNameWithRole()} => {target.GetNameWithRole()}{(target.protectedByGuardian ? "(Protected)" : "")}", "MurderPlayer");
        }
        public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            if (!target.Data.IsDead || !AmongUsClient.Instance.AmHost) return;

            PlayerControl killer = __instance; //読み替え変数
            if (PlayerState.GetDeathReason(target.PlayerId) == PlayerState.DeathReason.Sniped)
            {
                killer = Utils.GetPlayerById(Sniper.GetSniper(target.PlayerId));
            }
            if (PlayerState.GetDeathReason(target.PlayerId) == PlayerState.DeathReason.etc)
            {
                //死因が設定されていない場合は死亡判定
                PlayerState.SetDeathReason(target.PlayerId, PlayerState.DeathReason.Kill);
            }

            //When Bait is killed
            if (target.GetCustomRole() == CustomRoles.Bait && killer.PlayerId != target.PlayerId)
            {
                Logger.Info(target?.Data?.PlayerName + "はBaitだった", "MurderPlayer");
                new LateTask(() => killer.CmdReportDeadBody(target.Data), 0.15f, "Bait Self Report");
            }
            else
            //Terrorist
            if (target.Is(CustomRoles.Terrorist))
            {
                Logger.Info(target?.Data?.PlayerName + "はTerroristだった", "MurderPlayer");
                Utils.CheckTerroristWin(target.Data);
            }
            //Child
            else if (target.Is(CustomRoles.Child))
            {
                if (!killer.Is(CustomRoles.Arsonist)) //child doesn't win =(
                {
                    Logger.Info(target?.Data?.PlayerName + "はChildだった", "MurderPlayer");
                    Utils.ChildWin(target.Data);
                }
            }
            else
            //Pestilence
            if (target.Is(CustomRoles.Pestilence))
            {
                target.RpcMurderPlayer(killer);
                //PestiLince cannot die.
            }
            else
            if (target.Is(CustomRoles.Veteran) && Main.VetIsAlerted)
            {
                target.RpcMurderPlayer(killer);
                // return false;
            }
            else if (target.Is(CustomRoles.Bewilder))
            {
                Main.KilledBewilder.Add(killer.PlayerId);
            }

            if (target.Is(CustomRoles.Trapper) && !killer.Is(CustomRoles.Trapper))
                killer.TrapperKilled(target);
            if (target.Is(CustomRoles.Demolitionist) && !killer.Is(CustomRoles.Demolitionist))
                killer.DemoKilled(target);
            if (Main.ExecutionerTarget.ContainsValue(target.PlayerId))
            {
                List<byte> RemoveExecutionerKey = new();
                foreach (var ExecutionerTarget in Main.ExecutionerTarget)
                {
                    var executioner = Utils.GetPlayerById(ExecutionerTarget.Key);
                    if (executioner == null) continue;
                    if (target.PlayerId == ExecutionerTarget.Value && !executioner.Data.IsDead)
                    {
                        executioner.RpcSetCustomRole(Options.CRoleExecutionerChangeRoles[Options.ExecutionerChangeRolesAfterTargetKilled.GetSelection()]); //対象がキルされたらオプションで設定した役職にする
                        RemoveExecutionerKey.Add(ExecutionerTarget.Key);
                    }
                }
                foreach (var RemoveKey in RemoveExecutionerKey)
                {
                    Main.ExecutionerTarget.Remove(RemoveKey);
                    RPC.RemoveExecutionerKey(RemoveKey);
                }
            }
            if (target.Is(CustomRoles.Executioner) && Main.ExecutionerTarget.ContainsKey(target.PlayerId))
            {
                Main.ExecutionerTarget.Remove(target.PlayerId);
                RPC.RemoveExecutionerKey(target.PlayerId);
            }

            if (Main.GuardianAngelTarget.ContainsValue(target.PlayerId))
            {
                List<byte> RemoveGAKey = new();
                foreach (var gaTarget in Main.GuardianAngelTarget)
                {
                    var ga = Utils.GetPlayerById(gaTarget.Key);
                    if (ga == null) continue;
                    if (target.PlayerId == gaTarget.Value && !ga.Data.IsDead)
                    {
                        ga.RpcSetCustomRole(Options.CRoleGuardianAngelChangeRoles[Options.WhenGaTargetDies.GetSelection()]); //対象がキルされたらオプションで設定した役職にする
                        RemoveGAKey.Add(gaTarget.Key);
                    }
                }
                foreach (var RemoveKey in RemoveGAKey)
                {
                    Main.GuardianAngelTarget.Remove(RemoveKey);
                    RPC.RemoveGAKey(RemoveKey);
                }
            }
            if (target.Is(CustomRoles.GuardianAngelTOU) && Main.GuardianAngelTarget.ContainsKey(target.PlayerId))
            {
                Main.GuardianAngelTarget.Remove(target.PlayerId);
                RPC.RemoveGAKey(target.PlayerId);
            }
            if (target.Is(CustomRoles.TimeThief))
                target.ResetVotingTime();


            foreach (var pc in PlayerControl.AllPlayerControls)
            {
                if (pc.IsLastImpostor())
                    Main.AllPlayerKillCooldown[pc.PlayerId] = Options.LastImpostorKillCooldown.GetFloat();
            }
            FixedUpdatePatch.LoversSuicide(target.PlayerId);

            PlayerState.SetDead(target.PlayerId);
            Utils.CountAliveImpostors();
            Utils.CustomSyncAllSettings();
            Utils.NotifyRoles();
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Shapeshift))]
    class ShapeshiftPatch
    {
        public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            Logger.Info($"{__instance?.GetNameWithRole()} => {target?.GetNameWithRole()}", "Shapeshift");
            if (!AmongUsClient.Instance.AmHost) return;

            var shapeshifter = __instance;
            var shapeshifting = shapeshifter.PlayerId != target.PlayerId;

            Main.CheckShapeshift[shapeshifter.PlayerId] = shapeshifting;
            if (shapeshifter.Is(CustomRoles.Warlock))
            {
                if (Main.CursedPlayers[shapeshifter.PlayerId] != null)//呪われた人がいるか確認
                {
                    if (shapeshifting && !Main.CursedPlayers[shapeshifter.PlayerId].Data.IsDead)//変身解除の時に反応しない
                    {
                        var cp = Main.CursedPlayers[shapeshifter.PlayerId];
                        Vector2 cppos = cp.transform.position;//呪われた人の位置
                        Dictionary<PlayerControl, float> cpdistance = new();
                        float dis;
                        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                        {
                            if (!p.Data.IsDead && p != cp)
                            {
                                dis = Vector2.Distance(cppos, p.transform.position);
                                cpdistance.Add(p, dis);
                                Logger.Info($"{p?.Data?.PlayerName}の位置{dis}", "Warlock");
                            }
                        }
                        var min = cpdistance.OrderBy(c => c.Value).FirstOrDefault();//一番小さい値を取り出す
                        PlayerControl targetw = min.Key;
                        Logger.Info($"{targetw.GetNameWithRole()}was killed", "Warlock");
                        if (target.Is(CustomRoles.Pestilence))
                            targetw.RpcMurderPlayerV2(cp);
                        else
                            cp.RpcMurderPlayerV2(targetw);//殺す
                        shapeshifter.RpcGuardAndKill(shapeshifter);
                        Main.isCurseAndKill[shapeshifter.PlayerId] = false;
                    }
                    Main.CursedPlayers[shapeshifter.PlayerId] = null;
                }
            }

            if (shapeshifter.CanMakeMadmate() && shapeshifting)
            {//変身したとき一番近い人をマッドメイトにする処理
                Vector2 shapeshifterPosition = shapeshifter.transform.position;//変身者の位置
                Dictionary<PlayerControl, float> mpdistance = new();
                float dis;
                foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                {
                    if (!p.Data.IsDead && p.Data.Role.Role != RoleTypes.Shapeshifter && !p.Is(RoleType.Impostor) && !p.Is(CustomRoles.SKMadmate))
                    {
                        dis = Vector2.Distance(shapeshifterPosition, p.transform.position);
                        mpdistance.Add(p, dis);
                    }
                }
                if (mpdistance.Count() != 0)
                {
                    var min = mpdistance.OrderBy(c => c.Value).FirstOrDefault();//一番値が小さい
                    PlayerControl targetm = min.Key;
                    targetm.RpcSetCustomRole(CustomRoles.SKMadmate);
                    Logger.Info($"Make SKMadmate:{targetm.name}", "Shapeshift");
                    Main.SKMadmateNowCount++;
                    Utils.CustomSyncAllSettings();
                    Utils.NotifyRoles();
                }
            }
            if (shapeshifter.Is(CustomRoles.FireWorks)) FireWorks.ShapeShiftState(shapeshifter, shapeshifting);
            if (shapeshifter.Is(CustomRoles.Sniper)) Sniper.ShapeShiftCheck(shapeshifter, shapeshifting);

            //変身解除のタイミングがずれて名前が直せなかった時のために強制書き換え
            if (!shapeshifting)
            {
                new LateTask(() =>
                {
                    Utils.NotifyRoles(NoCache: true);
                },
                1.2f, "ShapeShiftNotify");
            }
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ReportDeadBody))]
    class ReportDeadBodyPatch
    {
        public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] GameData.PlayerInfo target)
        {
            if (GameStates.IsMeeting) return false;
            //if (Main.KilledDemo.Contains(__instance.PlayerId)) return false;
            if (target != null) //ボタン
            {
                if (__instance.Is(CustomRoles.Oblivious))
                {
                    return false;
                }
            }
            Logger.Info($"{__instance.GetNameWithRole()} => {target?.GetNameWithRole() ?? "null"}", "ReportDeadBody");
            if (target != null)
            {
                if (Main.unreportableBodies.Contains(target.PlayerId)) return false;
            }
            if (target != null)
            {
                if (__instance.Is(CustomRoles.Vulture) && !__instance.Data.IsDead && !Main.unreportableBodies.Contains(target.PlayerId))
                {
                    Main.unreportableBodies.Add(target.PlayerId);
                    Main.AteBodies++;
                    if (Main.AteBodies == Options.BodiesAmount.GetFloat())
                    {
                        //Vulture wins.
                        //CheckGameEndPatch.CheckAndEndGameForVultureWin();
                        //RPC.VultureWin();
                        //CheckForEndVotingPatch.Prefix();
                        return true;
                    }
                    return false;
                }
            }
            if (CustomRoles.TheGlitch.IsEnable())
            {
                List<PlayerControl> hackedPlayers = new();
                PlayerControl glitch;
                foreach (var cp in Main.CursedPlayers)
                {
                    if (Utils.GetPlayerById(cp.Key).Is(CustomRoles.TheGlitch))
                    {
                        hackedPlayers.Add(cp.Value);
                        glitch = Utils.GetPlayerById(cp.Key);
                    }
                }
                if (hackedPlayers.Contains(__instance))
                {
                    return false;
                }
            }
            if (Options.IsStandardHAS && target != null && __instance == target.Object) return true; //[StandardHAS] ボタンでなく、通報者と死体が同じなら許可
            if (Options.CurrentGameMode == CustomGameMode.HideAndSeek || Options.IsStandardHAS) return false;
            if (!AmongUsClient.Instance.AmHost) return true;
            BountyHunter.OnReportDeadBody();
            SerialKiller.OnReportDeadBody();
            Main.bombedVents.Clear();
            // Main.KilledDemo.Clear();
            Main.ArsonistTimer.Clear();
            Main.PlagueBearerTimer.Clear();
            Main.IsRoundOne = false;
            Main.IsRoundOneGA = false;
            Main.IsGazing = false;
            Main.GazeReady = false;
            if (target == null) //ボタン
            {
                if (__instance.Is(CustomRoles.Mayor))
                {
                    Main.MayorUsedButtonCount[__instance.PlayerId] += 1;
                }
            }

            if (target != null) //Sleuth Report for Non-Buttons
            {
                if (__instance.Is(CustomRoles.Sleuth))
                {
                    //Main.MayorUsedButtonCount[__instance.PlayerId] += 1;
                    Utils.SendMessage("The body you reported had a clue about their role. They were " + Utils.GetRoleName(target.GetCustomRole()) + ".", __instance.PlayerId);
                }
            }

            if (target != null) //Sleuth Report for Non-Buttons
            {
                if (__instance.Is(CustomRoles.Amnesiac))
                {
                    Utils.SendMessage("You stole that person's role! They were " + Utils.GetRoleName(target.GetCustomRole()) + ".", __instance.PlayerId);
                    Utils.SendMessage("The Amnesiac stole your role! Because of this, your role has been reset to the default one.", target.PlayerId);
                    __instance.RpcSetCustomRole(target.GetCustomRole());
                    __instance.RpcSetRole(target.Role.Role);
                    switch (target.GetCustomRole())
                    {
                        case CustomRoles.Arsonist:
                            foreach (var ar in PlayerControl.AllPlayerControls)
                                Main.isDoused.Add((__instance.PlayerId, ar.PlayerId), false);
                            break;
                        case CustomRoles.GuardianAngelTOU:
                            var rand = new System.Random();
                            List<PlayerControl> protectList = new();
                            rand = new System.Random();
                            foreach (var player in PlayerControl.AllPlayerControls)
                            {
                                if (__instance == player) continue;

                                protectList.Add(player);
                            }
                            var Person = protectList[rand.Next(protectList.Count)];
                            Main.GuardianAngelTarget.Add(__instance.PlayerId, Person.PlayerId);
                            RPC.SendGATarget(__instance.PlayerId, Person.PlayerId);
                            Logger.Info($"{__instance.GetNameWithRole()}:{Person.GetNameWithRole()}", "Guardian Angel");
                            break;
                        case CustomRoles.PlagueBearer:
                            foreach (var ar in PlayerControl.AllPlayerControls)
                                Main.isInfected.Add((__instance.PlayerId, ar.PlayerId), false);
                            break;
                        case CustomRoles.Executioner:
                            List<PlayerControl> targetList = new();
                            var randd = new System.Random();
                            randd = new System.Random();
                            foreach (var player in PlayerControl.AllPlayerControls)
                            {
                                if (__instance == player) continue;
                                else if (!Options.ExecutionerCanTargetImpostor.GetBool() && player.GetCustomRole().IsImpostor()) continue;

                                targetList.Add(player);
                            }
                            var Target = targetList[randd.Next(targetList.Count)];
                            Main.ExecutionerTarget.Add(__instance.PlayerId, Target.PlayerId);
                            RPC.SendExecutionerTarget(__instance.PlayerId, Target.PlayerId);
                            Logger.Info($"{__instance.GetNameWithRole()}:{Target.GetNameWithRole()}", "Executioner");
                            break;
                    }
                    Utils.GetPlayerById(target.PlayerId).SetDefaultRole();
                }
            }
            if (!Main.HasNecronomicon)
                Main.CovenMeetings++;
            if (Main.CovenMeetings == Options.CovenMeetings.GetFloat() && !Main.HasNecronomicon && CustomRoles.Coven.IsEnable())
            {
                Main.HasNecronomicon = true;
                foreach (var pc in PlayerControl.AllPlayerControls)
                {
                    //time for coven
                    if (CustomRolesHelper.GetRoleType(pc.GetCustomRole()) == RoleType.Coven)
                    {
                        //if they are coven.
                        Utils.SendMessage("You now weild the Necronomicon. With this power, you gain venting, guessing, and a whole lot of other powers depending on your role.", pc.PlayerId);
                        switch (pc.GetCustomRole())
                        {

                            case CustomRoles.Poisoner:
                                Utils.SendMessage("Also With this power, you gain nothing.", pc.PlayerId);
                                break;
                            case CustomRoles.CovenWitch:
                                Utils.SendMessage("Also With this power, you can kill normally.", pc.PlayerId);
                                break;
                            case CustomRoles.Coven:
                                Utils.SendMessage("Also With this power, you gain nothing.", pc.PlayerId);
                                break;
                            case CustomRoles.HexMaster:
                                Utils.SendMessage("Also With this power, you can kill normally.", pc.PlayerId);
                                break;
                            case CustomRoles.PotionMaster:
                                Utils.SendMessage("Also With this power, you have shorter cooldowns. And the ability to kill when shifted.", pc.PlayerId);
                                break;
                            case CustomRoles.Medusa:
                                Utils.SendMessage("Also With this power, you can kill normally. However, you still cannot vent normally.", pc.PlayerId);
                                break;
                            case CustomRoles.Mimic:
                                Utils.SendMessage("Your role prevents you from having this power however.", pc.PlayerId);
                                break;
                            case CustomRoles.Necromancer:
                                Utils.SendMessage("Also With this power, the Veteran cannot kill you.", pc.PlayerId);
                                break;
                            case CustomRoles.Conjuror:
                                Utils.SendMessage("Also With this power, you can kill normally.", pc.PlayerId);
                                break;
                        }
                    }
                    else
                    {
                        Utils.SendMessage("The Coven now weild Necronomicon. With this power, they gain venting, guessing, and more depending on their role.", pc.PlayerId);
                    }
                }
            }

            if (Options.SyncButtonMode.GetBool() && target == null)
            {
                Logger.Info("最大:" + Options.SyncedButtonCount.GetInt() + ", 現在:" + Options.UsedButtonCount, "ReportDeadBody");
                if (Options.SyncedButtonCount.GetFloat() <= Options.UsedButtonCount)
                {
                    Logger.Info("使用可能ボタン回数が最大数を超えているため、ボタンはキャンセルされました。", "ReportDeadBody");
                    return false;
                }
                else Options.UsedButtonCount++;
                if (Options.SyncedButtonCount.GetFloat() == Options.UsedButtonCount)
                {
                    Logger.Info("使用可能ボタン回数が最大数に達しました。", "ReportDeadBody");
                }
            }

            foreach (var bp in Main.BitPlayers)
            {
                var vampireID = bp.Value.Item1;
                var bitten = Utils.GetPlayerById(bp.Key);

                if (!bitten.Is(CustomRoles.Veteran))
                {
                    if (!bitten.Data.IsDead)
                    {
                        PlayerControl vampire = Utils.GetPlayerById(vampireID);
                        if (bitten.Is(CustomRoles.Pestilence))
                            PlayerState.SetDeathReason(vampire.PlayerId, PlayerState.DeathReason.Bite);
                        else
                            PlayerState.SetDeathReason(bitten.PlayerId, PlayerState.DeathReason.Bite);
                        //Protectは強制的にはがす
                        // PlayerControl vampire = Utils.GetPlayerById(vampireID);
                        if (bitten.protectedByGuardian)
                            bitten.RpcMurderPlayer(bitten);
                        if (bitten.Is(CustomRoles.Pestilence))
                            vampire.RpcMurderPlayer(vampire);
                        else
                            bitten.RpcMurderPlayer(bitten);
                        RPC.PlaySoundRPC(vampireID, Sounds.KillSound);
                        Logger.Info("Vampireに噛まれている" + bitten?.Data?.PlayerName + "を自爆させました。", "ReportDeadBody");
                    }
                    else
                        Logger.Info("Vampireに噛まれている" + bitten?.Data?.PlayerName + "はすでに死んでいました。", "ReportDeadBody");
                }
                else
                {
                    if (Main.VetIsAlerted)
                    {
                        PlayerState.SetDeathReason(vampireID, PlayerState.DeathReason.Kill);
                        //Protectは強制的にはがす
                        PlayerControl vampire = Utils.GetPlayerById(vampireID);
                        if (vampire.protectedByGuardian)
                            vampire.RpcMurderPlayer(vampire);
                        vampire.RpcMurderPlayer(vampire);
                        RPC.PlaySoundRPC(bitten.PlayerId, Sounds.KillSound);
                    }
                    else
                    {
                        if (!bitten.Data.IsDead)
                        {
                            PlayerState.SetDeathReason(bitten.PlayerId, PlayerState.DeathReason.Bite);
                            //Protectは強制的にはがす
                            if (bitten.protectedByGuardian)
                                bitten.RpcMurderPlayer(bitten);
                            bitten.RpcMurderPlayer(bitten);
                            RPC.PlaySoundRPC(vampireID, Sounds.KillSound);
                            Logger.Info("Vampireに噛まれている" + bitten?.Data?.PlayerName + "を自爆させました。", "ReportDeadBody");
                        }
                        else
                            Logger.Info("Vampireに噛まれている" + bitten?.Data?.PlayerName + "はすでに死んでいました。", "ReportDeadBody");
                    }
                }
            }
            foreach (var killer in Main.KilledDemo)
            {
                var realKiller = Utils.GetPlayerById(killer);
                if (!realKiller.Is(CustomRoles.Pestilence))
                {
                    if (!realKiller.inVent)
                    {
                        realKiller.CustomSyncSettings();
                        if (realKiller.protectedByGuardian)
                            realKiller.RpcMurderPlayer(realKiller);
                        realKiller.RpcMurderPlayer(realKiller);
                        PlayerState.SetDeathReason(killer, PlayerState.DeathReason.Suicide);
                        PlayerState.SetDead(killer);
                    }
                }
            }
            Main.BitPlayers = new Dictionary<byte, (byte, float)>();
            Main.KilledDemo.Clear();
            Main.PuppeteerList.Clear();
            Main.WitchedList.Clear();
            Sniper.OnStartMeeting();
            Main.VetIsAlerted = false;
            Main.IsRampaged = false;
            Main.RampageReady = false;

            if (__instance.Data.IsDead) return true;
            //=============================================
            //以下、ボタンが押されることが確定したものとする。
            //=============================================

            Utils.CustomSyncAllSettings();
            return true;
        }
        public static async void ChangeLocalNameAndRevert(string name, int time)
        {
            //async Taskじゃ警告出るから仕方ないよね。
            var revertName = PlayerControl.LocalPlayer.name;
            PlayerControl.LocalPlayer.RpcSetNameEx(name);
            await Task.Delay(time);
            PlayerControl.LocalPlayer.RpcSetNameEx(revertName);
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
    class FixedUpdatePatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            var player = __instance;

            if (AmongUsClient.Instance.AmHost)
            {//実行クライアントがホストの場合のみ実行
                if (GameStates.IsLobby && (ModUpdater.hasUpdate || ModUpdater.isBroken) && AmongUsClient.Instance.IsGamePublic)
                    AmongUsClient.Instance.ChangeGamePublic(false);

                if (GameStates.IsInTask && CustomRoles.Vampire.IsEnable())
                {
                    //Vampireの処理
                    if (Main.BitPlayers.ContainsKey(player.PlayerId))
                    {
                        //__instance:キルされる予定のプレイヤー
                        //main.BitPlayers[__instance.PlayerId].Item1:キルしたプレイヤーのID
                        //main.BitPlayers[__instance.PlayerId].Item2:キルするまでの秒数
                        byte vampireID = Main.BitPlayers[player.PlayerId].Item1;
                        float killTimer = Main.BitPlayers[player.PlayerId].Item2;
                        if (killTimer >= Options.VampireKillDelay.GetFloat())
                        {
                            var bitten = player;
                            if (!bitten.Data.IsDead)
                            {
                                PlayerState.SetDeathReason(bitten.PlayerId, PlayerState.DeathReason.Bite);
                                var vampirePC = Utils.GetPlayerById(vampireID);
                                if (!bitten.Is(CustomRoles.Bait))
                                {
                                    if (vampirePC.IsAlive())
                                    {
                                        if (bitten.Is(CustomRoles.Pestilence))
                                            vampirePC.RpcMurderPlayer(vampirePC);
                                        else
                                            bitten.RpcMurderPlayer(bitten);
                                    }
                                    //bitten.RpcMurderPlayer(bitten);
                                    else
                                    {
                                        if (Options.VampireBuff.GetBool())
                                            bitten.RpcMurderPlayer(bitten);
                                    }
                                }
                                else
                                {
                                    vampirePC.RpcMurderPlayer(bitten);
                                }

                                Logger.Info("Vampireに噛まれている" + bitten?.Data?.PlayerName + "を自爆させました。", "Vampire");
                                if (vampirePC.IsAlive())
                                {
                                    RPC.PlaySoundRPC(vampireID, Sounds.KillSound);
                                    if (bitten.Is(CustomRoles.Trapper))
                                        vampirePC.TrapperKilled(bitten);
                                }
                            }
                            else
                            {
                                Logger.Info("Vampireに噛まれている" + bitten?.Data?.PlayerName + "はすでに死んでいました。", "Vampire");
                            }
                            Main.BitPlayers.Remove(bitten.PlayerId);
                        }
                        else
                        {
                            Main.BitPlayers[player.PlayerId] =
                            (vampireID, killTimer + Time.fixedDeltaTime);
                        }
                    }
                }
                SerialKiller.FixedUpdate(player);
                if (GameStates.IsInTask && Main.WarlockTimer.ContainsKey(player.PlayerId))//処理を1秒遅らせる
                {
                    if (player.IsAlive())
                    {
                        if (Main.WarlockTimer[player.PlayerId] >= 1f)
                        {
                            player.RpcResetAbilityCooldown();
                            Main.isCursed = false;//変身クールを１秒に変更
                            Utils.CustomSyncAllSettings();
                            Main.WarlockTimer.Remove(player.PlayerId);
                        }
                        else Main.WarlockTimer[player.PlayerId] = Main.WarlockTimer[player.PlayerId] + Time.fixedDeltaTime;//時間をカウント
                    }
                    else
                    {
                        Main.WarlockTimer.Remove(player.PlayerId);
                    }
                }
                //ターゲットのリセット
                BountyHunter.FixedUpdate(player);
                if (GameStates.IsInTask && player.IsAlive() && Options.LadderDeath.GetBool())
                {
                    FallFromLadder.FixedUpdate(player);
                }
                /*if (GameStates.isInGame && main.AirshipMeetingTimer.ContainsKey(__instance.PlayerId)) //会議後すぐにここの処理をするため不要になったコードです。今後#465で変更した仕様がバグって、ここの処理が必要になった時のために残してコメントアウトしています
                {
                    if (main.AirshipMeetingTimer[__instance.PlayerId] >= 9f && !main.AirshipMeetingCheck)
                    {
                        main.AirshipMeetingCheck = true;
                        Utils.CustomSyncAllSettings();
                    }
                    if (main.AirshipMeetingTimer[__instance.PlayerId] >= 10f)
                    {
                        Utils.AfterMeetingTasks();
                        main.AirshipMeetingTimer.Remove(__instance.PlayerId);
                    }
                    else
                        main.AirshipMeetingTimer[__instance.PlayerId] = (main.AirshipMeetingTimer[__instance.PlayerId] + Time.fixedDeltaTime);
                    }
                }*/

                if (GameStates.IsInGame) LoversSuicide();

                if (GameStates.IsInTask && Main.ArsonistTimer.ContainsKey(player.PlayerId))//アーソニストが誰かを塗っているとき
                {
                    if (!player.IsAlive())
                    {
                        Main.ArsonistTimer.Remove(player.PlayerId);
                        Utils.NotifyRoles(SpecifySeer: __instance);
                        RPC.ResetCurrentDousingTarget(player.PlayerId);
                    }
                    else
                    {
                        var ar_target = Main.ArsonistTimer[player.PlayerId].Item1;//塗られる人
                        var ar_time = Main.ArsonistTimer[player.PlayerId].Item2;//塗った時間
                        if (!ar_target.IsAlive())
                        {
                            Main.ArsonistTimer.Remove(player.PlayerId);
                        }
                        else if (ar_time >= Options.ArsonistDouseTime.GetFloat())//時間以上一緒にいて塗れた時
                        {
                            Main.AllPlayerKillCooldown[player.PlayerId] = Options.ArsonistCooldown.GetFloat() * 2;
                            Utils.CustomSyncAllSettings();//同期
                            player.RpcGuardAndKill(ar_target);//通知とクールリセット
                            Main.ArsonistTimer.Remove(player.PlayerId);//塗が完了したのでDictionaryから削除
                            Main.isDoused[(player.PlayerId, ar_target.PlayerId)] = true;//塗り完了
                            player.RpcSetDousedPlayer(ar_target, true);
                            Utils.NotifyRoles();//名前変更
                            RPC.ResetCurrentDousingTarget(player.PlayerId);
                        }
                        else
                        {
                            float dis;
                            dis = Vector2.Distance(player.transform.position, ar_target.transform.position);//距離を出す
                            if (dis <= 1.75f)//一定の距離にターゲットがいるならば時間をカウント
                            {
                                Main.ArsonistTimer[player.PlayerId] = (ar_target, ar_time + Time.fixedDeltaTime);
                            }
                            else//それ以外は削除
                            {
                                Main.ArsonistTimer.Remove(player.PlayerId);
                                Utils.NotifyRoles(SpecifySeer: __instance);
                                RPC.ResetCurrentDousingTarget(player.PlayerId);

                                Logger.Info($"Canceled: {__instance.GetNameWithRole()}", "Arsonist");
                            }
                        }

                    }
                }
                if (GameStates.IsInTask && Main.PlagueBearerTimer.ContainsKey(player.PlayerId))//アーソニストが誰かを塗っているとき
                {
                    if (!player.IsAlive())
                    {
                        Main.PlagueBearerTimer.Remove(player.PlayerId);
                        Utils.NotifyRoles(SpecifySeer: __instance);
                        RPC.ResetCurrentInfectingTarget(player.PlayerId);
                    }
                    else
                    {
                        var ar_target = Main.PlagueBearerTimer[player.PlayerId].Item1;//塗られる人
                        var ar_time = Main.PlagueBearerTimer[player.PlayerId].Item2;//塗った時間
                        if (!ar_target.IsAlive())
                        {
                            Main.PlagueBearerTimer.Remove(player.PlayerId);
                        }
                        else if (ar_time >= 0)//時間以上一緒にいて塗れた時
                        {
                            Main.AllPlayerKillCooldown[player.PlayerId] = Options.InfectCooldown.GetFloat() * 2;
                            Utils.CustomSyncAllSettings();
                            player.RpcGuardAndKill(ar_target);
                            Main.PlagueBearerTimer.Remove(player.PlayerId);
                            Main.isInfected[(player.PlayerId, ar_target.PlayerId)] = true;
                            player.RpcSetDousedPlayer(ar_target, true);
                            Utils.NotifyRoles();//名前変更
                            RPC.ResetCurrentInfectingTarget(player.PlayerId);
                        }

                    }
                }

                if (GameStates.IsInTask && Main.PuppeteerList.ContainsKey(player.PlayerId))
                {
                    if (!player.IsAlive())
                    {
                        Main.PuppeteerList.Remove(player.PlayerId);
                    }
                    else
                    {
                        Vector2 puppeteerPos = player.transform.position;//PuppeteerListのKeyの位置
                        Dictionary<byte, float> targetDistance = new();
                        float dis;
                        foreach (var target in PlayerControl.AllPlayerControls)
                        {
                            if (!target.IsAlive()) continue;
                            if (target.PlayerId != player.PlayerId && !target.GetCustomRole().IsImpostor())
                            {
                                dis = Vector2.Distance(puppeteerPos, target.transform.position);
                                targetDistance.Add(target.PlayerId, dis);
                            }
                        }
                        if (targetDistance.Count() != 0)
                        {
                            var min = targetDistance.OrderBy(c => c.Value).FirstOrDefault();//一番値が小さい
                            PlayerControl target = Utils.GetPlayerById(min.Key);
                            var KillRange = GameOptionsData.KillDistances[Mathf.Clamp(PlayerControl.GameOptions.KillDistance, 0, 2)];
                            if (min.Value <= KillRange && player.CanMove && target.CanMove)
                            {
                                RPC.PlaySoundRPC(Main.PuppeteerList[player.PlayerId], Sounds.KillSound);
                                if (target.Is(CustomRoles.Pestilence))
                                    target.RpcMurderPlayer(player);
                                else
                                    player.RpcMurderPlayer(target);
                                Utils.CustomSyncAllSettings();
                                Main.PuppeteerList.Remove(player.PlayerId);
                                Utils.NotifyRoles();
                            }
                        }
                    }
                }
                if (GameStates.IsInTask && Main.WitchedList.ContainsKey(player.PlayerId))
                {
                    if (!player.IsAlive())
                    {
                        Main.WitchedList.Remove(player.PlayerId);
                    }
                    else
                    {
                        Vector2 puppeteerPos = player.transform.position;//WitchedListのKeyの位置
                        Dictionary<byte, float> targetDistance = new();
                        float dis;
                        foreach (var target in PlayerControl.AllPlayerControls)
                        {
                            if (!target.IsAlive()) continue;
                            if (target.PlayerId != player.PlayerId && !target.GetCustomRole().IsCoven())
                            {
                                dis = Vector2.Distance(puppeteerPos, target.transform.position);
                                targetDistance.Add(target.PlayerId, dis);
                            }
                        }
                        if (targetDistance.Count() != 0)
                        {
                            var min = targetDistance.OrderBy(c => c.Value).FirstOrDefault();//一番値が小さい
                            PlayerControl target = Utils.GetPlayerById(min.Key);
                            var KillRange = GameOptionsData.KillDistances[Mathf.Clamp(PlayerControl.GameOptions.KillDistance, 0, 2)];
                            if (min.Value <= KillRange && player.CanMove && target.CanMove)
                            {
                                RPC.PlaySoundRPC(Main.WitchedList[player.PlayerId], Sounds.KillSound);
                                if (target.Is(CustomRoles.Pestilence))
                                    target.RpcMurderPlayer(player);
                                else
                                    player.RpcMurderPlayer(target);
                                Utils.CustomSyncAllSettings();
                                Main.WitchedList.Remove(player.PlayerId);
                                Utils.NotifyRoles();
                            }
                        }
                    }
                }
                if (GameStates.IsInTask && player == PlayerControl.LocalPlayer)
                    DisableDevice.FixedUpdate();

                if (GameStates.IsInGame && Main.RefixCooldownDelay <= 0)
                    foreach (var pc in PlayerControl.AllPlayerControls)
                    {
                        if (pc.Is(CustomRoles.Vampire) || pc.Is(CustomRoles.Warlock))
                            Main.AllPlayerKillCooldown[pc.PlayerId] = Options.DefaultKillCooldown * 2;
                    }

                if (__instance.AmOwner) Utils.ApplySuffix();
            }

            //LocalPlayer専用
            if (__instance.AmOwner)
            {
                //キルターゲットの上書き処理
                if (GameStates.IsInTask && (__instance.Is(CustomRoles.Sheriff) || __instance.GetRoleType() == RoleType.Coven || __instance.Is(CustomRoles.Arsonist) || __instance.Is(CustomRoles.Werewolf) || __instance.Is(CustomRoles.TheGlitch) || __instance.Is(CustomRoles.Juggernaut) || __instance.Is(CustomRoles.PlagueBearer) || __instance.Is(CustomRoles.Pestilence) || __instance.Is(CustomRoles.Jackal)) && !__instance.Data.IsDead)
                {
                    var players = __instance.GetPlayersInAbilityRangeSorted(false);
                    PlayerControl closest = players.Count <= 0 ? null : players[0];
                    HudManager.Instance.KillButton.SetTarget(closest);
                }
            }

            //役職テキストの表示
            var RoleTextTransform = __instance.cosmetics.nameText.transform.Find("RoleText");
            var RoleText = RoleTextTransform.GetComponent<TMPro.TextMeshPro>();
            if (RoleText != null && __instance != null)
            {
                if (GameStates.IsLobby)
                {
                    if (Main.playerVersion.TryGetValue(__instance.PlayerId, out var ver))
                        if (Main.version.CompareTo(ver.version) == 0)
                            __instance.cosmetics.nameText.text = ver.tag == $"{ThisAssembly.Git.Commit}({ThisAssembly.Git.Branch})" ? $"<color=#87cefa>{__instance.name}</color>" : $"<color=#ffff00><size=1.2>{ver.tag}</size>\n{__instance?.name}</color>";
                        else __instance.cosmetics.nameText.text = $"<color=#ff0000><size=1.2>v{ver.version}</size>\n{__instance?.name}</color>";
                    else __instance.cosmetics.nameText.text = __instance?.Data?.PlayerName;
                }
                if (GameStates.IsInGame)
                {
                    var RoleTextData = Utils.GetRoleText(__instance);
                    //if (Options.CurrentGameMode == CustomGameMode.HideAndSeek)
                    //{
                    //    var hasRole = main.AllPlayerCustomRoles.TryGetValue(__instance.PlayerId, out var role);
                    //    if (hasRole) RoleTextData = Utils.GetRoleTextHideAndSeek(__instance.Data.Role.Role, role);
                    //}
                    RoleText.text = RoleTextData.Item1;
                    RoleText.color = RoleTextData.Item2;
                    if (__instance.AmOwner) RoleText.enabled = true; //自分ならロールを表示
                    else if (Main.VisibleTasksCount && PlayerControl.LocalPlayer.Data.IsDead && Options.GhostCanSeeOtherRoles.GetBool()) RoleText.enabled = true; //他プレイヤーでVisibleTasksCountが有効なおかつ自分が死んでいるならロールを表示
                    else RoleText.enabled = false; //そうでなければロールを非表示
                    if (!AmongUsClient.Instance.IsGameStarted && AmongUsClient.Instance.GameMode != GameModes.FreePlay)
                    {
                        RoleText.enabled = false; //ゲームが始まっておらずフリープレイでなければロールを非表示
                        if (!__instance.AmOwner) __instance.cosmetics.nameText.text = __instance?.Data?.PlayerName;
                    }
                    if (Main.VisibleTasksCount) //他プレイヤーでVisibleTasksCountは有効なら
                        RoleText.text += $" {Utils.GetProgressText(__instance)}"; //ロールの横にタスクなど進行状況表示


                    //変数定義
                    var seer = PlayerControl.LocalPlayer;
                    var target = __instance;

                    string RealName;
                    string Mark = "";
                    string Suffix = "";

                    //名前変更
                    RealName = target.GetRealName();

                    //名前色変更処理
                    //自分自身の名前の色を変更
                    if (target.AmOwner && AmongUsClient.Instance.IsGameStarted)
                    { //targetが自分自身
                        RealName = Helpers.ColorString(target.GetRoleColor(), RealName); //名前の色を変更
                                                                                         //   if (target.Is(CustomRoles.Child) && Options.ChildKnown.GetBool())
                                                                                         //            RealName += Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Jackal), " (C)");
                        if (target.Is(CustomRoles.Arsonist) && target.IsDouseDone())
                            RealName = Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Arsonist), GetString("EnterVentToWin"));
                        if (Main.KilledDemo.Contains(seer.PlayerId))
                            RealName = $"</size>\r\n{Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Demolitionist), "You killed Demolitionist!")}";
                    }
                    if (target.Is(CustomRoles.PlagueBearer) && target.IsInfectDone())
                        target.RpcSetCustomRole(CustomRoles.Pestilence);
                    //タスクを終わらせたMadSnitchがインポスターを確認できる
                    else if (seer.Is(CustomRoles.MadSnitch) && //seerがMadSnitch
                        target.GetCustomRole().IsImpostor() && //targetがインポスター
                        seer.GetPlayerTaskState().IsTaskFinished) //seerのタスクが終わっている
                    {
                        RealName = Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Impostor), RealName); //targetの名前を赤色で表示
                    }
                    else if (seer.GetCustomRole().IsCoven() && //seerがMadSnitch
                        target.GetCustomRole().IsCoven()) //seerのタスクが終わっている
                    {
                        RealName = Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Coven), RealName); //targetの名前を赤色で表示
                    }
                    //タスクを終わらせたSnitchがインポスターを確認できる
                    else if (PlayerControl.LocalPlayer.Is(CustomRoles.Snitch) && //LocalPlayerがSnitch
                        PlayerControl.LocalPlayer.GetPlayerTaskState().IsTaskFinished) //LocalPlayerのタスクが終わっている
                    {
                        var targetCheck = target.GetCustomRole().IsImpostor() || (Options.SnitchCanFindNeutralKiller.GetBool() && target.IsNeutralKiller());
                        if (targetCheck)//__instanceがターゲット
                        {
                            RealName = Helpers.ColorString(target.GetRoleColor(), RealName); //targetの名前を役職色で表示
                        }
                    }
                    else if (seer.GetCustomRole().IsImpostor() && //seerがインポスター
                        target.Is(CustomRoles.Egoist) //targetがエゴイスト
                    )
                        RealName = Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Egoist), RealName); //targetの名前をエゴイスト色で表示

                    else if ((seer.Is(CustomRoles.EgoSchrodingerCat) && target.Is(CustomRoles.Egoist)) || //エゴ猫 --> エゴイスト
                             (seer.Is(CustomRoles.JSchrodingerCat) && target.Is(CustomRoles.Jackal)) //J猫 --> ジャッカル
                    )
                        RealName = Helpers.ColorString(target.GetRoleColor(), RealName); //targetの名前をtargetの役職の色で表示
                    else if (target.Is(CustomRoles.Mare) && Utils.IsActive(SystemTypes.Electrical))
                        RealName = Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Impostor), RealName); //targetの赤色で表示
                    else if (seer != null)
                    {//NameColorManager準拠の処理
                        var ncd = NameColorManager.Instance.GetData(seer.PlayerId, target.PlayerId);
                        RealName = ncd.OpenTag + RealName + ncd.CloseTag;
                    }

                    //インポスター/キル可能な第三陣営がタスクが終わりそうなSnitchを確認できる
                    var canFindSnitchRole = seer.GetCustomRole().IsImpostor() || //LocalPlayerがインポスター
                        (Options.SnitchCanFindNeutralKiller.GetBool() && seer.IsNeutralKiller());//or キル可能な第三陣営


                    switch (seer.GetRoleType())
                    {
                        case RoleType.Coven:
                            if (target.GetRoleType() == RoleType.Coven)
                                RealName = Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Coven), RealName);
                            break;
                    }
                    if (seer.Is(CustomRoles.GuardianAngelTOU))
                    {

                    }
                    foreach (var TargetGA in Main.GuardianAngelTarget)
                    {
                        //if (Options.)
                        if ((seer.PlayerId == TargetGA.Key || seer.Data.IsDead) && //seerがKey or Dead
                        target.PlayerId == TargetGA.Value) //targetがValue
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.GuardianAngelTOU)}>♦</color>";
                    }
                    foreach (var TargetGA in Main.GuardianAngelTarget)
                    {
                        //if (Options.TargetKnowsGA.GetBool())
                        //{
                        //    if (seer.PlayerId == TargetGA.Value || seer.Data.IsDead)
                        //        Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.GuardianAngelTOU)}>♦</color>";
                        //}
                    }
                    if (seer.Is(CustomRoles.Arsonist))
                    {
                        if (seer.IsDousedPlayer(target))
                        {
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Arsonist)}>▲</color>";
                        }
                        else if (
                            Main.currentDousingTarget != 255 &&
                            Main.currentDousingTarget == target.PlayerId
                        )
                        {
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Arsonist)}>△</color>";
                        }
                    }
                    if (seer.Is(CustomRoles.HexMaster))
                    {
                        //†
                        if (seer.IsHexedPlayer(target))
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Coven)}>†</color>";
                    }
                    if (target.Is(CustomRoles.Child))
                    {
                        if (Options.ChildKnown.GetBool())
                            Mark += Helpers.ColorString(Utils.GetRoleColor(CustomRoles.Jackal), " (C)");
                    }
                    if (seer.Is(CustomRoles.PlagueBearer) || seer.Data.IsDead)
                    {
                        if (seer.IsInfectedPlayer(target))
                        {
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Pestilence)}>◆</color>";
                        }
                        else if (
                            Main.currentInfectingTarget != 255 &&
                            Main.currentInfectingTarget == target.PlayerId
                        )
                        {
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Pestilence)}>♦</color>";
                        }
                    }
                    foreach (var ExecutionerTarget in Main.ExecutionerTarget)
                    {
                        if ((seer.PlayerId == ExecutionerTarget.Key) && //seerがKey or Dead
                        target.PlayerId == ExecutionerTarget.Value) //targetがValue
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Executioner)}>♦</color>";
                    }
                    if (seer.Is(CustomRoles.Puppeteer))
                    {
                        if (seer.Is(CustomRoles.Puppeteer) &&
                        Main.PuppeteerList.ContainsValue(seer.PlayerId) &&
                        Main.PuppeteerList.ContainsKey(target.PlayerId))
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Impostor)}>◆</color>";
                    }
                    if (seer.Is(CustomRoles.CovenWitch) && !Main.HasNecronomicon)
                    {
                        if (seer.Is(CustomRoles.CovenWitch) &&
                        Main.WitchedList.ContainsValue(seer.PlayerId) &&
                        Main.WitchedList.ContainsKey(target.PlayerId))
                            Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.CovenWitch)}>◆</color>";
                    }
                    if (Sniper.IsEnable() && target.AmOwner)
                    {
                        //銃声が聞こえるかチェック
                        Mark += Sniper.GetShotNotify(target.PlayerId);

                    }
                    //タスクが終わりそうなSnitchがいるとき、インポスター/キル可能な第三陣営に警告が表示される
                    if ((!GameStates.IsMeeting && target.GetCustomRole().IsImpostor())
                        || (Options.SnitchCanFindNeutralKiller.GetBool() && target.IsNeutralKiller()))
                    { //targetがインポスターかつ自分自身
                        var found = false;
                        var update = false;
                        var arrows = "";
                        foreach (var pc in PlayerControl.AllPlayerControls)
                        { //全員分ループ
                            if (!pc.Is(CustomRoles.Snitch) || pc.Data.IsDead || pc.Data.Disconnected) continue; //(スニッチ以外 || 死者 || 切断者)に用はない
                            if (pc.GetPlayerTaskState().DoExpose)
                            { //タスクが終わりそうなSnitchが見つかった時
                                found = true;
                                //矢印表示しないならこれ以上は不要
                                if (!Options.SnitchEnableTargetArrow.GetBool()) break;
                                update = CheckArrowUpdate(target, pc, update, false);
                                var key = (target.PlayerId, pc.PlayerId);
                                arrows += Main.targetArrows[key];
                            }
                        }
                        if (found && target.AmOwner) Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Snitch)}>★{arrows}</color>"; //Snitch警告を表示
                        if (AmongUsClient.Instance.AmHost && seer.PlayerId != target.PlayerId && update)
                        {
                            //更新があったら非Modに通知
                            Utils.NotifyRoles(SpecifySeer: target);
                        }
                    }

                    //ハートマークを付ける(会議中MOD視点)
                    if (__instance.Is(CustomRoles.Lovers) && PlayerControl.LocalPlayer.Is(CustomRoles.Lovers))
                    {
                        Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Lovers)}>♡</color>";
                    }
                    else if (__instance.Is(CustomRoles.Lovers) && PlayerControl.LocalPlayer.Data.IsDead)
                    {
                        Mark += $"<color={Utils.GetRoleColorCode(CustomRoles.Lovers)}>♡</color>";
                    }

                    //矢印オプションありならタスクが終わったスニッチはインポスター/キル可能な第三陣営の方角がわかる
                    if (GameStates.IsInTask && Options.SnitchEnableTargetArrow.GetBool() && target.Is(CustomRoles.Snitch))
                    {
                        var TaskState = target.GetPlayerTaskState();
                        if (TaskState.IsTaskFinished)
                        {
                            var coloredArrow = Options.SnitchCanGetArrowColor.GetBool();
                            var update = false;
                            foreach (var pc in PlayerControl.AllPlayerControls)
                            {
                                var foundCheck =
                                    pc.GetCustomRole().IsImpostor() ||
                                    (Options.SnitchCanFindNeutralKiller.GetBool() && pc.IsNeutralKiller());

                                //発見対象じゃ無ければ次
                                if (!foundCheck) continue;

                                update = CheckArrowUpdate(target, pc, update, coloredArrow);
                                var key = (target.PlayerId, pc.PlayerId);
                                if (target.AmOwner)
                                {
                                    //MODなら矢印表示
                                    Suffix += Main.targetArrows[key];
                                }
                            }
                            if (AmongUsClient.Instance.AmHost && seer.PlayerId != target.PlayerId && update)
                            {
                                //更新があったら非Modに通知
                                Utils.NotifyRoles(SpecifySeer: target);
                            }
                        }
                    }
                    /*if(main.AmDebugger.Value && main.BlockKilling.TryGetValue(target.PlayerId, out var isBlocked)) {
                        Mark = isBlocked ? "(true)" : "(false)";
                    }*/

                    //Mark・Suffixの適用
                    target.cosmetics.nameText.text = $"{RealName}{Mark}";

                    if (Suffix != "")
                    {
                        //名前が2行になると役職テキストを上にずらす必要がある
                        RoleText.transform.SetLocalY(0.35f);
                        target.cosmetics.nameText.text += "\r\n" + Suffix;

                    }
                    else
                    {
                        //役職テキストの座標を初期値に戻す
                        RoleText.transform.SetLocalY(0.175f);
                    }
                }
                else
                {
                    //役職テキストの座標を初期値に戻す
                    RoleText.transform.SetLocalY(0.175f);
                }
            }
        }
        //FIXME: 役職クラス化のタイミングで、このメソッドは移動予定
        public static void LoversSuicide(byte deathId = 0x7f, bool isExiled = false)
        {
            if (CustomRoles.Lovers.IsEnable() && Main.isLoversDead == false)
            {
                foreach (var loversPlayer in Main.LoversPlayers)
                {
                    //生きていて死ぬ予定でなければスキップ
                    if (!loversPlayer.Data.IsDead && loversPlayer.PlayerId != deathId) continue;

                    Main.isLoversDead = true;
                    foreach (var partnerPlayer in Main.LoversPlayers)
                    {
                        //本人ならスキップ
                        if (loversPlayer.PlayerId == partnerPlayer.PlayerId) continue;

                        //残った恋人を全て殺す(2人以上可)
                        //生きていて死ぬ予定もない場合は心中
                        if (partnerPlayer.PlayerId != deathId && !partnerPlayer.Data.IsDead && !partnerPlayer.Is(CustomRoles.Pestilence))
                        {
                            PlayerState.SetDeathReason(partnerPlayer.PlayerId, PlayerState.DeathReason.LoversSuicide);
                            if (isExiled)
                                Main.AfterMeetingDeathPlayers.TryAdd(partnerPlayer.PlayerId, PlayerState.DeathReason.LoversSuicide);
                            else
                                partnerPlayer.RpcMurderPlayer(partnerPlayer);
                        }
                    }
                }
            }
        }

        public static bool CheckArrowUpdate(PlayerControl seer, PlayerControl target, bool updateFlag, bool coloredArrow)
        {
            var key = (seer.PlayerId, target.PlayerId);
            if (!Main.targetArrows.TryGetValue(key, out var oldArrow))
            {
                //初回は必ず被らないもの
                oldArrow = "_";
            }
            //初期値は死んでる場合の空白にしておく
            var arrow = "";
            if (!PlayerState.isDead[seer.PlayerId] && !PlayerState.isDead[target.PlayerId])
            {
                //対象の方角ベクトルを取る
                var dir = target.transform.position - seer.transform.position;
                byte index;
                if (dir.magnitude < 2)
                {
                    //近い時はドット表示
                    index = 8;
                }
                else
                {
                    //-22.5～22.5度を0とするindexに変換
                    var angle = Vector3.SignedAngle(Vector3.down, dir, Vector3.back) + 180 + 22.5;
                    index = (byte)(((int)(angle / 45)) % 8);
                }
                arrow = "↑↗→↘↓↙←↖・"[index].ToString();
                if (coloredArrow)
                {
                    arrow = $"<color={target.GetRoleColorCode()}>{arrow}</color>";
                }
            }
            if (oldArrow != arrow)
            {
                //前回から変わってたら登録して更新フラグ
                Main.targetArrows[key] = arrow;
                updateFlag = true;
                //Logger.info($"{seer.name}->{target.name}:{arrow}");
            }
            return updateFlag;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.Start))]
    class PlayerStartPatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            var roleText = UnityEngine.Object.Instantiate(__instance.cosmetics.nameText);
            roleText.transform.SetParent(__instance.cosmetics.nameText.transform);
            roleText.transform.localPosition = new Vector3(0f, 0.175f, 0f);
            roleText.fontSize = 0.55f;
            roleText.text = "RoleText";
            roleText.gameObject.name = "RoleText";
            roleText.enabled = false;
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetColor))]
    class SetColorPatch
    {
        public static bool IsAntiGlitchDisabled = false;
        public static bool Prefix(PlayerControl __instance, int bodyColor)
        {
            //色変更バグ対策
            if (!AmongUsClient.Instance.AmHost || __instance.CurrentOutfit.ColorId == bodyColor || IsAntiGlitchDisabled) return true;
            if (AmongUsClient.Instance.IsGameStarted && Options.CurrentGameMode == CustomGameMode.HideAndSeek)
            {
                //ゲーム中に色を変えた場合
                __instance.RpcMurderPlayer(__instance);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Vent), nameof(Vent.EnterVent))]
    class EnterVentPatch
    {
        public static void Postfix(Vent __instance, [HarmonyArgument(0)] PlayerControl pc)
        {
            bool skipCheck = false;
            if (CustomRoles.TheGlitch.IsEnable() && Options.GlitchCanVent.GetBool())
            {
                List<PlayerControl> hackedPlayers = new();
                PlayerControl glitch;
                foreach (var cp in Main.CursedPlayers)
                {
                    if (Utils.GetPlayerById(cp.Key).Is(CustomRoles.TheGlitch))
                    {
                        hackedPlayers.Add(cp.Value);
                        glitch = Utils.GetPlayerById(cp.Key);
                    }
                }

                if (hackedPlayers.Contains(pc))
                {
                    pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                    skipCheck = true;
                }
            }
            if (Options.CurrentGameMode == CustomGameMode.HideAndSeek && Options.IgnoreVent.GetBool())
                pc.MyPhysics.RpcBootFromVent(__instance.Id);
            if (pc.Is(CustomRoles.Mayor))
            {
                if (Main.MayorUsedButtonCount.TryGetValue(pc.PlayerId, out var count) && count < Options.MayorNumOfUseButton.GetInt())
                {
                    pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                    pc?.ReportDeadBody(null);
                }
                skipCheck = true;
            }
            if (pc.Is(CustomRoles.Veteran))
            {
                if (Main.VetAlerts != Options.NumOfVets.GetInt())
                {
                    pc.VetAlerted();
                }
                pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                skipCheck = true;
            }
            if (pc.Is(CustomRoles.Medusa))
            {
                pc.StoneGazed();
                pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                skipCheck = true;
                Utils.NotifyRoles();
            }
            if (pc.Is(CustomRoles.GuardianAngelTOU))
            {
                if (Main.GAprotects != Options.NumOfProtects.GetInt())
                {
                    pc.GaProtect();
                }
                pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                skipCheck = true;
            }
            if (pc.Is(CustomRoles.TheGlitch))
            {
                // pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                //pc.MyPhysics.RpcBootFromVent(__instance.Id);
                skipCheck = true;
                if (Main.IsHackMode)
                    Main.IsHackMode = false;
                else
                    Main.IsHackMode = true;
                pc.MyPhysics.RpcBootFromVent(__instance.Id);
                Utils.NotifyRoles();
            }
            if (pc.Is(CustomRoles.Bastion))
            {
                skipCheck = true;
                if (!Main.bombedVents.Contains(__instance.Id))
                    Main.bombedVents.Add(__instance.Id);
                else
                {
                    pc.MyPhysics.RpcBootFromVent(__instance.Id);
                    pc.RpcMurderPlayer(pc);
                    PlayerState.SetDeathReason(pc.PlayerId, PlayerState.DeathReason.Bombed);
                    PlayerState.SetDead(pc.PlayerId);
                }
                pc.MyPhysics.RpcBootFromVent(__instance.Id);
            }
            if (pc.Is(CustomRoles.Werewolf))
            {
                skipCheck = true;
                Utils.NotifyRoles();
                if (Main.IsRampaged)
                {

                    //do nothing.
                    if (!Options.VentWhileRampaged.GetBool())
                    {
                        // pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                        pc.MyPhysics.RpcBootFromVent(__instance.Id);
                        if (Options.VentWhileRampaged.GetBool())
                        {
                            if (Main.bombedVents.Contains(__instance.Id))
                            {
                                pc.RpcMurderPlayer(pc);
                                PlayerState.SetDeathReason(pc.PlayerId, PlayerState.DeathReason.Bombed);
                                PlayerState.SetDead(pc.PlayerId);
                            }
                        }
                    }
                }
                else
                {
                    if (Main.RampageReady)
                    {
                        Main.RampageReady = false;
                        Main.IsRampaged = true;
                        Utils.CustomSyncAllSettings();
                        new LateTask(() =>
                        {
                            Main.IsRampaged = false;
                            pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                            Utils.CustomSyncAllSettings();
                            new LateTask(() =>
                            {
                                pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                                Main.RampageReady = true;
                                Utils.CustomSyncAllSettings();
                            }, Options.RampageDur.GetFloat(), "Werewolf Rampage Cooldown");
                        }, Options.RampageDur.GetFloat(), "Werewolf Rampage Duration");
                    }
                    else
                    {
                        pc?.MyPhysics?.RpcBootFromVent(__instance.Id);
                    }
                }
            }

            if (pc.Is(CustomRoles.Jester) && !Options.JesterCanVent.GetBool())
                pc.MyPhysics.RpcBootFromVent(__instance.Id);
            if (Main.bombedVents.Contains(__instance.Id) && !skipCheck)
            {
                if (!pc.Is(CustomRoles.Pestilence))
                {
                    pc.MyPhysics.RpcBootFromVent(__instance.Id);
                    pc.RpcMurderPlayer(pc);
                    PlayerState.SetDeathReason(pc.PlayerId, PlayerState.DeathReason.Bombed);
                    PlayerState.SetDead(pc.PlayerId);
                }
            }
        }
    }
    [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.CoEnterVent))]
    class CoEnterVentPatch
    {
        public static bool Prefix(PlayerPhysics __instance, [HarmonyArgument(0)] int id)
        {
            if (AmongUsClient.Instance.AmHost)
            {
                if (AmongUsClient.Instance.IsGameStarted &&
                    __instance.myPlayer.IsDouseDone() && !Options.TOuRArso.GetBool())
                {
                    foreach (var pc in PlayerControl.AllPlayerControls)
                    {
                        if (!pc.Data.IsDead)
                        {
                            if (pc != __instance.myPlayer && !pc.Is(CustomRoles.Pestilence))
                            {
                                //生存者は焼殺
                                //if (!pc.Is(CustomRoles.Pestilence))
                                pc.RpcMurderPlayer(pc);
                                PlayerState.SetDeathReason(pc.PlayerId, PlayerState.DeathReason.Torched);
                                PlayerState.SetDead(pc.PlayerId);
                            }
                            else
                                RPC.PlaySoundRPC(pc.PlayerId, Sounds.KillSound);
                        }
                    }
                    MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.EndGame, Hazel.SendOption.Reliable, -1);
                    writer.Write((byte)CustomWinner.Arsonist);
                    writer.Write(__instance.myPlayer.PlayerId);
                    AmongUsClient.Instance.FinishRpcImmediately(writer);
                    RPC.ArsonistWin(__instance.myPlayer.PlayerId);
                    return true;
                }
                if (__instance.myPlayer.Is(CustomRoles.Arsonist) && Options.TOuRArso.GetBool())
                {
                    List<PlayerControl> doused = Utils.GetDousedPlayer(__instance.myPlayer.PlayerId);
                    foreach (var pc in doused)
                    {
                        if (!pc.Data.IsDead)
                        {
                            if (pc != __instance.myPlayer && !pc.Is(CustomRoles.Pestilence))
                            {
                                //生存者は焼殺
                                pc.RpcMurderPlayer(pc);
                                PlayerState.SetDeathReason(pc.PlayerId, PlayerState.DeathReason.Torched);
                                PlayerState.SetDead(pc.PlayerId);
                            }
                            else
                                RPC.PlaySoundRPC(pc.PlayerId, Sounds.KillSound);
                        }
                    }
                }
                if (__instance.myPlayer.Is(CustomRoles.Sheriff) ||
                __instance.myPlayer.Is(CustomRoles.SKMadmate) ||
                __instance.myPlayer.Is(CustomRoles.Arsonist) ||
                __instance.myPlayer.Is(CustomRoles.PlagueBearer) ||
                (__instance.myPlayer.GetCustomRole().IsCoven() && !__instance.myPlayer.Is(CustomRoles.Medusa)) ||
                (__instance.myPlayer.Is(CustomRoles.Mayor) && Main.MayorUsedButtonCount.TryGetValue(__instance.myPlayer.PlayerId, out var count) && count >= Options.MayorNumOfUseButton.GetInt()) ||
                (__instance.myPlayer.Is(CustomRoles.Jackal) && !Options.JackalCanVent.GetBool()) ||
                (__instance.myPlayer.Is(CustomRoles.Pestilence) && !Options.PestiCanVent.GetBool())
                )
                {
                    MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.BootFromVent, SendOption.Reliable, -1);
                    writer.WritePacked(127);
                    AmongUsClient.Instance.FinishRpcImmediately(writer);
                    new LateTask(() =>
                    {
                        int clientId = __instance.myPlayer.GetClientId();
                        MessageWriter writer2 = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.BootFromVent, SendOption.Reliable, clientId);
                        writer2.Write(id);
                        AmongUsClient.Instance.FinishRpcImmediately(writer2);
                    }, 0.5f, "Fix DesyncImpostor Stuck");
                    return false;
                }
                if (__instance.myPlayer.GetRoleType() == RoleType.Coven && !Main.HasNecronomicon && !__instance.myPlayer.Is(CustomRoles.Mimic))
                {
                    MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.BootFromVent, SendOption.Reliable, -1);
                    writer.WritePacked(127);
                    AmongUsClient.Instance.FinishRpcImmediately(writer);
                    new LateTask(() =>
                    {
                        int clientId = __instance.myPlayer.GetClientId();
                        MessageWriter writer2 = AmongUsClient.Instance.StartRpcImmediately(__instance.NetId, (byte)RpcCalls.BootFromVent, SendOption.Reliable, clientId);
                        writer2.Write(id);
                        AmongUsClient.Instance.FinishRpcImmediately(writer2);
                    }, 0.5f, "Fix DesyncImpostor Stuck");
                    return false;
                }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetName))]
    class SetNamePatch
    {
        public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] string name)
        {
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CompleteTask))]
    class PlayerControlCompleteTaskPatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            var pc = __instance;
            Logger.Info($"TaskComplete:{pc.PlayerId}", "CompleteTask");
            PlayerState.UpdateTask(pc);
            Utils.NotifyRoles();
            if (pc.GetPlayerTaskState().IsTaskFinished &&
                pc.GetCustomRole() is CustomRoles.Lighter or CustomRoles.SpeedBooster or CustomRoles.Doctor or CustomRoles.Doctor || Main.KilledBewilder.Contains(pc.PlayerId))
            {
                //ライターもしくはスピードブースターもしくはドクターがいる試合のみタスク終了時にCustomSyncAllSettingsを実行する
                Utils.CustomSyncAllSettings();
            }

        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.ProtectPlayer))]
    class PlayerControlProtectPlayerPatch
    {
        public static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
        {
            Logger.Info($"{__instance.GetNameWithRole()} => {target.GetNameWithRole()}", "ProtectPlayer");
        }
    }
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RemoveProtection))]
    class PlayerControlRemoveProtectionPatch
    {
        public static void Postfix(PlayerControl __instance)
        {
            Logger.Info($"{__instance.GetNameWithRole()}", "RemoveProtection");
        }
    }
}
