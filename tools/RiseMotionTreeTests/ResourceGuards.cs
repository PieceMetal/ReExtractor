using ReExtractor.Core;
using ReeLib;
using ReeLib.Motbank;
using System.Text.Json;

static class ResourceGuards
{
 public static void Run(PakService pak,string output)
 {
  const string treePath="natives/stm/player/mot/plw_bow_tree_51.motlist.528";
  const string bankPath="natives/stm/player/mot/plw_bow_bank.motbank.3";
  using var s=pak.ReadFile(treePath);using var tree=new MotlistFile(new FileHandler(s,treePath));tree.Read();
  var rows=new List<string>();
  void Check(bool good,string name){if(!good)throw new Exception(name);rows.Add(name);Console.WriteLine("PASS "+name);}
  var original=RiseMotionTrees.Describe(tree,treePath,0,pak.ReadFile);
  Check(original.Available&&original.Sources.All(s=>s.ListPath.Contains("/plw_bow_100.")),"default player bank does not use follower payloads");
  var follower=RiseMotionTrees.Describe(tree,treePath,0,pak.ReadFile,new MotionTreeSettings{BankPath="player/mot/fbw_bow_bank.motbank"});
  Check(follower.Available&&follower.Sources.All(s=>s.ListPath.Contains("/fbw_bow_100.")),"explicit follower bank remains distinct");
  using var bs=pak.ReadFile(bankPath);using var bank=new MotbankFile(new FileHandler(bs,bankPath));bank.Read();
  void Mount(string name){var root=Path.Combine(output,name);var destination=Path.Combine(root,bankPath.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
   using var buffer=new MemoryStream();if(!bank.WriteTo(new FileHandler(buffer,bankPath),false))throw new InvalidDataException("Fixture serialization failed");File.WriteAllBytes(destination,buffer.ToArray());pak.AddFolder(root);}
  bank.MotlistItems.Add(new MotbankEntry(3){BankID=999,Path="player/mot/missing_optional_test.motlist"});Mount("optional");
  var optional=RiseMotionTrees.Describe(tree,treePath,0,pak.ReadFile);
  Check(optional.Available,"missing optional library does not poison a valid tree");
  bank.MotlistItems.RemoveAll(e=>e.BankID==100);Mount("missing-source");
  var missing=RiseMotionTrees.Describe(tree,treePath,0,pak.ReadFile);
  Check(!missing.Available&&missing.Description.Contains("Bank 100"),"resource remount invalidates cache and missing player bank is not borrowed from a follower");
  File.WriteAllText(Path.Combine(output,"resource-guards.json"),JsonSerializer.Serialize(rows));
 }
}
