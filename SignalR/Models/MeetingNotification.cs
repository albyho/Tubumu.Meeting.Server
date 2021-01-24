using Newtonsoft.Json;

namespace Tubumu.Meeting.Server
{
    /// <summary>
    /// MeetingNotification
    /// </summary>
    public class MeetingNotification
    {
        public string Type { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object? Data { get; set; }

        public static string Stringify(string type, string? data = null)
        {
            if (data == null)
            {
                return $"{{\"type\":{type}}}";
            }
            return $"{{\"type\":{type},\"data\":{data}}}";
        }
    }
}
