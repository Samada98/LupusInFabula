using System;
using System.Collections.Generic;

namespace LupusInTabula.Models
{
    public class GameRoom
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 2).ToUpper();
        public string HostConnectionId { get; set; } = "";
        public string HostName { get; set; } = "";
        public bool GameStarted { get; set; }
        public bool VotingOpen { get; set; }
        public List<Player> Players { get; set; } = new();
        public string HostKey { get; set; } = "";

    }
}
