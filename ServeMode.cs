namespace Tubumu.Meeting.Server
{
    /// <summary>
    /// 服务器模式
    /// </summary>
    public enum ServeMode
    {
        /// <summary>
        /// 拉取模式。用户进入后手动拉取其他用户的流。
        /// </summary>
        Pull,

        /// <summary>
        /// 邀请模式。用户需会议室管理员邀请后才可发布流。
        /// </summary>
        Invite
    }
}
