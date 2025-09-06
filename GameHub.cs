using LupusInTabula.Models;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LupusInTabula.Hubs
{
    public class GameHub : Hub
    {
        private static readonly ConcurrentDictionary<string, GameRoom> Rooms = new();

        private static bool Same(string a, string b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        // ---------- Restart partita ----------
        public async Task RestartGame(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (room.HostConnectionId != Context.ConnectionId) return;

            room.GameStarted = false;
            room.VotingOpen = false;

            foreach (var p in room.Players)
            {
                p.Role = null;
                p.CurrentVote = null;
                p.Eliminated = false;
                // p.IsOnline resta com'è
            }

            await Clients.Group(roomId).SendAsync("GameRestarted", ToLobbyPlayers(room), room.HostName);
            await Clients.Group(roomId).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
        }

        // ---------- Creazione stanza ----------
        public async Task<string> CreateRoom(string hostName)
        {
            var room = new GameRoom
            {
                HostConnectionId = Context.ConnectionId,
                HostName = hostName,
                HostKey = Guid.NewGuid().ToString("N") // 🔑 chiave segreta host
            };

            Rooms[room.Id] = room;

            await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);

            // Invia subito lobby all’host
            await Clients.Caller.SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);

            // Invia la hostKey SOLO all’host che ha creato (il tuo client la deve salvare)
            await Clients.Caller.SendAsync("ReceiveHostKey", room.HostKey);

            return room.Id;
        }

        // ---------- Join (con controllo host) ----------
        // AGGIUNTA: hostKey opzionale per validare il "vero" host
        public async Task<JoinResult> JoinRoom(string roomId, string name, string hostKey = null)
        {
            if (!Rooms.TryGetValue(roomId, out var room))
            {
                await Clients.Caller.SendAsync("JoinError", "Stanza non trovata");
                return new JoinResult(false, "Stanza non trovata", roomId, "", false, false, false, null, new List<PlayerDto>());
            }

            bool isHostName = string.Equals(name?.Trim(), room.HostName?.Trim(), StringComparison.OrdinalIgnoreCase);

            // ---- Nome host: consenti solo se hostKey valida (host vero), altrimenti blocca
            if (isHostName)
            {
                if (!string.IsNullOrEmpty(hostKey) &&
                    string.Equals(hostKey, room.HostKey, StringComparison.OrdinalIgnoreCase))
                {
                    // rientro host: riaggancia/sostituisci
                    room.HostConnectionId = Context.ConnectionId;
                    await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);

                    await Clients.Group(room.Id).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
                    await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
                    if (room.VotingOpen) await Clients.Caller.SendAsync("VotingStarted");
                    else await Clients.Caller.SendAsync("VotingEnded");

                    return new JoinResult(true, null, room.Id, room.HostName, true, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
                }

                await Clients.Caller.SendAsync("JoinError", "Il nome dell'host è riservato.");
                return new JoinResult(false, "Il nome dell'host è riservato.", room.Id, room.HostName, false, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
            }

            // ---- Nome NON host: gestione normale con anti-duplicati
            var player = room.Players.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (player != null && player.IsOnline && player.ConnectionId != Context.ConnectionId)
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
                    Name = name.Trim(),
                    ConnectionId = Context.ConnectionId,
                    IsOnline = true
                };
                room.Players.Add(player);
            }
            else
            {
                // riconnessione stesso nome
                player.ConnectionId = Context.ConnectionId;
                player.IsOnline = true;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);

            await Clients.Group(room.Id).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));

            if (room.VotingOpen) await Clients.Caller.SendAsync("VotingStarted");
            else await Clients.Caller.SendAsync("VotingEnded");

            return new JoinResult(true, null, room.Id, room.HostName, false, room.GameStarted, room.VotingOpen, player.Role, MapPlayers(room));
        }

        // ---------- Disconnessione ----------
        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var room = GetRoomByConnectionId(Context.ConnectionId);
            if (room != null)
            {
                // Host che chiude: NON azzeriamo nulla, semplicemente resta senza connessione
                if (room.HostConnectionId == Context.ConnectionId)
                {
                    // l’host risulterà “offline” lato UI se lo mostri tra i players (di default non è nella lista)
                }
                else
                {
                    var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
                    if (player != null)
                    {
                        player.IsOnline = false;
                    }
                }

                await Clients.Group(room.Id).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
                await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            }

            await base.OnDisconnectedAsync(exception);
        }

        // ---------- Avvio gioco ----------
        public async Task StartGame(string roomId, int wolves, int villagers, int seers, int guards, int scemo, int hunter, int witch, int lara, int mayor, int hitman)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (room.HostConnectionId != Context.ConnectionId) return;

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

            var rnd = new Random();
            roles = roles.OrderBy(_ => rnd.Next()).ToList();

            for (int i = 0; i < room.Players.Count && i < roles.Count; i++)
                room.Players[i].Role = roles[i];

            room.GameStarted = true;
            room.VotingOpen = false;
            foreach (var p in room.Players)
                p.CurrentVote = null;

            foreach (var p in room.Players.Where(p => p.IsOnline))
                await Clients.Client(p.ConnectionId).SendAsync("ReceiveRole", p.Role);

            await Clients.Group(roomId).SendAsync("GameStarted", MapPlayers(room));
            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
        }

        // ---------- Votazioni ----------
        public async Task OpenVoting(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (room.HostConnectionId != Context.ConnectionId) return;

            room.VotingOpen = true;
            foreach (var p in room.Players) p.CurrentVote = null;

            await Clients.Group(room.Id).SendAsync("VotingStarted");
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        public async Task CloseVoting(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (room.HostConnectionId != Context.ConnectionId) return;

            room.VotingOpen = false;
            await Clients.Group(room.Id).SendAsync("VotingEnded");
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        public async Task VotePlayer(string roomId, string targetName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!room.VotingOpen) return;

            var voter = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (voter == null || voter.Eliminated) return;

            voter.CurrentVote = targetName;
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        // ---------- Eliminazione giocatore ----------
        public async Task EliminatePlayer(string roomId, string playerName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;

            var player = room.Players.FirstOrDefault(p => p.Name == playerName);
            if (player == null) return;

            player.Eliminated = true;

            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("PlayerEliminated", playerName);
        }

        // ---------- Helpers ----------
        private static List<object> ToLobbyPlayers(GameRoom room) =>
            room.Players.Select(p => new { Name = p.Name, IsOnline = p.IsOnline }).ToList<object>();

        private static List<PlayerDto> MapPlayers(GameRoom room)
        {
            var voteCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var voteDetails = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var voter in room.Players.Where(p => !p.Eliminated && !string.IsNullOrEmpty(p.CurrentVote)))
            {
                var target = voter.CurrentVote!;
                var isMayor = string.Equals(voter.Role, "mayor", StringComparison.OrdinalIgnoreCase);
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

        private GameRoom GetRoomByConnectionId(string connectionId)
        {
            return Rooms.Values.FirstOrDefault(r =>
                r.HostConnectionId == connectionId ||
                r.Players.Any(p => p.ConnectionId == connectionId)
            );
        }

        // Info stanza per host (resta invariato)
        public async Task SendHostRoomInfo(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (room.HostConnectionId != Context.ConnectionId) return;

            var roomInfo = new
            {
                RoomId = room.Id,
                Players = room.Players.Select(p => new { Name = p.Name, IsOnline = p.IsOnline }).ToList()
            };

            await Clients.Caller.SendAsync("ReceiveHostRoomInfo", roomInfo);
        }
    }
}
