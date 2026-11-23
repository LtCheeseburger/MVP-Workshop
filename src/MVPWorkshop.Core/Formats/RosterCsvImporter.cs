using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MVPWorkshop.Core.Formats;

/// <summary>Imports supplied roster facts without treating estimated ratings or missing fields as sourced facts.</summary>
public static class RosterCsvImporter
{
    public sealed record Result(JsonObject Players,JsonObject Registry,JsonObject Audit);
    static string Norm(string s)=>string.Concat(s.Normalize(NormalizationForm.FormD).Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark&&char.IsLetterOrDigit(c))).ToLowerInvariant();
    static uint U32(byte[] b,int p)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p,4));
    static string Match(Dictionary<string,string> r)=>r["school"]+"|"+r["first"]+"|"+r["last"];
    static bool Pitcher(Dictionary<string,string> r)=>r["pos"] is "P" or "RHP" or "LHP";
    static int Int(Dictionary<string,string> r,string field,int fallback)=>int.TryParse(r.GetValueOrDefault(field),out int n)?n:fallback;
    static string EngineName(string s,int limit){
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding=Encoding.GetEncoding(1252);s=encoding.GetString(encoding.GetBytes(s));
        return s.Length<=limit?s:s[..limit].TrimEnd();
    }
    public static Result Import(string csvDirectory,string league,string game,JsonObject? registry=null,string? supplementFile=null)
    {
        var teams=LeagueDatabase.ReadTeams(league);
        var aliases=new Dictionary<string,string>(StringComparer.Ordinal);
        void Alias(string name,string id){string n=Norm(name);if(aliases.TryGetValue(n,out var old)&&old!=id)throw new InvalidDataException("Ambiguous school alias: "+name);aliases[n]=id;}
        var metadata=TaggedDat.CsvRecords(File.ReadAllText(Path.Combine(league,"team-metadata.csv")));
        foreach(var r in metadata)Alias(r["athletics_name"],r["team_id"]);
        var extra=JsonNode.Parse(File.ReadAllText(Path.Combine(league,"metadata-aliases.json")))!.AsObject();
        foreach(var(id,names)in extra)foreach(var name in names!.AsArray())Alias(name!.ToString(),id);
        Alias("Connecticut","ncaa:164");
        foreach(var(name,id)in new Dictionary<string,string>{{"Queens","ncaa:11504"},{"Cal Baptist","ncaa:30135"},{"FIU","ncaa:231"},{"Penn","ncaa:540"},{"UMass","ncaa:400"},{"Hawaii","ncaa:277"},{"San Jose State","ncaa:630"},{"LIU","ncaa:361"},{"Grambling State","ncaa:261"},{"New Orleans","ncaa:474"},{"Southeastern Louisiana","ncaa:655"},{"Louisiana-Monroe","ncaa:498"},{"Seattle","ncaa:1356"},{"USC Upstate","ncaa:10411"}})Alias(name,id);
        List<Dictionary<string,string>> Read(string name)=>TaggedDat.CsvRecords(File.ReadAllText(Path.Combine(csvDirectory,name))).ToList();
        var rows=Read("roster_final.csv");var ratings=Read("ratings_2027.csv").ToDictionary(Match);
        var sourceRows=Read("roster_sources.csv").ToDictionary(r=>r["school"]);
        var appearances=Read("appearance.csv").ToDictionary(Match);
        JsonObject? supplement=supplementFile is null?null:JsonNode.Parse(File.ReadAllText(supplementFile))!.AsObject();
        if(supplement is not null){
            if((int?)supplement["format"]!=1)throw new InvalidDataException("Unsupported online roster supplement.");
            foreach(var school in supplement["teams"]!.AsArray()){
                string id=school!["teamId"]!.ToString(),name=school["school"]!.ToString();int season=(int)school["season"]!;
                if(!teams.Any(t=>t.Id==id)||season is not (2026 or 2027))throw new InvalidDataException("Unknown supplemental school/season.");
                Alias(name,id);
                if(rows.Any(r=>aliases.GetValueOrDefault(Norm(r["school"]))==id))throw new InvalidDataException("Supplement would overwrite a supplied school roster: "+name);
                sourceRows[name]=new(){["school"]=name,["roster_url"]=school["sourceUrl"]!.ToString()};
                foreach(var fact in school["players"]!.AsArray()){
                    var row=fact!.AsObject().ToDictionary(p=>p.Key,p=>p.Value?.ToString()??"");row["school"]=name;row["source"]=season==2027?"roster_2027":"fallback_site_2026";
                    rows.Add(row);
                    // Online additions have sourced biographies, not measured gameplay ratings. A continuing player keeps an already supplied rating.
                    ratings.TryAdd(Match(row),new(){["school"]=name,["first"]=row["first"],["last"]=row["last"],["pos"]=row["pos"],["rating_source"]="native_role_template",["stats_key"]="",["contact"]="50",["power"]="50",["discipline"]="5",["speed"]="50",["stealing"]="5",["take_fb"]="6",["chase_fb"]="6",["miss_fb"]="6"});
                }
            }
        }
        // A name-only statistics join cannot identify two school-specific players.
        // Keep supplied values as evidence, but never apply the shared ratings to both.
        var suppliedRatings=ratings.ToDictionary(p=>p.Key,p=>new Dictionary<string,string>(p.Value));
        var quarantinedStats=new JsonArray();
        foreach(var shared in ratings.Values.Where(r=>r.GetValueOrDefault("stats_key","")!=""&&r["rating_source"]!="fictional").GroupBy(r=>r["stats_key"]).Where(g=>g.Count()>1).ToArray()) {
            foreach(var rating in shared) {
                quarantinedStats.Add(new JsonObject{["player"]=Match(rating),["statsKey"]=shared.Key,["decision"]="Do not apply ambiguous shared statistics; retain explicit native-role estimate until a school-specific statistical identity is verified"});
                rating["rating_source"]="native_role_template_ambiguous_stats";rating["stats_key"]="";
                foreach(var (field,value) in new[]{("contact","50"),("power","50"),("discipline","5"),("speed","50"),("stealing","5"),("take_fb","6"),("chase_fb","6"),("miss_fb","6")})rating[field]=value;
                foreach(var field in new[]{"fb_velocity","fb_control","stamina"})rating.Remove(field);
            }
        }
        var groups=new Dictionary<string,List<Dictionary<string,string>>>();
        foreach(var row in rows){if(!aliases.TryGetValue(Norm(row["school"]),out string? id))throw new InvalidDataException("Unmapped roster school: "+row["school"]);if(!ratings.ContainsKey(Match(row)))throw new InvalidDataException("Missing ratings: "+Match(row));if(row["source"] is not ("roster_2027" or "fallback_site_2026" or "fallback_bref_2026"))throw new InvalidDataException("Unrecognized roster provenance: "+row["source"]);if(!groups.TryGetValue(id,out var list))groups[id]=list=[];list.Add(row);}
        if(rows.Select(Match).Distinct().Count()!=rows.Count)throw new InvalidDataException("Duplicate roster identity within a school.");
        var missing=teams.Where(t=>!groups.ContainsKey(t.Id)).Select(t=>t.Id).ToArray();
        var allowed=new[]{"ncaa:408","ncaa:470","ncaa:11740","ncaa:766"};
        if(missing.Except(allowed).Any())throw new InvalidDataException("Unexpected missing school roster.");
        var data=Path.Combine(game,"data/database/database_big");var general=TaggedDat.Read(File.ReadAllBytes(Path.Combine(data,"attrib.dat")));
        var pitching=TaggedDat.Read(File.ReadAllBytes(Path.Combine(data,"pitcher.dat")));
        var source=File.ReadAllBytes(Path.Combine(data,"roster.bin"));int offset=4;var slots=new List<(uint Key,uint Word)>();
        var allSlots=new List<(uint Key,uint Word)>();
        for(int team=0;team<U32(source,0);team++){int count=(int)U32(source,offset+192);for(int i=0;i<count;i++){var pair=(U32(source,offset+196+i*8),U32(source,offset+200+i*8));allSlots.Add(pair);if(team==1)slots.Add(pair);}offset+=487+count*8;}
        if(slots.Count!=25)throw new InvalidDataException("Expected 25-slot native role template.");
        int Position(uint key)=>int.Parse(general.Rows[key][5],CultureInfo.InvariantCulture);
        uint Template(int role)=>general.Rows.First(p=>p.Key!=0xF58F3C1B&&int.Parse(p.Value[5],CultureInfo.InvariantCulture)==role&&pitching.Rows.ContainsKey(p.Key)==(role is 0 or 10)).Key;
        // Keep this team's exact two batting-order bitfields. Borrowing a DH word
        // from another team duplicates batting slots. Only clear its pitcher-role bits.
        var roles=slots.Select((s,i)=>(Index:i,Role:(s.Word&15)==10?9:Position(s.Key),Word:(s.Word&15)==10?s.Word&0xffffu:s.Word)).ToArray();
        registry=(JsonObject?)registry?.DeepClone()??new JsonObject{["format"]=1,["nextKey"]="90000000",["entries"]=new JsonObject()};
        if((int?)registry["format"]!=1)throw new InvalidDataException("Unsupported identity registry.");
        var entries=registry["entries"]!.AsObject();var used=general.Rows.Keys.ToHashSet();
        foreach(var(_,node)in entries)if(!used.Add(Convert.ToUInt32(node!["key"]!.ToString(),16)))throw new InvalidDataException("Duplicate/reserved registry key.");
        uint next=Convert.ToUInt32(registry["nextKey"]!.ToString(),16);
        // Allocate every supplied identity, including reserves, once. Subsequent imports reuse the ledger.
        foreach(var(id,list)in groups.OrderBy(p=>p.Key,StringComparer.Ordinal))foreach(var row in list.OrderBy(Match,StringComparer.Ordinal)){
            string lookup=id+"|"+Norm(row["first"])+"|"+Norm(row["last"]);
            if(entries[lookup] is null){while(!used.Add(next))next++;entries[lookup]=new JsonObject{["id"]="mvp27-player:"+next.ToString("x8"),["key"]=(next++).ToString("x8"),["firstName"]=row["first"],["lastName"]=row["last"],["initialTeam"]=id};}
        }
        registry["nextKey"]=next.ToString("x8");
        var output=new JsonObject();var rotationSlots=new JsonObject();var selections=new JsonArray();var reserves=new JsonArray();var adjustments=new JsonArray();var shortTeams=new JsonArray();
        var provenance=new Dictionary<string,int>();var ratingSources=new Dictionary<string,int>();int selectedCount=0;
        foreach(var team in teams){
            if(!groups.TryGetValue(team.Id,out var candidates))continue;
            if(candidates.Count<9)throw new InvalidDataException("Unplayable short roster: "+team.Name);
            if(candidates.Count<25)shortTeams.Add(new JsonObject{["team"]=team.Id,["school"]=team.Name,["players"]=candidates.Count});
            double Score(Dictionary<string,string> row,int role){var rating=ratings[Match(row)];return role is 0 or 10?Int(rating,"fb_control",0)+Int(rating,"stamina",0)+Int(rating,"fb_velocity",0):Int(rating,"contact",0)+Int(rating,"power",0)+Int(rating,"speed",0)/4.0;}
            bool Fits(Dictionary<string,string> row,int role){string p=row["pos"];if(role is 0 or 10)return Pitcher(row);if(Pitcher(row))return row["two_way"]=="1";if(role==9)return true;return p switch{"C"=>role==1,"1B"=>role==2,"2B"=>role==3,"3B"=>role==4,"SS"=>role==5,"INF"=>role is >=2 and <=5,"OF"=>role is >=6 and <=8,"DH"=>role==9,_=>true};}
            var remaining=candidates.ToList();var assigned=new List<(int Index,Dictionary<string,string> Row,JsonObject Player)>();
            // Fill the nine native batting/fielding starters first, then pitchers, then bench roles.
            var order=roles.OrderBy(r=>(r.Word&15)is >=2 and <=10?0:r.Role is 0 or 10?1:2).ThenBy(r=>r.Index);
            foreach(var target in order.Take(Math.Min(25,candidates.Count))){
                var slot=target;
                if(slot.Role is 0 or 10 && !remaining.Any(Pitcher))slot=(slot.Index,9,0);
                var pool=remaining.Where(r=>slot.Role is 0 or 10 || !Pitcher(r) || remaining.Count(Pitcher)>Math.Min(4,candidates.Count-9)).ToList();
                var eligible=pool.Where(r=>Fits(r,slot.Role)).ToList();bool inferred=eligible.Count==0;
                if(pool.Count==0)pool=remaining;
                if(inferred && slot.Role is not (0 or 10) && pool.Any(r=>!Pitcher(r)))pool=pool.Where(r=>!Pitcher(r)).ToList();
                var row=(inferred?pool:eligible).OrderByDescending(r=>Score(r,slot.Role)).ThenBy(Match,StringComparer.Ordinal).First();remaining.Remove(row);
                var rating=ratings[Match(row)];string lookup=team.Id+"|"+Norm(row["first"])+"|"+Norm(row["last"]);var identity=entries[lookup]!;
                bool pitcher=Pitcher(row);int nativePosition=row["pos"] switch{"RHP" or "LHP" or "P"=>slot.Role==0?0:10,"C"=>1,"1B"=>2,"2B"=>3,"3B"=>4,"SS"=>5,"DH"=>9,_=>slot.Role is 0 or 10?9:slot.Role};
                int templateRole=pitcher?(nativePosition==0?0:10):nativePosition;
                string first=EngineName(row["first"],11),last=EngineName(row["last"],14);
                if(first.Length==0||last.Length==0)throw new InvalidDataException("Empty engine player name.");
                int sourceClass=Int(row,"class_year",1);if(sourceClass is <1 or >5)throw new InvalidDataException("Invalid source class: "+Match(row));
                var attrib=new JsonObject{["5"]=nativePosition.ToString(),["9"]=Math.Min(sourceClass-1,3).ToString()};
                void Field(JsonObject dest,string tag,Dictionary<string,string> input,string field,int low,int high){if(input.TryGetValue(field,out var value)&&value!=""){if(!int.TryParse(value,out int number)||number<low||number>high)throw new InvalidDataException($"Invalid {field}: {value} for {Match(row)}");dest[tag]=value;}}
                Field(attrib,"2",row,"jersey",0,99);Field(attrib,"31",rating,"discipline",0,15);Field(attrib,"33",rating,"stealing",0,15);Field(attrib,"35",rating,"speed",0,100);
                foreach(var(field,tag)in new[]{("bats","3"),("throws","4")})if(row[field] is "R" or "L" or "S")attrib[tag]=(row[field]=="R"?0:row[field]=="L"?1:2).ToString();
                // Missing biographical values remain in raw source; do not guess conversion rules for measurements.
                if(appearances.TryGetValue(Match(row),out var appearance)&&int.TryParse(appearance["skintone"],out int skin)&&skin is >=0 and <=3)attrib["13"]=skin.ToString();
                var split=new JsonObject();foreach(var(field,tag,high)in new[]{("contact","2",100),("power","3",100),("chase_fb","20",15),("take_fb","23",15),("miss_fb","26",15)})Field(split,tag,rating,field,0,high);
                int season=row["source"]=="roster_2027"?2027:2026;string sourceUrl=sourceRows[row["school"]]["roster_url"];
                if(row["source"]=="fallback_bref_2026")sourceUrl="user-supplied:roster_final.csv (fallback_bref_2026)";
                var player=new JsonObject{["id"]=identity["id"]!.DeepClone(),["key"]=identity["key"]!.DeepClone(),["firstName"]=first,["lastName"]=last,["fullFirstName"]=row["first"],["fullLastName"]=row["last"],["source"]=sourceUrl,["sourceKind"]=row["source"],["season"]=season,["templatePlayerKey"]=Template(templateRole).ToString("x8"),["rosterWord"]=slot.Word,["attrib.dat"]=attrib,["lhattrib.dat"]=split,["rhattrib.dat"]=split.DeepClone(),["modernClass"]=sourceClass-1,["suppliedRatings"]=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(suppliedRatings[Match(row)])),["ratingSource"]=rating["rating_source"],["sourceFacts"]=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(row)),["sourceRatings"]=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(rating))};
                if(pitcher){var pitch=new JsonObject();foreach(var(field,tag)in new[]{("stamina","2"),("fb_control","6"),("fb_velocity","7")})Field(pitch,tag,rating,field,0,127);player["pitcher.dat"]=pitch;}
                if(inferred||row["pos"] is "" or "INF" or "OF" or "UT"||first!=row["first"]||last!=row["last"])adjustments.Add(new JsonObject{["id"]=identity["id"]!.DeepClone(),["school"]=team.Name,["sourcePosition"]=row["pos"],["gamePosition"]=nativePosition,["lineupRole"]=slot.Role,["roleFallback"]=inferred,["fullName"]=row["first"]+" "+row["last"],["engineName"]=first+" "+last});
                assigned.Add((slot.Index,row,player));provenance[row["source"]]=provenance.GetValueOrDefault(row["source"])+1;ratingSources[rating["rating_source"]]=ratingSources.GetValueOrDefault(rating["rating_source"])+1;selectedCount++;
            }
            var ordered=assigned.OrderBy(a=>a.Index).ToArray();
            var rotation=new List<int>();
            foreach(int nativeSlot in new[]{21,16,19}){
                int index=Array.FindIndex(ordered,a=>a.Index==nativeSlot&&Pitcher(a.Row));
                if(index<0)index=Enumerable.Range(0,ordered.Length).First(i=>Pitcher(ordered[i].Row)&&!rotation.Contains(i));
                if(rotation.Contains(index))throw new InvalidDataException("Duplicate pitching rotation slot.");
                rotation.Add(index);ordered[index].Player["attrib.dat"]!["5"]="0";
            }
            rotationSlots[team.Id]=new JsonArray(rotation.Select(i=>JsonValue.Create(i)).ToArray());
            output[team.Id]=new JsonArray(ordered.Select(a=>(JsonNode)a.Player).ToArray());
            selections.Add(new JsonObject{["team"]=team.Id,["school"]=team.Name,["sourcePlayers"]=candidates.Count,["selected"]=assigned.Count,["pitchers"]=assigned.Count(a=>Pitcher(a.Row))});
            foreach(var row in remaining)reserves.Add(new JsonObject{["team"]=team.Id,["school"]=row["school"],["first"]=row["first"],["last"]=row["last"],["reason"]="25-player capacity; balanced native role selection"});
        }
        var inputHashes=new JsonObject();foreach(var path in Directory.EnumerateFiles(csvDirectory,"*.csv").OrderBy(p=>p,StringComparer.Ordinal))inputHashes[Path.GetFileName(path)]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if(supplementFile is not null)inputHashes["online-rosters.json"]=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(supplementFile)));
        var sharedStats=ratings.Values.Where(r=>r["stats_key"]!=""&&r["rating_source"]!="fictional").GroupBy(r=>r["stats_key"]).Where(g=>g.Count()>1).Select(g=>new JsonObject{["statsKey"]=g.Key,["schools"]=new JsonArray(g.Select(r=>JsonValue.Create(r["school"])).ToArray())}).ToArray();
        var audit=new JsonObject{["format"]=1,["sourceSchools"]=groups.Count,["sourcePlayers"]=rows.Count,["selectedRealPlayers"]=selectedCount,["maxSquadSize"]=25,["inputSha256"]=inputHashes,["selections"]=selections,["shortSquads"]=shortTeams,["reserves"]=reserves,["adjustments"]=adjustments,["rosterSources"]=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(provenance)),["ratingSources"]=JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(ratingSources)),["ambiguousSharedStatsLinks"]=new JsonArray(sharedStats.Select(x=>(JsonNode)x).ToArray()),["quarantinedSharedStatsRatings"]=quarantinedStats,["unappliedSourceFields"]="height, weight, redshirt and hometown remain in source facts; unknown game fields retain native template defaults",["ratingMethod"]="Supplied ratings, identical left/right splits; missing pitch repertoire/fielding values retain native role templates. Source stats links are not independently verified.",["productionReady"]=false};
        audit["onlineSources"]=supplement?["teams"] is JsonArray online?new JsonArray(online.Select(t=>{var summary=t!.DeepClone().AsObject();summary["publishedPlayers"]=summary["players"]!.AsArray().Count;summary.Remove("players");return (JsonNode)summary;}).ToArray()):null;
        var ledger=new JsonObject{["format"]=1,["teams"]=output,["rotationSlots"]=rotationSlots,["placeholderTeams"]=new JsonArray(missing.Select(id=>JsonValue.Create(id)).ToArray()),["productionReady"]=false,["importAudit"]=audit.DeepClone()};
        return new(ledger,registry,audit);
    }
}

