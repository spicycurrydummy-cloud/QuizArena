using System;

namespace GemmaQuiz.Data
{
    [Serializable]
    public class PlayerData
    {
        public string playerName;
        public int actorNumber;
        public bool isMasterClient;
        public int totalScore;

        public PlayerData(string playerName, int actorNumber, bool isMasterClient)
        {
            this.playerName = playerName;
            this.actorNumber = actorNumber;
            this.isMasterClient = isMasterClient;
            this.totalScore = 0;
        }
    }
}
