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
        // ---------- Restart partita ----------
        public async Task RestartGame(string roomId)
        {
            // Solo l'host della stanza può riavviare
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (room.HostConnectionId != Context.ConnectionId) return;

            // Reset stato stanza
            room.GameStarted = false;
            room.VotingOpen = false;

            foreach (var p in room.Players)
            {
                p.Role = null;
                p.CurrentVote = null;
                p.Eliminated = false;
                // p.IsOnline resta com'è (non toccarlo)
            }

            // Notifica tutti i client:
            // 1) evento dedicato per far tornare alla Lobby
            await Clients.Group(roomId).SendAsync("GameRestarted", ToLobbyPlayers(room), room.HostName);

            // 2) refresh liste/contatori coerenti con lo stato azzerato
            await Clients.Group(roomId).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
        }


        // ---------- Creazione stanza ----------
        public async Task<string> CreateRoom(string hostName)
        {
            var room = new GameRoom
            {
                HostConnectionId = Context.ConnectionId,
                HostName = hostName
            };

            Rooms[room.Id] = room;

            await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);
            await Clients.Caller.SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);

            return room.Id;
        }

        // ---------- Join stanza ----------
        public async Task<JoinResult> JoinRoom(string roomId, string name)
        {
            if (!Rooms.TryGetValue(roomId, out var room))
            {
                // stanza non esiste
                await Clients.Caller.SendAsync("JoinError", "Stanza non trovata");
                return new JoinResult(
                    Ok: false,
                    Error: "Stanza non trovata",
                    RoomId: roomId,
                    HostName: "",
                    IsHost: false,
                    GameStarted: false,
                    VotingOpen: false,
                    Role: null,
                    Players: new List<PlayerDto>()
                );
            }

            bool isHost = room.HostName.Equals(name, StringComparison.OrdinalIgnoreCase);

            // Cerca player esistente (anche se offline)
            var player = room.Players.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            // Se il nome è già online connesso da un altro client, blocca (come prima)
            if (player != null && player.IsOnline)
            {
                await Clients.Caller.SendAsync("JoinError", "Nome già presente nella stanza");
                return new JoinResult(
                    Ok: false,
                    Error: "Nome già presente nella stanza",
                    RoomId: room.Id,
                    HostName: room.HostName,
                    IsHost: false,
                    GameStarted: room.GameStarted,
                    VotingOpen: room.VotingOpen,
                    Role: null,
                    Players: MapPlayers(room)
                );
            }

            // Se chi entra è l'host
            if (isHost)
            {
                // NON aggiungo l'host a room.Players: resta fuori dall'elenco votabile
                // Se hai bisogno di tracciare "host online/offline", puoi aggiungere un flag in GameRoom,
                // altrimenti basta aggiornare la ConnectionId.
                room.HostConnectionId = Context.ConnectionId;

                await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);
                await Clients.Group(room.Id).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
                await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
                // Ribatti lo stato votazioni al caller dopo il join/rejoin
                if (room.VotingOpen)
                    await Clients.Caller.SendAsync("VotingStarted");
                else
                    await Clients.Caller.SendAsync("VotingEnded");

                return new JoinResult(
                    Ok: true,
                    Error: null,
                    RoomId: room.Id,
                    HostName: room.HostName,
                    IsHost: true,
                    GameStarted: room.GameStarted,
                    VotingOpen: room.VotingOpen,
                    Role: null, // l'host non ha ruolo
                    Players: MapPlayers(room)
                );
            }

            else
            {
                // Giocatore normale
                if (player == null)
                {
                    if (room.GameStarted)
                    {
                        await Clients.Caller.SendAsync("JoinError", "Partita già iniziata. Non puoi entrare.");
                        return new JoinResult(
                            Ok: false,
                            Error: "Partita già iniziata. Non puoi entrare.",
                            RoomId: room.Id,
                            HostName: room.HostName,
                            IsHost: false,
                            GameStarted: room.GameStarted,
                            VotingOpen: room.VotingOpen,
                            Role: null,
                            Players: MapPlayers(room)
                        );
                    }

                    player = new Player
                    {
                        Name = name,
                        ConnectionId = Context.ConnectionId,
                        IsOnline = true
                    };
                    room.Players.Add(player);
                }
                else
                {
                    player.ConnectionId = Context.ConnectionId;
                    player.IsOnline = true;
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);

                await Clients.Group(room.Id).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
                await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));

                // Se la partita è già iniziata e il giocatore rientra (caso particolare: era già dentro e si è disconnesso)
                if (room.GameStarted)
                {
                    // opzionale: puoi anche inviare gli eventi come prima
                    // await Clients.Caller.SendAsync("GameStarted", MapPlayers(room));
                    // if (!string.IsNullOrEmpty(player.Role))
                    //     await Clients.Caller.SendAsync("ReceiveRole", player.Role);
                    // if (room.VotingOpen)
                    //     await Clients.Caller.SendAsync("VotingStarted");
                    // else
                    //     await Clients.Caller.SendAsync("VotingEnded");
                }
                
                // Ribatti lo stato votazioni al caller dopo il join/rejoin
                if (room.VotingOpen)
                    await Clients.Caller.SendAsync("VotingStarted");
                else
                    await Clients.Caller.SendAsync("VotingEnded");

                return new JoinResult(
                    Ok: true,
                    Error: null,
                    RoomId: room.Id,
                    HostName: room.HostName,
                    IsHost: false,
                    GameStarted: room.GameStarted,
                    VotingOpen: room.VotingOpen,
                    Role: player.Role,
                    Players: MapPlayers(room)
                );
            }
        }

        // ---------- Disconnessione ----------
        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var room = GetRoomByConnectionId(Context.ConnectionId);
            if (room != null)
            {
                var player = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
                if (player != null)
                {
                    player.IsOnline = false;
                    // Non rimuovere l'host: resta offline
                    await Clients.Group(room.Id).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName);
                    await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
                }
            }

            await base.OnDisconnectedAsync(exception);
        }


        // ---------- Avvio gioco ----------
        public async Task StartGame(string roomId,int wolves, int villagers, int seers, int guards, int scemo, int hunter, int witch,int lara, int mayor, int hitman)
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

            // Azzeriamo voti e status
            room.GameStarted = true;
            room.VotingOpen = false;
            foreach (var p in room.Players)
                p.CurrentVote = null;

            // invio ruolo al singolo
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

            await Clients.Group(roomId).SendAsync("VotingStarted");
            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
        }

        public async Task CloseVoting(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (room.HostConnectionId != Context.ConnectionId) return;

            room.VotingOpen = false;
            await Clients.Group(roomId).SendAsync("VotingEnded");
            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
        }

        public async Task VotePlayer(string roomId, string targetName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!room.VotingOpen) return;

            var voter = room.Players.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
            if (voter == null) return;
            if (voter.Eliminated) return;

            voter.CurrentVote = targetName;
            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
        }

        // ---------- Eliminazione giocatore ----------
        public async Task EliminatePlayer(string roomId, string playerName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;

            var player = room.Players.FirstOrDefault(p => p.Name == playerName);
            if (player == null) return;

            player.Eliminated = true;

            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(roomId).SendAsync("PlayerEliminated", playerName);
        }

        // ---------- Helpers ----------
        private static List<object> ToLobbyPlayers(GameRoom room) =>
            room.Players.Select(p => new { Name = p.Name, IsOnline = p.IsOnline }).ToList<object>();

        private static List<PlayerDto> MapPlayers(GameRoom room)
        {
            // Dizionari case-insensitive
            var voteCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var voteDetails = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            // Conta i voti: il Sindaco pesa 2
            foreach (var voter in room.Players.Where(p => !p.Eliminated && !string.IsNullOrEmpty(p.CurrentVote)))
            {
                var target = voter.CurrentVote!;
                var isMayor = string.Equals(voter.Role, "mayor", StringComparison.OrdinalIgnoreCase);
                var weight = isMayor ? 2 : 1;

                if (!voteCounts.ContainsKey(target))
                    voteCounts[target] = 0;
                voteCounts[target] += weight;

                if (!voteDetails.ContainsKey(target))
                    voteDetails[target] = new List<string>();

                voteDetails[target].Add(isMayor ? $"{voter.Name} (x2)" : voter.Name);
            }

            // Mappa i giocatori verso il DTO
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


    }
}
