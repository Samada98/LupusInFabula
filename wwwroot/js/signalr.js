// js/signalr.js
import { state, HUB_URL, applyNameMapping, storage } from "./state.js";
import {
    showScreen, hardResetUI, applyRoleUI, renderRolesGuide, renderHostTable, renderPlayers,
    logLobby, logGame, updateLobbyCounters, setRoomLabels, setHostNames, refreshInviteUI
} from "./ui.js";

/* ---------- Connessione & heartbeat ---------- */
export async function ensureConnection() {
    if (state.connection && state.connection.state === signalR.HubConnectionState.Connected) return true;
    if (!state.connection) {
        state.connection = new signalR.HubConnectionBuilder()
            .withUrl(HUB_URL)
            .withAutomaticReconnect([0, 1000, 5000, 10000, 20000, 30000])
            .build();
        state.connection.serverTimeoutInMilliseconds = 120000;
        state.connection.keepAliveIntervalInMilliseconds = 15000;
        registerHandlers();
    }
    try {
        await state.connection.start();
        logLobby("? Connesso al server");
        return true;
    } catch (err) {
        console.error("Connessione fallita:", err);
        logLobby("? Server non raggiungibile (controlla percorso /gamehub). Riprova.");
        return false;
    }
}

function startHeartbeat() {
    if (state.hbTimer) return;
    state.hbTimer = setInterval(() => {
        if (state.connection?.state === signalR.HubConnectionState.Connected) {
            state.connection.invoke("Heartbeat").catch(() => { });
        }
    }, 20000);
}
function stopHeartbeat() { if (state.hbTimer) { clearInterval(state.hbTimer); state.hbTimer = null; } }

/* ---------- Handlers di connessione ---------- */
function registerHandlers() {
    const c = state.connection;

    c.onreconnecting(() => { });
    c.onreconnected(async () => {
        const rid = storage.roomId;
        const nm = storage.name;
        const auto = storage.autoJoin;
        if (auto && rid && nm) {
            try { await joinRoom(rid, nm); } catch { }
        }
    });
    c.onclose(() => { });

    /* ===== Hub events ===== */
    c.on("UpdateLobby", (players, hostName) => {
        setHostNames(hostName);
        const list = document.getElementById("playerList");
        if (list) {
            list.innerHTML = "";
            players.forEach(p => {
                const pname = p.name ?? p.Name ?? "";
                const online = (typeof p.isOnline === "boolean") ? p.isOnline : !!p.IsOnline;

                const d = document.createElement("div");
                d.classList.add("playerCard", online ? "online" : "offline");

                const nameSpan = document.createElement("span");
                nameSpan.textContent = `${pname || "(sconosciuto)"} ${online ? "" : "(offline)"} `;
                d.appendChild(nameSpan);

                if (state.isHost && !state.gameStarted && pname) {
                    const btn = document.createElement("button");
                    btn.className = "kickBtn";
                    btn.title = `Espelli ${pname}`;
                    btn.textContent = "?";
                    btn.addEventListener('click', () => kickPlayer(pname));
                    d.appendChild(btn);
                }
                list.appendChild(d);
            });
        }

        state.currentPlayers = players || [];
        updateLobbyCounters(state.currentPlayers);
        renderRolesGuide("rolesGuideHost");

        if (!state.gameStarted) showScreen("screenLobby");
        applyRoleUI();
        setRoomLabels();
        refreshInviteUI(state.currentRoomId);
    });

    c.on("JoinError", (msg) => logLobby(`? ${msg}`));

    c.on("ReceiveRole", role => { state.myRole = role; applyRoleUI(); });

    c.on("VotingStarted", () => {
        state.votingOpen = true; logGame("??? Votazioni aperte!");
        renderPlayers(state.currentPlayers);
        applyRoleUI();
    });

    c.on("VotingEnded", () => {
        state.votingOpen = false; state.myVote = null; logGame("?? Votazioni chiuse!");
        renderPlayers(state.currentPlayers);
        applyRoleUI();
    });

    c.on("UpdateVotes", (players) => {
        state.currentPlayers = players || [];
        if (state.myName) {
            let mine = null;
            for (const p of state.currentPlayers) {
                if (!p.eliminated && Array.isArray(p.votedBy) && p.votedBy.includes(state.myName)) { mine = p.name; break; }
            }
            state.myVote = mine;
        }
        renderPlayers(state.currentPlayers);
        if (state.isHost) renderHostTable(state.currentPlayers);
    });

    c.on("PlayerKicked", (playerName) => { logLobby(`?? ${playerName} è stato espulso dall'host.`); });

    c.on("PlayerEliminated", (playerName) => {
        const player = state.currentPlayers.find(p => p.name === playerName);
        if (player) player.eliminated = true;
        if (state.myVote === playerName) state.myVote = null;
        renderHostTable(state.currentPlayers);
        renderPlayers(state.currentPlayers);
        logGame(`?? ${playerName} è stato eliminato!`);
    });

    c.on("GameRestarted", (players, hostName) => {
        state.gameStarted = false; state.votingOpen = false; state.myVote = null; state.myRole = null; state.currentPlayers = players || [];
        document.getElementById("overlay").style.display = "none";
        document.getElementById("roleCard").style.display = "none";
        applyRoleUI();

        setHostNames(hostName);
        (document.getElementById("playersGrid") || {}).innerHTML = "";
        (document.getElementById("gameLog") || {}).innerHTML = "";
        (document.getElementById("gameLogPlayer") || {}).innerHTML = "";
        (document.getElementById("playersTable") || {}).innerHTML = "";

        updateLobbyCounters(players || []);
        showScreen("screenLobby");
        applyRoleUI();

        logLobby("?? La partita è stata riavviata, siete tornati in lobby.");
        renderRolesGuide("rolesGuideHost");
        setRoomLabels();
        refreshInviteUI(state.currentRoomId);
    });

    c.on("GameStarted", (players) => {
        state.gameStarted = true; state.myVote = null; state.currentPlayers = players || [];

        if (state.isHost) { showScreen("screenGame"); renderHostTable(state.currentPlayers); }
        else { showScreen("screenGamePlayer"); renderPlayers(state.currentPlayers); renderRolesGuide("rolesGuidePlayer"); }

        refreshInviteUI(state.currentRoomId);
        setRoomLabels();
        applyRoleUI();
    });

    c.on("ReceiveHostKey", (hostKey) => { storage.hostKey = hostKey; });

    c.on("Kicked", () => {
        alert("Sei stato espulso dalla stanza dall'host.");
        exitToLogin();
    });
}

