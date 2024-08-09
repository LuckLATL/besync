using Spectre.Console;

namespace BeSync.Models;

public class VideoFile : ISelectionNode
{
    public List<AudioTrack> AudioTracks { get; set; }
    public string FilePath { get; set; }
    
    public string GetReadableName()
    {
        return $"[green]{Markup.Escape(Path.GetFileName(FilePath))}[/] [gray]({AudioTracks.Count})[/]";
    }
}