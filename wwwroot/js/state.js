// js/state.js
export const state = {
    connection: null,
    // game/session
    currentRoomId: null,
    isHost: false,
    myRole: null,
    votingOpen: false,
    myVote: null,
    currentPlayers: [],
    gameStarted: false,
    myName: null,
    // flags
    isJoining: false,
    // heartbeat timer
    hbTimer: null
};

export const HUB_URL = "/gamehub";

export const NAME_MAP = {
    "nico": "N1k0GgAY", "niko": "N1k0GgAY", "nicolo": "N1k0GgAY", "nicco": "N1k0GgAY",
    "gioia": "TriS5tezZa", "carly": "Carmine", "carlotta": "Carmine", "carli": "Carmine",
    "benni": "Benagol", "benny": "Benagol", "matte": "MaTTe0R0Li", "matteo": "MaTTe0R0Li"
};
export const normalizeName = s => (s || "").trim().toLowerCase();
export const applyNameMapping = s => NAME_MAP[normalizeName(s || "")] || (s || "").trim();

export const roleNames = {
    wolf: "?? Lupo", villager: "????? Contadino", seer: "?? Veggente", guard: "?? Puttana",
    scemo: "?? Scemo", hunter: "?? Cacciatore", witch: "?? Strega", lara: "?? Lara",
    mayor: "??? Sindaco", hitman: "?? Sicario"
};

export const roleDescriptions = {
    wolf: `Di notte scegliete insieme una vittima.\nVincete se i lupi eliminano tutti gli altri.`,
    villager: `Non hai poteri speciali.\nUsa l'astuzia per smascherare i lupi.`,
    seer: `Ogni notte puoi scoprire se un giocatore è lupo.`,
    guard: `Ogni notte puoi proteggere un giocatore o te stessa\ndall'attacco dei lupi.`,
    scemo: `Vinci se vieni eliminato dai voti del villaggio.`,
    hunter: `Se vieni eliminato, puoi scegliere immediatamente\nun altro giocatore da portare con te.`,
    witch: `Hai due pozioni: una per salvare una vittima, una per uccidere.\nPuoi usarle solo una volta ciascuna.`,
    lara: `All'inizio sei una cittadina.\nSe vieni uccisa dai lupi, diventi un Lupo.\nVinci con i cittadini finché sei cittadina,\nma se diventi Lupo vinci con i lupi.`,
    mayor: `Durante le votazioni il tuo voto conta doppio.`,
    hitman: `Hai una pistola con un solo colpo.\nPuoi usarla una volta in tutta la partita per eliminare un giocatore.`
};

// LocalStorage helpers
export const storage = {
    get roomId() { return localStorage.getItem("lupus_roomId") || null; },
    set roomId(v) { v ? localStorage.setItem("lupus_roomId", v) : localStorage.removeItem("lupus_roomId"); },

    get name() { return localStorage.getItem("lupus_name") || ""; },
    set name(v) { v ? localStorage.setItem("lupus_name", v) : localStorage.removeItem("lupus_name"); },

    get autoJoin() { return localStorage.getItem("lupus_autoJoin") === "true"; },
    set autoJoin(v) { localStorage.setItem("lupus_autoJoin", v ? "true" : "false"); },

    get hostKey() { return localStorage.getItem("lupus_hostKey") || null; },
    set hostKey(v) { v ? localStorage.setItem("lupus_hostKey", v) : localStorage.removeItem("lupus_hostKey"); },

    get hostRoomId() { return localStorage.getItem("lupus_hostRoomId") || null; },
    set hostRoomId(v) { v ? localStorage.setItem("lupus_hostRoomId", v) : localStorage.removeItem("lupus_hostRoomId"); }
};
