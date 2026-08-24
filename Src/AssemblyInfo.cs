using System.Reflection;

// KSP's own assembly graph. The dependency is what makes KSP load ParallaxContinued BEFORE this mod and
// resolve the reference when it enumerates our types: we now derive from a Parallax type, so without it
// AssemblyLoader fails with "Could not load file or assembly 'ParallaxContinued'" and the mod never loads.
// The name is the one Parallax declares in its own [KSPAssembly], i.e. "Parallax" — NOT its file name.
[assembly: KSPAssembly("BulldozerMod", 1, 0)]
[assembly: KSPAssemblyDependency("Parallax", 1, 0)]

[assembly: AssemblyDescription("Mod for KSP to clear the ground scatters (Parallax rocks) around a rover")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: AssemblyVersion("1.0.*")]
