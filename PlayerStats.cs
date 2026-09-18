using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PlayerStats", "Maison Studio", "1.0.1")]
    [Description("Player statistics: kills, deaths, K/D, headshots e playtime.")]
    public class PlayerStats : RustPlugin
    {
        // Carbon is compatible with plugins written in the Oxide format (Oxide.Plugins namespace,
        // class inheriting from RustPlugin), so this file runs on both Carbon and Oxide.
        // Data is saved to oxide/data/PlayerStats.json using the native Oxide/Carbon
        // data system (no extra assembly required, unlike System.Data.SQLite).

        #region Data (persistent JSON file)

        private class PlayerRecord
        {
            public string Name;
            public int Kills;
            public int Deaths;
            public int Headshots;
            public long PlaytimeSeconds;
        }

        private Dictionary<string, PlayerRecord> playerData = new Dictionary<string, PlayerRecord>();

        private void LoadData()
        {
            playerData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, PlayerRecord>>(Name)
                         ?? new Dictionary<string, PlayerRecord>();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, playerData);
        }

        private PlayerRecord GetOrCreate(string steamId, string name)
        {
            if (!playerData.TryGetValue(steamId, out var record))
            {
                record = new PlayerRecord { Name = name };
                playerData[steamId] = record;
            }
            else
            {
                record.Name = name;
            }
            return record;
        }

        private PlayerRecord GetRecord(string steamId)
        {
            return playerData.TryGetValue(steamId, out var record) ? record : null;
        }

        #endregion

        #region In-memory state

        private readonly Dictionary<ulong, DateTime> sessionStart = new Dictionary<ulong, DateTime>();
        private const string UiPanelName = "PlayerStats.Panel";

        private const string CreditsLine = "Developed by Maison Studio";

        #endregion

        #region Life cycle

        private void Init()
        {
            AddCovalenceCommand("stats", nameof(CmdStats));
        }

        private void OnServerInitialized()
        {
            LoadData();
            foreach (var player in BasePlayer.activePlayerList)
                StartSession(player);

            // Saves data periodically to avoid losing progress in the event of a crash.
            timer.Every(300f, SaveData);

            Puts($"{CreditsLine}");
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                EndSession(player);
                CuiHelper.DestroyUi(player, UiPanelName);
            }
            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player) => StartSession(player);

        private void OnPlayerDisconnected(BasePlayer player, string reason) => EndSession(player);

        #endregion

        #region Session / Playtime

        private void StartSession(BasePlayer player)
        {
            if (player == null) return;
            GetOrCreate(player.UserIDString, player.displayName);
            sessionStart[player.userID] = DateTime.UtcNow;
        }

        private void EndSession(BasePlayer player)
        {
            if (player == null) return;
            if (sessionStart.TryGetValue(player.userID, out var start))
            {
                var seconds = (long)(DateTime.UtcNow - start).TotalSeconds;
                if (seconds > 0)
                {
                    var record = GetOrCreate(player.UserIDString, player.displayName);
                    record.PlaytimeSeconds += seconds;
                }
                sessionStart.Remove(player.userID);
            }
        }

        // Adds the current session time to the stored value, so that /stats always shows the actual time.
        private long GetLivePlaytime(BasePlayer player, long storedSeconds)
        {
            if (sessionStart.TryGetValue(player.userID, out var start))
                return storedSeconds + (long)(DateTime.UtcNow - start).TotalSeconds;
            return storedSeconds;
        }

        #endregion

        #region Gameplay events

        private void OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            if (victim == null || !victim.userID.IsSteamId()) return;

            var victimRecord = GetOrCreate(victim.UserIDString, victim.displayName);
            victimRecord.Deaths++;

            var attacker = info?.InitiatorPlayer;
            if (attacker == null || attacker == victim || !attacker.userID.IsSteamId()) return;

            var attackerRecord = GetOrCreate(attacker.UserIDString, attacker.displayName);
            attackerRecord.Kills++;

            if (info.boneArea == HitArea.Head)
                attackerRecord.Headshots++;
        }

        #endregion

        #region /stats command

        private void CmdStats(IPlayer iplayer, string command, string[] args)
        {
            var basePlayer = iplayer.Object as BasePlayer;
            if (basePlayer == null) { iplayer.Reply("This command can only be used in-game."); return; }

            string targetId = basePlayer.UserIDString;

            if (args.Length > 0)
            {
                var found = BasePlayer.activePlayerList.FirstOrDefault(p =>
                    p.displayName.IndexOf(args[0], StringComparison.OrdinalIgnoreCase) >= 0);
                if (found != null) targetId = found.UserIDString;
            }

            var record = GetRecord(targetId);
            if (record == null)
            {
                iplayer.Reply("No statistics recorded for this player.");
                return;
            }

            long playtime = targetId == basePlayer.UserIDString
                ? GetLivePlaytime(basePlayer, record.PlaytimeSeconds)
                : record.PlaytimeSeconds;

            ShowStatsUi(basePlayer, record, playtime);
        }

        private string FormatPlaytime(long seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        #endregion

        #region UI (CUI)

        // Accent color (red) used for values ​​and the footer — change here to adjust the tone.
        private const string AccentRed = "0.82 0.14 0.18 1";
        private const string AccentRedHex = "D1242E";

        private void ShowStatsUi(BasePlayer player, PlayerRecord record, long playtimeSeconds)
        {
            CuiHelper.DestroyUi(player, UiPanelName);

            double kd = record.Deaths == 0 ? record.Kills : (double)record.Kills / record.Deaths;

            var container = new CuiElementContainer();

            // Main panel
            container.Add(new CuiPanel
            {
                Image = { Color = "0.05 0.05 0.06 0.95" },
                RectTransform = { AnchorMin = "0.325 0.32", AnchorMax = "0.675 0.725" },
                CursorEnabled = true
            }, "Overlay", UiPanelName);

            // Header: studio name (left) + panel title (right)
            container.Add(new CuiLabel
            {
                Text = { Text = "<b>Maison Studio</b>", FontSize = 15, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.06 0.90", AnchorMax = "0.55 1.0" }
            }, UiPanelName);

            container.Add(new CuiLabel
            {
                Text = { Text = record.Name.ToUpper(), FontSize = 12, Align = TextAnchor.MiddleRight, Color = "0.6 0.6 0.6 1" },
                RectTransform = { AnchorMin = "0.40 0.90", AnchorMax = "0.855 1.0" }
            }, UiPanelName);

            // Close button — top right corner, unobtrusive
            container.Add(new CuiButton
            {
                Button = { Command = "playerstats.close", Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.865 0.905", AnchorMax = "0.955 0.995" },
                Text = { Text = "✕", Align = TextAnchor.MiddleCenter, FontSize = 16, Color = "0.65 0.65 0.65 1" }
            }, UiPanelName);

            // Divider below the header
            container.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0.12" },
                RectTransform = { AnchorMin = "0.06 0.865", AnchorMax = "0.94 0.875" }
            }, UiPanelName);

            // Statistics lines — icon + label on the left, value in red on the right
            var rows = new[]
            {
                new { Icon = "☠", Label = "Kills:", Value = record.Kills.ToString() },
                new { Icon = "†", Label = "Deaths:", Value = record.Deaths.ToString() },
                new { Icon = "⊕", Label = "K/D:", Value = kd.ToString("F2") },
                new { Icon = "✛", Label = "Headshots:", Value = record.Headshots.ToString() }
            };

            float rowTop = 0.815f;
            const float rowHeight = 0.105f;
            foreach (var row in rows)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = $"{row.Icon}  {row.Label}", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.85 0.85 0.85 1" },
                    RectTransform = { AnchorMin = $"0.08 {rowTop - rowHeight}", AnchorMax = $"0.62 {rowTop}" }
                }, UiPanelName);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"<b>{row.Value}</b>", FontSize = 16, Align = TextAnchor.MiddleRight, Color = AccentRed },
                    RectTransform = { AnchorMin = $"0.62 {rowTop - rowHeight}", AnchorMax = $"0.92 {rowTop}" }
                }, UiPanelName);

                rowTop -= rowHeight;
            }

            // Divider before game time
            container.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0.12" },
                RectTransform = { AnchorMin = "0.06 0.375", AnchorMax = "0.94 0.385" }
            }, UiPanelName);

            container.Add(new CuiLabel
            {
                Text = { Text = "▶  Playing time:", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.85 0.85 0.85 1" },
                RectTransform = { AnchorMin = "0.08 0.27", AnchorMax = "0.62 0.365" }
            }, UiPanelName);

            container.Add(new CuiLabel
            {
                Text = { Text = $"<b>{FormatPlaytime(playtimeSeconds)}</b>", FontSize = 16, Align = TextAnchor.MiddleRight, Color = AccentRed },
                RectTransform = { AnchorMin = "0.62 0.27", AnchorMax = "0.92 0.365" }
            }, UiPanelName);

            // Footer divider
            container.Add(new CuiPanel
            {
                Image = { Color = "1 1 1 0.12" },
                RectTransform = { AnchorMin = "0.06 0.235", AnchorMax = "0.94 0.245" }
            }, UiPanelName);

            // Credits footer — change CreditsLine above and AccentRedHex if you want to edit the text/color.
            container.Add(new CuiLabel
            {
                Text = { Text = $"Developed by <color=#{AccentRedHex}><b>{CreditsLine.Replace("Developed by ", "")}</b></color>", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.6 0.6 0.6 1" },
                RectTransform = { AnchorMin = "0 0.03", AnchorMax = "1 0.13" }
            }, UiPanelName);

            CuiHelper.AddUi(player, container);
        }

        [ConsoleCommand("playerstats.close")]
        private void CcmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiPanelName);
        }

        #endregion
    }
}
