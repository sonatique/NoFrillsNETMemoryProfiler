# Assemblies from dotnet-gcdump

The heap graph reader and the live collector are published only inside the `dotnet-gcdump` global
tool. No NuGet package exposes them: `Microsoft.Diagnostics.Tracing.TraceEvent` does not contain the
`Graphs` namespace, which is why these files are copied here instead of referenced as packages.

Copied from `dotnet-gcdump` 9.0.661903 (MIT licensed, part of dotnet/diagnostics):

| File | Used for |
|---|---|
| `dotnet-gcdump.dll` | `Graphs.MemoryGraph`, `GCHeapDump`, `RefGraph`, `EventPipeDotNetHeapDumper` |
| `Microsoft.Diagnostics.FastSerialization.dll` | reading the serialized graph |
| `Microsoft.Diagnostics.Tracing.TraceEvent.dll` | event parsing used by the collector |
| `Microsoft.Diagnostics.NETCore.Client.dll` | listing and contacting live processes |
| `Dia2Lib.dll`, `TraceReloggerLib.dll` | dependencies of TraceEvent |

## Refreshing them

```
dotnet tool update --global dotnet-gcdump
```

then copy the files again from

```
%USERPROFILE%\.dotnet\tools\.store\dotnet-gcdump\<version>\dotnet-gcdump\<version>\tools\net8.0\any\
```

and update the version above. Run the tests afterwards: they exercise the reading path against a
real dump, so an incompatible update shows up immediately.
