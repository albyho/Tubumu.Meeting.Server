using Tubumu.Meeting.Server;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class MeetingServerServiceCollectionExtensions
    {
        public static IServiceCollection AddMeetingServer(this IServiceCollection services)
        {
            services.AddSingleton<Scheduler>();
            services.AddSingleton<BadDisconnectSocketService>();
            return services;
        }
    }
}
