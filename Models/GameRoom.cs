using System;
using System.Collections.Generic;

namespace LupusInTabula.Models
{
    public class GameRoom
    {
        public string Id { get; set; } = "";
        public string? HostConnectionId { get; set; }
        public string HostName { get; set; } = "";
        public string HostKey { get; set; } = "";

        public List<Player> Players { get; } = new();

        public bool GameStarted { get; set; } = false;
        public bool VotingOpen { get; set; } = false;
        public bool NightInProgress { get; set; } = false;
        public int NightNumber { get; set; } = 0;
        public string? NightWolfTarget { get; set; }
        public string? NightProtectedTarget { get; set; }
        public bool NightWitchSave { get; set; } = false;
        public string? NightWitchKillTarget { get; set; }
        public bool WitchSavePotionUsed { get; set; } = false;
        public bool WitchKillPotionUsed { get; set; } = false;
        public bool HostAvailableForJukeboxRandom { get; set; } = true;
        public string? CoupleRomeoName { get; set; }
        public string? CoupleJulietName { get; set; }
        public string CoupleSleepAt { get; set; } = "romeo"; // "romeo" | "giulietta"

        public IDictionary<string, int>? SavedRoleCounts { get; set; }
        public Dictionary<string, string> PreviousRolesByPlayer { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
