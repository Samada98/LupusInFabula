// js/ui.js
import { state, roleNames, roleDescriptions } from "./state.js";

/* ---------- DOM refs (lookup on demand, sono statici quindi ok) ---------- */
const el = sel => document.querySelector(sel);

const overlay = el("#overlay");
const roleCard = el("#roleCard");
const closeRoleBtn = el("#closeRoleCard");
const roleText = el("#roleText");
const roleDescEl = el("#roleDescription");
const nicoEE = el("#nicoEasterEgg");

/* ---------- A11y modale ruolo ---------- */
let lastFocused = null;

function getFocusable(container) {
    return container.querySelectorAll('button,[href],input,select,textarea,[tabindex]:not([tabindex="-1"])');
}
function openEasterEgg() { if (nicoEE) nicoEE.style.display = "block"; }
function closeEasterEgg() { if (nicoEE) nicoEE.style.display = "none"; }

function openRoleCard() {
    overlay.style.display = "block";
    roleCard.style.display = "block";
    roleCard.setAttribute('aria-hidden', 'false');
    lastFocused = document.activeElement;
    const focusables = getFocusable(roleCard);
    (focusables[0] || closeRoleBtn).focus();
    roleCard._trap = (e) => {
        if (e.key === 'Tab') {
            const f = Array.from(getFocusable(roleCard));
            const first = f[0], last = f[f.length - 1];
            if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
            else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        }
    };
    roleCard.addEventListener('keydown', roleCard._trap);
}

export function closeRoleCard() {
    overlay.style.display = "none";
    roleCard.style.display = "none";
    roleCard.setAttribute('aria-hidden', 'true');
    if (roleCard._trap) roleCard.removeEventListener('keydown', roleCard._trap);
    closeEasterEgg();
    if (lastFocused && typeof lastFocused.focus === 'function') lastFocused.focus();
}

export function initRoleModal() {
    el("#showRoleBtn")?.addEventListener("click", () => {
        if (!state.myRole) { alert("? Nessun ruolo assegnato ancora."); return; }
        closeEasterEgg();
        roleText.textContent = roleNames[state.myRole] || state.myRole;
        roleDescEl.textContent = roleDescriptions[state.myRole] || "Nessuna descrizione.";
        openRoleCard();
        const currName = (localStorage.getItem("lupus_name") || "");
        if (["N1k0GgAY", "nicco"].includes(currName) || currName.toLowerCase().includes("nic")) openEasterEgg();
    });
    closeRoleBtn.onclick = closeRoleCard;
    overlay.onclick = closeRoleCard;
    window.addEventListener("keydown", e => { if (e.key === "Escape") closeRoleCard(); });
}

/* ---------- Routing schermate ---------- */
export function focusFirstInside(id) {
    const target = el(`#${id}`);
    if (!target) return;
    const f = target.querySelector('button,[href],input,select,textarea,[tabindex]:not([tabindex="-1"])');
    if (f) f.focus();
}

export function showScreen(id) {
    document.querySelectorAll(".screen").forEach(s => {
        s.classList.remove("active"); s.setAttribute("aria-hidden", "true"); s.hidden = true;
        if ("inert" in s) s.inert = true;
    });
    const target = el(`#${id}`);
    if (target) {
        target.classList.add("active");
        target.removeAttribute("aria-hidden"); target.hidden = false;
        if ("inert" in target) target.inert = false;
    }
    focusFirstInside(id);
}

/* ---------- Log ---------- */
export function logLobby(msg) {
    const logEl = el("#log"); if (!logEl) return;
    const li = document.createElement("li"); li.textContent = msg; logEl.appendChild(li);
    if (logEl.children.length > 80) logEl.removeChild(logEl.firstChild);
}
export function logGame(msg) {
    const logEl = state.isHost ? el("#gameLog") : el("#gameLogPlayer"); if (!logEl) return;
    const li = document.createElement("li"); li.textContent = msg; logEl.appendChild(li);
    if (logEl.children.length > 80) logEl.removeChild(logEl.firstChild);
}

