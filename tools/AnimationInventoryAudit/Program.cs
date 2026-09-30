using ReExtractor.Core;
using ReeLib;
using ReeLib.Pak;
using System.Text.Json;

var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var inputs = JsonSerializer.Deserialize<Input[]>(File.ReadAllText(args[0]), options)!;
bool inventoryOnly = args.Contains("--inventory-only");
bool banksOnly = args.Contains("--banks-only");
bool headersOnly = args.Contains("--headers-only");
foreach (var input in inputs)
{
    Directory.CreateDirectory(input.Output);
    var pak = new PakService();
    var pakPaths=Directory.GetFiles(input.GameDirectory,"*.pak",SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    foreach (var p in pakPaths)
        if (new FileInfo(p).Length > 0) pak.AddPak(p);
    pak.LoadListFile(input.ListPath);
    var files = pak.EnumerateFiles();
    var lists = files.Where(f => f.Path.Contains(".motlist.", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path).ToArray();
    var banks = files.Where(f => f.Path.Contains(".motbank.", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path).ToArray();
    var fsms = files.Where(f => f.Path.Contains(".motfsm2.", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path).ToArray();
    File.WriteAllText(Path.Combine(input.Output,"inventory.json"), JsonSerializer.Serialize(new {input.Name,input.GameDirectory,input.ListPath,pak.PakCount,
        ResolvedFiles=files.Count, Lists=lists,Banks=banks,Fsms=fsms, UnresolvedHashesExcluded=true},options));
    Console.WriteLine($"INVENTORY {input.Name} paks={pak.PakCount} lists={lists.Length} bytes={lists.Sum(f=>f.DecompressedSize)} banks={banks.Length} fsms={fsms.Length}");
    if(inventoryOnly) continue;
    var archives=new Dictionary<string,PakFile>();
    MemoryStream Read(PakEntryInfo entry)
    {
        if(!archives.TryGetValue(entry.SourcePak,out var archive))
        {
            var source=pakPaths.Single(p=>Path.GetFileName(p)==entry.SourcePak);
            archive=new PakFile{filepath=source,IncludeUnknowns=true};archive.ReadContents(source,new Dictionary<ulong,string>());
            archives.Add(entry.SourcePak,archive);
        }
        var hash=PakUtils.GetFilepathHash(entry.Path);
        var item=archive.Entries.First(e=>e.CombinedHash==hash);
        var result=new MemoryStream((int)entry.DecompressedSize);archive.Read(item,result);result.Position=0;return result;
    }
    var output=Path.Combine(input.Output,"motions.jsonl");
    // Each complete list is a checkpoint. A process interruption can be resumed.
    var done=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if(!banksOnly && File.Exists(output)) foreach(var line in File.ReadLines(output))
        try { using var doc=JsonDocument.Parse(line);done.Add(doc.RootElement.GetProperty("Path").GetString()!); } catch(JsonException) { }
    using TextWriter writer=banksOnly ? TextWriter.Null : new StreamWriter(output,append:true){AutoFlush=true};
    int index=0;
    foreach(var entry in banksOnly ? Array.Empty<PakEntryInfo>() : lists)
    {
        index++;
        if(done.Contains(entry.Path))continue;
        try
        {
            using var s=Read(entry);
            if(headersOnly)
            {
                using var handler=new FileHandler(s,entry.Path);
                var header=new ReeLib.Motlist.Header();header.Read(handler);
                if(header.magic!=MotlistFile.Magic||header.numMots<0||header.numMots>s.Length/8)throw new InvalidDataException("Invalid motion-list header");
                handler.Seek(header.pointersOffset);var offsets=handler.ReadArray<long>(header.numMots);
                var metadata=new List<object>();
                handler.Seek(header.motionIndicesOffset);
                var indices=offsets.Select(_=>{var index=new ReeLib.Motlist.MotIndex(header.version);index.Read(handler);return index;}).ToArray();
                for(int i=0;i<offsets.Length;i++)
                {
                    ReeLib.Mot.MotHeader? mh=null;string? type=null;
                    if(offsets[i]!=0)
                    {
                        var local=handler.WithOffset(offsets[i]);
                        if(local.ReadInt(0)==1)type="MotFileLink";
                        else if(local.ReadUInt(4)==MotFile.Magic){local.Seek(0);mh=new();mh.Read(local);type="MotFile";}
                        else type="OtherEmbedded";
                    }
                    metadata.Add(new{Index=i,Id=indices[i].motNumber,Flags=indices[i].flags.ToString(),Mask=indices[i].jointMaskId,
                        Type=type,Name=mh?.motName,Version=mh==null?(int?)null:(int)mh.version,Frames=mh?.frameCount,BoneCount=mh?.boneCount,TrackCount=mh?.boneClipCount,Tracks=(object?)null});
                }
                writer.WriteLine(JsonSerializer.Serialize(new{Path=entry.Path,Status="HEADER_ONLY",Version=(int)header.version,BasePath=header.BaseMotListPath,Motions=metadata}));
                if(index%25==0||index==lists.Length)Console.WriteLine($"HEADERS {input.Name} {index}/{lists.Length}");
                continue;
            }
            using var list=new MotlistFile(new FileHandler(s,entry.Path));
            if(!list.Read())throw new InvalidDataException("Parser returned false");
            var motions=list.Motions.Select((m,i)=>new {Index=i,Id=m.motNumber,Flags=m.flags.ToString(),Mask=m.jointMaskId,
                Type=m.MotFile?.GetType().Name,Name=m.MotFile?.Name,
                Version=m.MotFile is MotFile mot?(int?)mot.Header.version:null,
                Frames=m.MotFile is MotFile mt?(float?)mt.Header.frameCount:null,
                BoneCount=m.MotFile is MotFile mb?mb.Bones.Count:0,
                Tracks=m.MotFile is MotFile mc?mc.BoneClips.Select(c=>new {Name=c.ClipHeader.boneName,Hash=c.ClipHeader.boneHash,T=c.HasTranslation,R=c.HasRotation}).ToArray():null
            }).ToArray();
            writer.WriteLine(JsonSerializer.Serialize(new {Path=entry.Path,Status="PARSED",Version=(int)list.Header.version,BasePath=list.Header.BaseMotListPath,Motions=motions}));
        }
        catch(Exception ex){writer.WriteLine(JsonSerializer.Serialize(new{Path=entry.Path,Status="ERROR",Error=ex.GetType().Name+": "+ex.Message}));}
        if(index%25==0||index==lists.Length)Console.WriteLine($"SCAN {input.Name} {index}/{lists.Length}");
    }
    using var bankWriter=new StreamWriter(Path.Combine(input.Output,"banks.jsonl")){AutoFlush=true};
    foreach(var entry in banks)
        try {using var s=Read(entry);using var bank=new MotbankFile(new FileHandler(s,entry.Path));if(!bank.Read())throw new InvalidDataException("Parser returned false");
            bankWriter.WriteLine(JsonSerializer.Serialize(new{Path=entry.Path,Status="PARSED",bank.JmapPath,Entries=bank.MotlistItems.Select(b=>new{b.BankID,b.BankType,b.Path}).ToArray()}));}
        catch(Exception ex){bankWriter.WriteLine(JsonSerializer.Serialize(new{Path=entry.Path,Status="ERROR",Error=ex.Message}));}
    Console.WriteLine($"DONE {input.Name}");
    foreach(var archive in archives.Values)archive.Dispose();
}
record Input(string Name,string GameDirectory,string ListPath,string Output);
