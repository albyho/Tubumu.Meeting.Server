using Tubumu.Mediasoup;

namespace Tubumu.Meeting.Server
{
    public class PeerPullResult
    {
        public Producer[] ExistsProducers { get; set; }

        public string[] ProduceSources { get; set; }
    }
}