/* ---------- Labels e contatori ---------- */
export function setRoomLabels() {
    const rid = state.currentRoomId || "";
    const ridHost = el("#roomIdInGame");
    const ridLobby = el("#currentRoomIdText");
    if (ridLobby) ridLobby.textContent = rid;
    if (ridHost) ridHost.textContent = rid;
}
export function updateLobbyCounters(players) {
    const total = players.length;
    const totalEl = el("#playerCount");
    if (totalEl) totalEl.textContent = total;
    updateRolesLeft(total);
}

/* ---------- Ruoli: counts UI ---------- */
export function readRoleCounts() {
    const counts = {};
    document.querySelectorAll(".rolesBox .number-control").forEach(ctrl => {
        const key = ctrl.dataset.role;
        const val = parseInt(ctrl.querySelector(".count").textContent) || 0;
        counts[key] = val;
    });
    return counts;
}
function calcAssignedRoles() { return Object.values(readRoleCounts()).reduce((a, b) => a + (parseInt(b) || 0), 0); }
export function updateRolesLeft(totalPlayersOverride = null) {
    const totalPlayers = (typeof totalPlayersOverride === 'number')
        ? totalPlayersOverride
        : (parseInt(el("#playerCount")?.textContent) || 0);
    const assigned = calcAssignedRoles();
    const left = Math.max(0, totalPlayers - assigned);
    const elLeft = el("#rolesRemaining");
    if (elLeft) elLeft.textContent = String(left);
}
export function initRoleCounters() {
    document.querySelectorAll(".rolesBox .number-control").forEach(ctrl => {
        const dec = ctrl.querySelector(".dec-btn"), inc = ctrl.querySelector(".inc-btn"), cnt = ctrl.querySelector(".count");
        const get = () => parseInt(cnt.textContent) || 0;
        const decHandler = () => { cnt.textContent = Math.max(0, get() - 1); updateRolesLeft(); };
        const incHandler = () => { cnt.textContent = get() + 1; updateRolesLeft(); };
        dec?.addEventListener("click", decHandler);
        inc?.addEventListener("click", incHandler);
        dec?.addEventListener("keydown", e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); decHandler(); } });
        inc?.addEventListener("keydown", e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); incHandler(); } });
    });
    updateRolesLeft();
}

/* ---------- Render: guide ruoli ---------- */
export function renderRolesGuide(containerId) {
    const container = el(`#${containerId}`); if (!container) return;
    container.innerHTML = "";
    const order = ["wolf", "villager", "seer", "guard", "scemo", "hunter", "witch", "lara", "mayor", "hitman"];
    order.forEach(k => {
        const card = document.createElement("div"); card.className = "roleCardMini";
        const title = document.createElement("div"); title.className = "title";
        const badge = document.createElement("span"); badge.className = "badge";
        const rn = roleNames[k] || k;
        const maybeEmoji = rn.split(" ")[0];
        badge.textContent = /\p{Extended_Pictographic}/u.test(maybeEmoji) ? maybeEmoji : "??";
        const text = document.createElement("span"); text.textContent = rn;
        const desc = document.createElement("div"); desc.className = "desc"; desc.textContent = roleDescriptions[k] || "";
        title.appendChild(badge); title.appendChild(text);
        card.appendChild(title); card.appendChild(desc);
        container.appendChild(card);
    });
}

