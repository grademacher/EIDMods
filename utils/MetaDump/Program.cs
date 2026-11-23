using System.Reflection.Metadata; using System.Reflection.PortableExecutable;
// usage: metadump <dll> <typeRegex> [memberRegex]  -> prints "Ns.Type : Base" and matching fields/props/methods
var pe = new PEReader(File.OpenRead(args[0])); var md = pe.GetMetadataReader();
var tre = new System.Text.RegularExpressions.Regex(args[1]);
var mre = args.Length > 2 ? new System.Text.RegularExpressions.Regex(args[2]) : null;
string N(EntityHandle h) => h.Kind switch {
  HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name),
  HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name),
  HandleKind.TypeSpecification => "<generic>", _ => "" };
foreach (var th in md.TypeDefinitions) {
  var t = md.GetTypeDefinition(th); var name = md.GetString(t.Name); var ns = md.GetString(t.Namespace);
  var full = (ns.Length>0? ns+".":"") + name;
  bool typeHit = tre.IsMatch(full);
  var members = new List<string>();
  if (mre != null) {
    foreach (var fh in t.GetFields()) { var f = md.GetFieldDefinition(fh); var n = md.GetString(f.Name); if (!n.StartsWith("NativeFieldInfoPtr") && mre.IsMatch(n)) members.Add("  field " + n); }
    foreach (var ph in t.GetProperties()) { var p = md.GetPropertyDefinition(ph); var n = md.GetString(p.Name); if (mre.IsMatch(n)) members.Add("  prop  " + n); }
    foreach (var mh in t.GetMethods()) { var m = md.GetMethodDefinition(mh); var n = md.GetString(m.Name); if (!n.StartsWith("get_") && !n.StartsWith("set_") && mre.IsMatch(n)) members.Add("  meth  " + n); }
  }
  if (typeHit && (mre == null || members.Count > 0)) { Console.WriteLine($"{full} : {(t.BaseType.IsNil?"":N(t.BaseType))}"); members.Distinct().ToList().ForEach(Console.WriteLine); }
}
