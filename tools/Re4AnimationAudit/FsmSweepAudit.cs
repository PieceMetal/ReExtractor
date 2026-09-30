using ReExtractor.Core;
using ReeLib;
using System.Collections;
using System.Text.Json;

static class FsmSweepAudit
{
    public static void Run(PakService pak)
    {
        const string root="artifacts/preview-fix/full-audit/RE4";
        Directory.CreateDirectory(root);
        using var ws=new Workspace(new GameConfig(new GameIdentifier("re4")));
        object? Value(object? value,int depth=0)
        {
            if(depth>7)return "[depth limit]";
            if(value is RszInstance instance)return new {Type=instance.RszClass.name,Fields=instance.IndexedFields.ToDictionary(x=>x.field.name,x=>Value(instance.Values[x.index],depth+1))};
            if(value is string||value==null||value is bool)return value;
            if(value is IEnumerable sequence)return sequence.Cast<object>().Select(x=>Value(x,depth+1)).ToArray();
            if(value.GetType().IsPrimitive||value is decimal)return value;
            return value.ToString();
        }
        const string prefabPath="natives/stm/_chainsaw/appsystem/character/ch0a0z0/ch0a0z0_body.pfb.17";
        using(var s=pak.ReadFile(prefabPath))using(var pfb=new PfbFile(ws.RszFileOption,new FileHandler(s,prefabPath)))
        {
            pfb.Read();
            var items=pfb.GetAllRSZFiles().SelectMany(r=>r.InstanceList).Where(i=>i.RszClass.name is "via.motion.Motion" or "via.motion.MotionFsm2").Select(i=>Value(i)).ToArray();
            File.WriteAllText(root+"/player-layer-context.json",JsonSerializer.Serialize(new{Path=prefabPath,Items=items},new JsonSerializerOptions{WriteIndented=true}));
        }
        using var writer=new StreamWriter(root+"/fsms.jsonl"){AutoFlush=true};
        var files=pak.EnumerateFiles().Where(f=>f.Path.Contains(".motfsm2.")).OrderBy(f=>f.Path).ToArray();
        int index=0;
        foreach(var file in files)
        {
            try
            {
                using var s=pak.ReadFile(file.Path);using var fsm=new Motfsm2File(ws.RszFileOption,new FileHandler(s,file.Path));
                if(!fsm.Read())throw new InvalidDataException("Parser returned false");
                writer.WriteLine(JsonSerializer.Serialize(new{Path=file.Path,Status="PARSED",Nodes=fsm.BhvtFile.Nodes.Select(n=>new{n.Name,Id=n.ID.ToString(),Parent=n.ParentID.ToString(),Actions=n.Actions.Actions.Select(a=>Value(a.Instance)).ToArray()}).ToArray()}));
            }
            catch(Exception ex){writer.WriteLine(JsonSerializer.Serialize(new{Path=file.Path,Status="ERROR",Error=ex.GetType().Name+": "+ex.Message}));}
            if(++index%25==0||index==files.Length)Console.WriteLine($"FSM_SCAN {index}/{files.Length}");
        }
    }
}
