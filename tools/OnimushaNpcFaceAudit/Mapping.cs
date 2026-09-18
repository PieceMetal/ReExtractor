using ReExtractor.Core;
using System.Text;
using System.Text.Json;
static class Mapping
{
 public static void Run(PakService pak,string[] files)
 {
  var rows=new List<object>();
  foreach(var p in files.Where(p=>p.EndsWith(".pfb.18")&&p.Contains("/gamedesign/"))) {
   using var s=pak.ReadFile(p);using var r=new BinaryReader(s,Encoding.Unicode);
   s.Position=8;var count=r.ReadInt32();s.Position=32;var offset=r.ReadInt64();var resources=new List<string>();
   for(int i=0;i<count;i++) {s.Position=offset+8*i;var pos=r.ReadInt64();s.Position=pos;var b=new StringBuilder();ushort c;while((c=r.ReadUInt16())!=0)b.Append((char)c);resources.Add(b.ToString().Replace('\\','/').ToLowerInvariant());}
   if(resources.Any(x=>x.Contains("/ch0/")&&x.Contains(".mesh"))) rows.Add(new{path=p,resources});
  }
  Directory.CreateDirectory("artifacts/onimusha-all-faces");File.WriteAllText("artifacts/onimusha-all-faces/mapping.json",JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
 }
}
