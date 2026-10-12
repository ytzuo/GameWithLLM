# GameWithLLM Unity 6000.3 compatibility patch

This embedded package is Unity Collections 2.6.6, the version bundled with
Unity 6000.3.19f1. `NativeList<T>.AsReadOnly` and `AsParallelReader` use
`NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray(...).AsReadOnly()`
when collection safety checks are disabled.

Unity 6000.3 removed the two-argument internal `NativeArray<T>.ReadOnly`
constructor from its Windows Player reference assemblies. The registry package
still calls that constructor in release/player compilation, which prevents
HybridCLR Generate/All and Windows IL2CPP builds. The replacement preserves the
same non-owning, read-only view and leaves the safety-check path unchanged.
