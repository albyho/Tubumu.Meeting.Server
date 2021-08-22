using System;

namespace Tubumu.Meeting.Server
{
    public class PeerNotInAnyRoomException : MeetingException
    {
        public PeerNotInAnyRoomException(string peerId) : base($"Peer:{peerId} is not in any room.")
        {
        }
    }
}
