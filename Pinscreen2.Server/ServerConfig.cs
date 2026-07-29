namespace Pinscreen2.Server;

public class ServerConfig
{
    public string Root { get; set; } = "";
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Push a sync command to every connected screen when a timed refresh finds
    /// the library changed. This is what makes curating on the server enough --
    /// nobody has to touch a pinscreen.
    /// </summary>
    public bool AutoPushOnChange { get; set; } = true;

    /// <summary>How often to rescan the library folder, in minutes.</summary>
    public int RefreshMinutes { get; set; } = 5;
}
