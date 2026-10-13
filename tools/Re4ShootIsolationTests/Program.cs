using ReExtractor.Core;
using ReeLib;
using System.Text.Json;

if(args.Length is <3 or >4)throw new ArgumentException("game-directory list-file output-directory [case-name]");
var output=Path.GetFullPath(args[2]);Directory.CreateDirectory(output);
PakService Open(string? folder=null){var p=new PakService();foreach(var f in Directory.GetFiles(args[0],"*.pak",SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))p.AddPak(f);p.LoadListFile(args[1]);if(folder!=null)p.AddFolder(folder);return p;}
const string prefix="natives/stm/_chainsaw/animation/ch/cha0/";
const string target=prefix+"motlist/cha0_wp4100h.motlist.663";
const string bankPath=prefix+"motbank/cha0_wp9999_audit.motbank.3";
void Check(bool value,string message){if(!value)throw new Exception(message);}
string Key(AnimationLayerPair p)=>$"{p.Path}#{p.BaseId}+{p.AdditiveId}:{p.Composition}";
var baseline=Re4AnimationLayerResolver.Resolve(Open());
Check(baseline.Diagnostics.Count==0,"Baseline diagnostics");
var expected=baseline.Pairs.Where(p=>p.Composition==AnimationLayerComposition.PlayerShootEndpoint).ToArray();
Check(expected.Length==198,"Unexpected baseline; review installed resources");
var reports=new List<object>();
byte[] Bank(params string[] references){using var stream=new MemoryStream();using var bank=new MotbankFile(new FileHandler(stream,bankPath));bank.version=3;foreach(var path in references)bank.MotlistItems.Add(new(3){Path=path,BankID=2000});Check(bank.Write(),"Fixture write");return stream.ToArray();}
string Fixture(string name,string path,byte[] bytes){var folder=Path.Combine(output,name);var file=Path.Combine(folder,"re_chunk_fixture",path);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllBytes(file,bytes);return folder;}
void Run(string name,string path,byte[] bytes,string? excluded=null,(string Path,byte[] Bytes)? companion=null)
{
 if(args.Length==4 && args[3]!=name)return;
 var folder=Fixture(name,path,bytes);
 if(companion is {} extra)Fixture(name,extra.Path,extra.Bytes);
 var pak=Open(folder);
 Check(pak.EnumerateFiles().Any(f=>f.Path.Equals(path,StringComparison.OrdinalIgnoreCase)&&f.DecompressedSize==bytes.Length),"Fixture not mounted");
 var actual=Re4AnimationLayerResolver.Resolve(pak);
 var wanted=baseline.Pairs.Where(p=>excluded==null||p.Path!=excluded).Select(Key).ToHashSet();
 var baselinePaths=baseline.Pairs.Select(p=>p.Path).ToHashSet();
 var got=actual.Pairs.Where(p=>baselinePaths.Contains(p.Path)&&(excluded==null||p.Path!=excluded)).Select(Key).ToHashSet();
 Check(wanted.SetEquals(got),name+": unrelated pairs changed");
 var wantedShots=expected.Where(p=>excluded==null||p.Path!=excluded).Select(Key).ToHashSet();
 Check(wantedShots.SetEquals(actual.Pairs.Where(p=>p.Composition==AnimationLayerComposition.PlayerShootEndpoint).Select(Key)),name+": lost or speculative shoot pairs");
 var wantedUsage=baseline.Usages!.Where(u=>excluded==null||u.Path!=excluded).Select(u=>$"{u.Path}#{u.Id}:{u.Name}:{u.Usage.Kind}").ToHashSet();
 var gotUsage=actual.Usages!.Where(u=>baselinePaths.Contains(u.Path)&&(excluded==null||u.Path!=excluded)).Select(u=>$"{u.Path}#{u.Id}:{u.Name}:{u.Usage.Kind}").ToHashSet();
 Check(wantedUsage.SetEquals(gotUsage),name+": unrelated classifications changed");
 Check(actual.Diagnostics.Count>0,name+": missing diagnostic");
 Check(actual.Diagnostics.Any(d=>d.Contains(path,StringComparison.OrdinalIgnoreCase)),name+": diagnostic lacks affected resource");
 if(excluded!=null)Check(!actual.Pairs.Any(p=>p.Path==excluded&&p.Composition==AnimationLayerComposition.PlayerShootEndpoint),"Bad list received shoot pairs");
 foreach(var list in new[]{prefix+"motlist/cha0_wp4000h_ex.motlist.663",target}.Where(p=>p!=excluded))
   Check(actual.Pairs.Count(p=>p.Path==list&&p.AdditiveId==500&&p.Composition==AnimationLayerComposition.PlayerShootEndpoint)==3,"Feedback target lost shoot pairing");
 var count=actual.Pairs.Count(p=>p.Composition==AnimationLayerComposition.PlayerShootEndpoint);
 reports.Add(new{Name=name,ShootPairs=count,PreservedUnrelatedPairs=wanted.Count,Status="PASS",actual.Diagnostics});
 Console.WriteLine($"PASS {name} shootPairs={count} unrelatedPairs={wanted.Count}");
}
Run("malformed-bank",bankPath,new byte[8]);
Run("missing-reference",bankPath,Bank("_Chainsaw/Animation/ch/cha0/motlist/cha0_wp_missing_test.motlist"));
Run("mixed-valid-missing-reference",bankPath,Bank("_Chainsaw/Animation/ch/cha0/motlist/cha0_wp4100H.motlist","_Chainsaw/Animation/ch/cha0/motlist/cha0_wp_missing_test.motlist"));
Run("malformed-list",target,new byte[8],target);
using(var source=Open().ReadFile(target))
 Run("atomic-bank-context",bankPath,Bank("_Chainsaw/Animation/ch/cha0/motlist/cha0_wp9998_audit.motlist","_Chainsaw/Animation/ch/cha0/motlist/cha0_wp_missing_test.motlist"),
   companion:(prefix+"motlist/cha0_wp9998_audit.motlist.663",source.ToArray()));
Check(reports.Count>0,"No matching test case");
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{BaselineShootPairs=expected.Length,Cases=reports,ReporterAssetsUsed=false},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("SHOOT_ERROR_ISOLATION_PASS");
