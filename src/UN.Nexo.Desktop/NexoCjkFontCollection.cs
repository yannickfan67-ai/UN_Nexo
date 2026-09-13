using Avalonia.Media.Fonts;

namespace UN.Nexo.Desktop;

internal sealed class NexoCjkFontCollection : EmbeddedFontCollection
{
    public NexoCjkFontCollection()
        : base(
            new Uri("fonts:NexoCjk", UriKind.Absolute),
            new Uri("avares://UN_Nexo/Assets/Fonts", UriKind.Absolute))
    {
    }
}
