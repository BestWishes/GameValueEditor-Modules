using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace GameValueEditor.Modules.Runtime;

internal static class EmbeddedRuntimeDependencies
{
#pragma warning disable CA2255 // Deliberate module-local loader, not host infrastructure.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var assembly = typeof(EmbeddedRuntimeDependencies).Assembly;
        var context = AssemblyLoadContext.GetLoadContext(assembly)!;
        context.Resolving += (_, name) =>
        {
            if (name.Name != "Iced") return null;
            using var stream = assembly.GetManifestResourceStream("ModuleRuntime.Iced.dll");
            return stream == null ? null : context.LoadFromStream(stream);
        };
    }
#pragma warning restore CA2255
}
