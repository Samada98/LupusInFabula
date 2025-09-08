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
    }
}