/* ---------- Azioni invocabili ---------- */
export async function createRoom(inputName) {
    const name = applyNameMapping(inputName || "Host");
    state.myName = name; state.isHost = true;

    const ok = await ensureConnection();
    if (!ok) { alert("Server non raggiungibile. Verifica /gamehub."); return; }

    try {
        const id = await state.connection.invoke("CreateRoom", name);
        state.currentRoomId = id;
        storage.roomId = id;
        storage.name = name;
        storage.autoJoin = true;
        storage.hostRoomId = id;

        (document.getElementById("hostNameText") || {}).textContent = name;
        setRoomLabels();
        logLobby(`?? Stanza creata: ${state.currentRoomId}`);

        showScreen("screenLobby");
        renderRolesGuide("rolesGuideHost");
        applyRoleUI();
        refreshInviteUI(state.currentRoomId);
    } catch (err) {
        console.error(err);
        logLobby("? Errore nella creazione della stanza");
        alert("Errore nella creazione della stanza");
    }
}

export async function joinRoom(roomIdInput, nameInput) {
    const name = applyNameMapping(nameInput || "Giocatore");
    state.myName = name;

    const roomId = (roomIdInput || "").trim();
    if (!roomId) { alert("Inserisci l'ID stanza"); return; }

    // neutral UI/state per evitare mischiamenti
    state.isJoining = true;
    state.isHost = false;
    hardResetUI();

    const ok = await ensureConnection();
    if (!ok) { logLobby("? Non connesso al server."); state.isJoining = false; return; }

    state.currentRoomId = roomId;
    state.myRole = null; state.votingOpen = false; state.myVote = null;

    const savedHostName = (storage.name || "").trim().toLowerCase();
    const hostKey = storage.hostKey || null;
    const storedHostRoomId = storage.hostRoomId || null;
    const isHostName = (name.trim().toLowerCase() === savedHostName);
    const hostKeyToSend = (isHostName && storedHostRoomId && storedHostRoomId === roomId) ? hostKey : null;

    try {
        const result = await state.connection.invoke("JoinRoom", roomId, name, hostKeyToSend);
        if (result && result.ok === false) { alert(result.error || "Impossibile entrare nella stanza."); state.isJoining = false; return; }

        state.isHost = !!result.isHost;
        state.myRole = result.role || null;
        state.currentPlayers = result.players || [];
        state.votingOpen = !!result.votingOpen;
        state.gameStarted = !!result.gameStarted;

        storage.roomId = state.currentRoomId;
        storage.name = name;
        storage.autoJoin = true;

        if (state.isHost) {
            if (state.gameStarted) { showScreen("screenGame"); renderHostTable(state.currentPlayers); }
            else { showScreen("screenLobby"); }
        } else {
            if (state.gameStarted) { showScreen("screenGamePlayer"); renderPlayers(state.currentPlayers); }
            else { showScreen("screenLobby"); }
        }

        if (state.myRole) {
            const roleBtn = document.getElementById("showRoleBtn");
            if (roleBtn) roleBtn.style.display = "inline-block";
        }

        updateLobbyCounters(state.currentPlayers);
        setRoomLabels();
        refreshInviteUI(state.currentRoomId);
        applyRoleUI();
        startHeartbeat();
    } catch (err) {
        console.error(err);
        logLobby("? Errore durante il join");
    } finally {
        state.isJoining = false;
    }
}

