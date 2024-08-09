using System.Text;
using BeSync.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace BeSync.Extensions;

public static class VideoFileExtensions
{
    public static string GetFormattedPrompt(this VideoFile videoFile)
    {
        var tree = new Tree( Markup.Escape(Path.GetFileName(videoFile.FilePath)));
        foreach (var track in videoFile.AudioTracks)
        {
            tree.AddNode($"{track.Index} - {track.Language}");
        }
                
        var sb = new StringBuilder();
        var segments = ((IRenderable)tree).Render(new RenderOptions(AnsiConsole.Console.Profile.Capabilities, new Size(AnsiConsole.Console.Profile.Width, AnsiConsole.Console.Profile.Height)), 120);
        foreach (var segment in segments)
        {
            sb.Append(Markup.Escape(segment.Text));

            if (segment.IsLineBreak)
                sb.Append("  ");
        }

        return sb.ToString();
    }
}