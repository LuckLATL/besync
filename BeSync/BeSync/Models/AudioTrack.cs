namespace BeSync.Models;

public class AudioTrack : ISelectionNode
{
    public string FilePath { get; set; }
    public int Index { get; set; }
    public Language Language { get; set; }

    public string Title { get; set; }
    
    public string GetReadableName()
    {
        return $"[gray]#{Index}[/] - {Language.GeneralDisplayName} [gray]({Language.OriginDisplayName})[/]";
    }
}