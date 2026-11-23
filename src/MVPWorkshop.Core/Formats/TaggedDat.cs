using System.Globalization;
using System.Text;

namespace MVPWorkshop.Core.Formats;

/// <summary>The game's indexed CSV tables, with explicit row keys and field tags.</summary>
public sealed class TaggedDat
{
    public SortedDictionary<int,string> Labels { get; } = [];
    public SortedDictionary<uint,Dictionary<int,string>> Rows { get; } = [];
    static readonly Encoding TextEncoding;
    static TaggedDat() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); TextEncoding=Encoding.GetEncoding(1252,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback); }

    public static IReadOnlyList<string[]> Csv(string text)
    {
        var rows=new List<string[]>();var row=new List<string>();var cell=new StringBuilder();bool quoted=false;
        for(int i=0;i<text.Length;i++) {
            char c=text[i];
            if(c=='"') {if(quoted && i+1<text.Length && text[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}
            else if(!quoted && (c==',' || c=='\r' || c=='\n')) {
                row.Add(cell.ToString());cell.Clear();
                if(c!=','){if(row.Any(s=>s.Length>0))rows.Add(row.ToArray());row.Clear();if(c=='\r' && i+1<text.Length && text[i+1]=='\n')i++;}
            }else cell.Append(c);
        }
        if(quoted)throw new InvalidDataException("Unterminated CSV string.");
        if(cell.Length>0 || row.Count>0){row.Add(cell.ToString());rows.Add(row.ToArray());}
        return rows;
    }
    public static List<Dictionary<string,string>> CsvRecords(string text)
    {
        var rows=Csv(text);if(rows.Count==0)throw new InvalidDataException("CSV has no header.");
        if(rows[0].Distinct().Count()!=rows[0].Length)throw new InvalidDataException("Duplicate CSV header.");
        return rows.Skip(1).Select(row=>{if(row.Length!=rows[0].Length)throw new InvalidDataException("CSV field count mismatch.");return rows[0].Select((name,i)=>(name,row[i])).ToDictionary(p=>p.name,p=>p.Item2);}).ToList();
    }
    public static string CsvLine(IEnumerable<string> cells)=>string.Join(',',cells.Select(s=>s.IndexOfAny([',','"','\r','\n'])>=0?'"'+s.Replace("\"","\"\"")+'"':s));
    public static TaggedDat Read(byte[] bytes)
    {
        var rows=Csv(TextEncoding.GetString(bytes));if(rows.Count==0)throw new InvalidDataException("Empty tagged table.");
        var result=new TaggedDat();
        static (int,string) Field(string value){int split=value.IndexOf(' ');if(split<1 || !int.TryParse(value[..split],out int tag))throw new InvalidDataException("Invalid field tag.");return(tag,value[(split+1)..]);}
        foreach(var value in rows[0].Where(s=>s.Trim() is not ("" or ";"))){var(tag,label)=Field(value);result.Labels.Add(tag,label);}
        foreach(var row in rows.Skip(1)) {
            if(row[0].Length!=9 || !uint.TryParse(row[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint key))throw new InvalidDataException("Invalid tagged row key.");
            var fields=new Dictionary<int,string>();
            foreach(var value in row.Skip(1).Where(s=>s.Trim() is not ("" or ";"))){var(tag,text)=Field(value);if(!result.Labels.ContainsKey(tag))throw new InvalidDataException("Unknown field tag.");fields.Add(tag,text);}
            if(fields.Count!=result.Labels.Count)throw new InvalidDataException("Missing tagged fields.");
            result.Rows.Add(key,fields);
        }
        return result;
    }
    public byte[] Write()
    {
        var text=new StringBuilder(CsvLine(Labels.Select(p=>$"{p.Key} {p.Value}").Append(";"))+"\r\n");
        foreach(var(key,fields)in Rows){if(fields.Count!=Labels.Count || Labels.Keys.Any(k=>!fields.ContainsKey(k)))throw new InvalidDataException("Incomplete table row.");text.Append(CsvLine(new[]{key.ToString("x9")}.Concat(Labels.Keys.Select(k=>$"{k} {fields[k]}")).Append(";"))).Append("\r\n");}
        return TextEncoding.GetBytes(text.ToString());
    }
}
