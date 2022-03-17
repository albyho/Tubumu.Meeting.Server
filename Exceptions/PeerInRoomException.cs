namespace Tubumu.Meeting.Server
{
    public class PeerNotInAnyRoomException : MeetingException
    {
        public PeerNotInAnyRoomException(string tag, string peerId) : base($"{tag} | Peer:{peerId} is not in any room.")
        {

        }
    }
}
