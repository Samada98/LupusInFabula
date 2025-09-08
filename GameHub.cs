using LupusInTabula.Models;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;


namespace LupusInTabula.Hubs
{
    public class GameHub : Hub
    {
        // Stanza → dati
        private static readonly ConcurrentDictionary<string, GameRoom> Rooms = new();

        // Connessione → (roomId, playerName, isHost)  (playerName null = host)
        private static readonly ConcurrentDictionary<string, (string roomId, string? playerName, bool isHost)> ConnIndex = new();

        private static bool Same(string a, string b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool SameConn(string? a, string b) => !string.IsNullOrEmpty(a) && a == b;

        private static string NewRoomId(int len = 2)
        {
            const string chars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // niente O/0/I/1
            var rnd = Random.Shared;
            Span<char> buf = stackalloc char[len];
            for (int i = 0; i < len; i++) buf[i] = chars[rnd.Next(chars.Length)];
            return new string(buf);
        }

        // ---------- Restart partita ----------
        public async Task RestartGame(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            room.GameStarted = false;
            room.VotingOpen = false;

            foreach (var p in room.Players)
            {
                p.Role = "";
                p.CurrentVote = null;
                p.Eliminated = false;
            }

            await Clients.Group(room.Id).SendAsync("GameRestarted", ToLobbyPlayers(room), room.HostName);
            await BroadcastLobbyAndVotes(room);
        }

        // ---------- Creazione stanza ----------
        public async Task<string> CreateRoom(string hostName)
        {
            string id;
            do { id = NewRoomId(); } while (Rooms.ContainsKey(id));

            var room = new GameRoom
            {
                Id = id,
                HostConnectionId = Context.ConnectionId,
                HostName = hostName,
                HostKey = Guid.NewGuid().ToString("N")
            };

            Rooms[id] = room;

            await Groups.AddToGroupAsync(Context.ConnectionId, id);
            ConnIndex[Context.ConnectionId] = (id, null, true);

            await Clients.Caller.SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
            await Clients.Caller.SendAsync("ReceiveHostKey", room.HostKey);

            return id;
        }

        // ---------- Join (con controllo host) ----------
        public async Task<JoinResult> JoinRoom(string roomId, string name, string hostKey = null)
        {
            if (!Rooms.TryGetValue(roomId, out var room))
            {
                await Clients.Caller.SendAsync("JoinError", "Stanza non trovata");
                return new JoinResult(false, "Stanza non trovata", roomId, "", false, false, false, null, new List<PlayerDto>());
            }

            var connId = Context.ConnectionId;
            var trimmedName = name?.Trim() ?? "";

            // Host che rientra (solo con hostKey valida)
            var isNameHost = Same(trimmedName, room.HostName);
            if (isNameHost)
            {
                if (!string.IsNullOrEmpty(hostKey) && Same(hostKey, room.HostKey))
                {
                    room.HostConnectionId = connId;
                    ConnIndex[connId] = (room.Id, null, true);
                    await Groups.AddToGroupAsync(connId, room.Id);

                    await BroadcastLobbyAndVotes(room);

                    if (room.VotingOpen) await Clients.Caller.SendAsync("VotingStarted");
                    else await Clients.Caller.SendAsync("VotingEnded");

                    return new JoinResult(true, null, room.Id, room.HostName, true, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
                }

                await Clients.Caller.SendAsync("JoinError", "Il nome dell'host è riservato.");
                return new JoinResult(false, "Il nome dell'host è riservato.", room.Id, room.HostName, false, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
            }

            // Giocatore
            var player = room.Players.FirstOrDefault(p => Same(p.Name, trimmedName));

            if (player != null && player.IsOnline && player.ConnectionId != connId)
            {
                await Clients.Caller.SendAsync("JoinError", "Nome già presente nella stanza");
                return new JoinResult(false, "Nome già presente nella stanza", room.Id, room.HostName, false, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
            }

            if (player == null)
            {
                if (room.GameStarted)
                {
                    await Clients.Caller.SendAsync("JoinError", "Partita già iniziata. Non puoi entrare.");
                    return new JoinResult(false, "Partita già iniziata. Non puoi entrare.", room.Id, room.HostName, false, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
                }

                player = new Player
                {
                    Name = trimmedName,
                    ConnectionId = connId,
                    IsOnline = true
                };
                room.Players.Add(player);
            }
            else
            {
                // riconnessione
                player.ConnectionId = connId;
                player.IsOnline = true;
                if (!string.IsNullOrEmpty(player.Role))
                {
                    await Clients.Caller.SendAsync("ReceiveRole", player.Role);
                }
            }

            ConnIndex[connId] = (room.Id, player.Name, false);
            await Groups.AddToGroupAsync(connId, room.Id);

            await BroadcastLobbyAndVotes(room);

            if (room.VotingOpen) await Clients.Caller.SendAsync("VotingStarted");
            else await Clients.Caller.SendAsync("VotingEnded");

            return new JoinResult(true, null, room.Id, room.HostName, false, room.GameStarted, room.VotingOpen, player.Role, MapPlayers(room));
        }

        // ---------- Disconnessione ----------
        public override async Task OnDisconnectedAsync(Exception exception)
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

        // ---------- Avvio gioco ----------
        public async Task StartGame(
            string roomId, int wolves, int villagers, int seers, int guards, int scemo, int hunter, int witch, int lara, int mayor, int hitman)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            // 1) Ruoli
            var roles = new List<string>();
            roles.AddRange(Enumerable.Repeat("wolf", wolves));
            roles.AddRange(Enumerable.Repeat("villager", villagers));
            roles.AddRange(Enumerable.Repeat("seer", seers));
            roles.AddRange(Enumerable.Repeat("guard", guards));
            roles.AddRange(Enumerable.Repeat("scemo", scemo));
            roles.AddRange(Enumerable.Repeat("hunter", hunter));
            roles.AddRange(Enumerable.Repeat("witch", witch));
            roles.AddRange(Enumerable.Repeat("lara", lara));
            roles.AddRange(Enumerable.Repeat("mayor", mayor));
            roles.AddRange(Enumerable.Repeat("hitman", hitman));

            // 2) Candidati (online e non eliminati)
            var candidates = room.Players.Where(p => !p.Eliminated).ToList();
            if (roles.Count < candidates.Count)
            {
                await Clients.Caller.SendAsync("JoinError", "Ruoli insufficienti per i giocatori presenti.");
                return;
            }

            // 3) Reset ruoli
            foreach (var p in room.Players) p.Role = "";

            // 4) Shuffle (crypto-uniforme, in place)
            Shuffle(roles);
            Shuffle(candidates);

            // 5) Assegna
            int n = Math.Min(candidates.Count, roles.Count);
            for (int i = 0; i < n; i++)
                candidates[i].Role = roles[i];


            // 6) Stato partita
            room.GameStarted = true;
            room.VotingOpen = false;
            foreach (var p in room.Players) p.CurrentVote = null;

            // 7) Invia ruolo ai soli online
            foreach (var p in room.Players.Where(p => p.IsOnline && !string.IsNullOrEmpty(p.ConnectionId)))
                await Clients.Client(p.ConnectionId!).SendAsync("ReceiveRole", p.Role);

            // 8) Aggiorna tutti
            await Clients.Group(roomId).SendAsync("GameStarted", MapPlayers(room));
            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
        }

        // ---------- Votazioni ----------
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

        // Accetta null/"" come "togli voto"
        public async Task VotePlayer(string roomId, string targetName)
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

        // ---------- Eliminazione giocatore ----------
        public async Task EliminatePlayer(string roomId, string playerName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            var player = room.Players.FirstOrDefault(p => Same(p.Name, playerName));
            if (player == null) return;

            player.Eliminated = true;

            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("PlayerEliminated", playerName);
        }

        // ---------- Espulsione (solo lobby, solo host) ----------
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

        // ---------- Info stanza per host (opzionale) ----------
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

        // ---------- Keep-alive ----------
        public Task Heartbeat() => Task.CompletedTask;

        // ---------- Uscita esplicita ----------
        public async Task LeaveRoom(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;

            if (SameConn(room.HostConnectionId, Context.ConnectionId))
            {
                room.HostConnectionId = null;
                ConnIndex.TryRemove(Context.ConnectionId, out _);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
                await BroadcastLobbyAndVotes(room);
                return;
            }

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

        // ---------- Helpers ----------
        private static List<object> ToLobbyPlayers(GameRoom room) =>
            room.Players
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(p => new { Name = p.Name, IsOnline = p.IsOnline })
                .ToList<object>();
        private static void Shuffle<T>(IList<T> list)
        {
            // Fisher–Yates/Durstenfeld con RNG crittografico
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = RandomNumberGenerator.GetInt32(i + 1); // 0..i inclusi
                (list[i], list[j]) = (list[j], list[i]);
            }
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

        private async Task BroadcastLobbyAndVotes(GameRoom room)
        {
            await Clients.Group(room.Id).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }
    }
}
