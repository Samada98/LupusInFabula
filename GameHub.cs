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

            // 🔴 2.1 — reset anche la Coppia
            room.CoupleRomeoName = null;
            room.CoupleJulietName = null;
            room.CoupleSleepAt = "romeo";

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

            // Invia la chiave all’host UNA sola volta
            await Clients.Caller.SendAsync("ReceiveHostKey", room.HostKey);

            // Subito dopo, manda lobby + votes con il flag hostOnline corretto
            await BroadcastLobbyAndVotes(room);

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

            // Host che rientra (richiede hostKey se offline)
            var isNameHost = Same(trimmedName, room.HostName);

            // host considerato online se la sua connectionId è ancora presente
            bool hostOnline = !string.IsNullOrEmpty(room.HostConnectionId)
                              && ConnIndex.ContainsKey(room.HostConnectionId);

            if (isNameHost)
            {
                // Se host è online e non è questa stessa connessione → blocca
                if (hostOnline && room.HostConnectionId != Context.ConnectionId)
                {
                    await Clients.Caller.SendAsync("JoinError", "Host già online in questa stanza.");
                    return new JoinResult(false, "Host già online in questa stanza.", room.Id, room.HostName,
                                          false, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
                }

                // Se host offline → richiedi hostKey
                if (!hostOnline)
                {
                    if (string.IsNullOrEmpty(hostKey) || !Same(hostKey, room.HostKey))
                    {
                        await Clients.Caller.SendAsync("JoinError", "Per rientrare come host serve l'hostKey corretta.");
                        return new JoinResult(false, "HostKey mancante o errata.", room.Id, room.HostName,
                                              false, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
                    }
                }

                // a questo punto è valido: aggiorna connessione come host
                var connIdHost = Context.ConnectionId;
                room.HostConnectionId = connIdHost;
                ConnIndex[connIdHost] = (room.Id, null, true);
                await Groups.AddToGroupAsync(connIdHost, room.Id);

                await BroadcastLobbyAndVotes(room);

                if (room.VotingOpen) await Clients.Caller.SendAsync("VotingStarted");
                else await Clients.Caller.SendAsync("VotingEnded");

                return new JoinResult(true, null, room.Id, room.HostName,
                                      true, room.GameStarted, room.VotingOpen, null, MapPlayers(room));
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
            string roomId,
            int wolves, int villagers, int seers, int guards, int scemo, int hunter, int witch, int lara, int mayor, int hitman,
            int medium, int couple)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            // 0) Reset coerente
            room.GameStarted = false;
            room.VotingOpen = false;
            foreach (var p in room.Players)
            {
                p.Role = "";
                p.CurrentVote = null;
                p.Eliminated = false;
            }

            // reset coppia (letto notturno)
            room.CoupleRomeoName = null;
            room.CoupleJulietName = null;
            room.CoupleSleepAt = "romeo";

            // 1) Costruisci TUTTI i ruoli (inclusi romeo/giulietta x2 per ogni coppia)
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
            roles.AddRange(Enumerable.Repeat("medium", medium));

            // 👇 ogni "coppia" vale due carte: 1 romeo + 1 giulietta
            int couplesToMake = Math.Max(0, Math.Min(couple, 2)); // massimo 2 coppie
            roles.AddRange(Enumerable.Repeat("romeo", couplesToMake));
            roles.AddRange(Enumerable.Repeat("giulietta", couplesToMake));

            // 2) Candidati = tutti i giocatori vivi (abbiamo appena azzerato i ruoli)
            var candidates = room.Players.Where(p => !p.Eliminated).ToList();

            // Controllo di consistenza
            if (roles.Count < candidates.Count)
            {
                await Clients.Caller.SendAsync("JoinError",
                    $"Ruoli insufficienti per i giocatori presenti. Assegnati {roles.Count}, giocatori {candidates.Count}.");
                return;
            }

            // 3) Assegna (se hai messo più ruoli del necessario, l'eccedenza viene ignorata)
            Shuffle(roles);
            Shuffle(candidates);
            int n = Math.Min(candidates.Count, roles.Count);
            for (int i = 0; i < n; i++)
                candidates[i].Role = roles[i];

            // 4) Accoppia i Romeo con le Giuliette assegnate
            var romeos = room.Players.Where(p => !p.Eliminated && Same(p.Role, "romeo")).ToList();
            var julietts = room.Players.Where(p => !p.Eliminated && Same(p.Role, "giulietta")).ToList();
            int pairs = Math.Min(romeos.Count, julietts.Count);

            for (int i = 0; i < pairs; i++)
            {
                var r = romeos[i];
                var g = julietts[i];

                // la PRIMA coppia governa il "dove dormire" (API esistente)
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

            // 5) Stato partita
            room.GameStarted = true;
            room.VotingOpen = false;

            // 6) Invia il ruolo ad ogni online
            foreach (var p in room.Players.Where(p => p.IsOnline && !string.IsNullOrEmpty(p.ConnectionId)))
                await Clients.Client(p.ConnectionId!).SendAsync("ReceiveRole", p.Role);

            // 7) Broadcast iniziale
            await Clients.Group(roomId).SendAsync("GameStarted", MapPlayers(room), room.HostName, IsHostOnline(room));
            await Clients.Group(roomId).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(roomId).SendAsync("UpdateLobby", ToLobbyPlayers(room), room.HostName, IsHostOnline(room));
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

        // ---------------------ROMEOEGIULIETTA DOVE DORMIRE
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


        // ---------- Eliminazione giocatore ----------
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
                // annulla l'uccisione
                await Clients.Group(room.Id).SendAsync("CoupleSaved", player.Name, room.CoupleRomeoName);
                await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
                return;
            }

            // Uccisione del target
            player.Eliminated = true;
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("PlayerEliminated", player.Name);
            await RevealToMediums(room, player);

            // Se muore Romeo → muoiono entrambi (anche se dormivano da Romeo)
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
        // ---------- Resurrezione giocatore ----------
        public async Task RevivePlayer(string roomId, string playerName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            var player = room.Players.FirstOrDefault(p => Same(p.Name, playerName));
            if (player == null) return;

            // annulla l’eliminazione
            player.Eliminated = false;
            // non far pesare eventuali voti precedenti del resuscitato
            player.CurrentVote = null;

            // aggiorna le UI dei client
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("PlayerRevived", playerName);
        }

        // alias opzionali (il frontend li prova come fallback)
        public Task ResurrectPlayer(string roomId, string playerName) => RevivePlayer(roomId, playerName);
        public Task UneliminatePlayer(string roomId, string playerName) => RevivePlayer(roomId, playerName);


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
        private static bool Same(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        private static bool SameConn(string? a, string b) => !string.IsNullOrEmpty(a) && a == b;
        private static string NewRoomId(int len = 2)
        {
            const string chars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // niente O/0/I/1
            var rnd = Random.Shared;
            Span<char> buf = stackalloc char[len];
            for (int i = 0; i < len; i++) buf[i] = chars[rnd.Next(chars.Length)];
            return new string(buf);
        }
        private static bool IsHostOnline(GameRoom room) =>
            !string.IsNullOrEmpty(room.HostConnectionId)
            && ConnIndex.ContainsKey(room.HostConnectionId);
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
            var deadRole = string.IsNullOrWhiteSpace(dead.Role) ? "villager" : dead.Role;
            foreach (var m in mediums)
                await Clients.Client(m.ConnectionId!).SendAsync("MediumReveal", dead.Name, deadRole);
        }

    }
}
