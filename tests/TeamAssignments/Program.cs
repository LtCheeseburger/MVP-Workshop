using MVPWorkshop.Core.Vfs;
if (args.Length != 2) throw new ArgumentException("Usage: TeamAssignments <game folder> <separate output folder>");
return TeamAssignmentChecks.Run(GameFileSystem.OpenFolder(Path.GetFullPath(args[0])), Path.GetFullPath(args[1]));
