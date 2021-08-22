using System;

namespace Tubumu.Meeting.Server
{
    public class PeerNotJoinedException : MeetingException
    {
        public PeerNotJoinedException(string peerId) : base($"Peer:{peerId} is not joined.")
        {
        }
    }
}
