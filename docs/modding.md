# Writing OpenFairway mods

The modding guide and API reference live in [`index.html`](index.html), published at
https://xk0fe.github.io/pga2k25-openfairway/. Working example mods are in [`../examples`](../examples).

In short: a mod is a C# library for .NET 8 implementing `IFairwayMod`, built to one DLL and placed with a
`mod.json` in `<game>\OpenFairway\mods\<Name>\`. Regenerate the guide with `python tools/make-docs.py` after changing
the API or the examples.
