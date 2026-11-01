namespace MVPWorkshop.Core.Editing;

public enum GameLaunchKind { MainMenu, Team, Stadium, Exhibition }
public sealed record GameLaunchTarget(GameLaunchKind Kind, string Home = "", string Away = "", string Stadium = "", int? StadiumId = null)
{
    public string? Problem => !Enum.IsDefined(Kind) ? "Unknown launch target." : Kind is GameLaunchKind.Team or GameLaunchKind.Exhibition && string.IsNullOrWhiteSpace(Home) ? "Choose a school." :
        Kind == GameLaunchKind.Exhibition && (string.IsNullOrWhiteSpace(Away) || Home.Equals(Away, StringComparison.OrdinalIgnoreCase)) ? "Choose two different schools." :
        Kind == GameLaunchKind.Stadium && string.IsNullOrWhiteSpace(Stadium) ? "Choose a stadium." :
        StadiumId is < 0 or > 29 ? "Invalid stadium identifier." : new[] { Home, Away, Stadium }.Any(text => text.Length > 160 || text.Any(char.IsControl)) ? "Invalid launch target text." : null;
    public IReadOnlyList<string> Arguments()
    {
        if (Problem is { } problem) throw new InvalidDataException(problem);
        var args = new List<string> { "--workshop-target", Kind.ToString().ToLowerInvariant() };
        if (Kind != GameLaunchKind.MainMenu && Home.Length > 0) { args.Add(Kind == GameLaunchKind.Team ? "--team" : "--home"); args.Add(Home); }
        if (Away.Length > 0) { args.Add("--away"); args.Add(Away); }
        if (Stadium.Length > 0) { args.Add("--stadium"); args.Add(Stadium); }
        if (StadiumId is { } id) { args.Add("--stadium-id"); args.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        return args;
    }
    public string Label => Kind switch { GameLaunchKind.Team => "Team: " + Home, GameLaunchKind.Stadium => "Stadium: " + Stadium, GameLaunchKind.Exhibition => Away + " at " + Home, _ => "Main menu" };
}
