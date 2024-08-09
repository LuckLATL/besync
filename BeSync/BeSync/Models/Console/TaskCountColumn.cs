using Spectre.Console;
using Spectre.Console.Rendering;

namespace BeSync.Models.Console;

public class TaskCountColumn : ProgressColumn
{
    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        return new Markup($"{task.Value}[gray]/[/]{task.MaxValue}");
    }
}