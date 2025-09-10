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
        public string? CoupleRomeoName { get; set; }
        public string? CoupleJulietName { get; set; }
        public string CoupleSleepAt { get; set; } = "romeo"; // "romeo" | "giulietta"

        public IDictionary<string, int>? SavedRoleCounts { get; set; }
    }
}
