// The HUD compiles source plugins in Debug, which marks the assembly
// DisableOptimizations: the JIT then runs every method unoptimized (~2-4x
// slower). Declaring the attribute without that flag keeps the JIT optimizing;
// the compiler does not synthesize its own when one is present.
[assembly: System.Diagnostics.Debuggable(
    System.Diagnostics.DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