/* ---------- Render: tabella host ---------- */
export function renderHostTable(players) {
    const table = el("#playersTable"); if (!table) return;
    table.innerHTML = "";

    const header = document.createElement("tr");
    ["Nome", "Ruolo", "Stato", "Voti", "Azioni"].forEach(h => { const th = document.createElement("th"); th.textContent = h; header.appendChild(th); });
    table.appendChild(header);

    const votes = players.filter(p => !p.eliminated).map(p => p.votes || 0);
    const maxVotes = votes.length ? Math.max(...votes) : 0;
    const leaders = players.filter(p => !p.eliminated && (p.votes || 0) === maxVotes);
    const isTie = (maxVotes > 0 && leaders.length > 1);

    players.forEach(p => {
        const tr = document.createElement("tr");
        if (maxVotes > 0 && !p.eliminated && (p.votes || 0) === maxVotes) tr.className = isTie ? "row-tied" : "row-leader";

        const tdName = document.createElement("td");
        const nameDisplay = p.eliminated ? `?? ${p.name}` : p.name;
        if (p.eliminated) { tdName.style.color = 'gray'; tdName.style.textDecoration = 'line-through'; }
        tdName.appendChild(document.createTextNode(nameDisplay));

        const tdRole = document.createElement("td"); tdRole.textContent = p.role ? (roleNames[p.role] || p.role) : "";

        const tdStatus = document.createElement("td");
        tdStatus.textContent = p.isOnline ? 'Online' : 'Offline';
        tdStatus.style.color = p.isOnline ? 'green' : 'red';

        const tdVotes = document.createElement("td"); tdVotes.textContent = String(p.votes || 0);

        const tdActions = document.createElement("td");
        if (!p.eliminated) {
            const btn = document.createElement("button");
            btn.textContent = 'Elimina';
            btn.addEventListener('click', () => {
                const ev = new CustomEvent("ui:eliminate-request", { detail: { name: p.name } });
                window.dispatchEvent(ev);
            });
            tdActions.appendChild(btn);
        }

        tr.append(tdName, tdRole, tdStatus, tdVotes, tdActions);
        table.appendChild(tr);
    });
}

/* ---------- Render: griglia giocatori (client) ---------- */
export function renderPlayers(players) {
    const container = el("#playersGrid"); if (!container) return;
    container.innerHTML = "";

    players.forEach(p => {
        const card = document.createElement("div");
        card.className = "playersGridItem";

        let displayName = p.eliminated ? `?? ${p.name}` : p.name;
        if (p.role === "mayor") displayName += " ???";
        const baseText = (!p.eliminated && p.votes > 0) ? `${displayName} (${p.votes})` : displayName;
        card.textContent = baseText;

        const isMyPick = (state.myVote === p.name);
        card.style.backgroundColor = p.eliminated ? "#9ca3af" : (state.votingOpen ? (isMyPick ? "#f59e0b" : "#4A90E2") : "#6b7280");
        card.style.cursor = (state.votingOpen && !p.eliminated) ? "pointer" : "default";
        card.style.opacity = p.isOnline ? "1" : ".7";

        if (Array.isArray(p.votedBy) && p.votedBy.length > 0) {
            const votersList = document.createElement("div"); votersList.className = "voters";
            const title = document.createElement("div"); title.textContent = "Votato da:"; votersList.appendChild(title);
            const ul = document.createElement("ul"); ul.style.margin = "4px 0 0"; ul.style.padding = "0"; ul.style.listStyle = "none";
            p.votedBy.forEach(name => { const li = document.createElement("li"); li.textContent = name; ul.appendChild(li); });
            votersList.appendChild(ul);
            card.appendChild(votersList);
        }

        if (state.votingOpen && !p.eliminated) {
            card.setAttribute('role', 'button');
            card.tabIndex = 0;
            card.title = isMyPick ? "Riclicca per togliere il voto" : "Clicca per votare";
            card.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); card.click(); } });
            card.onclick = () => window.dispatchEvent(new CustomEvent("ui:vote-click", { detail: { name: p.name } }));
        }

        container.appendChild(card);
    });
}

