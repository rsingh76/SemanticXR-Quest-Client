# gRPC + Protobuf setup for the Unity client

This folder holds the `.proto` sources and the C# code we use to talk to the
server over gRPC. This README documents both the **compile-time** pieces
(how `.proto` turns into C#) and the **runtime** pieces (how a gRPC call
actually gets bytes over the wire), because Unity makes the runtime story
non-obvious.

If you're just editing a `.proto` and want to regenerate the C#, jump to
[Regenerating the C# message stubs](#regenerating-the-c-message-stubs).

---

## Overview

```
  .proto (source)
      │
      │ protoc --csharp_out=.          ← compile-time
      ▼
  XrService.cs / Vis.cs                ← message classes (auto-generated)
  XrServiceGrpc.cs / VisGrpc.cs        ← gRPC client stubs (hand-written, see below)
      │
      │ referenced by application code
      ▼
  GrpcFramesClient.cs                  ← runtime
  AudioStreamController.cs
      │
      ▼
  Grpc.Net.Client (managed)            ← needs an HTTP/2 transport
      │
      ▼
  YetAnotherHttpHandler (YAHA)         ← native Rust/hyper HTTP/2
  Packages/com.cysharp.yetanotherhttphandler/
```

Codegen and runtime are independent concerns — `protoc` and YAHA never see each
other. The stubs just reference runtime types; YAHA just handles the bytes.

---

## File inventory

### In this folder (`Assets/Scripts/Proto/`)

| File | Kind | Origin |
|------|------|--------|
| `xr_service.proto` | source | Copy of `debug_server/xr_service.proto` and of the server's equivalent |
| `vis.proto`        | source | Copy of `semantic-slam-server/slam/protocols/vis_proto/vis.proto` |
| `XrService.cs`     | **auto-generated** | `protoc --csharp_out=.` of `xr_service.proto`. Contains message classes only (no service stubs). |
| `Vis.cs`           | **auto-generated** | `protoc --csharp_out=.` of `vis.proto`. Messages only. |
| `XrServiceGrpc.cs` | **hand-written** | Minimal gRPC client stubs for the `XrService` service. |
| `VisGrpc.cs`       | **hand-written** | Minimal gRPC client stubs for the `VisualizerServer` service. |

The hand-written `*Grpc.cs` files follow the exact pattern the standard
`grpc_csharp_plugin` would have produced, just trimmed to client-only (we never
host a gRPC server inside Unity).

### In `Assets/Plugins/Grpc/` — runtime gRPC stack

Ship these DLLs so Unity's IL2CPP/Mono can actually run gRPC calls:

| DLL | Role |
|-----|------|
| `Google.Protobuf.dll` | Message serialization (referenced by the generated `.cs` files) |
| `Grpc.Core.Api.dll` | Type definitions (`Marshaller`, `Method`, `ClientBase`, `CallInvoker`). Referenced by the hand-written `*Grpc.cs` files. |
| `Grpc.Net.Client.dll` | The managed gRPC client (`GrpcChannel.ForAddress`) |
| `Grpc.Net.Common.dll` | Shared helpers |
| `Microsoft.Extensions.Logging.Abstractions.dll` | Logging interfaces used by `Grpc.Net.Client` |
| `System.Buffers.dll`, `System.Memory.dll`, `System.Runtime.CompilerServices.Unsafe.dll`, `System.Diagnostics.DiagnosticSource.dll`, `System.IO.Pipelines.dll` | Modern BCL bits that Unity's Mono doesn't ship standalone |
| `link.xml` | Keeps the IL2CPP linker from stripping gRPC types we reach only via reflection |

These were dropped in from the NuGet packages of matching versions. If you ever
bump them, pull the same set from `Grpc.Net.Client <new version>` NuGet and keep
them in sync.

### In `Packages/com.cysharp.yetanotherhttphandler/` — the HTTP/2 fix

See the "Runtime — why YAHA is required" section below. Installed as a local
UPM package; contains a C# `asmdef` + native `.so`/`.dll` for each supported
platform.

---

## Compile-time — why the gRPC stubs are hand-written

The canonical flow in a non-Unity gRPC C# project is:

```
protoc --csharp_out=gen --grpc_out=gen --plugin=protoc-gen-grpc=grpc_csharp_plugin foo.proto
```

…which emits **two** files per proto: a messages file and a `*Grpc.cs`
file containing `FooClient`, `FooBase`, and the associated marshallers. The
plugin binary comes from the `Grpc.Tools` NuGet package.

### Why we don't use that plugin

1. **The native `Grpc.Core` C# package was deprecated in 2022** (see
   <https://grpc.io/blog/grpc-csharp-future/>). The plugin binary still works,
   but the future of gRPC C# is `Grpc.Net.Client` (managed) + `Grpc.Tools`.
2. `Grpc.Tools` is primarily an MSBuild integration — it runs the plugin
   automatically during a `.csproj` build. Unity doesn't use standard `.csproj`
   semantics (it auto-generates its own), so wiring `Grpc.Tools` into a Unity
   project is fiddly.
3. The plugin emits more than we need (server base classes, async/sync
   variants, partial class hooks we don't use). Trimming is simpler than
   integrating.

For a tiny number of services, hand-writing the client stub is both simpler and
easier to audit than making the MSBuild plugin path work. If the service count
grows, switching to `Grpc.Tools` becomes a better trade-off — see "Optional:
switch to Grpc.Tools" at the bottom.

### What a hand-written stub looks like (anatomy of `VisGrpc.cs`)

```csharp
namespace XrVis                                       // <- matches .proto "package" name
{
    public static partial class VisualizerServer      // <- matches .proto "service" name
    {
        static readonly string __ServiceName = "xrVis.VisualizerServer";

        // 1) A Marshaller<T> per message type we send or receive.
        //    Wraps the protobuf ToByteArray / Parser.ParseFrom methods.
        static readonly grpc::Marshaller<AudioFile> __Marshaller_AudioFile = ...;
        static readonly grpc::Marshaller<allPointClouds> __Marshaller_allPointClouds = ...;

        // 2) A Method<TReq, TResp> descriptor per RPC.
        //    Second arg picks the call shape: Unary, ClientStreaming,
        //    ServerStreaming, or DuplexStreaming.
        static readonly grpc::Method<AudioFile, allPointClouds> __Method_clientTextQuery =
            new grpc::Method<AudioFile, allPointClouds>(
                grpc::MethodType.ClientStreaming, __ServiceName,
                "clientTextQuery",
                __Marshaller_AudioFile, __Marshaller_allPointClouds);

        // 3) A client class deriving from ClientBase<T>, exposing one method
        //    per RPC. Pick the matching CallInvoker.AsyncXxxCall overload for
        //    the call shape.
        public partial class VisualizerServerClient : grpc::ClientBase<VisualizerServerClient>
        {
            public VisualizerServerClient(grpc::ChannelBase channel) : base(channel) { }

            public virtual grpc::AsyncClientStreamingCall<AudioFile, allPointClouds>
                clientTextQuery(grpc::Metadata headers = null,
                                System.DateTime? deadline = null,
                                System.Threading.CancellationToken cancellationToken = default)
            {
                return CallInvoker.AsyncClientStreamingCall(
                    __Method_clientTextQuery,
                    null, new grpc::CallOptions(headers, deadline, cancellationToken));
            }

            protected override VisualizerServerClient NewInstance(ClientBaseConfiguration c)
                => new VisualizerServerClient(c);
        }
    }
}
```

`XrServiceGrpc.cs` has the same shape. When a `.proto` adds or removes an RPC,
mirror that change by:

1. Adding/removing a `Marshaller<T>` for any new request/response types.
2. Adding/removing a `Method<TReq, TResp>` descriptor with the correct
   `MethodType`.
3. Adding/removing a method in the `...Client` class that routes to
   `CallInvoker.AsyncUnaryCall` / `AsyncClientStreamingCall` /
   `AsyncServerStreamingCall` / `AsyncDuplexStreamingCall` depending on the
   RPC's streaming shape.

---

## Regenerating the C# message stubs

You only need to regenerate when `xr_service.proto` or `vis.proto` changes.
The hand-written `*Grpc.cs` files are edited manually (see "Anatomy" above).

### Step 0: get `protoc`

Any recent `protoc` works. In this project we use the one that ships with
the `grpc_tools` Python package inside the `semanticxr` conda env:

```
C:\Users\<you>\miniconda3\envs\semanticxr\Scripts\protoc.exe
```

If you don't have it, `pip install grpcio-tools` in any Python env gives you
`python -m grpc_tools.protoc` which works identically.

### Step 1: regenerate

From this folder (`Assets/Scripts/Proto/`):

```powershell
$protoc = "C:\Users\$env:USERNAME\miniconda3\envs\semanticxr\Scripts\protoc.exe"

& $protoc --csharp_out=. xr_service.proto
& $protoc --csharp_out=. vis.proto
```

Equivalent with the Python path:

```powershell
python -m grpc_tools.protoc -I. --csharp_out=. xr_service.proto
python -m grpc_tools.protoc -I. --csharp_out=. vis.proto
```

Both commands overwrite `XrService.cs` / `Vis.cs` in place. **Do not edit
these files by hand** — your edits would be wiped on the next regen.

### Step 2: reconcile `*Grpc.cs` if service methods changed

Check the `service { ... }` blocks in the `.proto`. If any method was added,
removed, or had its signature changed, edit the corresponding hand-written
`*Grpc.cs` per the anatomy above. Keep `MethodType` matching the streaming
shape declared in the `.proto`.

If only **message** fields changed (not services), the hand-written files
need no edits.

### Step 3: refresh Unity

Let Unity reimport the generated files (auto-triggered when Editor regains
focus). Fix any call sites if message field names changed.

---

## Runtime — why YAHA is required

A Unity player running on IL2CPP Android (Quest) uses Mono's BCL for networking.
Mono's HTTP stack supports HTTP/1.1 but its HTTP/2 implementation doesn't work
reliably for gRPC. Symptoms:

- `Grpc.Net.Client` silently fails with
  `Status(StatusCode="Internal", Detail="Error starting gRPC call. HttpRequestException: ... WebException: Error getting response stream (ReadDoneAsync2): ReceiveFailure")`
- Even after setting `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)`, the handshake still fails.
- `SocketsHttpHandler` itself isn't in Unity's .NET Standard 2.1 surface (`error CS0234: 'SocketsHttpHandler' does not exist in the namespace 'System.Net.Http'`).

**YAHA (Cysharp.Net.Http.YetAnotherHttpHandler)** is the canonical fix. It's a
drop-in `HttpMessageHandler` implementation backed by native Rust
([hyper](https://hyper.rs/) + [rustls](https://github.com/rustls/rustls)) that
works on all Unity targets (Android, iOS, desktop standalones). It ships a
pre-built `.so` / `.dylib` / `.dll` for each ABI inside its Unity package.

With YAHA in place, `Grpc.Net.Client` works normally on Quest — the HTTP/2
handshake and framing happen inside the native library, bypassing Mono's
broken implementation entirely.

### Wiring it into a gRPC channel

```csharp
var yaha = new YetAnotherHttpHandler { Http2Only = true };   // h2c; skip upgrade

var channel = GrpcChannel.ForAddress($"http://{host}:{port}", new GrpcChannelOptions
{
    HttpHandler        = yaha,
    MaxSendMessageSize = 100 * 1024 * 1024,
    DisposeHttpClient  = false,
});

var client = new VisualizerServer.VisualizerServerClient(channel);
// ... use client normally ...

// On shutdown: dispose in this order to force-abort any pending calls fast.
// (Without this, disconnect can hang ~5s waiting for graceful CompleteAsync.)
call?.Dispose(); channel?.Dispose(); yaha?.Dispose();
```

The `Http2Only = true` option is mandatory for plain-text gRPC: it tells YAHA
to use HTTP/2 "prior knowledge" (h2c) and skip the HTTP/1.1→HTTP/2 upgrade
handshake that gRPC servers don't speak.

Both `GrpcFramesClient.cs` and `AudioStreamController.cs` follow this exact
pattern — use them as templates if you add a third gRPC client.

### Installing YAHA

The canonical Cysharp install routes a git URL through Unity Package Manager
(`com.cysharp.yetanotherhttphandler` via `git+https://...`). We avoided that
because (a) the deps it pulls in would collide with the DLLs already in
`Assets/Plugins/Grpc/` (`System.Memory`, `System.Buffers`, etc.) and (b) Unity's
git-UPM resolver is flaky when offline.

Instead, YAHA is installed as a **local** UPM package, vendored into this repo:

1. `git clone https://github.com/Cysharp/YetAnotherHttpHandler.git -b 1.11.5 /tmp/yaha`
2. Copy `src/YetAnotherHttpHandler/` → `Packages/com.cysharp.yetanotherhttphandler/` at the repo root. This folder contains C# sources, the `asmdef`, and prebuilt native binaries under `Plugins/Cysharp.Net.Http.YetAnotherHttpHandler.Native/runtimes/<platform>/native/`.
3. The one extra dep that isn't already in `Assets/Plugins/Grpc/` is
   `System.IO.Pipelines.dll` — grab it from the YAHA redist unitypackage
   (`Cysharp.Net.Http.YetAnotherHttpHandler.Dependencies.unitypackage`, release
   tag `redist-20240111-01`), extract only `System.IO.Pipelines.dll`, and drop
   it into `Assets/Plugins/Grpc/` alongside the other BCL DLLs. (`System.Memory`
   / `System.Buffers` / `System.Runtime.CompilerServices.Unsafe` already live
   there.)

Unity picks up the local package automatically — `Packages/<name>/package.json`
is how UPM recognizes an embedded package.

### Upgrading YAHA

1. Note the current version in `Packages/com.cysharp.yetanotherhttphandler/package.json`.
2. Check <https://github.com/Cysharp/YetAnotherHttpHandler/releases> for a newer tag.
3. Clone that tag and replace the package folder the same way as step 2 above.
4. Check whether the release notes mention any new managed dep DLLs. If so, extract them from the matching `...Dependencies.unitypackage` into `Assets/Plugins/Grpc/`.
5. Smoke test: tap the mic button in the streaming UI with a fake-points
   server, expect no `ReceiveFailure` in logcat.

---

## Keeping three proto copies in sync

`xr_service.proto` and `vis.proto` each exist in three places:

```
semantic-slam-server/slam/protocols/vis_proto/vis.proto          ← server source of truth
SemanticXR/debug_server/vis.proto                                 ← regen Python stubs
SemanticXR/Assets/Scripts/Proto/vis.proto                         ← regen C# stubs
```

When the server's canonical proto changes:

```powershell
# From the SemanticXR repo root
copy ..\semantic-slam-server\slam\protocols\vis_proto\vis.proto debug_server\vis.proto
copy ..\semantic-slam-server\slam\protocols\vis_proto\vis.proto Assets\Scripts\Proto\vis.proto
```

Then regenerate both sets of stubs:

- Python: see `debug_server/README.md` (runs `grpc_tools.protoc` with both
  `--python_out` and `--grpc_python_out`).
- C#: this file's [Regenerating](#regenerating-the-c-message-stubs) section.

---

## Troubleshooting

### `error CS0246: The type or namespace name 'YetAnotherHttpHandler' could not be found`

YAHA package not recognized by Unity. Check that
`Packages/com.cysharp.yetanotherhttphandler/package.json` exists and has a valid
`name` field, then in Unity: **Assets → Refresh** (or just Alt-Tab into the
Editor).

### `ReadDoneAsync2: ReceiveFailure` at runtime

YAHA isn't being used. Verify the channel is constructed with
`HttpHandler = yaha` (not default-constructed). If building for Android,
verify the `.so` files under `Packages/com.cysharp.yetanotherhttphandler/Plugins/Cysharp.Net.Http.YetAnotherHttpHandler.Native/runtimes/android-arm64/` have their `PluginImporter` metadata correctly set (Android + ARM64 enabled) — they should be pre-configured but can drift on package upgrade.

### `error CS0234: 'SocketsHttpHandler' does not exist in the namespace 'System.Net.Http'`

Someone tried to use `System.Net.Http.SocketsHttpHandler` directly. That type
isn't in Unity's BCL. Use `YetAnotherHttpHandler` instead.

### Generated `*.cs` file is empty or only has `namespace { }`

`protoc` couldn't find the `.proto` it was invoked with, or the proto has no
message declarations. Check working directory and `-I` include paths.

### Hand-written `*Grpc.cs` references a missing message type

After regenerating the message file, a message class got renamed (e.g. proto
field renamed from `clientTextQuery` → `ClientTextQuery` because of a case
change). Search the hand-written file for the old name and update. The
hand-written stubs don't regenerate themselves.

### Unity build fails with `__Method_clientTextQuery` being stripped by IL2CPP

Add the relevant gRPC types to `Assets/Plugins/Grpc/link.xml` to keep the linker
from stripping them.

---

## Optional: switch to `Grpc.Tools`

If hand-writing stubs becomes painful (dozens of services, frequent churn),
swap in the `Grpc.Tools` flow:

1. Install the NuGet `Grpc.Tools` via [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) (or vendor the plugin binary into the repo).
2. Add an editor script that invokes `grpc_csharp_plugin` during the build:
   `protoc --csharp_out=. --grpc_out=. --plugin=protoc-gen-grpc=<path>/grpc_csharp_plugin.exe *.proto`
3. Delete `XrServiceGrpc.cs` / `VisGrpc.cs` — the plugin will regenerate them with a `Base` class and full async/sync variants. Fix call sites if client method signatures differ slightly.
4. Keep YAHA (runtime) unchanged — `Grpc.Tools` only affects codegen.

Not worth it for two services. Easy migration later if the count grows.
