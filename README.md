# ObjectPoolLinter

A Roslyn analyzer for Unity C# that detects object allocations in hot paths (like `Update`, `FixedUpdate`, etc.) and suggests using object pools to avoid garbage collection pressure and frame hitches.

## Features

- **Detects allocations in Unity hot paths**: Flags `new` object allocations and `Object.Instantiate()` calls inside frequently-called Unity methods
- **Covers 18 Unity message methods**: `Update`, `FixedUpdate`, `LateUpdate`, `OnGUI`, `OnTriggerStay`, `OnTriggerStay2D`, `OnCollisionStay`, `OnCollisionStay2D`, `OnMouseOver`, `OnMouseDrag`, `OnAnimatorMove`, `OnAnimatorIK`, `OnRenderObject`, `OnWillRenderObject`, `OnPreRender`, `OnPostRender`, `OnDrawGizmos`, `OnDrawGizmosSelected`
- **Code fixes**: Provides quick actions to replace allocations with object pool `Get()` calls or add TODO comments
- **Works with any object pool implementation**: The fix assumes a `{TypeName}Pool.Get()` pattern (e.g., `ListPool<int>.Get()`)

## Usage

The analyzer runs automatically during build and in IDEs that support Roslyn analyzers (Visual Studio, VS Code with C# Dev Kit, Rider).

### Code Fixes

When a diagnostic is reported, you can apply one of these quick fixes:

1. **Replace with object pool Get()** - Replaces `new Type()` with `TypePool.Get()`
2. **Add pooling TODO comment** - Adds a comment reminding you to use pooling

