using Microsoft.Extensions.Logging;
using System.Text;

namespace Armory.Services;

/// <summary>
///     Reads CS2's own schema files through Valve's filesystem.
///     <br /><br />
///     Everything the browser knows about weapons, finishes, stickers, gloves and music kits comes
///     out of <c>items_game.txt</c> and <c>csgo_english.txt</c>. Those used to be extracted from
///     pak01 by a Python script at build time and shipped as JSON, which meant a CS2 update left
///     the catalogues quietly stale until someone remembered to regenerate and redeploy them.
///     Reading them on load means they are always whatever the installed game actually has.
/// </summary>
internal interface IGameFiles
{
    /// <summary>The whole of a game file as text, or null when it cannot be read.</summary>
    string? ReadText(string path);

    /// <summary>Every filename in a game directory. Empty when it cannot be opened.</summary>
    IReadOnlyList<string> List(string directory);
}

internal sealed class GameFiles : IGameFiles, IArmoryService
{
    private readonly InterfaceBridge     _bridge;
    private readonly ILogger<GameFiles>  _logger;

    public GameFiles(InterfaceBridge bridge, ILogger<GameFiles> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init()
        => true;

    public void Shutdown()
    {
    }

    public string? ReadText(string path)
    {
        try
        {
            using var file = _bridge.FileManager.OpenFile(path);

            if (file is null)
            {
                _logger.LogWarning("game file {path} could not be opened", path);

                return null;
            }

            var size = file.Size();

            if (size <= 0)
            {
                return null;
            }

            var buffer = new byte[size];
            file.Read(buffer);

            // csgo_english.txt carries a BOM and items_game.txt does not, so let the decoder
            // work it out rather than assuming either way.
            return new StreamReader(new MemoryStream(buffer), Encoding.UTF8, true).ReadToEnd();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("could not read {path}: {msg}", path, ex.Message);

            return null;
        }
    }

    public IReadOnlyList<string> List(string directory)
    {
        var names = new List<string>();

        try
        {
            using var dir = _bridge.FileManager.OpenDirectory(directory);

            if (dir is null)
            {
                _logger.LogWarning("game directory {dir} could not be opened", directory);

                return names;
            }

            var it = dir.GetEnumerator();

            while (it.MoveNext())
            {
                names.Add(it.Current);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("could not list {dir}: {msg}", directory, ex.Message);
        }

        return names;
    }
}
