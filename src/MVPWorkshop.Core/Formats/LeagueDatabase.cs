using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MVPWorkshop.Core.Formats;

public sealed record LeagueTeam(string Id,int Index,int AssetId,int LegacyIndex,int Conference,string Name,string Abbreviation,string City,string State,string Nickname,string PrimaryColor,string SecondaryColor,string Venue);
public sealed record LeagueDatabaseResult(int Teams,int RosterRows,int Players,int Pitchers,bool PlaceholderRosters,IReadOnlyDictionary<string,byte[]> Files,JsonObject Audit);

/// <summary>Builds original-layout Xbox database members; never uses the kit's incompatible wide roster format.</summary>
public static class LeagueDatabase
{
    static JsonObject Object(string path)=>JsonNode.Parse(File.ReadAllText(path)) as JsonObject??throw new InvalidDataException(path);
    static int Number(JsonNode? value,string field)=> (int?)value?[field]??throw new InvalidDataException("Missing "+field);
    static string Value(Dictionary<string,string> row,string key)=>row.GetValueOrDefault(key)??throw new InvalidDataException("Missing "+key);
    static uint U32(byte[] b,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at,4));
    static void W32(byte[] b,int at,uint n)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at,4),n);
    static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    static List<byte[]> RosterRecords(byte[] data)
    {
        if(data.Length<4)throw new InvalidDataException("Missing roster header.");
        var rows=new List<byte[]>();int at=4;uint count=U32(data,0);if(count>320)throw new InvalidDataException("Roster capacity.");
        for(int i=0;i<count;i++){if(at+487>data.Length)throw new InvalidDataException("Short roster record.");uint slots=U32(data,at+192);if(slots>30)throw new InvalidDataException("More than 30 roster slots.");int size=checked(487+(int)slots*8);if(at+size>data.Length)throw new InvalidDataException("Short roster slots.");rows.Add(data.AsSpan(at,size).ToArray());at+=size;}
        if(at!=data.Length)throw new InvalidDataException("Roster trailing bytes.");return rows;
    }
    static void Text(byte[] row,int at,int width,int limit,string value)
    {
        if(value.Length>limit || value.IndexOfAny(['\0','\r','\n'])>=0)throw new InvalidDataException($"Engine label exceeds {limit} characters: {value}");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var bytes=Encoding.GetEncoding(1252,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetBytes(value);
        if(bytes.Length>=width)throw new InvalidDataException("Engine label byte overflow.");Array.Clear(row,at,width);bytes.CopyTo(row,at);
    }
    public static IReadOnlyList<LeagueTeam> ReadTeams(string league)
    {
        var identities=Object(Path.Combine(league,"identities.json"));var overrides=Object(Path.Combine(league,"engine-text-overrides.json"))["teams"]!;
        var metadata=TaggedDat.CsvRecords(File.ReadAllText(Path.Combine(league,"team-metadata.csv"))).ToDictionary(r=>Value(r,"team_id"));
        var workbook=TaggedDat.CsvRecords(File.ReadAllText(Path.Combine(league,"team-workbook.csv"))).ToDictionary(r=>Value(r,"team_id"));
        var result=new List<LeagueTeam>();
        foreach(var(id,identity)in (JsonObject)identities["teams"]!) {
            if((bool?)identity?["retired"]==true)continue;
            var row=metadata[id];var source=workbook[id];var labels=overrides[id];
            string Label(string field,string fallback)=>(string?)labels?[field]??fallback;
            result.Add(new(id,Number(identity,"engine_index"),Number(identity,"asset_id"),(int?)identity?["legacy_engine_index"]??0,
                Number(identities["conferences"]![Value(source,"conference_id")],"engine_key"),Label("full_name",Value(row,"athletics_name")),Label("abbreviation",Value(row,"abbreviation")),
                Label("city",Value(row,"school_city")),Value(row,"school_state"),Label("nickname",Value(row,"mascot_nickname")),Value(row,"primary_color"),Value(row,"secondary_color"),Value(row,"stadium_name")));
        }
        if(result.Count!=309 || result.Select(t=>t.Index).Distinct().Count()!=309 || result.Select(t=>t.AssetId).Distinct().Count()!=309 || result.Any(t=>t.Index is <1 or >=320 || t.AssetId is <1 or >511 || t.Conference is <1 or >30))throw new InvalidDataException("Expected 309 unique schools in 30 conferences within engine bounds.");
        return result.OrderBy(t=>t.Index).ToList();
    }

    /// <param name="allowTestPlaceholders">Explicit diagnostic mode only; these rosters are never labeled real/current.</param>
    /// <param name="playersFile">Curated format-1 player ledger with teams keyed by canonical school ID.</param>
    public static LeagueDatabaseResult Build(string game,string league,bool allowTestPlaceholders=false,string? playersFile=null)
    {
        var teams=ReadTeams(league);var database=Path.Combine(game,"data/database/database_big");var misc=Path.Combine(game,"data/database/dbmisc_big");
        var rosterInput=File.ReadAllBytes(Path.Combine(database,"roster.bin"));var old=RosterRecords(rosterInput);
        var locations=TaggedDat.Read(File.ReadAllBytes(Path.Combine(database,"location.dat"))).Rows.ToDictionary(p=>p.Value[1],p=>p.Key,StringComparer.OrdinalIgnoreCase);
        if(old.Count!=129)throw new InvalidDataException("Expected the unmodified 128-school Xbox source roster.");
        JsonObject? real=playersFile is null?null:Object(playersFile);
        if(real is null && !allowTestPlaceholders)throw new InvalidDataException("Final player data is missing. Test placeholders require an explicit opt-in.");
        if(real is not null && (int?)real["format"]!=1)throw new InvalidDataException("Unsupported player ledger.");
        var names=new[]{"attrib.dat","lhattrib.dat","rhattrib.dat","pitcher.dat"};
        var tables=names.ToDictionary(n=>n,n=>TaggedDat.Read(File.ReadAllBytes(Path.Combine(database,n))));
        var output=tables.ToDictionary(p=>p.Key,p=>new TaggedDat());
        foreach(var(name,table)in tables)foreach(var(tag,label)in table.Labels)output[name].Labels.Add(tag,label);
        const uint defaultKey=0xF58F3C1B;
        foreach(var name in names)if(tables[name].Rows.TryGetValue(defaultKey,out var row))output[name].Rows.Add(defaultKey,new(row));
        var reserved=tables["attrib.dat"].Rows.Keys.ToHashSet();reserved.Add(0);
        var owned=new HashSet<uint>();var playerIds=new HashSet<string>(StringComparer.Ordinal);var identities=new JsonArray();var fallbacks=new JsonArray();var placeholderTeams=new JsonArray();
        var records=Enumerable.Range(0,teams.Max(t=>t.Index)+1).Select(_=>old[0].ToArray()).ToArray();
        var graduateClasses=new List<string>{"key,class"};
        var teamNames=new List<string[]>();var playerNames=new List<string[]>{new[]{"key","team_id","identity","first_name","last_name"}};
        foreach(var team in teams) {
            var template=old[team.LegacyIndex is >0 and <129 ? team.LegacyIndex:1];int templateCount=(int)U32(template,192);
            JsonArray? players=real?["teams"]?[team.Id] as JsonArray;
            bool permittedPlaceholder=allowTestPlaceholders && real?["placeholderTeams"] is JsonArray declared && declared.Any(id=>(string?)id==team.Id);
            if(real is not null && ((players is null && !permittedPlaceholder) || players?.Count is <9 or >30))throw new InvalidDataException("Missing/playability-invalid roster for "+team.Id);
            if(players is null)placeholderTeams.Add(team.Id);
            int count=players?.Count??templateCount;if(count is <9 or >30)throw new InvalidDataException("Roster slot count.");
            byte[] record=new byte[487+count*8];Array.Copy(template,record,196);Array.Copy(template,196+templateCount*8,record,196+count*8,291);W32(record,192,(uint)count);
            for(int slot=0;slot<count;slot++) {
                uint sourceKey=U32(template,196+(slot%templateCount)*8);JsonNode? player=players?[slot];
                uint key;string identity,first,last;
                if(player is not null) {
                    identity=(string?)player["id"]??throw new InvalidDataException("Player requires an append-only identity.");
                    key=Convert.ToUInt32((string?)player["key"]??throw new InvalidDataException("Player requires a persistent engine key."),16);
                    first=(string?)player["firstName"]??throw new InvalidDataException("Missing first name.");last=(string?)player["lastName"]??throw new InvalidDataException("Missing last name.");
                    if((string?)player["source"] is not {Length:>0} || (int?)player["season"] is not (2026 or 2027))throw new InvalidDataException("Player requires sourced 2027 data or labeled 2026 fallback.");
                    if((string?)player["templatePlayerKey"] is string source)sourceKey=Convert.ToUInt32(source,16);
                } else {
                    identity=$"test:{team.Id}:{slot}";first=$"Test{slot+1:00}";last=$"School{team.Index}";
                    if(team.LegacyIndex is >0 and <129)key=sourceKey;
                    else {int salt=0;do{key=BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"MVP27-TEST-V1|{identity}|{salt++}")));}while(!reserved.Add(key));}
                }
                if(key==0 || key==defaultKey || !owned.Add(key) || string.IsNullOrWhiteSpace(identity) || !playerIds.Add(identity))throw new InvalidDataException("Duplicate/reserved player key or identity.");
                if(string.IsNullOrWhiteSpace(first)||string.IsNullOrWhiteSpace(last))throw new InvalidDataException("Missing player name.");
                if(first.Length>11 || last.Length>14)throw new InvalidDataException("Player name exceeds native buffers.");
                foreach(var name in names)if(tables[name].Rows.TryGetValue(sourceKey,out var source)) {
                    var fields=new Dictionary<int,string>(source){[0]=first,[1]=last};
                    if(player?[name] is JsonObject values)foreach(var(tag,value)in values){int index=int.Parse(tag);if(!fields.ContainsKey(index))throw new InvalidDataException("Unknown player field.");fields[index]=(string?)value??throw new InvalidDataException("Player fields are strings.");}
                    output[name].Rows.Add(key,fields);
                }
                if(!output["attrib.dat"].Rows.ContainsKey(key) || !output["lhattrib.dat"].Rows.ContainsKey(key) || !output["rhattrib.dat"].Rows.ContainsKey(key))throw new InvalidDataException("Unresolved player template.");
                if(player?["modernClass"] is JsonNode classNode) {
                    int sourceClass=(int)classNode;if(sourceClass is <0 or >4)throw new InvalidDataException("Invalid modern source class.");
                    if(sourceClass==4)graduateClasses.Add(key.ToString("x8")+",4");
                }
                W32(record,196+slot*8,key);W32(record,200+slot*8,(uint?)player?["rosterWord"]??U32(template,200+(slot%templateCount)*8));
                identities.Add(new JsonObject{["id"]=identity,["key"]=key.ToString("x8"),["team"]=team.Id,["slot"]=slot});playerNames.Add([key.ToString("x9"),team.Id,identity,first,last]);
            }
            if(players is not null){
                var rotation=real?["rotationSlots"]?[team.Id] as JsonArray??throw new InvalidDataException("Missing native pitching rotation for "+team.Id);
                if(rotation.Count!=3||rotation.Select(n=>(int)n!).Distinct().Count()!=3)throw new InvalidDataException("Pitching rotation requires three distinct slots.");
                for(int i=0;i<3;i++){int index=(int)rotation[i]!;if(index<0||index>=count||!output["pitcher.dat"].Rows.ContainsKey(U32(record,196+index*8)))throw new InvalidDataException("Unresolved pitching rotation player.");record[196+count*8+i]=(byte)index;}
            }
            Text(record,0,8,7,team.Abbreviation);Text(record,8,48,23,team.Name);Text(record,56,16,7,team.Abbreviation);Text(record,72,40,19,team.City);Text(record,112,32,15,team.Nickname);
            uint flags=U32(record,160);flags=(flags & ~((31u<<6)|(511u<<11)))|((uint)team.Conference<<6)|((uint)team.AssetId<<11);W32(record,160,flags);
            // Logo identity is new; the uniform and stadium remain verified native stand-ins until separately authored.
            W32(record,164,(U32(record,164)&~511u)|(uint)team.AssetId);
            records[team.Index]=record;
            if(team.LegacyIndex<=0)fallbacks.Add(new JsonObject{["team"]=team.Id,["uniformTemplateTeam"]=1,["stadiumEnum"]=(U32(record,168)>>4)&63,["canonicalVenue"]=team.Venue});
            if(!locations.TryGetValue(team.State,out var location))throw new InvalidDataException("Unmapped school state: "+team.State);
            teamNames.Add([team.Abbreviation,team.AssetId.ToString(),team.Nickname,team.City,location.ToString()]);
        }
        if(output["attrib.dat"].Rows.Count>7775 || output["pitcher.dat"].Rows.Count>3708)throw new InvalidDataException("Player table exceeds runtime capacity.");
        using var rosterStream=new MemoryStream();using(var writer=new BinaryWriter(rosterStream,Encoding.ASCII,true)){writer.Write(records.Length);foreach(var row in records)writer.Write(row);}
        var files=new SortedDictionary<string,byte[]>(StringComparer.Ordinal);
        void Put(string p,byte[] data)=>files.Add(p,data);
        Put("data/database/database_big/roster.bin",rosterStream.ToArray());foreach(var(name,table)in output)Put("data/database/database_big/"+name,table.Write());
        var conf=TaggedDat.Read(File.ReadAllBytes(Path.Combine(database,"conf.dat")));var confTemplate=new Dictionary<int,string>(conf.Rows.First().Value);var oldConf=conf.Rows.ToDictionary(p=>p.Key,p=>p.Value);conf.Rows.Clear();
        var conferences=TaggedDat.CsvRecords(File.ReadAllText(Path.Combine(league,"conference-metadata.csv")));
        foreach(var row in conferences){uint key=uint.Parse(Value(row,"engine_key"));var fields=new Dictionary<int,string>(confTemplate){[0]=Value(row,"name"),[1]=Value(row,"short_label"),[2]=(((key-1)%14)+1).ToString(),[3]=oldConf.TryGetValue(key,out var prior)?prior[3]:$"conf{key:00}",[4]="1",[5]="",[6]=""};conf.Rows.Add(key,fields);}
        if(conf.Rows.Count!=30)throw new InvalidDataException("Conference count.");Put("data/database/database_big/conf.dat",conf.Write());
        var defaults=TaggedDat.Read(File.ReadAllBytes(Path.Combine(database,"default.dat")));var validIds=teams.Select(t=>(uint)t.Index).ToHashSet();
        foreach(var row in defaults.Rows.Values)foreach(var tag in row.Keys.ToArray())if(uint.TryParse(row[tag],out var id) && id!=0 && !validIds.Contains(id))row[tag]=teams[0].Index.ToString();
        Put("data/database/database_big/default.dat",defaults.Write());
        // An explicit authored baseline, not official schedules. Phase 9 replaces
        // this with the researched series/rules generator. All teams participate.
        int ivy=int.Parse(Value(conferences.Single(row=>Value(row,"conference_id")=="ncaa-conf:865"),"engine_key"));
        if(teams.Count(t=>t.Conference==ivy)!=8)throw new InvalidDataException("Expected eight Ivy schools.");
        var remaining=teams.ToDictionary(t=>t.Index,t=>t.Conference==ivy?40:56);
        var games=new List<(int Home,int Away,int Day)>();var used=teams.ToDictionary(t=>t.Index,_=>new HashSet<int>());
        while(remaining.Values.Any(n=>n>0)) {
            int home=remaining.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).First().Key, need=remaining[home];remaining[home]=0;
            var opponents=remaining.Where(p=>p.Value>0).OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).Take(need).Select(p=>p.Key).ToArray();
            if(opponents.Length!=need)throw new InvalidDataException("Baseline schedule degree sequence is not realizable.");
            foreach(int away in opponents){remaining[away]--;int day=0;while(used[home].Contains(day)||used[away].Contains(day))day++;if(day>110)throw new InvalidDataException("Baseline schedule exceeds season.");used[home].Add(day);used[away].Add(day);games.Add((home,away,day));}
        }
        var tokens=new Dictionary<int,string>();foreach(var group in teams.GroupBy(t=>t.Conference))foreach(var(t,index)in group.OrderBy(t=>t.Index).Select((t,i)=>(t,i)))tokens[t.Index]=conf.Rows[(uint)group.Key][1]+(index+1);
        var schedule=new List<string[]> {new[]{"DATE","TIME","HOME","ROAD"}};
        foreach(var match in games.OrderBy(g=>g.Day).ThenBy(g=>g.Home)){var date=new DateTime(2006,2,7).AddDays(match.Day);schedule.Add([date.ToString("M/d/yyyy",System.Globalization.CultureInfo.InvariantCulture),"13:00",tokens[match.Home],tokens[match.Away]]);}
        Put("data/database/schedule_big/intrconf.dat",Encoding.Latin1.GetBytes(string.Join("\r\n",schedule.Select(TaggedDat.CsvLine))+"\r\n"));
        foreach(var row in conf.Rows.Where(p=>p.Key<=14))Put("data/database/schedule_big/"+row.Value[3]+".dat",Encoding.ASCII.GetBytes("DATE,TIME,HOME,ROAD\r\n"));
        var stats=TaggedDat.Read(File.ReadAllBytes(Path.Combine(database,"tstat.dat")));stats.Rows.Clear();int rank=0;foreach(var team in teams)stats.Rows.Add((uint)team.Index,stats.Labels.Keys.ToDictionary(k=>k,k=>k==6?(++rank).ToString():"0"));Put("data/database/database_big/tstat.dat",stats.Write());
        var lines=new List<string[]>{new[]{"Abbr","Logo ID","Nickname","City","Location ID"}};lines.AddRange(teamNames);Put("data/database/dbmisc_big/teaminfo.csv",Encoding.Latin1.GetBytes(string.Join("\r\n",lines.Select(TaggedDat.CsvLine))+"\r\n"));
        // logotable is the generic create-a-school catalog (200 native slots),
        // not the school registry. School/logo mappings belong to teaminfo and
        // roster records; adding 309 schools here corrupts the frontend cache.
        var logos=File.ReadAllLines(Path.Combine(misc,"logotable.csv")).Take(3).ToList();logos[1]="Custom,400,442";logos[2]="Alpha,443,468";logos.Add("Logo Name,Logo ID,Default Nickname");
        foreach(var row in TaggedDat.Csv(File.ReadAllText(Path.Combine(misc,"logotable.csv"))).Skip(4))if(row.Length>=3 && int.TryParse(row[1],out int asset) && asset is >=201 and <=269) {
            var renamed=row.ToArray();renamed[1]=(asset+199).ToString();logos.Add(TaggedDat.CsvLine(renamed));
            var folder=Path.Combine(game,$"data/frontend/logos_big/l{asset:000}.xsh");
            var original=Directory.EnumerateFiles(folder).ToDictionary(p=>Path.GetFileName(p),p=>File.ReadAllBytes(p));
            foreach(var(name,bytes)in XshFolder.RetagSchool(original,asset+199))Put($"data/frontend/logos_big/l{asset+199:000}.xsh/"+name,bytes);
        }
        Put("data/database/dbmisc_big/logotable.csv",Encoding.Latin1.GetBytes(string.Join("\r\n",logos)+"\r\n"));
        Put("data/league/school-logos.csv",Encoding.Latin1.GetBytes("School,Logo ID,Nickname\r\n"+string.Join("\r\n",teams.Select(t=>TaggedDat.CsvLine([t.Name,t.AssetId.ToString(),t.Nickname])))+"\r\n"));
        Put("data/league/teams.json",JsonSerializer.SerializeToUtf8Bytes(teams,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        Put("data/league/graduate-classes.csv",Encoding.UTF8.GetBytes(string.Join("\r\n",graduateClasses)+"\r\n"));
        Put("data/league/player-identities.json",Encoding.UTF8.GetBytes(identities.ToJsonString(new(){WriteIndented=true})));
        Put("data/league/player_names.csv",Encoding.UTF8.GetBytes(string.Join("\r\n",playerNames.Select(TaggedDat.CsvLine))+"\r\n"));
        bool hasPlaceholders=placeholderTeams.Count>0;
        var audit=new JsonObject{["format"]=1,["teams"]=309,["rosterRows"]=records.Length,["activeSchoolIds"]=new JsonArray(teams.Select(t=>JsonValue.Create(t.Index)).ToArray()),["retiredIdsPreserved"]=true,["conferences"]=30,["players"]=owned.Count,["pitchers"]=output["pitcher.dat"].Rows.Count,["placeholderRosters"]=hasPlaceholders,["placeholderTeams"]=placeholderTeams,["productionReady"]=!hasPlaceholders&&(bool?)real?["productionReady"]==true,["playerImport"]=real?["importAudit"]?.DeepClone(),["nativeStandIns"]=fallbacks,["stockRosterSha256"]=Hash(rosterInput),["outputs"]=new JsonObject(files.Select(p=>KeyValuePair.Create<string,JsonNode?>(p.Key,JsonValue.Create(Hash(p.Value)))))};
        audit["schedule"]=new JsonObject{["status"]="authored-baseline-not-official",["games"]=games.Count,["nativeCalendarYear"]=2006,["nonIvyGamesPerTeam"]=56,["ivyGamesPerTeam"]=40,["formalAnnualRulesPendingPhase9"]=true};
        Put("data/league/database-audit.json",Encoding.UTF8.GetBytes(audit.ToJsonString(new(){WriteIndented=true})));
        // Reparse every emitted structure instead of trusting writer offsets.
        if(TeamRoster.Read(rosterStream.ToArray(),true).Count!=309)throw new InvalidDataException("Roster round-trip team count.");
        foreach(var name in names)if(TaggedDat.Read(files["data/database/database_big/"+name]).Rows.Count!=output[name].Rows.Count)throw new InvalidDataException("Player table round-trip.");
        return new(309,records.Length,owned.Count,output["pitcher.dat"].Rows.Count,hasPlaceholders,files,audit);
    }
}
