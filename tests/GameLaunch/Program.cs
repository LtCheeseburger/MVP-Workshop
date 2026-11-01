using MVPWorkshop.Core.Editing;
using MVPWorkshop.Core.Vfs;
if(args.Length>=2 && args[0]=="--list-game")
{
    var root=GameFileSystem.OpenGame(args[1]);
    foreach(var school in TeamAssetCatalog.Build(root.EnumerateDeep().ToList()).Where(s=>args.Length<3 || s.Team.Name.Contains(args[2],StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine(school.Team.Name+" | "+school.StadiumName+" | "+school.Team.StadiumId);
    return 0;
}
int failed=0;
void Check(string name,bool value){Console.WriteLine($"{(value?"PASS":"FAIL")} {name}");if(!value)failed++;}
Check("main menu has an explicit native target flag",new GameLaunchTarget(GameLaunchKind.MainMenu).Arguments().SequenceEqual(new[]{"--workshop-target","mainmenu"}));
Check("team names remain single argument values",new GameLaunchTarget(GameLaunchKind.Team,"Arizona State").Arguments().SequenceEqual(new[]{"--workshop-target","team","--team","Arizona State"}));
Check("stadium flag retains full display name",new GameLaunchTarget(GameLaunchKind.Stadium,Stadium:"Alex Box Stadium").Arguments().Contains("Alex Box Stadium"));
Check("decoded stadium identifier is passed separately from its name",new GameLaunchTarget(GameLaunchKind.Stadium,Stadium:"Packard Stadium",StadiumId:8).Arguments().TakeLast(2).SequenceEqual(new[]{"--stadium-id","8"}));
Check("invalid stadium identifier is rejected",new GameLaunchTarget(GameLaunchKind.Stadium,Stadium:"Unknown",StadiumId:30).Problem is not null);
var exhibition=new GameLaunchTarget(GameLaunchKind.Exhibition,"Arizona State","Florida State","Packard Stadium");
Check("exhibition supplies both teams and optional stadium",exhibition.Problem is null && exhibition.Arguments().Contains("--home") && exhibition.Arguments().Contains("--away") && exhibition.Arguments().Contains("--stadium"));
Check("missing school is rejected",new GameLaunchTarget(GameLaunchKind.Team).Problem is not null);
Check("exhibition rejects identical schools",new GameLaunchTarget(GameLaunchKind.Exhibition,"LSU","lsu").Problem is not null);
Check("missing stadium is rejected",new GameLaunchTarget(GameLaunchKind.Stadium).Problem is not null);
Check("control characters cannot enter launch flags",new GameLaunchTarget(GameLaunchKind.Team,"LSU\nBad").Problem is not null);
Check("unknown target kind is rejected",new GameLaunchTarget((GameLaunchKind)99).Problem is not null);
return failed==0?0:1;