export async function startGame() {
    if (!state.isHost) { alert("? Solo l'host può avviare la partita!"); return; }
    const r = getRoleCountsFromUI();
    const totalRoles = Object.values(r).reduce((a, b) => a + b, 0);
    if (totalRoles < state.currentPlayers.length) { alert("? Ruoli insufficienti!"); return; }
    try {
        await state.connection.invoke("StartGame", state.currentRoomId, r.wolves, r.villagers, r.seers, r.guards, r.scemo, r.hunter, r.witch, r.lara, r.mayor, r.hitman, r.medium, r.couple);
        logLobby("?? Partita avviata!");
    } catch (err) { console.error(err); logLobby("? Errore nell'avvio della partita"); }
}

export async function restartGame() {
    if (!state.isHost) return;
    if (!confirm("Vuoi davvero riavviare la partita e tornare in lobby?")) return;
    try { await state.connection.invoke("RestartGame", state.currentRoomId); }
    catch (err) { console.error(err); alert("Errore durante il riavvio della partita"); }
}

export function toggleVoting() {
    if (!state.isHost) return;
    if (!state.votingOpen) state.connection.invoke("OpenVoting", state.currentRoomId).catch(console.error);
    else state.connection.invoke("CloseVoting", state.currentRoomId).catch(console.error);
}

export async function eliminatePlayer(playerName) {
    if (!state.isHost) return;
    if (!confirm(`Sei sicuro di eliminare ${playerName}?`)) return;
    try { await state.connection.invoke("EliminatePlayer", state.currentRoomId, playerName); }
    catch (err) { console.error(err); alert("Errore durante l'eliminazione"); }
}

export async function kickPlayer(playerName) {
    if (!state.isHost || state.gameStarted) return;
    if (!playerName) return;
    if (!confirm(`Vuoi espellere ${playerName} dalla stanza?`)) return;
    try {
        await state.connection.invoke("KickPlayer", state.currentRoomId, playerName);
        logLobby(`?? Hai espulso ${playerName}`);
    } catch (err) { console.error(err); alert("Errore durante l'espulsione"); }
}

export async function exitToLogin() {
    try { if (state.currentRoomId) { try { await state.connection.invoke('LeaveRoom', state.currentRoomId); } catch { } } } catch { }
    try { await state.connection?.stop(); } catch { }

    state.currentRoomId = null; state.isHost = false; state.myRole = null;
    state.votingOpen = false; state.myVote = null; state.gameStarted = false; state.isJoining = false;

    storage.roomId = null;
    storage.autoJoin = false;
    storage.hostRoomId = null;

    hardResetUI();
    stopHeartbeat();
}

/* ---------- helpers ---------- */
function getRoleCountsFromUI() {
    const counts = {};
    document.querySelectorAll(".rolesBox .number-control").forEach(ctrl => {
        const key = ctrl.dataset.role;
        const val = parseInt(ctrl.querySelector(".count").textContent) || 0;
        counts[key] = val;
    });
    // normalizza nomi coerenti con server
    return {
        wolves: counts.wolves || 0,
        villagers: counts.villagers || 0,
        seers: counts.seers || 0,
        guards: counts.guards || 0,
        scemo: counts.scemo || 0,
        hunter: counts.hunter || 0,
        witch: counts.witch || 0,
        lara: counts.lara || 0,
        mayor: counts.mayor || 0,
        hitman: counts.hitman || 0,
        medium: || count.medium || 0,
        couple: count.couple || 0
    };
}