/* ---------- Hard reset UI per evitare mischioni host/player ---------- */
export function hardResetUI() {
    if (overlay) overlay.style.display = "none";
    if (roleCard) roleCard.style.display = "none";

    const hostCtrls = el("#hostControls");
    if (hostCtrls) hostCtrls.style.display = "none";

    const clear = s => { const n = el(s); if (n) n.innerHTML = ""; };
    clear("#playersTable");
    clear("#playersGrid");
    clear("#log");
    clear("#gameLog");
    clear("#gameLogPlayer");
    clear("#rolesGuideHost");
    clear("#rolesGuidePlayer");

    const pc = el("#playerCount");
    const rr = el("#rolesRemaining");
    if (pc) pc.textContent = "0";
    if (rr) rr.textContent = "0";

    const vbtn = el("#votingBtn");
    if (vbtn) { vbtn.style.backgroundColor = "var(--brand)"; vbtn.textContent = "Votazione"; vbtn.title = "Apri votazioni"; }

    const roleBtn = el("#showRoleBtn");
    if (roleBtn) roleBtn.style.display = "none";

    document.querySelectorAll(".screen").forEach(s => {
        s.classList.remove("active"); s.setAttribute("aria-hidden", "true"); s.hidden = true;
        if ("inert" in s) s.inert = true;
    });
    const access = el("#screenAccess");
    if (access) { access.classList.add("active"); access.removeAttribute("aria-hidden"); access.hidden = false; if ("inert" in access) access.inert = false; }
}

/* ---------- Stato UI in base a ruolo/partita ---------- */
export function applyRoleUI() {
    const hostCtrls = el("#hostControls");
    if (hostCtrls) hostCtrls.style.display = (state.isHost && !state.gameStarted) ? "block" : "none";

    if (!state.isHost) { const table = el("#playersTable"); if (table) table.innerHTML = ""; }

    const roleBtn = el("#showRoleBtn");
    if (roleBtn) roleBtn.style.display = state.myRole ? "inline-block" : "none";

    const vbtn = el("#votingBtn");
    if (vbtn) {
        if (state.isHost) {
            if (state.votingOpen) { vbtn.style.backgroundColor = "#4A90E2"; vbtn.textContent = "Il dado è tratto"; }
            else { vbtn.style.backgroundColor = "var(--brand)"; vbtn.textContent = "Votazione"; }
        } else {
            vbtn.style.backgroundColor = "var(--brand)"; vbtn.textContent = "Votazione";
        }
    }
}

/* ---------- Invite box ---------- */
export function refreshInviteUI(roomId) {
    const link = roomId ? `${window.location.origin}${window.location.pathname}?roomId=${encodeURIComponent(roomId)}` : '';
    const box = el('#inviteBox');
    const input = el('#inviteLink');
    if (roomId) { if (box) box.style.display = 'block'; if (input) input.value = link; }
    else { if (box) box.style.display = 'none'; }
}
export function copyInvite() {
    const input = el('#inviteLink'); if (!input) return;
    if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(input.value).then(() => alert('?? Link copiato!')).catch(() => fallbackCopy(input));
    } else fallbackCopy(input);
}
function fallbackCopy(input) {
    const tmp = document.createElement('textarea'); tmp.value = input.value; tmp.style.position = 'absolute'; tmp.style.left = '-9999px';
    document.body.appendChild(tmp); tmp.select();
    try { const ok = document.execCommand('copy'); alert(ok ? '?? Link copiato!' : '?? Copia manuale'); }
    catch { alert('?? Copia manuale'); }
    document.body.removeChild(tmp);
}
export function shareInvite() {
    const input = el('#inviteLink'); if (!input) return;
    const link = input.value;
    if (navigator.share) navigator.share({ title: 'Invito Lupus in Tabula', text: 'Entra nella mia stanza!', url: link }).catch(() => { });
    else alert('Copia il link manualmente: ' + link);
}

/* ---------- Misc UI ---------- */
export function setHostNames(hostName) {
    (el("#hostNameText") || {}).textContent = hostName || "Sconosciuto";
    (el("#hostNameInGame") || {}).textContent = hostName || "Sconosciuto";
    (el("#hostNamePlayer") || {}).textContent = hostName || "Sconosciuto";
}
