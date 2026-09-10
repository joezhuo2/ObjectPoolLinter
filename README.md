# ObjectPoolLinter

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

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

1. **Replace with object pool Get()** - Replaces `new Type(args)` with `TypePool.Get(args)`
2. **Add pooling TODO comment** - Adds a comment reminding you to use pooling

Constructor arguments are forwarded to `Get()` unchanged, so `new Enemy(hp)` becomes
`EnemyPool.Get(hp)`. The fix assumes your pool exposes a `Get` overload matching the constructor
signature; if it does not, the result will not compile and the missing overload is the thing to add.

The fix is **not** offered when the allocation carries an object or collection initializer
(`new Enemy { Hp = 5 }`, `new List<int> { 1, 2 }`), because an initializer cannot be attached to a
method call and dropping it would silently lose code. Use the TODO-comment fix there and rewrite the
initializer by hand.

## License

MIT - see [LICENSE](LICENSE).
