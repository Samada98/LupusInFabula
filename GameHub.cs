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
        // RoomId -> connessioni che hanno sbloccato l'audio nel browser
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> AudioReady = new();

        // =========================================================
        // =============== LIFECYCLE & CONNESSIONI =================
        // =========================================================

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (ConnIndex.TryRemove(Context.ConnectionId, out var info)
                && Rooms.TryGetValue(info.roomId, out var room))
            {
                RemoveAudioReady(info.roomId, Context.ConnectionId);

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
                RemoveAudioReady(roomId, Context.ConnectionId);
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
            RemoveAudioReady(roomId, Context.ConnectionId);
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
            ResetNight(room, resetCounter: true);

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
            room.WitchSavePotionUsed = false;
            room.WitchKillPotionUsed = false;

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
            ResetNight(room, resetCounter: true);
            foreach (var p in room.Players)
            {
                p.Role = "";
                p.CurrentVote = null;
                p.Eliminated = false;
            }
            room.CoupleRomeoName = null;
            room.CoupleJulietName = null;
            room.CoupleSleepAt = "romeo";
            room.WitchSavePotionUsed = false;
            room.WitchKillPotionUsed = false;

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
            AssignRoles(roles, candidates, room);

            // 4) Accoppia le coppie
            await PairCouples(room);

            // 5) Stato partita
            room.GameStarted = true;
            room.VotingOpen = false;
            ResetNight(room, resetCounter: true);

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

            await EliminatePlayerCore(room, player, "popolo");
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

        public async Task BeginNight(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;
            if (!room.GameStarted) return;

            room.NightInProgress = true;
            room.NightNumber++;
            room.NightWolfTarget = null;
            room.NightProtectedTarget = null;
            room.NightWitchSave = false;
            room.NightWitchKillTarget = null;
            room.VotingOpen = false;
            foreach (var p in room.Players) p.CurrentVote = null;

            var state = BuildNightState(room);
            await Clients.Group(room.Id).SendAsync("NightStarted", state);
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

        public async Task<object?> SetNightChoice(string roomId, string choiceType, string targetName)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return null;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return null;
            if (!room.NightInProgress) return null;

            choiceType = (choiceType ?? "").Trim().ToLowerInvariant();
            targetName = (targetName ?? "").Trim();

            if (choiceType == "couplehouse")
            {
                var house = targetName.ToLowerInvariant();
                if (house != "romeo" && house != "giulietta") return null;

                room.CoupleSleepAt = house;
                var state = BuildNightState(room);
                await Clients.Caller.SendAsync("NightChoiceUpdated", state);
                return state;
            }

            if (choiceType == "witchsave")
            {
                room.NightWitchSave = !room.WitchSavePotionUsed && Same(targetName, "save");
                var state = BuildNightState(room);
                await Clients.Caller.SendAsync("NightChoiceUpdated", state);
                return state;
            }

            if (choiceType == "witchkill")
            {
                if (room.WitchKillPotionUsed || string.IsNullOrWhiteSpace(targetName) || Same(targetName, "skip"))
                {
                    room.NightWitchKillTarget = null;
                    var state = BuildNightState(room);
                    await Clients.Caller.SendAsync("NightChoiceUpdated", state);
                    return state;
                }

                var poisonTarget = room.Players.FirstOrDefault(p => Same(p.Name, targetName) && !p.Eliminated);
                if (poisonTarget == null) return null;

                room.NightWitchKillTarget = poisonTarget.Name;
                var poisonState = BuildNightState(room);
                await Clients.Caller.SendAsync("NightChoiceUpdated", poisonState);
                return poisonState;
            }

            var target = room.Players.FirstOrDefault(p => Same(p.Name, targetName) && !p.Eliminated);
            if (target == null) return null;

            if (choiceType == "wolf")
            {
                room.NightWolfTarget = target.Name;
                var state = BuildNightState(room);
                await Clients.Caller.SendAsync("NightChoiceUpdated", state);
                return state;
            }

            if (choiceType == "protect")
            {
                room.NightProtectedTarget = target.Name;
                var state = BuildNightState(room);
                await Clients.Caller.SendAsync("NightChoiceUpdated", state);
                return state;
            }

            if (choiceType == "seer")
            {
                var result = new
                {
                    target = target.Name,
                    role = target.Role,
                    isWolf = Same(target.Role, "wolf")
                };
                await Clients.Caller.SendAsync("NightSeerResult", result);
                return result;
            }

            return null;
        }

        public async Task EndNight(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;
            if (!room.NightInProgress) return;

            var wolfTarget = room.NightWolfTarget;
            var protectedTarget = room.NightProtectedTarget;
            var messages = new List<string>();

            if (string.IsNullOrWhiteSpace(wolfTarget))
            {
                messages.Add("I lupi non hanno scelto nessuna vittima.");
            }
            else if (!string.IsNullOrWhiteSpace(protectedTarget) && Same(wolfTarget, protectedTarget))
            {
                messages.Add($"{wolfTarget} è stato protetto: l'attacco dei lupi è stato annullato.");
            }
            else if (room.NightWitchSave)
            {
                room.WitchSavePotionUsed = true;
                messages.Add($"La Strega ha salvato {wolfTarget}: l'attacco dei lupi è stato annullato.");
            }
            else
            {
                var target = room.Players.FirstOrDefault(p => Same(p.Name, wolfTarget));
                if (target == null || target.Eliminated)
                {
                    messages.Add("I lupi hanno trovato una strada vuota: nessuna vittima.");
                }
                else
                {
                    var coupleResult = await ResolveCoupleHouseAttack(room, target);
                    if (coupleResult.handled)
                    {
                        messages.Add(coupleResult.message);
                    }
                    else if (Same(target.Role, "lara"))
                    {
                        target.Role = "wolf";
                        messages.Add($"{target.Name} è stata morsa dai lupi e ora gioca con loro.");
                        if (!string.IsNullOrWhiteSpace(target.ConnectionId))
                            await Clients.Client(target.ConnectionId!).SendAsync("ReceiveRole", target.Role);
                    }
                    else
                    {
                        await EliminatePlayerCore(room, target, "lupi");
                        messages.Add($"{target.Name} non si è svegliato.");
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(room.NightWitchKillTarget))
            {
                var poisonTarget = room.Players.FirstOrDefault(p => Same(p.Name, room.NightWitchKillTarget));
                if (poisonTarget != null && !poisonTarget.Eliminated)
                {
                    room.WitchKillPotionUsed = true;
                    await EliminatePlayerCore(room, poisonTarget, "strega");
                    messages.Add($"La Strega ha usato il veleno su {poisonTarget.Name}.");
                }
            }

            var message = messages.Count == 0
                ? "La notte passa senza vittime."
                : string.Join(" ", messages);

            ResetNight(room, resetCounter: false);
            await Clients.Group(room.Id).SendAsync("NightEnded", message, MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
        }

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

        public async Task JukeboxPlay(string roomId, string preset, double volume)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            preset = (preset ?? "").Trim();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "bastaldo", "fikaculo", "gallo", "muniz", "profondoRosso"
            };
            if (!allowed.Contains(preset)) return;

            volume = Math.Clamp(volume, 0, 1);
            await Clients.Group(room.Id).SendAsync("JukeboxPlay", preset, volume);
            await Clients.Group(room.Id).SendAsync("JukeboxPlayLocal", preset, volume, false);
        }

        public async Task JukeboxStop(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            await Clients.Group(room.Id).SendAsync("JukeboxStop");
        }

        public Task JukeboxAudioReady(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return Task.CompletedTask;

            var connId = Context.ConnectionId;
            var isHost = SameConn(room.HostConnectionId, connId);
            var isPlayer = room.Players.Any(p => SameConn(p.ConnectionId, connId));
            if (!isHost && !isPlayer) return Task.CompletedTask;

            AudioReady.GetOrAdd(room.Id, _ => new ConcurrentDictionary<string, byte>())[connId] = 1;
            return Task.CompletedTask;
        }

        public async Task JukeboxSetHostRandomAudio(string roomId, bool enabled)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            room.HostAvailableForJukeboxRandom = enabled;
            await Clients.Caller.SendAsync("JukeboxHostRandomAudioChanged", enabled);
        }

        public async Task JukeboxStartNight(string roomId)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            var targetConn = PickRandomAudioTarget(room);
            if (string.IsNullOrEmpty(targetConn))
            {
                await Clients.Caller.SendAsync("JukeboxNoAudioTargets");
                return;
            }

            await Clients.Client(targetConn).SendAsync("JukeboxPlayLocal", "notte/ambiente", 0.75, true);
            await Clients.Group(room.Id).SendAsync("JukeboxNightStarted");
        }

        public async Task JukeboxNightIntervention(string roomId, string sound)
        {
            if (!Rooms.TryGetValue(roomId, out var room)) return;
            if (!SameConn(room.HostConnectionId, Context.ConnectionId)) return;

            sound = (sound ?? "").Trim();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ambiente2", "gufo", "passi", "Ullulo"
            };
            if (!allowed.Contains(sound)) return;

            var targetConn = PickRandomAudioTarget(room);
            if (string.IsNullOrEmpty(targetConn))
            {
                await Clients.Caller.SendAsync("JukeboxNoAudioTargets");
                return;
            }

            await Clients.Client(targetConn).SendAsync("JukeboxPlayLocal", $"notte/{sound}", 0.95, false);
            await Clients.Group(room.Id).SendAsync("JukeboxNightIntervention", sound);
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

        private static void RemoveAudioReady(string roomId, string connId)
        {
            if (AudioReady.TryGetValue(roomId, out var ready))
                ready.TryRemove(connId, out _);
        }

        private static string? PickRandomAudioTarget(GameRoom room)
        {
            if (!AudioReady.TryGetValue(room.Id, out var ready)) return null;

            var validConnections = new HashSet<string>(StringComparer.Ordinal);
            if (room.HostAvailableForJukeboxRandom
                && !string.IsNullOrEmpty(room.HostConnectionId)
                && ConnIndex.ContainsKey(room.HostConnectionId))
                validConnections.Add(room.HostConnectionId);

            foreach (var connId in room.Players
                .Where(p => p.IsOnline && !string.IsNullOrEmpty(p.ConnectionId))
                .Select(p => p.ConnectionId!))
                validConnections.Add(connId);

            var targets = ready.Keys.Where(validConnections.Contains).ToList();

            return targets.Count == 0 ? null : targets[RandomNumberGenerator.GetInt32(targets.Count)];
        }

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

        private static void AssignRoles(List<string> roles, List<Player> candidates, GameRoom room)
        {
            int n = Math.Min(candidates.Count, roles.Count);
            if (n <= 0) return;

            var bestRoles = roles.Take(n).ToList();
            var bestCandidates = candidates.Take(n).ToList();
            var bestScore = int.MaxValue;

            // Random puro può ripetere spesso gli stessi ruoli. Proviamo più mazzi
            // e scegliamo quello che ripete meno i ruoli della partita precedente.
            var attempts = Math.Clamp(n * 18, 80, 260);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var trialRoles = roles.ToList();
                var trialCandidates = candidates.ToList();
                Shuffle(trialRoles);
                Shuffle(trialCandidates);

                var score = ScoreRoleAssignment(trialRoles, trialCandidates, room.PreviousRolesByPlayer, n);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestRoles = trialRoles.Take(n).ToList();
                    bestCandidates = trialCandidates.Take(n).ToList();
                    if (bestScore == 0) break;
                }
            }

            for (int i = 0; i < n; i++)
                bestCandidates[i].Role = bestRoles[i];

            room.PreviousRolesByPlayer = candidates
                .Where(p => !string.IsNullOrWhiteSpace(p.Role))
                .ToDictionary(p => p.Name, p => p.Role, StringComparer.OrdinalIgnoreCase);
        }

        private static int ScoreRoleAssignment(
            IReadOnlyList<string> roles,
            IReadOnlyList<Player> candidates,
            IReadOnlyDictionary<string, string> previousRoles,
            int n)
        {
            var score = 0;
            for (int i = 0; i < n; i++)
            {
                var player = candidates[i];
                var role = roles[i];
                if (!previousRoles.TryGetValue(player.Name, out var previous)) continue;

                if (Same(previous, role))
                    score += Same(role, "villager") ? 2 : 14;

                if (IsWolfTeam(previous) && IsWolfTeam(role))
                    score += 9;

                if (IsPowerRole(previous) && IsPowerRole(role))
                    score += 4;
            }

            return score;
        }

        private static bool IsWolfTeam(string role) => Same(role, "wolf");

        private static bool IsPowerRole(string role) =>
            !Same(role, "villager") && !Same(role, "wolf");

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

        private static object BuildNightState(GameRoom room) => new
        {
            active = room.NightInProgress,
            nightNumber = room.NightNumber,
            wolfTarget = room.NightWolfTarget,
            protectedTarget = room.NightProtectedTarget,
            coupleHouse = room.CoupleSleepAt,
            witchSave = room.NightWitchSave,
            witchKillTarget = room.NightWitchKillTarget,
            witchSavePotionUsed = room.WitchSavePotionUsed,
            witchKillPotionUsed = room.WitchKillPotionUsed
        };

        private static void ResetNight(GameRoom room, bool resetCounter)
        {
            room.NightInProgress = false;
            if (resetCounter) room.NightNumber = 0;
            room.NightWolfTarget = null;
            room.NightProtectedTarget = null;
            room.NightWitchSave = false;
            room.NightWitchKillTarget = null;
        }

        private async Task<(bool handled, string message)> ResolveCoupleHouseAttack(GameRoom room, Player target)
        {
            var targetIsRomeo = room.CoupleRomeoName != null && Same(target.Name, room.CoupleRomeoName);
            var targetIsJuliet = room.CoupleJulietName != null && Same(target.Name, room.CoupleJulietName);
            if (!targetIsRomeo && !targetIsJuliet) return (false, "");

            var attackedHouse = targetIsRomeo ? "romeo" : "giulietta";
            var houseName = targetIsRomeo ? "Romeo" : "Giulietta";
            var sleepingAt = Same(room.CoupleSleepAt, "giulietta") ? "giulietta" : "romeo";

            if (!Same(attackedHouse, sleepingAt))
            {
                return (true, $"I lupi hanno attaccato casa di {houseName}, ma era vuota: Romeo e Giulietta sono salvi.");
            }

            var romeo = room.Players.FirstOrDefault(p => room.CoupleRomeoName != null && Same(p.Name, room.CoupleRomeoName));
            var juliet = room.Players.FirstOrDefault(p => room.CoupleJulietName != null && Same(p.Name, room.CoupleJulietName));

            if (romeo != null && !romeo.Eliminated)
                await EliminatePlayerDirect(room, romeo, "lupi");
            if (juliet != null && !juliet.Eliminated)
                await EliminatePlayerDirect(room, juliet, "lupi");

            await Clients.Group(room.Id).SendAsync("CoupleDied", room.CoupleRomeoName, room.CoupleJulietName);
            return (true, "I lupi hanno trovato Romeo e Giulietta nella stessa casa: muoiono entrambi.");
        }

        private async Task EliminatePlayerDirect(GameRoom room, Player player, string reason)
        {
            player.Eliminated = true;
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("PlayerEliminated", player.Name, reason);
            await RevealToMediums(room, player);
        }

        private async Task EliminatePlayerCore(GameRoom room, Player player, string reason)
        {
            var isJuliet = room.CoupleJulietName != null && Same(player.Name, room.CoupleJulietName);
            var isRomeo = room.CoupleRomeoName != null && Same(player.Name, room.CoupleRomeoName);

            // Giulietta protetta se dorme da Romeo.
            if (isJuliet && Same(room.CoupleSleepAt, "romeo"))
            {
                await Clients.Group(room.Id).SendAsync("CoupleSaved", player.Name, room.CoupleRomeoName);
                await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
                return;
            }

            player.Eliminated = true;
            await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
            await Clients.Group(room.Id).SendAsync("PlayerEliminated", player.Name, reason);
            await RevealToMediums(room, player);

            // Se muore Romeo muore anche Giulietta.
            if (isRomeo && room.CoupleJulietName != null)
            {
                var juliet = room.Players.FirstOrDefault(p => Same(p.Name, room.CoupleJulietName));
                if (juliet != null && !juliet.Eliminated)
                {
                    juliet.Eliminated = true;
                    await Clients.Group(room.Id).SendAsync("UpdateVotes", MapPlayers(room));
                    await Clients.Group(room.Id).SendAsync("PlayerEliminated", juliet.Name, reason);
                    await RevealToMediums(room, juliet);
                    await Clients.Group(room.Id).SendAsync("CoupleDied", room.CoupleRomeoName, room.CoupleJulietName);
                }
            }
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
