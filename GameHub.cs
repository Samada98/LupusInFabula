using LupusInTabula.Models;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace LupusInTabula.Hubs
{
    public class GameHub : Hub
    {
        // ===== Stato globale =====
        private static readonly ConcurrentDictionary<string, GameRoom> Rooms = new();
        // ConnId -> (roomId, playerName, isHost) | playerName null = host
        private static readonly ConcurrentDictionary<string, (string roomId, string? playerName, bool isHost)> ConnIndex = new();

        // =========================================================
        // =============== LIFECYCLE & CONNESSIONI =================
        // =========================================================

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (ConnIndex.TryRemove(Context.ConnectionId, out var info)
                && Rooms.TryGetValue(info.roomId, out var room))
            {
                if (info.isHost)
                {
                    room.HostConnectionId = null;
                }
                else if (!string.IsNullOrEmpty(info.playerName))
                {
                    var p = room.Players.FirstOrDefault(x => Same(x.Name, info.playerName));
                    if (p != null)
                    {
                        p.IsOnline = false;
                        p.ConnectionId = null;
                    }
                }

                await BroadcastLobbyAndVotes(room);
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task<string> CreateRoom(string hostName)
        {
            string id;
            do { id = NewRoomId(); } while (Rooms.ContainsKey(id));

            var room = new GameRoom
            {
                Id = id,
                HostConnectionId = Context.ConnectionId,
                HostName = hostName,
                HostKey = Guid.NewGuid().ToString("N"),
                GameStarted = false,
                VotingOpen = false,
                CoupleSleepAt = "romeo"
            };

            Rooms[id] = room;

            await Groups.AddToGroupAsync(Context.ConnectionId, id);
            ConnIndex[Context.ConnectionId] = (id, null, true);

            await Clients.Caller.SendAsync("ReceiveHostKey", room.HostKey);
            await BroadcastLobbyAndVotes(room);

            return id;
        }

        /// <summary>
        /// Join “elastico”: gestisce host (con hostKey in rientro) e giocatori.
        /// Restituisce un oggetto con lo stato iniziale; se il gioco è già avviato,
        /// include anche i roleCounts salvati.
        /// </summary>
        public async Task<object> JoinRoom(string roomId, string name, string? hostKey = null)
        {
            if (!Rooms.TryGetValue(roomId, out var room))
            {
                await Clients.Caller.SendAsync("JoinError", "Stanza non trovata");
                return new
                {
                    ok = false,
                    error = "Stanza non trovata",
                    roomId,
                    hostName = "",
                    isHost = false,
                    gameStarted = false,
                    votingOpen = false,
                    role = (string?)null,
                    players = new List<PlayerDto>(),
                    roleCounts = (IDictionary<string, int>?)null
                };
            }

            var connId = Context.ConnectionId;
            var trimmedName = (name ?? "").Trim();

            // -------- Host --------
            var isNameHost = Same(trimmedName, room.HostName);
            var hostOnline = IsHostOnline(room);

            if (isNameHost)
            {
                // Host già online con altra connessione
                if (hostOnline && room.HostConnectionId != connId)
                {
                    await Clients.Caller.SendAsync("JoinError", "Host già online in questa stanza.");
                    return new
                    {
                        ok = false,
                        error = "Host già online in questa stanza.",
                        roomId = room.Id,
                        hostName = room.HostName,
                        isHost = false,
                        gameStarted = room.GameStarted,
                        votingOpen = room.VotingOpen,
                        role = (string?)null,
                        players = MapPlayers(room),
                        roleCounts = room.GameStarted ? room.SavedRoleCounts : null
                    };
                }

                // Host offline: richiedi hostKey corretta
                if (!hostOnline)
                {
                    if (string.IsNullOrEmpty(hostKey) || !Same(hostKey, room.HostKey))
                    {
                        await Clients.Caller.SendAsync("JoinError", "Per rientrare come host serve l'hostKey corretta.");
                        return new
                        {
                            ok = false,
                            error = "HostKey mancante o errata.",
                            roomId = room.Id,
                            hostName = room.HostName,
                            isHost = false,
                            gameStarted = room.GameStarted,
                            votingOpen = room.VotingOpen,
                            role = (string?)null,
                            players = MapPlayers(room),
                            roleCounts = room.GameStarted ? room.SavedRoleCounts : null
                        };
                    }
                }

                // Promuovi questa connessione a host
                room.HostConnectionId = connId;
                ConnIndex[connId] = (room.Id, null, true);
                await Groups.AddToGroupAsync(connId, room.Id);

                await BroadcastLobbyAndVotes(room);
                if (room.VotingOpen) await Clients.Caller.SendAsync("VotingStarted");
                else await Clients.Caller.SendAsync("VotingEnded");

                return new
                {
                    ok = true,
                    error = (string?)null,
                    roomId = room.Id,
                    hostName = room.HostName,
                    isHost = true,
                    gameStarted = room.GameStarted,
                    votingOpen = room.VotingOpen,
                    role = (string?)null,
                    players = MapPlayers(room),
                    roleCounts = room.GameStarted ? room.SavedRoleCounts : null
                };
            }

            // -------- Giocatore --------
            var player = room.Players.FirstOrDefault(p => Same(p.Name, trimmedName));

            // nome già in uso (giocatore online con altro connId)
            if (player != null && player.IsOnline && player.ConnectionId != connId)
            {
                await Clients.Caller.SendAsync("JoinError", "Nome già presente nella stanza");
                return new
                {
                    ok = false,
                    error = "Nome già presente nella stanza",
                    roomId = room.Id,
                    hostName = room.HostName,
                    isHost = false,
                    gameStarted = room.GameStarted,
                    votingOpen = room.VotingOpen,
                    role = (string?)null,
                    players = MapPlayers(room),
                    roleCounts = room.GameStarted ? room.SavedRoleCounts : null
                };
            }

            if (player == null)
            {
                if (room.GameStarted)
                {
                    await Clients.Caller.SendAsync("JoinError", "Partita già iniziata. Non puoi entrare.");
                    return new
                    {
                        ok = false,
                        error = "Partita già iniziata. Non puoi entrare.",
                        roomId = room.Id,
                        hostName = room.HostName,
                        isHost = false,
                        gameStarted = room.GameStarted,
                        votingOpen = room.VotingOpen,
                        role = (string?)null,
                        players = MapPlayers(room),
                        roleCounts = room.GameStarted ? room.SavedRoleCounts : null
                    };
                }

                player = new Player
                {
                    Name = trimmedName,
                    ConnectionId = connId,
                    IsOnline = true,
                    Role = ""
                };
                room.Players.Add(player);
            }
            else
            {
                // Riconnessione
                player.ConnectionId = connId;
                player.IsOnline = true;

                if (!string.IsNullOrEmpty(player.Role))
                    await Clients.Caller.SendAsync("ReceiveRole", player.Role);
            }

            ConnIndex[connId] = (room.Id, player.Name, false);
            await Groups.AddToGroupAsync(connId, room.Id);

            await BroadcastLobbyAndVotes(room);
            if (room.VotingOpen) await Clients.Caller.SendAsync("VotingStarted");
            else await Clients.Caller.SendAsync("VotingEnded");

            return new
            {
                ok = true,
                error = (string?)null,
                roomId = room.Id,
                hostName = room.HostName,
                isHost = false,
                gameStarted = room.GameStarted,
                votingOpen = room.VotingOpen,
                role = player.Role,
                players = MapPlayers(room),
                roleCounts = room.GameStarted ? room.SavedRoleCounts : null
            };
        }

        public async Task LeaveRoom(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;

            // Host che lascia
            if (SameConn(room.HostConnectionId, Context.ConnectionId))
            {
                room.HostConnectionId = null;
                ConnIndex.TryRemove(Context.ConnectionId, out _);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
                await BroadcastLobbyAndVotes(room);
                return;
            }

            // Giocatore che lascia
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (player != null)
            {
                player.IsOnline = false;
                player.ConnectionId = null;
            }

            ConnIndex.TryRemove(Context.ConnectionId, out _);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
            await BroadcastLobbyAndVotes(room);
        }

        // =========================================================
        // ====================== GAME FLOW =========================
        // =========================================================

        public async Task RestartGame(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            room.GameStarted = false;
            room.VotingOpen = false;
            room.SavedRoleCounts = null; // ← azzera i conteggi salvati

            foreach (var p in room.Players)
            {
                p.Role = "";
                p.CurrentVote = null;
                p.Eliminated = false;
            }

            // reset Coppia
            room.CoupleRomeoName = null;
            room.CoupleJulietName = null;
            room.CoupleSleepAt = "romeo";

            await Clients.Group(room.Id).SendAsync("GameRestarted", ToLobbyPlayers(room), room.HostName);
            await BroadcastLobbyAndVotes(room);
        }

        public async Task StartGame(
            string roomId,
            int wolves, int villagers, int seers, int guards, int scemo, int hunter, int witch, int lara, int mayor, int hitman,
            int medium, int couple)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            // 0) Reset pulito
            room.GameStarted = false;
            room.VotingOpen = false;
            foreach (var p in room.Players)
            {
                p.Role = "";
                p.CurrentVote = null;
                p.Eliminated = false;
            }
            room.CoupleRomeoName = null;
            room.CoupleJulietName = null;
            room.CoupleSleepAt = "romeo";

            // 1) Costruisci deck ruoli
            var roleCounts = BuildRoleCounts(wolves, villagers, seers, guards, scemo, hunter, witch, lara, mayor, hitman, medium, couple);
            var roles = BuildRoleDeck(roleCounts);

            // 2) Candidati: tutti i vivi
            var candidates = room.Players.Where(p => !p.Eliminated).ToList();

            // 2.1) Consistenza
            if (roles.Count < candidates.Count)
            {
                await Clients.Caller.SendAsync("JoinError",
                    $"Ruoli insufficienti per i giocatori presenti. Assegnati {roles.Count}, giocatori {candidates.Count}.");
                return;
            }

            // 3) Assegna ruoli (eccedenze ignorate)
            AssignRoles(roles, candidates);

            // 4) Accoppia le coppie
            await PairCouples(room);

            // 5) Stato partita
            room.GameStarted = true;
            room.VotingOpen = false;

            // 5.1) Persisti i conteggi ruoli scelti (servono ai player che si connettono dopo)
            room.SavedRoleCounts = roleCounts;

            // 6) Spedisci ruolo individuale
            foreach (var p in room.Players.Where(p => p.IsOnline && !string.IsNullOrEmpty(p.ConnectionId)))
                await Clients.Client(p.ConnectionId!).SendAsync("ReceiveRole", p.Role);

            // 7) Broadcast iniziale (ora includiamo roleCounts)
            await Clients.Group(roomId).SendAsync(
                "GameStarted",
                MapPlayers(room),
                room.HostName,
                IsHostOnline(room),
                roleCounts
            );

            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(roomId).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName, IsHostOnline(room));
        }

        public async Task OpenVoting(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            room.VotingOpen = true;
            foreach (var p in room.Players) p.CurrentVote = null;

            await Clients.Group(room.Id).SendAsync("VotingStarted");
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        public async Task CloseVoting(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            room.VotingOpen = false;
            await Clients.Group(room.Id).SendAsync("VotingEnded");
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        // targetName: null/"" -> togli voto
        public async Task VotePlayer(string roomId, string? targetName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!room.VotingOpen) return;

            var voter = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (voter == null || voter.Eliminated) return;

            voter.CurrentVote = string.IsNullOrWhiteSpace(targetName) ? null : targetName;
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        public async Task UnvotePlayer(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!room.VotingOpen) return;

            var voter = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (voter == null || voter.Eliminated) return;

            voter.CurrentVote = null;
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        // Dove dorme la coppia (può chiamarlo uno dei due)
        public async Task CoupleSleepAt(string roomId, string where)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            where = (where ?? "").Trim().ToLowerInvariant();
            if (where != "romeo" && where != "giulietta") return;

            var me = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (me == null || me.Eliminated) return;
            if (!Same(me.Role, "romeo") && !Same(me.Role, "giulietta")) return;

            room.CoupleSleepAt = where;
            await Clients.Group(room.Id).SendAsync("CoupleSleepSet", where);
        }

        public async Task EliminatePlayer(string roomId, string playerName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            var player = room.Players.FirstOrDefault(p => Same(p.Name, playerName));
            if (player == null || player.Eliminated) return;

            var isJuliet = room.CoupleJulietName != null && Same(player.Name, room.CoupleJulietName);
            var isRomeo = room.CoupleRomeoName != null && Same(player.Name, room.CoupleRomeoName);

            // Giulietta protetta se dormono da Romeo
            if (isJuliet && Same(room.CoupleSleepAt, "romeo"))
            {
                await Clients.Group(room.Id).SendAsync("CoupleSaved", player.Name, room.CoupleRomeoName);
                await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
                return;
            }

            // Elimina target
            player.Eliminated = true;
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("PlayerEliminated", player.Name);
            await RevealToMediums(room, player);

            // Se muore Romeo -> muore anche Giulietta (anche se dormivano da Romeo)
            if (isRomeo && room.CoupleJulietName != null)
            {
                var juliet = room.Players.FirstOrDefault(p => Same(p.Name, room.CoupleJulietName));
                if (juliet != null && !juliet.Eliminated)
                {
                    juliet.Eliminated = true;
                    await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
                    await Clients.Group(room.Id).SendAsync("PlayerEliminated", juliet.Name);
                    await RevealToMediums(room, juliet);
                    await Clients.Group(room.Id).SendAsync("CoupleDied", room.CoupleRomeoName, room.CoupleJulietName);
                }
            }
        }

        public async Task RevivePlayer(string roomId, string playerName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            var player = room.Players.FirstOrDefault(p => Same(p.Name, playerName));
            if (player == null) return;

            player.Eliminated = false;
            player.CurrentVote = null;

            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("PlayerRevived", playerName);
        }

        public Task ResurrectPlayer(string roomId, string playerName) => RevivePlayer(roomId, playerName);
        public Task UneliminatePlayer(string roomId, string playerName) => RevivePlayer(roomId, playerName);

        public async Task KickPlayer(string roomId, string playerName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;
            if (room.GameStarted) return;
            if (Same(playerName, room.HostName)) return;

            var player = room.Players.FirstOrDefault(p => Same(p.Name, playerName));
            if (player == null) return;

            var kickedConn = player.ConnectionId;

            room.Players.Remove(player);

            if (!string.IsNullOrEmpty(kickedConn))
            {
                await Groups.RemoveFromGroupAsync(kickedConn!, room.Id);
                ConnIndex.TryRemove(kickedConn!, out _);
                try { await Clients.Client(kickedConn!).SendAsync("Kicked", room.Id, room.HostName); } catch { }
            }

            await Clients.Group(room.Id).SendAsync("PlayerKicked", playerName);
            await BroadcastLobbyAndVotes(room);
        }

        public async Task SendHostRoomInfo(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            var roomInfo = new
            {
                RoomId = room.Id,
                Players = room.Players.Select(p => new { p.Name, p.IsOnline }).ToList()
            };

            await Clients.Caller.SendAsync("ReceiveHostRoomInfo", roomInfo);
        }

        public Task Heartbeat() => Task.CompletedTask;

        // =========================================================
        // ======================== HELPERS ========================
        // =========================================================

        private static bool Same(string? a, string? b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool SameConn(string? a, string b) =>
            !string.IsNullOrEmpty(a) && a == b;

        private static string NewRoomId(int len = 2)
        {
            const string chars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no O/0/I/1
            var rnd = Random.Shared;
            Span<char> buf = stackalloc char[len];
            for (int i = 0; i < len; i++) buf[i] = chars[rnd.Next(chars.Length)];
            return new string(buf);
        }

        private static bool IsHostOnline(GameRoom room) =>
            !string.IsNullOrEmpty(room.HostConnectionId) && ConnIndex.ContainsKey(room.HostConnectionId);

        private static List<object> ToLobbyPlayers(GameRoom room) =>
            room.Players
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new { Name = p.Name, IsOnline = p.IsOnline })
                .Cast<object>()
                .ToList();

        private static void Shuffle<T>(IList<T> list)
        {
            // Fisher–Yates/Durstenfeld con RNG crittografico
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1); // 0..i inclusi
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static IDictionary<string, int> BuildRoleCounts(
            int wolves, int villagers, int seers, int guards, int scemo, int hunter, int witch, int lara, int mayor, int hitman, int medium, int couple)
        {
            return new Dictionary<string, int>
            {
                ["wolves"] = wolves,
                ["villagers"] = villagers,
                ["seers"] = seers,
                ["guards"] = guards,
                ["scemo"] = scemo,
                ["hunter"] = hunter,
                ["witch"] = witch,
                ["lara"] = lara,
                ["mayor"] = mayor,
                ["hitman"] = hitman,
                ["medium"] = medium,
                ["couple"] = couple // n° coppie (il client mostra ×2 persone)
            };
        }

        private static List<string> BuildRoleDeck(IDictionary<string, int> rc)
        {
            var roles = new List<string>();

            roles.AddRange(Enumerable.Repeat("wolf", rc.GetValueOrDefault("wolves")));
            roles.AddRange(Enumerable.Repeat("villager", rc.GetValueOrDefault("villagers")));
            roles.AddRange(Enumerable.Repeat("seer", rc.GetValueOrDefault("seers")));
            roles.AddRange(Enumerable.Repeat("guard", rc.GetValueOrDefault("guards")));
            roles.AddRange(Enumerable.Repeat("scemo", rc.GetValueOrDefault("scemo")));
            roles.AddRange(Enumerable.Repeat("hunter", rc.GetValueOrDefault("hunter")));
            roles.AddRange(Enumerable.Repeat("witch", rc.GetValueOrDefault("witch")));
            roles.AddRange(Enumerable.Repeat("lara", rc.GetValueOrDefault("lara")));
            roles.AddRange(Enumerable.Repeat("mayor", rc.GetValueOrDefault("mayor")));
            roles.AddRange(Enumerable.Repeat("hitman", rc.GetValueOrDefault("hitman")));
            roles.AddRange(Enumerable.Repeat("medium", rc.GetValueOrDefault("medium")));

            // Ogni “coppia” = 1 romeo + 1 giulietta, cap a 2
            var couplesToMake = Math.Max(0, Math.Min(rc.GetValueOrDefault("couple"), 2));
            roles.AddRange(Enumerable.Repeat("romeo", couplesToMake));
            roles.AddRange(Enumerable.Repeat("giulietta", couplesToMake));

            return roles;
        }

        private static void AssignRoles(List<string> roles, List<Player> candidates)
        {
            Shuffle(roles);
            Shuffle(candidates);

            int n = Math.Min(candidates.Count, roles.Count);
            for (int i = 0; i < n; i++)
                candidates[i].Role = roles[i];
        }

        private static List<PlayerDto> MapPlayers(GameRoom room)
        {
            var voteCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var voteDetails = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var voter in room.Players.Where(p => !p.Eliminated && !string.IsNullOrEmpty(p.CurrentVote)))
            {
                var target = voter.CurrentVote!;
                var isMayor = Same(voter.Role, "mayor");
                var weight = isMayor ? 2 : 1;

                if (!voteCounts.ContainsKey(target)) voteCounts[target] = 0;
                voteCounts[target] += weight;

                if (!voteDetails.ContainsKey(target)) voteDetails[target] = new List<string>();
                voteDetails[target].Add(isMayor ? $"{voter.Name} (x2)" : voter.Name);
            }

            return room.Players
                .Select(p => new PlayerDto(
                    Name: p.Name,
                    IsOnline: p.IsOnline,
                    Role: p.Role ?? "",
                    Votes: voteCounts.TryGetValue(p.Name, out var c) ? c : 0,
                    Eliminated: p.Eliminated,
                    CurrentVote: p.CurrentVote,
                    VotedBy: voteDetails.TryGetValue(p.Name, out var list) ? list : new List<string>()
                ))
                .ToList();
        }

        private async Task PairCouples(GameRoom room)
        {
            var romeos = room.Players.Where(p => !p.Eliminated && Same(p.Role, "romeo")).ToList();
            var julietts = room.Players.Where(p => !p.Eliminated && Same(p.Role, "giulietta")).ToList();
            int pairs = Math.Min(romeos.Count, julietts.Count);

            for (int i = 0; i < pairs; i++)
            {
                var r = romeos[i];
                var g = julietts[i];

                // La prima coppia "governa" dove dormire (API esistente)
                if (i == 0)
                {
                    room.CoupleRomeoName = r.Name;
                    room.CoupleJulietName = g.Name;
                    room.CoupleSleepAt = "romeo";
                }

                if (!string.IsNullOrEmpty(r.ConnectionId))
                    await Clients.Client(r.ConnectionId!).SendAsync("CouplePaired", g.Name, g.Role);
                if (!string.IsNullOrEmpty(g.ConnectionId))
                    await Clients.Client(g.ConnectionId!).SendAsync("CouplePaired", r.Name, r.Role);
            }
        }

        private async Task BroadcastLobbyAndVotes(GameRoom room)
        {
            var hostOnline = IsHostOnline(room);

            await Clients.Group(room.Id).SendAsync(
                "UpdateLobby",
                ToLobbyPlayers(room),
                room.HostName,
                hostOnline
            );

            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        private async Task RevealToMediums(GameRoom room, Player dead)
        {
            var mediums = room.Players.Where(p => !p.Eliminated && Same(p.Role, "medium") && p.IsOnline && !string.IsNullOrEmpty(p.ConnectionId));
            var deadRole = string.IsNullOrWhiteSpace(dead.Role) ? "villager" : dead.Role; // fallback safe

            foreach (var m in mediums)
                await Clients.Client(m.ConnectionId!).SendAsync("MediumReveal", dead.Name, deadRole);
        }
    }

    // .NET Standard compat:
    internal static class DictExt
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key, TValue fallback = default!)
            where TKey : notnull
        {
            return dict != null && dict.TryGetValue(key, out var v) ? v : fallback;
        }
    }
}
