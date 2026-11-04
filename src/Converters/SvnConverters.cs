using Avalonia.Data.Converters;

namespace SourceGit.Converters
{
    public static class SvnConverters
    {
        public static readonly FuncValueConverter<string, string> ActionText =
            new(value => value is "A" or "M" or "D" or "R" ? App.Text($"SVN.Action.{value}") : value);
    }
}
