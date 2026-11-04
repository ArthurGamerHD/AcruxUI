using System.Reflection;
using System.Runtime.Loader;

// usage: WhitelistCheck <packed mod folder> [more folders...]
string bin = Environment.GetEnvironmentVariable("SE_BIN64") ??
             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/Steam/steamapps/common/SpaceEngineers/Bin64");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string candidate = Path.Combine(bin, name.Name + ".dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};
string compat = Environment.GetEnvironmentVariable("DOTNET_COMPAT") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RiderProjects/se-dotnet-compat/ClientPlugin/bin/Debug/net10.0-windows/DotNetCompat.dll");
Checker.ApplyCompatPatches(compat);
return Checker.Run(bin, args);

static class Checker
{
    public static void ApplyCompatPatches(string path)
    {
        var compat = Assembly.LoadFrom(path);
        var harmony = new HarmonyLib.Harmony("whitelist-check");
        foreach (var type in compat.GetTypes().Where(t => t.Namespace == "ClientPlugin.Patches.Scripting" && t.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), false).Length > 0))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception error)
            {
                Console.WriteLine("  (compat patch " + type.Name + " skipped: " + error.GetBaseException().Message + ")");
            }
        }
    }

    public static int Run(string bin, string[] folders)
    {
        var compiler = VRage.Scripting.MyScriptCompiler.Static;
        var references = new List<string> { Assembly.Load("netstandard").Location };
        foreach (string name in new[] { "Sandbox.Game", "Sandbox.Common", "Sandbox.Graphics", "VRage", "VRage.Library", "VRage.Math", "VRage.Game",
                     "VRage.Render", "VRage.Input", "SpaceEngineers.ObjectBuilders", "SpaceEngineers.Game", "ProtoBuf.Net.Core" })
            references.Add(Path.Combine(bin, name + ".dll"));
        references.Add(Path.Combine(bin, "VRage.Scripting.dll"));
        // se-dotnet-compat adds every loaded System.* assembly; the whitelist, not the references, decides what is allowed.
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        references.AddRange(Directory.GetFiles(runtime, "System.*.dll"));
        references.Add(typeof(object).Assembly.Location);
        compiler.AddReferencedAssemblies(references.Where(File.Exists).ToArray());

        // Same whitelist the game builds at startup.
        var checker = typeof(SpaceEngineers.Game.MySpaceGameDefaultIlChecker);
        using (var batch = compiler.Whitelist.OpenBatch())
            foreach (string step in new[] { "AllowDefaultNamespaces", "AllowSandboxNamespaces", "AllowSpaceEngineersNamespaces" })
                checker.GetMethod(step, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { batch });

        int failures = 0;
        foreach (string folder in folders)
        {
            string scripts = Path.Combine(folder, "Data", "Scripts");
            var files = Directory.GetFiles(scripts, "*.cs", SearchOption.AllDirectories);
            var sources = files.Select(f => new VRage.Scripting.Script(f, File.ReadAllText(f))).ToList();
            var messages = new List<VRage.Scripting.Message>();
            var assembly = compiler.Compile(VRage.Scripting.MyApiTarget.Mod, "check_" + Path.GetFileName(folder), sources, messages, Path.GetFileName(folder)).Result;
            var errors = messages.Where(m => m.IsError).ToList();
            Console.WriteLine("== " + Path.GetFileName(folder) + ": " + files.Length + " files, " + errors.Count + " errors" + (assembly != null ? ", compiled" : ""));
            foreach (var group in errors.GroupBy(m => System.Text.RegularExpressions.Regex.Replace(m.Text, @"^.*?\): ", "")).OrderByDescending(g => g.Count()).Take(40))
                Console.WriteLine("  " + group.Count() + "x " + group.Key + "   e.g. " + group.First().Text.Split(':')[0]);
            if (errors.Count > 0 || assembly == null)
                failures++;
        }
        return failures;
    }
}
