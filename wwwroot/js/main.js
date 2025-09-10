// js/main.js
import { state, storage, applyNameMapping } from "./state.js";
import {
    showScreen, initRoleModal, renderRolesGuide, initRoleCounters,
    copyInvite, shareInvite, logLobby, applyRoleUI, hardResetUI
} from "./ui.js";
import {
    ensureConnection, createRoom, joinRoom, startGame, toggleVoting, restartGame,
    exitToLogin, eliminatePlayer
} from "./signalr.js";

/* ---------- SignalR fallback loader ---------- */
function ensureSignalR(callback) {
    if (window.signalR) return callback();
    const s = document.createElement('script');
    s.src = '/js/signalr.min.js'; // fallback locale (metti il file in wwwroot/js)
    s.defer = true;
    s.onload = callback;
    s.onerror = () => {
        console.error('Impossibile caricare SignalR (CDN e locale).');
        alert('?? Errore nel caricare la libreria SignalR.');
    };
    document.head.appendChild(s);
}

/* ---------- Boot app ---------- */
function initApp() {

    // --- JukeBox modal ---
    const jbBtn = document.getElementById("jukeboxBtn");
    const jbOverlay = document.getElementById("jukeboxOverlay");
    const closeJB = document.getElementById("closeJukebox");
    const backJB = document.getElementById("backFromJukebox");

    if (jbBtn && jbOverlay) {
        jbBtn.addEventListener("click", () => {
            jbOverlay.style.display = "block";
        });
    }
    if (closeJB) {
        closeJB.addEventListener("click", () => {
            jbOverlay.style.display = "none";
        });
    }
    if (backJB) {
        backJB.addEventListener("click", () => {
            jbOverlay.style.display = "none";
        });
    }

    // Modale ruolo
    initRoleModal();

    // Bottoni principali
    document.getElementById("createBtn")?.addEventListener("click", async () => {
        const rawName = document.getElementById("name").value || "Host";
        await createRoom(rawName);
    });
    document.getElementById("joinBtn")?.addEventListener("click", async () => {
        const rawName = document.getElementById("name").value || "Giocatore";
        const roomId = (document.getElementById("roomIdInput").value || "").trim();
        await joinRoom(roomId, rawName);
    });
    document.getElementById("roomIdInput")?.addEventListener("keydown", e => {
        if (e.key === "Enter") document.getElementById("joinBtn")?.click();
    });

    // Invite box
    document.getElementById("copyInviteBtn")?.addEventListener("click", copyInvite);
    document.getElementById("shareInviteBtn")?.addEventListener("click", shareInvite);

    // Esci (vari contesti)
    document.getElementById("exitLobbyBtn")?.addEventListener("click", exitToLogin);
    document.getElementById("exitGameHostBtn")?.addEventListener("click", exitToLogin);
    document.getElementById("exitGamePlayerBtn")?.addEventListener("click", exitToLogin);

    // Host controls
    document.getElementById("startGameBtn")?.addEventListener("click", startGame);
    document.getElementById("votingBtn")?.addEventListener("click", toggleVoting);
    document.getElementById("restartBtn")?.addEventListener("click", restartGame);

    // UI counters/guide ruoli
    initRoleCounters();
    renderRolesGuide("rolesGuideHost");

    // Eventi UI -> Hub (vote ed eliminate)
    window.addEventListener("ui:vote-click", async (e) => {
        if (!state.votingOpen) return;
        const targetName = e.detail?.name;
        const prev = state.myVote;
        if (state.myVote === targetName) {
            state.myVote = null;
            try {
                try { await state.connection.invoke("UnvotePlayer", state.currentRoomId); }
                catch (_) { try { await state.connection.invoke("VotePlayer", state.currentRoomId, null); } catch (_) { await state.connection.invoke("VotePlayer", state.currentRoomId, ""); } }
            } catch (err) { state.myVote = prev; }
            return;
        }
        state.myVote = targetName;
        try { await state.connection.invoke("VotePlayer", state.currentRoomId, targetName); }
        catch (err) { state.myVote = prev; }
    });
    window.addEventListener("ui:eliminate-request", async (e) => {
        const name = e.detail?.name;
        if (name) await eliminatePlayer(name);
    });

    // Prefill + autojoin
    const urlParams = new URLSearchParams(window.location.search);
    const roomIdFromLink = urlParams.get("roomId");
    const nameInput = document.getElementById("name");
    const roomInput = document.getElementById("roomIdInput");

    if (storage.name) nameInput.value = storage.name;
    if (roomIdFromLink) { roomInput.value = roomIdFromLink; logLobby(`?? Sei stato invitato alla stanza: ${roomIdFromLink}`); }
    else if (storage.roomId) { roomInput.value = storage.roomId; }

    const wantAutoJoin = storage.autoJoin && nameInput.value.trim() && roomInput.value.trim();

    // Connessione soft + eventuale auto-join
    (async () => {
        const ok = await ensureConnection();
        if (!ok) return;
        if (wantAutoJoin) {
            try { await joinRoom(roomInput.value.trim(), nameInput.value.trim()); }
            catch { }
        }
    })();

    document.addEventListener("visibilitychange", () => {
        if (!document.hidden) {
            (async () => {
                const ok = await ensureConnection();
                if (!ok) return;
                const rid = storage.roomId;
                const nm = storage.name;
                if (storage.autoJoin && rid && nm) {
                    try { await joinRoom(rid, nm); } catch { }
                }
            })();
        }
    });
}

/* ---------- Boot con SignalR pronto (CDN o fallback) ---------- */
if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => ensureSignalR(initApp));
} else {
    ensureSignalR(initApp);
}
