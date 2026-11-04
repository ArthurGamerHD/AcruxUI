using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Acrux.ClientApi.Generator
{
    /// <summary>
    /// Adds the Acrux UI ClientApi (wrappers, bridge runtime, shared types) to the client mod as generated source.
    /// MDK packs generated source under MdkGenerated/, so the package never compiles files from the NuGet cache,
    /// whose path relative to the project differs per machine.
    /// </summary>
    [Generator(LanguageNames.CSharp)]
    public sealed class ClientApiGenerator : IIncrementalGenerator
    {
        const string Prefix = "ClientApi/";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Post-initialization sources join the compilation before any generator's main pass, so the XAML view
            // generator and the ADK generators see the Acrux types as if they were project files.
            context.RegisterPostInitializationOutput(Emit);
        }

        static void Emit(IncrementalGeneratorPostInitializationContext context)
        {
            Assembly assembly = typeof(ClientApiGenerator).Assembly;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(Prefix) || !name.EndsWith(".cs"))
                    continue;
                string text;
                using (Stream stream = assembly.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
                // Flat hint names: MDK writes each one as MdkGenerated/<hint name>.
                string hint = "Acrux." + name.Substring(0, name.Length - 3).Replace('/', '.') + ".g.cs";
                context.AddSource(hint, SourceText.From(text, Encoding.UTF8));
            }
        }
    }
}
